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
/// 다시 만들기. A finished job whose subtitle turned out wrong could not be made again: 재시도 put it
/// back in the queue, but the run reused the cached result and the default 결과 파일 충돌 처리
/// (건너뛰기) refused to replace the subtitle already on disk — and a rescan flipped the row straight
/// back to 완료 because that subtitle existed.
/// </summary>
public sealed class RemakeTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job FinishedJob(string id = "a") => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = JobStatus.Completed,
        WhisperModel = "whisper-medium",
        TranslationEngine = TranslationEngineKind.LocalTranslationModel,
        TranslationModel = "nllb-200-distilled-1.3B",
        DetectedLanguage = "ja"
    };

    private static JobQueueService NewQueue(
        InMemoryJobRepository repository,
        RecordingCheckpointStore store,
        IJobProcessor? processor = null) =>
        new(
            repository,
            processor is null ? new NeverRunsProcessorSelector() : new SingleProcessorSelector(processor),
            store,
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    private static async Task RunToIdleAsync(JobQueueService queue, AppSettings settings)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(object? sender, QueueStateChangedEventArgs args)
        {
            if (args.State is QueueState.Idle or QueueState.Paused)
            {
                idle.TrySetResult();
            }
        }

        queue.StateChanged += OnState;
        try
        {
            await queue.StartAsync(settings);
            await idle.Task.WaitAsync(RunTimeout);
        }
        finally
        {
            queue.StateChanged -= OnState;
        }
    }

    [Fact]
    public async Task A_remade_job_overwrites_its_subtitle_once_and_then_follows_the_global_policy_again()
    {
        var repository = new InMemoryJobRepository(FinishedJob());
        var processor = new ScriptedJobProcessor();
        var queue = NewQueue(repository, new RecordingCheckpointStore(), processor);
        await queue.LoadAsync();
        var settings = new AppSettings
        {
            ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
            OutputConflictPolicy = OutputConflictPolicy.Skip
        };

        (await queue.RemakeAsync(["a"], clearCache: false)).Should().Equal("a");
        await RunToIdleAsync(queue, settings);

        processor.ConflictPolicyOnEntry.Should().Equal(OutputConflictPolicy.Overwrite);
        settings.OutputConflictPolicy.Should().Be(OutputConflictPolicy.Skip, "only that job's copy changes");
        var job = queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Completed);
        job.RemakeRequested.Should().BeFalse();

        await queue.RetryAsync(["a"]);
        await RunToIdleAsync(queue, settings);
        processor.ConflictPolicyOnEntry.Should().Equal(OutputConflictPolicy.Overwrite, OutputConflictPolicy.Skip);
    }

    /// <summary>
    /// 다시 만들기 starts right away, or — with the queue already busy on something else — rides along
    /// with that run. A run started over another selection or folder must not leave it waiting.
    /// </summary>
    [Fact]
    public async Task A_run_over_another_selection_still_takes_a_remade_job()
    {
        var other = FinishedJob("b");
        other.Status = JobStatus.Pending;
        other.QueueOrder = 1;
        var processor = new ScriptedJobProcessor();
        var queue = NewQueue(new InMemoryJobRepository(FinishedJob(), other), new RecordingCheckpointStore(), processor);
        await queue.LoadAsync();
        await queue.RemakeAsync(["a"], clearCache: false);

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.StateChanged += (_, args) =>
        {
            if (args.State is QueueState.Idle)
            {
                idle.TrySetResult();
            }
        };
        await queue.StartAsync(
            new AppSettings { ProcessingStrategy = ProcessingStrategy.SequentialPerFile },
            ["b"],
            job => job.Id == "b");
        await idle.Task.WaitAsync(RunTimeout);

        processor.Calls.Should().Be(2);
        queue.Jobs.Should().OnlyContain(j => j.Status == JobStatus.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_cache_is_cleared_only_when_asked(bool clearCache)
    {
        var store = new RecordingCheckpointStore();
        var repository = new InMemoryJobRepository(FinishedJob());
        var queue = NewQueue(repository, store);
        await queue.LoadAsync();

        await queue.RemakeAsync(["a"], clearCache);

        store.Cleared.Should().Equal(clearCache ? ["a"] : Array.Empty<string>());
        var job = queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Pending);
        job.RemakeRequested.Should().BeTrue();
        (await repository.FindAsync("a"))!.RemakeRequested.Should().BeTrue("a restart before the run keeps the request");
    }

    [Fact]
    public async Task Only_completed_jobs_are_remade()
    {
        var failed = FinishedJob("f");
        failed.Status = JobStatus.Failed;
        var store = new RecordingCheckpointStore();
        var queue = NewQueue(new InMemoryJobRepository(failed), store);
        await queue.LoadAsync();

        (await queue.RemakeAsync(["f"], clearCache: true)).Should().BeEmpty();

        queue.Jobs.Single().Status.Should().Be(JobStatus.Failed);
        store.Cleared.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rescan_does_not_mark_a_remade_job_done_because_its_old_subtitle_exists()
    {
        var queue = NewQueue(new InMemoryJobRepository(FinishedJob()), new RecordingCheckpointStore());
        await queue.LoadAsync();
        await queue.RemakeAsync(["a"], clearCache: false);

        var scanned = new VideoFile
        {
            FullPath = "/videos/a.mkv",
            FileName = "a.mkv",
            Extension = ".mkv",
            SizeBytes = 0,
            LastWriteTimeUtc = default,
            ExternalSubtitlePaths = ["/videos/a.ko.srt"],
            HasKoreanExternalSubtitle = true
        };
        await queue.EnqueueAsync([scanned], new AppSettings());

        queue.Jobs.Single().Status.Should().Be(JobStatus.Pending);
    }
}
