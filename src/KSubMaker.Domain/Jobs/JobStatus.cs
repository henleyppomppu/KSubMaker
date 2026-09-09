namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Lifecycle state of a single video-to-Korean-subtitle job.
/// Persisted by name (not by ordinal) so that reordering never corrupts an existing database.
/// </summary>
public enum JobStatus
{
    Pending,
    Probing,
    ExtractingAudio,
    Transcribing,
    Translating,
    WritingSubtitle,
    Completed,
    Failed,

    /// <summary>
    /// Interrupted while it was actually running (or paused with progress already made) — the user
    /// stopped work that was under way. Distinct from <see cref="Skipped"/>, which never started.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Taken out of the queue while it was still <see cref="Pending"/> and had never run — the user
    /// decided not to bother with it, not that in-progress work was abandoned. Only reachable from
    /// <see cref="Pending"/>; see <see cref="JobStateMachine"/>.
    /// </summary>
    Skipped,

    Paused
}

/// <summary>
/// The processing stage a job is currently executing. Distinct from <see cref="JobStatus"/> because a
/// job can be <see cref="JobStatus.Paused"/> or <see cref="JobStatus.Failed"/> while still remembering
/// which stage it stopped in, which is what checkpoint resume keys off.
/// </summary>
public enum JobStage
{
    None,
    Probing,
    ExtractingAudio,
    Transcribing,
    Translating,
    WritingSubtitle,
    Done
}
