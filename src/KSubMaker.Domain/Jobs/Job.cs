using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Jobs;

/// <summary>
/// A per-file answer to "what should be translated for this one video?", overriding
/// <see cref="AppSettings.ExistingSubtitlePolicy"/>.
///
/// <see cref="None"/> is the default for every job and means the MVP core path
/// (영상 음성 → Whisper → 번역 → ko.srt). The other two exist because a container's subtitle
/// language metadata is routinely wrong or missing, so the only reliable chooser is the user.
/// </summary>
public enum JobSourceOverride
{
    /// <summary>No override. The application-wide policy decides.</summary>
    None,

    /// <summary>Transcribe the audio, optionally from a specific track.</summary>
    Audio,

    /// <summary>Extract an embedded subtitle track and translate that instead of running ASR.</summary>
    EmbeddedSubtitle,

    /// <summary>Translate a sidecar subtitle file instead of running ASR.</summary>
    ExternalSubtitle
}

/// <summary>
/// A unit of work: one source video that must end up with a Korean SRT next to it.
/// This is both the persisted entity and the in-memory domain object; the UI projects it
/// onto a view model rather than mutating it directly.
/// </summary>
public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    // ---- source identity -------------------------------------------------
    public string VideoPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }

    /// <summary>Media duration in seconds; 0 until FFprobe has run.</summary>
    public double DurationSeconds { get; set; }

    public bool HasAudioTrack { get; set; } = true;
    public bool HasEmbeddedSubtitle { get; set; }
    public bool HasExternalSubtitle { get; set; }
    public bool HasKoreanSubtitle { get; set; }

    // ---- per-file source override ----------------------------------------
    // Persisted so a choice made before a restart is still honoured afterwards, and so the grid can
    // show it without re-probing every file.

    /// <summary><see cref="JobSourceOverride.None"/> keeps the MVP core path.</summary>
    public JobSourceOverride SourceOverride { get; set; } = JobSourceOverride.None;

    /// <summary>Null means "let FFmpeg pick the default audio stream".</summary>
    public int? SelectedAudioTrackIndex { get; set; }

    /// <summary>Stream index of the chosen embedded subtitle track.</summary>
    public int? SelectedSubtitleTrackIndex { get; set; }

    /// <summary>
    /// Language of the chosen subtitle track, as the user confirmed it. Kept separately from the
    /// container's own tag because that tag is exactly what cannot be trusted here — a track labelled
    /// <c>und</c> (or mislabelled <c>eng</c>) is the reason this override exists at all.
    /// </summary>
    public string? SelectedSubtitleLanguage { get; set; }

    /// <summary>True when this file's source was chosen by hand rather than by the global policy.</summary>
    public bool HasSourceOverride => SourceOverride != JobSourceOverride.None;

    /// <summary>Clears the per-file choice so the job falls back to the application-wide policy.</summary>
    public void ClearSourceOverride()
    {
        SourceOverride = JobSourceOverride.None;
        SelectedAudioTrackIndex = null;
        SelectedSubtitleTrackIndex = null;
        SelectedSubtitleLanguage = null;
    }

    // ---- progress --------------------------------------------------------
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public JobStage CurrentStage { get; set; } = JobStage.None;
    public double OverallProgress { get; set; }
    public double StageProgress { get; set; }

    /// <summary>Media seconds processed per wall-clock second, as reported by the worker.</summary>
    public double ProcessingSpeed { get; set; }

    public TimeSpan? EstimatedTimeRemaining { get; set; }

    // ---- results ---------------------------------------------------------
    public string? DetectedLanguage { get; set; }
    public double? LanguageProbability { get; set; }
    public string? WhisperModel { get; set; }
    public TranslationEngineKind? TranslationEngine { get; set; }
    public string? TranslationModel { get; set; }
    public string? OutputPath { get; set; }

    // ---- failure ---------------------------------------------------------
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Free-text note the user attached from the grid. Never touched by the pipeline.</summary>
    public string? Note { get; set; }

    // ---- bookkeeping -----------------------------------------------------
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Ordering hint so the queue is stable across restarts.</summary>
    public int QueueOrder { get; set; }

    /// <summary>
    /// Applies a status change through <see cref="JobStateMachine"/>.
    /// Throws <see cref="InvalidJobTransitionException"/> when the transition is not legal, because a
    /// silently-ignored transition would leave the queue and the database disagreeing.
    /// </summary>
    public void TransitionTo(JobStatus next, TimeProvider? timeProvider = null)
    {
        if (!JobStateMachine.TryTransition(Status, next, out var error))
        {
            throw new InvalidJobTransitionException(error!);
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        Status = next;
        UpdatedAtUtc = now;

        switch (next)
        {
            case JobStatus.Completed:
                CompletedAtUtc = now;
                CurrentStage = JobStage.Done;
                OverallProgress = 100d;
                StageProgress = 100d;
                ErrorCode = null;
                ErrorMessage = null;
                break;

            case JobStatus.Pending:
                CurrentStage = JobStage.None;
                OverallProgress = 0d;
                StageProgress = 0d;
                CompletedAtUtc = null;
                EstimatedTimeRemaining = null;
                ProcessingSpeed = 0d;

                // Requeueing means the previous outcome no longer applies. Leaving the error text in
                // place shows a row reading "대기 중" alongside the failure message it was retried for.
                ErrorCode = null;
                ErrorMessage = null;
                break;

            case JobStatus.Cancelled:
            case JobStatus.Skipped:
            case JobStatus.Paused:
                EstimatedTimeRemaining = null;
                ProcessingSpeed = 0d;
                break;
        }

        if (JobStateMachine.IsActive(next))
        {
            ErrorCode = null;
            ErrorMessage = null;
        }
    }

    public void MarkFailed(string errorCode, string message, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        // Failure is always reachable; force it even from odd states so an error is never lost.
        Status = JobStatus.Failed;
        ErrorCode = errorCode;
        ErrorMessage = message;
        UpdatedAtUtc = now;
        EstimatedTimeRemaining = null;
        ProcessingSpeed = 0d;
    }

    public void EnterStage(JobStage stage, TimeProvider? timeProvider = null)
    {
        TransitionTo(JobStateMachine.StatusForStage(stage), timeProvider);
        CurrentStage = stage;
        StageProgress = 0d;
        OverallProgress = ProgressCalculator.Overall(stage, 0d);
    }

    /// <summary>
    /// Records a progress report from a running processor, and moves <see cref="Status"/> to keep up
    /// with the reported stage.
    ///
    /// <para>The status half is not cosmetic. Without it the status a job is given when the pump
    /// starts it is the status it keeps for the entire run: the grid's 상태 column reads "검사 중"
    /// from the first second to the last while 현재 단계 moves underneath it, and — far worse —
    /// every later transition is computed from a state that stopped being true minutes ago.</para>
    ///
    /// <para>It is deliberately gentler than <see cref="TransitionTo"/>, because progress arrives
    /// many times a second from a background thread and must never throw or overwrite a decision
    /// made elsewhere:</para>
    /// <list type="bullet">
    /// <item>only an <see cref="JobStateMachine.IsActive"/> status is ever written, so a
    /// <see cref="JobStage.Done"/> report cannot complete a job behind the result path's back;</item>
    /// <item>a terminal or paused job keeps its status, because a report already in flight must not
    /// undo the 취소 the user just pressed;</item>
    /// <item>a backward report — a straggler from a stage the job has already left — updates the
    /// displayed stage but leaves the status where it is.</item>
    /// </list>
    /// </summary>
    public void ReportProgress(JobStage stage, double stageProgress, TimeProvider? timeProvider = null)
    {
        CurrentStage = stage;
        StageProgress = Math.Clamp(stageProgress, 0d, 100d);
        OverallProgress = ProgressCalculator.Overall(stage, StageProgress);
        UpdatedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        var reported = JobStateMachine.StatusForStage(stage);

        if (JobStateMachine.IsActive(reported)
            && !JobStateMachine.IsTerminal(Status)
            && Status != JobStatus.Paused
            && JobStateMachine.CanTransition(Status, reported))
        {
            Status = reported;
        }
    }
}

public sealed class InvalidJobTransitionException(string message) : InvalidOperationException(message);
