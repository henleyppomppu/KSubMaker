using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// 건너뛰기 (<see cref="JobQueueService.CancelAsync"/>) is one button with two honest outcomes: a job
/// that never left <see cref="JobStatus.Pending"/> becomes <see cref="JobStatus.Skipped"/> — nothing
/// was abandoned — while a job that was actually running becomes <see cref="JobStatus.Cancelled"/>.
/// Before this split both read as "건너뜀" on screen, which told the user a run they had to forcibly
/// stop was really just a file that never needed doing.
/// </summary>
public sealed class JobCancelTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job NewJob(string id, JobStatus status = JobStatus.Pending) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = status
    };

    private static AppSettings SequentialSettings() => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AutoRetryOnRecoverableError = false
    };

    private static JobQueueService NewQueue(
        InMemoryJobRepository repository,
        RecordingCheckpointStore store,
        IJobProcessorSelector? selector = null) =>
        new(
            repository,
            selector ?? new NeverRunsProcessorSelector(),
            store,
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    [Fact]
    public async Task Cancelling_a_job_that_never_started_marks_it_skipped_not_cancelled()
    {
        var repository = new InMemoryJobRepository(NewJob("a"));
        var queue = NewQueue(repository, new RecordingCheckpointStore());
        await queue.LoadAsync();

        await queue.CancelAsync(["a"]);

        queue.Jobs.Single(j => j.Id == "a").Status.Should().Be(JobStatus.Skipped);
    }

    [Fact]
    public async Task Cancelling_a_job_that_is_actively_running_marks_it_cancelled_not_skipped()
    {
        var processor = new BlockingJobProcessor();
        var repository = new InMemoryJobRepository(NewJob("a"));
        var queue = NewQueue(repository, new RecordingCheckpointStore(), new SingleProcessorSelector(processor));
        await queue.LoadAsync();

        await queue.StartAsync(SequentialSettings());
        await processor.Started.WaitAsync(RunTimeout);

        await queue.CancelAsync(["a"]);

        // CancelAsync sets the terminal status synchronously — it does not wait for the worker to
        // actually unwind — so the outcome is already visible without waiting on RemoveAsync.
        queue.Jobs.Single(j => j.Id == "a").Status.Should().Be(JobStatus.Cancelled);

        // Let the worker actually leave before the queue is disposed, matching how a real 취소
        // finishes: RequestCancel only asks, this is what proves it was honoured.
        await queue.RemoveAsync(["a"], TimeSpan.FromSeconds(5));
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_a_paused_job_with_progress_already_made_is_cancelled_not_skipped()
    {
        var job = NewJob("a", JobStatus.Paused);
        job.CurrentStage = JobStage.Transcribing;
        job.OverallProgress = 40d;

        var repository = new InMemoryJobRepository(job);
        var queue = NewQueue(repository, new RecordingCheckpointStore());
        await queue.LoadAsync();

        await queue.CancelAsync(["a"]);

        // Paused already means "some work happened"; calling that 건너뜀 would misreport it as work
        // that was never attempted.
        queue.Jobs.Single(j => j.Id == "a").Status.Should().Be(JobStatus.Cancelled);
    }
}
