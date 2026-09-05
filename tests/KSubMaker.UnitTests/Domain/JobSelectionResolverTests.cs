using FluentAssertions;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The decision table behind 취소 / 재시도 / 선택 항목 제거 / 자막 원본 선택.
///
/// The bug this fixes: a user whose jobs had failed selected a row, pressed 취소, and was told
/// "먼저 목록에서 항목을 선택하세요". 실패 is terminal, so nothing was cancellable — but the empty
/// result was reported as an empty *selection*. Every test below that asserts
/// <see cref="SelectionOutcome.NoneEligible"/> is guarding that distinction.
/// </summary>
public sealed class JobSelectionResolverTests
{
    private static JobSelectionCandidate Row(string id, JobStatus status, bool isChecked = false) =>
        new(id, isChecked, status);

    // -----------------------------------------------------------------------
    // eligibility per action
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(JobStatus.Pending, true)]
    [InlineData(JobStatus.Paused, true)]
    [InlineData(JobStatus.Probing, true)]
    [InlineData(JobStatus.ExtractingAudio, true)]
    [InlineData(JobStatus.Transcribing, true)]
    [InlineData(JobStatus.Translating, true)]
    [InlineData(JobStatus.WritingSubtitle, true)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Skipped, false)]
    public void Cancel_accepts_everything_that_has_not_finished(JobStatus status, bool expected)
    {
        JobSelectionResolver.IsEligible(JobAction.Cancel, status).Should().Be(expected);
    }

    [Theory]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Skipped, true)]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Paused, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Transcribing, false)]
    [InlineData(JobStatus.Translating, false)]
    public void Retry_accepts_only_what_the_queue_will_requeue(JobStatus status, bool expected)
    {
        // Mirrors JobQueueService.RetryAsync; the two drifting apart is how a button ends up doing
        // nothing at all.
        JobSelectionResolver.IsEligible(JobAction.Retry, status).Should().Be(expected);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    [InlineData(JobStatus.Paused)]
    public void Remove_accepts_every_status(JobStatus status)
    {
        // A running job is cancelled and waited for by the queue rather than being refused here.
        JobSelectionResolver.IsEligible(JobAction.Remove, status).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Pending, true)]
    [InlineData(JobStatus.Paused, true)]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Skipped, true)]
    [InlineData(JobStatus.Probing, false)]
    [InlineData(JobStatus.Transcribing, false)]
    [InlineData(JobStatus.Translating, false)]
    [InlineData(JobStatus.WritingSubtitle, false)]
    public void The_subtitle_source_picker_refuses_a_job_the_worker_is_holding(JobStatus status, bool expected)
    {
        JobSelectionResolver.IsEligible(JobAction.ChooseSubtitleSource, status).Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // the three outcomes
    // -----------------------------------------------------------------------

    [Fact]
    public void Nothing_checked_and_nothing_highlighted_is_reported_as_no_selection()
    {
        var selection = JobSelectionResolver.Resolve(
            [Row("a", JobStatus.Pending), Row("b", JobStatus.Failed)],
            highlighted: null,
            JobAction.Cancel);

        selection.Outcome.Should().Be(SelectionOutcome.NothingSelected);
        selection.Ids.Should().BeEmpty();
        selection.IsOk.Should().BeFalse();
    }

    [Fact]
    public void The_reported_bug_a_highlighted_failed_job_and_cancel_is_none_eligible()
    {
        var failed = Row("a", JobStatus.Failed);

        var selection = JobSelectionResolver.Resolve([failed], failed, JobAction.Cancel);

        selection.Outcome.Should().Be(
            SelectionOutcome.NoneEligible,
            "the user did select a row; 취소 just cannot apply to a job that already failed");
    }

    [Fact]
    public void Checked_rows_that_all_fail_the_rule_are_none_eligible_too()
    {
        var selection = JobSelectionResolver.Resolve(
            [Row("a", JobStatus.Completed, isChecked: true), Row("b", JobStatus.Cancelled, isChecked: true)],
            highlighted: null,
            JobAction.Cancel);

        selection.Outcome.Should().Be(SelectionOutcome.NoneEligible);
    }

    [Fact]
    public void A_pending_job_is_cancellable_and_comes_back_as_ok()
    {
        var pending = Row("a", JobStatus.Pending);

        var selection = JobSelectionResolver.Resolve([pending], pending, JobAction.Cancel);

        selection.Outcome.Should().Be(SelectionOutcome.Ok);
        selection.Ids.Should().Equal("a");
        selection.Count.Should().Be(1);
    }

    [Fact]
    public void A_failed_job_is_retryable_even_though_it_is_not_cancellable()
    {
        var failed = Row("a", JobStatus.Failed);

        JobSelectionResolver.Resolve([failed], failed, JobAction.Retry).Ids.Should().Equal("a");
        JobSelectionResolver.Resolve([failed], failed, JobAction.Cancel).Outcome
            .Should().Be(SelectionOutcome.NoneEligible);
    }

    [Fact]
    public void A_failed_job_can_always_be_removed()
    {
        var failed = Row("a", JobStatus.Failed);

        var selection = JobSelectionResolver.Resolve([failed], failed, JobAction.Remove);

        selection.Outcome.Should().Be(SelectionOutcome.Ok);
        selection.Ids.Should().Equal("a");
    }

    [Fact]
    public void Removing_with_nothing_selected_is_still_a_no_selection()
    {
        JobSelectionResolver.Resolve([Row("a", JobStatus.Pending)], null, JobAction.Remove)
            .Outcome.Should().Be(SelectionOutcome.NothingSelected);
    }

    // -----------------------------------------------------------------------
    // precedence
    // -----------------------------------------------------------------------

    [Fact]
    public void Checked_rows_win_over_the_highlighted_row()
    {
        var highlighted = Row("c", JobStatus.Pending);

        var selection = JobSelectionResolver.Resolve(
            [
                Row("a", JobStatus.Pending, isChecked: true),
                Row("b", JobStatus.Pending, isChecked: true),
                highlighted
            ],
            highlighted,
            JobAction.Cancel);

        // A bulk 취소 must never silently collapse onto the one row that happens to be highlighted.
        selection.Ids.Should().Equal("a", "b");
    }

    [Fact]
    public void Ineligible_checked_rows_fall_back_to_the_highlighted_row()
    {
        var highlighted = Row("c", JobStatus.Pending);

        var selection = JobSelectionResolver.Resolve(
            [Row("a", JobStatus.Completed, isChecked: true), highlighted],
            highlighted,
            JobAction.Cancel);

        selection.Ids.Should().Equal("c");
    }

    [Fact]
    public void Only_the_checked_rows_the_action_accepts_are_returned()
    {
        var selection = JobSelectionResolver.Resolve(
            [
                Row("a", JobStatus.Pending, isChecked: true),
                Row("b", JobStatus.Completed, isChecked: true),
                Row("c", JobStatus.Transcribing, isChecked: true),
                Row("d", JobStatus.Pending)
            ],
            highlighted: null,
            JobAction.Cancel);

        selection.Ids.Should().Equal("a", "c");
    }

    [Fact]
    public void The_single_row_rule_prefers_the_highlighted_row_over_a_checkbox()
    {
        var highlighted = Row("c", JobStatus.Pending);

        var selection = JobSelectionResolver.ResolveSingle(
            [Row("a", JobStatus.Pending, isChecked: true), highlighted],
            highlighted,
            JobAction.ChooseSubtitleSource);

        // 자막 원본 선택 opens a modal about one file, so the row the user is looking at wins.
        selection.Ids.Should().Equal("c");
    }

    [Fact]
    public void The_single_row_rule_falls_back_to_the_first_checked_row()
    {
        var selection = JobSelectionResolver.ResolveSingle(
            [Row("a", JobStatus.Completed), Row("b", JobStatus.Pending, isChecked: true)],
            highlighted: null,
            JobAction.ChooseSubtitleSource);

        selection.Ids.Should().Equal("b");
    }

    [Fact]
    public void The_single_row_rule_separates_no_selection_from_a_running_job()
    {
        var running = Row("a", JobStatus.Translating);

        JobSelectionResolver.ResolveSingle([running], running, JobAction.ChooseSubtitleSource)
            .Outcome.Should().Be(SelectionOutcome.NoneEligible);

        JobSelectionResolver.ResolveSingle([running], null, JobAction.ChooseSubtitleSource)
            .Outcome.Should().Be(SelectionOutcome.NothingSelected);
    }

    [Fact]
    public void An_empty_grid_is_a_no_selection_for_every_action()
    {
        foreach (var action in Enum.GetValues<JobAction>())
        {
            JobSelectionResolver.Resolve([], null, action).Outcome
                .Should().Be(SelectionOutcome.NothingSelected, "{0}", action);

            JobSelectionResolver.ResolveSingle([], null, action).Outcome
                .Should().Be(SelectionOutcome.NothingSelected, "{0}", action);
        }
    }

    [Fact]
    public void A_null_row_sequence_is_rejected_rather_than_silently_treated_as_empty()
    {
        var act = () => JobSelectionResolver.Resolve(null!, null, JobAction.Cancel);

        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // 시작
    //
    // Reported from the desktop: 147 jobs cancelled, the app restarted, one row checked, 시작
    // pressed — and the answer was "시작할 수 있는 작업이 없습니다", the same lumped message that
    // JobSelectionResolver exists to replace. 시작 had never been routed through it.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(JobStatus.Pending, true)]
    [InlineData(JobStatus.Paused, true)]
    [InlineData(JobStatus.Probing, false)]
    [InlineData(JobStatus.Transcribing, false)]
    [InlineData(JobStatus.Translating, false)]
    [InlineData(JobStatus.WritingSubtitle, false)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Skipped, false)]
    public void Start_accepts_only_what_is_waiting(JobStatus status, bool expected)
    {
        // 취소 and 완료 are decisions the user already made; 시작 must not quietly undo them.
        JobSelectionResolver.IsEligible(JobAction.Start, status).Should().Be(expected);
    }

    [Fact]
    public void Start_with_nothing_checked_runs_every_waiting_job()
    {
        var selection = JobSelectionResolver.ResolveStart(
            [Row("a", JobStatus.Pending), Row("b", JobStatus.Cancelled), Row("c", JobStatus.Paused)]);

        selection.Outcome.Should().Be(SelectionOutcome.Ok);
        selection.Ids.Should().Equal("a", "c");
    }

    [Fact]
    public void Start_with_nothing_checked_and_no_waiting_job_is_not_reported_as_no_selection()
    {
        var selection = JobSelectionResolver.ResolveStart(
            [Row("a", JobStatus.Cancelled), Row("b", JobStatus.Completed)]);

        // NothingSelected here means "the queue holds nothing runnable". Telling the user to pick a
        // row would contradict a grid full of rows.
        selection.Outcome.Should().Be(SelectionOutcome.NothingSelected);
        selection.Ids.Should().BeEmpty();
    }

    [Fact]
    public void Start_honours_a_checked_selection()
    {
        var selection = JobSelectionResolver.ResolveStart(
        [
            Row("a", JobStatus.Pending),
            Row("b", JobStatus.Pending, isChecked: true),
            Row("c", JobStatus.Pending)
        ]);

        selection.Ids.Should().Equal("b");
    }

    [Fact]
    public void Start_on_a_checked_cancelled_row_says_it_is_ineligible_not_unselected()
    {
        var selection = JobSelectionResolver.ResolveStart(
            [Row("a", JobStatus.Pending), Row("b", JobStatus.Cancelled, isChecked: true)]);

        // The reported bug exactly: something *was* selected, so the message must explain the state
        // and point at 재시도 — not claim nothing was picked.
        selection.Outcome.Should().Be(SelectionOutcome.NoneEligible);
        selection.Ids.Should().BeEmpty();
    }

    [Fact]
    public void A_checked_selection_never_widens_to_the_rest_of_the_queue()
    {
        var selection = JobSelectionResolver.ResolveStart(
            [Row("a", JobStatus.Pending), Row("b", JobStatus.Cancelled, isChecked: true)]);

        // Falling back to "everything runnable" would start job "a", which the user did not pick.
        selection.Ids.Should().NotContain("a");
    }

    [Fact]
    public void Start_ignores_the_highlighted_row()
    {
        // Resolve() falls back to the highlighted row; ResolveStart deliberately has no such
        // fallback, so pressing 시작 with nothing checked cannot collapse onto one file. Running a
        // single file is the context menu's "이 파일만 실행".
        var selection = JobSelectionResolver.ResolveStart(
            [Row("a", JobStatus.Pending), Row("b", JobStatus.Pending)]);

        selection.Ids.Should().Equal("a", "b");
    }

    [Fact]
    public void Start_on_an_empty_grid_reports_nothing_runnable()
    {
        JobSelectionResolver.ResolveStart([]).Outcome.Should().Be(SelectionOutcome.NothingSelected);
    }

    [Fact]
    public void ResolveStart_rejects_a_null_row_sequence()
    {
        var act = () => JobSelectionResolver.ResolveStart(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
