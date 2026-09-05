using FluentAssertions;
using KSubMaker.Domain.Jobs;
using KSubMaker.UnitTests.Fakes;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Side effects of <see cref="Job.TransitionTo"/>, <see cref="Job.MarkFailed"/> and friends.</summary>
public sealed class JobTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static Job NewJob(JobStatus status = JobStatus.Pending) => new()
    {
        VideoPath = "/videos/movie.mkv",
        FileName = "movie.mkv",
        Status = status
    };

    [Fact]
    public void Transition_to_completed_sets_full_progress_and_a_completion_timestamp()
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.WritingSubtitle);
        job.OverallProgress = 42d;
        job.StageProgress = 17d;
        job.ErrorCode = "STALE";
        job.ErrorMessage = "stale";

        job.TransitionTo(JobStatus.Completed, clock);

        job.Status.Should().Be(JobStatus.Completed);
        job.CurrentStage.Should().Be(JobStage.Done);
        job.OverallProgress.Should().Be(100d);
        job.StageProgress.Should().Be(100d);
        job.CompletedAtUtc.Should().Be(Now.UtcDateTime);
        job.UpdatedAtUtc.Should().Be(Now.UtcDateTime);
        job.ErrorCode.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Transition_to_pending_resets_every_run_scoped_field()
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Completed);
        job.CurrentStage = JobStage.Done;
        job.OverallProgress = 100d;
        job.StageProgress = 100d;
        job.CompletedAtUtc = Now.UtcDateTime.AddMinutes(-5);
        job.EstimatedTimeRemaining = TimeSpan.FromMinutes(3);
        job.ProcessingSpeed = 12.5d;

        job.TransitionTo(JobStatus.Pending, clock);

        job.Status.Should().Be(JobStatus.Pending);
        job.CurrentStage.Should().Be(JobStage.None);
        job.OverallProgress.Should().Be(0d);
        job.StageProgress.Should().Be(0d);
        job.CompletedAtUtc.Should().BeNull();
        job.EstimatedTimeRemaining.Should().BeNull();
        job.ProcessingSpeed.Should().Be(0d);
    }

    [Theory]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Paused)]
    public void Cancelling_or_pausing_clears_the_live_estimates_but_keeps_the_stage(JobStatus next)
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Transcribing);
        job.CurrentStage = JobStage.Transcribing;
        job.OverallProgress = 55d;
        job.EstimatedTimeRemaining = TimeSpan.FromMinutes(9);
        job.ProcessingSpeed = 4d;

        job.TransitionTo(next, clock);

        job.Status.Should().Be(next);
        job.CurrentStage.Should().Be(JobStage.Transcribing);
        job.OverallProgress.Should().Be(55d);
        job.EstimatedTimeRemaining.Should().BeNull();
        job.ProcessingSpeed.Should().Be(0d);
    }

    /// <summary>
    /// 건너뜀 only ever happens to a job that never left Pending, so unlike the
    /// cancel-or-pause-while-running case above, there is no stage or progress to preserve.
    /// </summary>
    [Fact]
    public void Skipping_a_pending_job_leaves_it_at_zero_progress()
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Pending);

        job.TransitionTo(JobStatus.Skipped, clock);

        job.Status.Should().Be(JobStatus.Skipped);
        job.CurrentStage.Should().Be(JobStage.None);
        job.OverallProgress.Should().Be(0d);
        job.EstimatedTimeRemaining.Should().BeNull();
        job.ProcessingSpeed.Should().Be(0d);
    }

    [Fact]
    public void Entering_an_active_status_clears_a_previous_error()
    {
        var job = NewJob(JobStatus.Paused);
        job.ErrorCode = "FFMPEG_FAILED";
        job.ErrorMessage = "이전 실패";

        job.TransitionTo(JobStatus.Transcribing, new FixedTimeProvider(Now));

        job.ErrorCode.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void An_illegal_transition_throws_InvalidJobTransitionException_with_a_korean_message()
    {
        var job = NewJob(JobStatus.Completed);

        var act = () => job.TransitionTo(JobStatus.Transcribing, new FixedTimeProvider(Now));

        act.Should().Throw<InvalidJobTransitionException>()
            .Which.Message.Should().Contain("전환할 수 없습니다");
    }

    [Fact]
    public void An_illegal_transition_leaves_the_job_untouched()
    {
        var job = NewJob(JobStatus.Completed);
        job.OverallProgress = 100d;
        var before = job.UpdatedAtUtc;

        var act = () => job.TransitionTo(JobStatus.Translating, new FixedTimeProvider(Now));

        act.Should().Throw<InvalidJobTransitionException>();
        job.Status.Should().Be(JobStatus.Completed);
        job.OverallProgress.Should().Be(100d);
        job.UpdatedAtUtc.Should().Be(before);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.WritingSubtitle)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    [InlineData(JobStatus.Paused)]
    public void MarkFailed_always_succeeds_whatever_the_current_status(JobStatus from)
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(from);
        job.EstimatedTimeRemaining = TimeSpan.FromMinutes(2);
        job.ProcessingSpeed = 7d;

        job.MarkFailed("CUDA_OUT_OF_MEMORY", "GPU 메모리가 부족합니다.", clock);

        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().Be("CUDA_OUT_OF_MEMORY");
        job.ErrorMessage.Should().Be("GPU 메모리가 부족합니다.");
        job.UpdatedAtUtc.Should().Be(Now.UtcDateTime);
        job.EstimatedTimeRemaining.Should().BeNull();
        job.ProcessingSpeed.Should().Be(0d);
    }

    [Fact]
    public void MarkFailed_keeps_the_stage_so_a_retry_can_resume_from_it()
    {
        var job = NewJob(JobStatus.Translating);
        job.CurrentStage = JobStage.Translating;

        job.MarkFailed("TRANSLATION_FAILED", "번역 실패", new FixedTimeProvider(Now));

        job.CurrentStage.Should().Be(JobStage.Translating);
    }

    [Fact]
    public void EnterStage_moves_the_status_and_resets_the_stage_progress()
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Probing);
        job.StageProgress = 88d;

        job.EnterStage(JobStage.Transcribing, clock);

        job.Status.Should().Be(JobStatus.Transcribing);
        job.CurrentStage.Should().Be(JobStage.Transcribing);
        job.StageProgress.Should().Be(0d);
        job.OverallProgress.Should().Be(ProgressCalculator.Overall(JobStage.Transcribing, 0d));
    }

    [Fact]
    public void EnterStage_rejects_a_stage_that_is_not_reachable_from_the_current_status()
    {
        var job = NewJob(JobStatus.Completed);

        var act = () => job.EnterStage(JobStage.Transcribing, new FixedTimeProvider(Now));

        act.Should().Throw<InvalidJobTransitionException>();
    }

    [Theory]
    [InlineData(-50d, 0d)]
    [InlineData(0d, 0d)]
    [InlineData(50d, 50d)]
    [InlineData(100d, 100d)]
    [InlineData(180d, 100d)]
    public void ReportProgress_clamps_the_stage_percentage(double reported, double expected)
    {
        var job = NewJob(JobStatus.Transcribing);

        job.ReportProgress(JobStage.Transcribing, reported, new FixedTimeProvider(Now));

        job.StageProgress.Should().Be(expected);
        job.OverallProgress.Should().Be(ProgressCalculator.Overall(JobStage.Transcribing, expected));
    }

    [Theory]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Paused)]
    public void ReportProgress_never_throws_and_never_overwrites_a_terminal_or_paused_status(JobStatus status)
    {
        // Progress arrives many times a second from a background thread and must never be able to
        // throw on — or resurrect — a job the user has just cancelled from the UI thread.
        var job = NewJob(status);

        var act = () => job.ReportProgress(JobStage.Transcribing, 40d, new FixedTimeProvider(Now));

        act.Should().NotThrow();
        job.Status.Should().Be(status);
    }

    [Theory]
    [InlineData(JobStage.Probing, JobStatus.Probing)]
    [InlineData(JobStage.ExtractingAudio, JobStatus.ExtractingAudio)]
    [InlineData(JobStage.Transcribing, JobStatus.Transcribing)]
    [InlineData(JobStage.Translating, JobStatus.Translating)]
    [InlineData(JobStage.WritingSubtitle, JobStatus.WritingSubtitle)]
    public void ReportProgress_moves_the_status_to_match_the_reported_stage(JobStage stage, JobStatus expected)
    {
        var job = NewJob(JobStatus.Pending);

        job.ReportProgress(stage, 10d, new FixedTimeProvider(Now));

        job.Status.Should().Be(expected);
        job.CurrentStage.Should().Be(stage);
    }

    [Fact]
    public void The_status_tracks_the_stage_through_a_whole_run()
    {
        // The bug this pins: the pump set Probing once and nothing moved it afterwards, so 상태 read
        // "검사 중" from the first second to the last while 현재 단계 changed underneath it.
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Pending);
        job.TransitionTo(JobStatus.Probing, clock);

        var seen = new List<JobStatus>();

        foreach (var stage in new[]
                 {
                     JobStage.ExtractingAudio, JobStage.Transcribing, JobStage.Translating, JobStage.WritingSubtitle
                 })
        {
            job.ReportProgress(stage, 0d, clock);
            job.ReportProgress(stage, 100d, clock);
            seen.Add(job.Status);
        }

        seen.Should().Equal(
            JobStatus.ExtractingAudio, JobStatus.Transcribing, JobStatus.Translating, JobStatus.WritingSubtitle);

        // ...and the run can now finish through the one door that leads to Completed.
        job.TransitionTo(JobStatus.Completed, clock);
        job.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void ReportProgress_accepts_a_resume_that_jumps_straight_to_translating()
    {
        // "체크포인트에서 이어서 진행합니다: translating" — the audio and the transcript are already
        // on disk, so the worker's first progress report is for a stage two steps ahead.
        var job = NewJob(JobStatus.Probing);

        job.ReportProgress(JobStage.Translating, 12d, new FixedTimeProvider(Now));

        job.Status.Should().Be(JobStatus.Translating);
        job.CurrentStage.Should().Be(JobStage.Translating);
    }

    [Fact]
    public void A_backward_progress_report_does_not_rewind_the_status()
    {
        var job = NewJob(JobStatus.Translating);

        job.ReportProgress(JobStage.Probing, 5d, new FixedTimeProvider(Now));

        job.Status.Should().Be(JobStatus.Translating, "a straggling report from an earlier stage is not a resume");
    }

    [Fact]
    public void A_done_report_does_not_complete_the_job()
    {
        // Completion belongs to the result path, which is the only place that knows whether a
        // subtitle file actually got written.
        var job = NewJob(JobStatus.Translating);

        job.ReportProgress(JobStage.Done, 100d, new FixedTimeProvider(Now));

        job.Status.Should().Be(JobStatus.Translating);
        job.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void A_none_report_does_not_send_the_job_back_to_the_queue()
    {
        var job = NewJob(JobStatus.Transcribing);

        job.ReportProgress(JobStage.None, 0d, new FixedTimeProvider(Now));

        job.Status.Should().Be(JobStatus.Transcribing);
    }

    [Fact]
    public void A_failed_job_can_be_retried_and_then_completed_again()
    {
        var clock = new FixedTimeProvider(Now);
        var job = NewJob(JobStatus.Transcribing);

        job.MarkFailed("FFMPEG_FAILED", "실패", clock);
        job.TransitionTo(JobStatus.Pending, clock);
        job.TransitionTo(JobStatus.Probing, clock);
        job.TransitionTo(JobStatus.Transcribing, clock);
        job.TransitionTo(JobStatus.Translating, clock);
        job.TransitionTo(JobStatus.WritingSubtitle, clock);
        job.TransitionTo(JobStatus.Completed, clock);

        job.Status.Should().Be(JobStatus.Completed);
        job.ErrorCode.Should().BeNull();
    }
}
