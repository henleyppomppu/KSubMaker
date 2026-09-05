namespace KSubMaker.Domain.Jobs;

/// <summary>
/// A user action the job grid can start on a selection. Each one accepts a different set of
/// statuses, and a refusal has to be able to say <i>which</i> action refused, so the reason can be
/// spelled out instead of being reduced to "아무것도 선택하지 않았습니다".
/// </summary>
public enum JobAction
{
    /// <summary>시작 — run the jobs that are waiting.</summary>
    Start,

    /// <summary>취소 — stop a job that has not finished yet.</summary>
    Cancel,

    /// <summary>재시도 — put a finished job back into the queue.</summary>
    Retry,

    /// <summary>선택 항목 제거 — drop the row, its database record and its cache.</summary>
    Remove,

    /// <summary>자막 원본 선택 — re-point one job at another audio or subtitle track.</summary>
    ChooseSubtitleSource
}

/// <summary>
/// Why a selection-driven command did or did not find something to act on.
///
/// The distinction between the last two is the whole point of this type: they used to collapse into
/// a single "먼저 목록에서 항목을 선택하세요" alert, so pressing 취소 with a *failed* job selected told
/// the user they had selected nothing.
/// </summary>
public enum SelectionOutcome
{
    /// <summary>At least one job is eligible; <see cref="JobSelection.Ids"/> holds them.</summary>
    Ok,

    /// <summary>No row is checked and none is highlighted. There is genuinely nothing to act on.</summary>
    NothingSelected,

    /// <summary>Rows are selected, but none of them is in a state this action accepts.</summary>
    NoneEligible
}

/// <summary>One grid row as the resolver sees it: identity, checkbox state, current status.</summary>
/// <param name="Id">Job id.</param>
/// <param name="IsChecked">State of the 선택 column checkbox.</param>
/// <param name="Status">Current job status, which decides eligibility.</param>
public readonly record struct JobSelectionCandidate(string Id, bool IsChecked, JobStatus Status);

/// <summary>What a selection-driven command should act on, and why it should not when it should not.</summary>
/// <param name="Ids">Jobs to act on. Empty unless <paramref name="Outcome"/> is <see cref="SelectionOutcome.Ok"/>.</param>
/// <param name="Outcome">The verdict the caller turns into a message.</param>
public sealed record JobSelection(IReadOnlyList<string> Ids, SelectionOutcome Outcome)
{
    /// <summary>Nothing checked, nothing highlighted.</summary>
    public static JobSelection Nothing { get; } = new([], SelectionOutcome.NothingSelected);

    /// <summary>Something is selected, but this action cannot be applied to any of it.</summary>
    public static JobSelection Ineligible { get; } = new([], SelectionOutcome.NoneEligible);

    public bool IsOk => Outcome == SelectionOutcome.Ok;

    public int Count => Ids.Count;
}

/// <summary>
/// Decides what a selection-driven grid command acts on.
///
/// <para>Why it exists: the view model used to answer "which rows does 취소 apply to?" with a single
/// filtered list, and an empty list meant both "nothing selected" and "nothing selectable". A user
/// whose jobs had all failed (<c>WHISPER_MODEL_NOT_FOUND</c>) selected a row, pressed 취소 and was
/// told to select something first — a message that contradicted what was on screen.</para>
///
/// <para>Pure and side-effect free, and deliberately in Domain: <c>KSubMaker.App</c> is
/// <c>net10.0-windows</c> and cannot be referenced from the Linux test suite at all, so the decision
/// table lives here where it is tested and only the wording lives up there
/// (same reasoning as <c>ModelSelectionValidator</c>).</para>
/// </summary>
public static class JobSelectionResolver
{
    /// <summary>Can <paramref name="action"/> be applied to a job in <paramref name="status"/>?</summary>
    public static bool IsEligible(JobAction action, JobStatus status) => action switch
    {
        // Mirrors JobQueueService.IsRunnable. Terminal statuses are excluded on purpose: 취소 and
        // 완료 are decisions the user already made, and 시작 must not quietly undo them. Putting one
        // back in the queue is what 재시도 is for.
        JobAction.Start => status is JobStatus.Pending or JobStatus.Paused,

        // Mirrors JobQueueService.CancelAsync: a job that already reached a terminal state has
        // nothing left to stop.
        JobAction.Cancel => !JobStateMachine.IsTerminal(status),

        // Mirrors JobQueueService.RetryAsync.
        JobAction.Retry => status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped
            or JobStatus.Completed or JobStatus.Paused,

        // Anything can be removed: JobQueueService.RemoveAsync cancels a running job and waits for
        // the pump to let go of it before the row disappears.
        JobAction.Remove => true,

        // Mirrors JobQueueService.SetSourceOverrideAsync: the worker already holds a process command
        // built from the old value, so changing it mid-run would make the grid lie.
        JobAction.ChooseSubtitleSource => !JobStateMachine.IsActive(status),

        _ => false
    };

    /// <summary>
    /// The bulk rule, used by 취소 / 재시도 / 선택 항목 제거: every checked row this action accepts,
    /// falling back to the highlighted row.
    ///
    /// Checkbox selection wins so a bulk action never silently collapses onto the one row that
    /// happens to be highlighted. The fallback still applies when the checked rows yield nothing —
    /// that is the long-standing behaviour and is what makes right-click → act on one row work.
    /// </summary>
    /// <param name="rows">Every row in the grid, in display order.</param>
    /// <param name="highlighted">The highlighted row, or null when the grid has no current item.</param>
    /// <param name="action">The action about to run.</param>
    public static JobSelection Resolve(
        IEnumerable<JobSelectionCandidate> rows,
        JobSelectionCandidate? highlighted,
        JobAction action)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var ids = new List<string>();
        var anySelected = highlighted is not null;

        foreach (var row in rows)
        {
            if (!row.IsChecked)
            {
                continue;
            }

            anySelected = true;

            if (IsEligible(action, row.Status))
            {
                ids.Add(row.Id);
            }
        }

        if (ids.Count > 0)
        {
            return new JobSelection(ids, SelectionOutcome.Ok);
        }

        if (highlighted is { } row2 && IsEligible(action, row2.Status))
        {
            return new JobSelection([row2.Id], SelectionOutcome.Ok);
        }

        return anySelected ? JobSelection.Ineligible : JobSelection.Nothing;
    }

    /// <summary>
    /// The rule for 시작, which is deliberately not <see cref="Resolve"/>.
    ///
    /// <para>There is no fallback to the highlighted row: pressing 시작 with nothing checked means
    /// "run the queue", and collapsing that onto whichever row happens to be highlighted would
    /// silently run one file out of a hundred. Running a single file is the context menu's
    /// "이 파일만 실행", which is explicit. An empty result with nothing checked is not "you selected
    /// nothing" — it is "the queue holds nothing runnable".</para>
    /// </summary>
    /// <param name="rows">Every row in the grid, in display order.</param>
    public static JobSelection ResolveStart(IEnumerable<JobSelectionCandidate> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var eligible = new List<string>();
        var checkedEligible = new List<string>();
        var anyChecked = false;

        foreach (var row in rows)
        {
            var runnable = IsEligible(JobAction.Start, row.Status);

            if (runnable)
            {
                eligible.Add(row.Id);
            }

            if (!row.IsChecked)
            {
                continue;
            }

            anyChecked = true;

            if (runnable)
            {
                checkedEligible.Add(row.Id);
            }
        }

        if (anyChecked)
        {
            // A checked selection is a restriction, so it is honoured even when it turns out to be
            // empty — falling back to the whole queue would start files the user did not pick.
            return checkedEligible.Count > 0
                ? new JobSelection(checkedEligible, SelectionOutcome.Ok)
                : JobSelection.Ineligible;
        }

        return eligible.Count > 0
            ? new JobSelection(eligible, SelectionOutcome.Ok)
            : JobSelection.Nothing;
    }

    /// <summary>
    /// The single-row rule, used by 자막 원본 선택: the highlighted row, else the first checked one.
    ///
    /// The precedence is the mirror image of <see cref="Resolve"/> on purpose. This action opens a
    /// modal dialog about one specific file, so the row the user is looking at must win over a
    /// checkbox they ticked earlier for a bulk operation.
    /// </summary>
    public static JobSelection ResolveSingle(
        IEnumerable<JobSelectionCandidate> rows,
        JobSelectionCandidate? highlighted,
        JobAction action)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var primary = highlighted;

        if (primary is null)
        {
            foreach (var row in rows)
            {
                if (row.IsChecked)
                {
                    primary = row;
                    break;
                }
            }
        }

        if (primary is not { } candidate)
        {
            return JobSelection.Nothing;
        }

        return IsEligible(action, candidate.Status)
            ? new JobSelection([candidate.Id], SelectionOutcome.Ok)
            : JobSelection.Ineligible;
    }
}
