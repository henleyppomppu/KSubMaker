using FluentAssertions;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The 진행 bar counts only the stages a run does. A report that jumps ahead marks the stages in
/// between as not done this run; a stage dropped early (Korean audio → no 번역) rescales at once.
/// </summary>
public sealed class JobStagePlanTests
{
    private static Job Running() => new()
    {
        VideoPath = "/videos/a.mkv",
        FileName = "a.mkv",
        Status = JobStatus.Probing,
        CurrentStage = JobStage.Probing
    };

    [Fact]
    public void A_report_that_jumps_ahead_drops_the_stages_it_passed()
    {
        var job = Running();
        job.ReportProgress(JobStage.ExtractingAudio, 100d);

        // From an embedded subtitle: straight from 음성 추출 to 번역.
        job.ReportProgress(JobStage.Translating, 0d);

        job.SkippedStages.Should().BeEquivalentTo([JobStage.Transcribing]);
        job.OverallProgress.Should().BeApproximately(10d / 45d * 100d, 0.01d);
    }

    [Fact]
    public void Dropping_translation_mid_transcription_moves_the_bar_forward_and_finishes_at_100()
    {
        var job = Running();
        job.ReportProgress(JobStage.Transcribing, 50d);
        var before = job.OverallProgress;

        job.SkipStage(JobStage.Translating);

        job.OverallProgress.Should().BeGreaterThan(before);
        job.ReportProgress(JobStage.WritingSubtitle, 100d);
        job.OverallProgress.Should().Be(100d);
    }

    [Fact]
    public void A_new_run_starts_with_every_stage_counted_again()
    {
        var job = Running();
        job.SkipStage(JobStage.Translating);

        job.ResetStagePlan();

        job.SkippedStages.Should().BeEmpty();
    }
}
