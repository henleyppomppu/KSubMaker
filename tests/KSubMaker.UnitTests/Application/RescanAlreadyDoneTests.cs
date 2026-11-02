using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// A rescan that finds a finished Korean subtitle beside a job that is still waiting. The factory
/// flips the row to 완료; the queue must persist that as an update to the existing row rather than
/// treating it like a brand-new file (which would try to insert a duplicate and hand out a fresh
/// queue position).
/// </summary>
public sealed class RescanAlreadyDoneTests
{
    private static Job WaitingJob(string id, JobStatus status = JobStatus.Pending) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = status,
        QueueOrder = 0
    };

    private static VideoFile ScannedWithKorean(string id) => new()
    {
        FullPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Extension = ".mkv",
        SizeBytes = 0,
        LastWriteTimeUtc = default,
        ExternalSubtitlePaths = [$"/videos/{id}.ko.srt"],
        HasKoreanExternalSubtitle = true
    };

    private static JobQueueService NewQueue(InMemoryJobRepository repository) =>
        new(
            repository,
            new NeverRunsProcessorSelector(),
            new RecordingCheckpointStore(),
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    [Fact]
    public async Task A_waiting_job_whose_subtitle_now_exists_is_updated_in_place_as_completed()
    {
        var repository = new InMemoryJobRepository(WaitingJob("a"));
        var queue = NewQueue(repository);
        await queue.LoadAsync();

        var results = await queue.EnqueueAsync([ScannedWithKorean("a")], new AppSettings());

        results.Single().Decision.Should().Be(EnqueueDecision.AlreadyDone);
        queue.Jobs.Should().ContainSingle().Which.Id.Should().Be("a");
        queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        queue.Jobs.Single().QueueOrder.Should().Be(0, "an existing row keeps its place in the queue");
        repository.Updated.Should().Equal("a");
        (await repository.FindAsync("a"))!.Status.Should().Be(JobStatus.Completed);
    }

    /// <summary>
    /// After rows were removed the count is below the highest number in use. New jobs used to be
    /// numbered from the count and collided with existing rows (2026-11-02: 181..185 twice).
    /// </summary>
    [Fact]
    public async Task New_jobs_are_numbered_after_the_highest_existing_queue_order()
    {
        var early = WaitingJob("a");
        early.QueueOrder = 5;
        var late = WaitingJob("b");
        late.QueueOrder = 9;

        var repository = new InMemoryJobRepository(early, late);
        var queue = NewQueue(repository);
        await queue.LoadAsync();

        var file = ScannedWithKorean("c") with { ExternalSubtitlePaths = [], HasKoreanExternalSubtitle = false };
        await queue.EnqueueAsync([file], new AppSettings());

        queue.Jobs.Select(j => (j.FileName, j.QueueOrder)).Should().Equal(("a.mkv", 5), ("b.mkv", 9), ("c.mkv", 10));
    }

    [Fact]
    public async Task A_brand_new_file_with_korean_is_still_inserted_as_completed()
    {
        var repository = new InMemoryJobRepository();
        var queue = NewQueue(repository);
        await queue.LoadAsync();

        var results = await queue.EnqueueAsync([ScannedWithKorean("b")], new AppSettings());

        results.Single().Decision.Should().Be(EnqueueDecision.AlreadyDone);
        queue.Jobs.Should().ContainSingle().Which.Status.Should().Be(JobStatus.Completed);
        repository.Updated.Should().BeEmpty("a new row is inserted, not updated");
        (await repository.GetAllAsync()).Should().ContainSingle();
    }
}
