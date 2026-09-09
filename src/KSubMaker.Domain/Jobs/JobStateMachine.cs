namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Single source of truth for legal job status transitions.
/// Every status change in the application must go through <see cref="TryTransition"/> so that
/// invalid transitions surface as a domain error rather than silently corrupting the queue.
/// </summary>
public static class JobStateMachine
{
    /// <summary>
    /// The in-flight stages in the order the pipeline runs them. Position in this array is what
    /// "forward" means, and it is the only ordering the transition table knows about.
    /// </summary>
    private static readonly JobStatus[] ActiveOrder =
    [
        JobStatus.Probing,
        JobStatus.ExtractingAudio,
        JobStatus.Transcribing,
        JobStatus.Translating,
        JobStatus.WritingSubtitle
    ];

    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> Allowed = BuildTable();

    /// <summary>
    /// Builds the transition table from the rules rather than listing forty edges by hand.
    ///
    /// <para><b>Forward movement among the active stages is legal; backward movement is not.</b> A
    /// job resuming from a checkpoint jumps straight to the stage the checkpoint recorded — the
    /// worker announces <c>체크포인트에서 이어서 진행합니다: translating</c> and then reports
    /// translating progress without ever touching 음성 추출 — so a strictly linear table rejected
    /// perfectly ordinary runs. Going backwards is always a bug (a stale event, a confused worker),
    /// so <see cref="JobStatus.Translating"/> → <see cref="JobStatus.Probing"/> stays rejected.</para>
    ///
    /// <para><b><see cref="JobStatus.Completed"/> is only reachable from
    /// <see cref="JobStatus.WritingSubtitle"/>.</b> A job is finished when its subtitle has been
    /// written and at no other moment; the old table also allowed 검사 중 → 완료, which is how a
    /// success could skip 자막 저장 중 entirely and still look right.</para>
    ///
    /// <para><b><see cref="JobStatus.Pending"/> is reachable from every non-terminal status.</b>
    /// Putting a job back in the queue is always a legal request — the automatic single retry does
    /// exactly that from whatever stage failed. The terminal states keep their existing retry edges.</para>
    /// </summary>
    private static Dictionary<JobStatus, JobStatus[]> BuildTable()
    {
        var allowed = new Dictionary<JobStatus, JobStatus[]>();

        for (var i = 0; i < ActiveOrder.Length; i++)
        {
            var targets = new List<JobStatus>(ActiveOrder[(i + 1)..]);

            if (i == ActiveOrder.Length - 1)
            {
                targets.Add(JobStatus.Completed);
            }

            targets.Add(JobStatus.Pending);
            targets.Add(JobStatus.Failed);
            targets.Add(JobStatus.Cancelled);
            targets.Add(JobStatus.Paused);

            allowed[ActiveOrder[i]] = [.. targets];
        }

        // From the queue a job may enter whichever stage its checkpoint resumes at, which is also
        // what lets pass 2 of the two-pass strategy start a queued job directly at 번역 중. A job
        // that never left Pending goes to Skipped, not Cancelled, when the user takes it out of the
        // queue — there is no in-progress work to abandon.
        allowed[JobStatus.Pending] = [.. ActiveOrder, JobStatus.Skipped, JobStatus.Failed, JobStatus.Paused];

        // Terminal-ish states. Completed jobs can be forced back to Pending by an explicit
        // "reprocess" action; Failed/Cancelled/Skipped can be retried; Paused resumes.
        allowed[JobStatus.Completed] = [JobStatus.Pending];
        allowed[JobStatus.Failed] = [JobStatus.Pending, JobStatus.Cancelled];
        allowed[JobStatus.Cancelled] = [JobStatus.Pending];
        allowed[JobStatus.Skipped] = [JobStatus.Pending];
        allowed[JobStatus.Paused] = [JobStatus.Pending, .. ActiveOrder, JobStatus.Cancelled];

        return allowed;
    }

    /// <summary>Statuses from which no further work happens without an explicit user action.</summary>
    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;

    /// <summary>Statuses that mean "a worker is actively touching this job right now".</summary>
    public static bool IsActive(JobStatus status) =>
        status is JobStatus.Probing or JobStatus.ExtractingAudio or JobStatus.Transcribing
            or JobStatus.Translating or JobStatus.WritingSubtitle;

    public static bool CanTransition(JobStatus from, JobStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;
    }

    /// <summary>
    /// Attempts the transition. Returns false and leaves <paramref name="error"/> populated with a
    /// Korean, user-presentable message when the transition is not legal.
    /// </summary>
    public static bool TryTransition(JobStatus from, JobStatus to, out string? error)
    {
        if (CanTransition(from, to))
        {
            error = null;
            return true;
        }

        error = $"'{from}' 상태에서 '{to}' 상태로 전환할 수 없습니다.";
        return false;
    }

    /// <summary>Maps an in-flight stage onto the status that represents it.</summary>
    public static JobStatus StatusForStage(JobStage stage) => stage switch
    {
        JobStage.Probing => JobStatus.Probing,
        JobStage.ExtractingAudio => JobStatus.ExtractingAudio,
        JobStage.Transcribing => JobStatus.Transcribing,
        JobStage.Translating => JobStatus.Translating,
        JobStage.WritingSubtitle => JobStatus.WritingSubtitle,
        JobStage.Done => JobStatus.Completed,
        _ => JobStatus.Pending
    };
}
