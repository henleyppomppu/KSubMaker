using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// 복구 가능한 오류 자동 재시도, end to end through <see cref="JobQueueService"/>.
///
/// The regression this file exists for: a worker reported <c>INVALID_TRANSLATION_RESPONSE</c>, the
/// queue logged "복구 가능한 오류로 작업을 한 번 자동 재시도합니다", and then
/// <c>job.TransitionTo(Pending)</c> threw <see cref="InvalidJobTransitionException"/> because the
/// transition table had no edge out of an in-flight stage back into the queue. The exception was
/// swallowed by the pump's generic handler, so the user saw UNKNOWN instead of the real error and
/// automatic retry never actually ran — for any recoverable error, ever.
/// </summary>
public sealed class AutoRetryTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job NewJob(string id = "job-1") => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = JobStatus.Pending
    };

    private static AppSettings Settings(bool autoRetry = true) => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AutoRetryOnRecoverableError = autoRetry
    };

    private static JobQueueService NewQueue(InMemoryJobRepository repository, IJobProcessor processor) =>
        new(
            repository,
            new SingleProcessorSelector(processor),
            new RecordingCheckpointStore(),
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

    /// <summary>
    /// The one that matters: a processor that fails recoverably once and then succeeds must leave a
    /// completed job behind, with no exception anywhere in between.
    /// </summary>
    [Fact]
    public async Task A_recoverable_failure_is_retried_once_and_the_job_then_completes()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(
                ErrorCodes.InvalidTranslationResponse,
                UserFacingErrors.Describe(ErrorCodes.InvalidTranslationResponse),
                recoverable: true),
            JobExecutionResult.Ok("/videos/job-1.ko.srt", cueCount: 12));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();

        processor.Calls.Should().Be(2, "the failure is retried exactly once");
        job.Status.Should().Be(JobStatus.Completed);
        job.ErrorCode.Should().BeNull("a job that succeeded on the retry carries no error");
        job.ErrorMessage.Should().BeNull();
        job.RetryCount.Should().Be(1);
        job.OutputPath.Should().Be("/videos/job-1.ko.srt");
    }

    /// <summary>
    /// A silent audio track is not a failure: there is simply no subtitle to make. Two lectures in
    /// the user's library sat in red 실패 for exactly this (2026-11-02).
    /// </summary>
    [Fact]
    public async Task A_silent_track_is_parked_as_nothing_to_do_and_is_never_retried()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(ErrorCodes.NoSpeech, "whisper returned no segments", recoverable: true));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        QueueDrainedEventArgs? drained = null;
        queue.QueueDrained += (_, e) => drained = e;

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();

        processor.Calls.Should().Be(1, "the same silence would come back on a retry");
        job.Status.Should().Be(JobStatus.Skipped);
        job.ErrorCode.Should().Be(ErrorCodes.NoSpeech);
        job.ErrorMessage.Should().Be(UserFacingErrors.Describe(ErrorCodes.NoSpeech));
        job.OverallProgress.Should().Be(100d);
        job.RetryCount.Should().Be(0);
        drained.Should().NotBeNull();
        drained!.Outcome.Failed.Should().Be(0, "a silent track is not a failure");
    }

    /// <summary>
    /// The failure mode that made the bug invisible: the transition threw, the pump's catch-all
    /// turned it into UNKNOWN, and the real error code never reached the user.
    /// </summary>
    [Fact]
    public async Task A_retry_that_fails_again_reports_the_real_error_code_not_UNKNOWN()
    {
        var failure = JobExecutionResult.Fail(
            ErrorCodes.InvalidTranslationResponse,
            "번역 결과가 올바르지 않습니다(빈 번역 1건).",
            recoverable: true);

        var processor = new ScriptedJobProcessor(failure, failure);

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();

        processor.Calls.Should().Be(2);
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().Be(ErrorCodes.InvalidTranslationResponse);
        job.ErrorCode.Should().NotBe(ErrorCodes.Unknown);
        job.ErrorMessage.Should().Contain("빈 번역");
    }

    /// <summary>
    /// The retry starts from Pending — that is what clears the previous error — and then re-enters
    /// the pipeline, so the grid shows the stage the retry is actually in rather than "대기 중".
    /// </summary>
    [Fact]
    public async Task The_retry_re_enters_the_pipeline_instead_of_sitting_in_the_queue()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(ErrorCodes.CudaOutOfMemory, "GPU 메모리가 부족합니다.", recoverable: true),
            JobExecutionResult.Ok("/videos/job-1.ko.srt", cueCount: 3));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        processor.StatusOnEntry.Should().HaveCount(2);
        processor.StatusOnEntry[0].Should().Be(JobStatus.Probing);
        processor.StatusOnEntry[1].Should().Be(
            JobStatus.Probing, "the retry is a fresh run of the same phase, not a job parked in the queue");
    }

    /// <summary>
    /// A processor that reports nothing at all still has to reach Completed through 자막 저장 중 —
    /// the two-step walk must not depend on a progress report having dragged the status forward.
    /// </summary>
    [Fact]
    public async Task A_silent_processor_still_completes_through_the_writing_stage()
    {
        var processor = new ScriptedJobProcessor(JobExecutionResult.Ok("/videos/job-1.ko.srt", cueCount: 1))
        {
            StagesToReport = []
        };

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Completed);
        job.CurrentStage.Should().Be(JobStage.Done);
        job.OverallProgress.Should().Be(100d);
    }

    /// <summary>
    /// Status has to follow the worker through the run; a job stuck on "검사 중" for its whole
    /// lifetime is what made the stale-state bug invisible in the UI.
    /// </summary>
    [Fact]
    public async Task The_status_follows_the_reported_stage_through_the_run()
    {
        var seen = new List<JobStatus>();

        var processor = new ScriptedJobProcessor(JobExecutionResult.Ok("/videos/job-1.ko.srt", cueCount: 1));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        queue.JobChanged += (_, args) =>
        {
            lock (seen)
            {
                if (seen.Count == 0 || seen[^1] != args.Job.Status)
                {
                    seen.Add(args.Job.Status);
                }
            }
        };

        await RunToIdleAsync(queue, Settings());

        lock (seen)
        {
            seen.Should().ContainInOrder(
                JobStatus.Probing,
                JobStatus.ExtractingAudio,
                JobStatus.Transcribing,
                JobStatus.Translating,
                JobStatus.Completed);
        }
    }

    /// <summary>A resumed job reports 번역 중 first; the status must accept the jump, not reject it.</summary>
    [Fact]
    public async Task A_job_resuming_straight_into_translation_is_not_rejected()
    {
        var processor = new ScriptedJobProcessor(JobExecutionResult.Ok("/videos/job-1.ko.srt", cueCount: 5))
        {
            // "체크포인트에서 이어서 진행합니다: translating" — no audio extraction, no transcription.
            StagesToReport = [JobStage.Translating]
        };

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Completed);
        job.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Auto_retry_stays_off_when_the_setting_says_so()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(ErrorCodes.CudaOutOfMemory, "GPU 메모리가 부족합니다.", recoverable: true));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings(autoRetry: false));

        var job = queue.Jobs.Single();
        processor.Calls.Should().Be(1);
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().Be(ErrorCodes.CudaOutOfMemory);
        job.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task A_non_recoverable_failure_is_not_retried()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(ErrorCodes.VideoNotFound, UserFacingErrors.Describe(ErrorCodes.VideoNotFound)));

        var repository = new InMemoryJobRepository(NewJob());
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        await RunToIdleAsync(queue, Settings());

        var job = queue.Jobs.Single();
        processor.Calls.Should().Be(1);
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().Be(ErrorCodes.VideoNotFound);
    }
}
