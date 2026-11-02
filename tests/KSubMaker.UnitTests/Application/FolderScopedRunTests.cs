using FluentAssertions;
using KSubMaker.Application.Abstractions;
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
/// The list holds jobs from every folder opened so far and the grid shows one folder at a time.
/// 전체 실행 runs the folder on screen, and the folder can be refreshed (or another one opened)
/// while the queue runs.
/// </summary>
public sealed class FolderScopedRunTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job NewJob(string id, string folder, int order) => new()
    {
        Id = id,
        VideoPath = $"{folder}/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = JobStatus.Pending,
        QueueOrder = order
    };

    private static VideoFile ScannedWithKorean(Job job) => new()
    {
        FullPath = job.VideoPath,
        FileName = job.FileName,
        Extension = ".mkv",
        SizeBytes = 0,
        LastWriteTimeUtc = default,
        ExternalSubtitlePaths = [Path.ChangeExtension(job.VideoPath, ".ko.srt")],
        HasKoreanExternalSubtitle = true
    };

    private static AppSettings Settings() => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AutoRetryOnRecoverableError = false
    };

    private static JobQueueService NewQueue(InMemoryJobRepository repository, IJobProcessor processor) =>
        new(
            repository,
            new SingleProcessorSelector(processor),
            new RecordingCheckpointStore(),
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    private static async Task WaitForIdleAsync(JobQueueService queue)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(object? sender, QueueStateChangedEventArgs args)
        {
            if (args.State is QueueState.Idle)
            {
                idle.TrySetResult();
            }
        }

        queue.StateChanged += OnState;
        try
        {
            if (queue.State is QueueState.Idle)
            {
                return;
            }

            await idle.Task.WaitAsync(RunTimeout);
        }
        finally
        {
            queue.StateChanged -= OnState;
        }
    }

    [Fact]
    public async Task A_run_scoped_to_a_folder_leaves_the_other_folders_jobs_waiting()
    {
        var here = NewJob("a", "/lectures/2021", 0);
        var elsewhere = NewJob("b", "/lectures/2022", 1);
        var processor = new ScriptedJobProcessor();
        var queue = NewQueue(new InMemoryJobRepository(here, elsewhere), processor);
        await queue.LoadAsync();

        await queue.StartAsync(
            Settings(),
            scope: job => QueueListReconciler.IsInFolder(job.VideoPath, "/lectures/2021", includeSubfolders: true));
        await WaitForIdleAsync(queue);

        processor.Calls.Should().Be(1);
        here.Status.Should().Be(JobStatus.Completed);
        elsewhere.Status.Should().Be(JobStatus.Pending, "전체 실행 runs the folder on screen, not every folder ever opened");
    }

    /// <summary>
    /// 목록 새로고침 while the queue runs: the waiting job whose subtitle has appeared is marked 완료
    /// and never handed to the processor; the running one is left alone.
    /// </summary>
    [Fact]
    public async Task A_refresh_during_a_run_finishes_a_waiting_job_without_running_it()
    {
        var running = NewJob("a", "/lectures/2021", 0);
        var waiting = NewJob("b", "/lectures/2021", 1);
        var processor = new BlockingJobProcessor();
        var queue = NewQueue(new InMemoryJobRepository(running, waiting), processor);
        await queue.LoadAsync();

        await queue.StartAsync(Settings());
        await processor.Started.WaitAsync(RunTimeout);

        var results = await queue.EnqueueAsync([ScannedWithKorean(running), ScannedWithKorean(waiting)], Settings());

        results.Select(r => r.Decision).Should().Equal(EnqueueDecision.Unchanged, EnqueueDecision.AlreadyDone);
        JobStateMachine.IsActive(running.Status).Should().BeTrue("the refresh leaves the running job alone");

        processor.Release();
        await WaitForIdleAsync(queue);

        processor.Calls.Should().Be(1, "the job the refresh finished must not be started");
        running.Status.Should().Be(JobStatus.Completed);
        waiting.Status.Should().Be(JobStatus.Completed);
    }
}
