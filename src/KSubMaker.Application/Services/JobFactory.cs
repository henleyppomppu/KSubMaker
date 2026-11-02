using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.Application.Services;

/// <summary>Why a scanned file did or did not become a queued job.</summary>
public enum EnqueueDecision
{
    /// <summary>New job created.</summary>
    Created,

    /// <summary>Existing job reset back to Pending so it runs again.</summary>
    Requeued,

    /// <summary>Existing job left as it is.</summary>
    Unchanged,

    /// <summary>Immediately marked Completed because a Korean subtitle already exists.</summary>
    AlreadyDone,

    /// <summary>Filtered out by the current options.</summary>
    Skipped
}

public sealed record EnqueueResult(EnqueueDecision Decision, Job? Job, string? Reason);

/// <summary>
/// Turns a scan result into queue entries, applying the "이미 한국어 SRT가 있는 파일 건너뛰기",
/// "완료된 파일 다시 처리", "실패한 파일만 다시 처리" and existing-subtitle options.
///
/// Pure and synchronous so the decision table is directly unit testable.
/// </summary>
public static class JobFactory
{
    public static EnqueueResult Create(
        VideoFile file,
        Job? existing,
        AppSettings settings,
        Func<string, bool>? fileExists = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);

        fileExists ??= File.Exists;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        var outputPath = OutputPathResolver.BuildDefaultPath(
            file.FullPath, settings.OutputSuffix, settings.OutputDirectory);

        // "이미 한국어 자막이 있음" now means either the sidecar next to the source or the file at the
        // configured output location — otherwise pointing OutputDirectory somewhere new would
        // reprocess a whole library that is already done.
        var koreanExists = file.HasKoreanExternalSubtitle || fileExists(outputPath);

        // ---- filters that apply before anything is created -------------------
        if (settings.RetryFailedOnly && existing is not null && existing.Status != JobStatus.Failed)
        {
            return new EnqueueResult(EnqueueDecision.Skipped, existing, "실패한 작업만 다시 처리하도록 설정되어 있습니다.");
        }

        if (settings.ExistingSubtitleRule == ExistingSubtitleRule.SkipIfAnySubtitleExists &&
            file.HasExternalSubtitle && !settings.ReprocessCompleted)
        {
            return existing is null
                ? new EnqueueResult(EnqueueDecision.Skipped, null, "동일 이름의 외부 자막이 있어 건너뜁니다.")
                : new EnqueueResult(EnqueueDecision.Unchanged, existing, "동일 이름의 외부 자막이 있어 건너뜁니다.");
        }

        if (settings.ExistingSubtitleRule == ExistingSubtitleRule.CompleteIfKoreanExists &&
            koreanExists && !settings.ReprocessCompleted)
        {
            if (existing is not null)
            {
                // A job that is finished, or that a worker is touching right now, is left as it is.
                // Everything else — Pending, Failed, Cancelled, Skipped — is still going to run (or be
                // retried) even though the subtitle it would produce is already on disk. That is
                // exactly the case this rule exists for, and it is what a user sees after fixing the
                // output suffix or copying finished subtitles in: a rescan must flip those rows to
                // 완료, not leave them queued because they happened to be queued before.
                if (existing.Status == JobStatus.Completed || JobStateMachine.IsActive(existing.Status)
                    || existing.Status == JobStatus.Paused)
                {
                    return new EnqueueResult(EnqueueDecision.Unchanged, existing, "이미 한국어 자막이 있습니다.");
                }

                existing.HasExternalSubtitle = file.HasExternalSubtitle;
                existing.HasKoreanSubtitle = true;
                MarkAlreadyDone(existing, outputPath, now);
                return new EnqueueResult(EnqueueDecision.AlreadyDone, existing, "이미 한국어 자막이 있어 완료로 표시했습니다.");
            }

            var done = Build(file, settings, outputPath, now);
            done.HasKoreanSubtitle = true;
            MarkAlreadyDone(done, outputPath, now);
            return new EnqueueResult(EnqueueDecision.AlreadyDone, done, "이미 한국어 자막이 있어 완료로 표시했습니다.");
        }

        // ---- existing job -----------------------------------------------------
        if (existing is not null)
        {
            var sourceChanged = existing.FileSize != file.SizeBytes ||
                                existing.LastWriteTimeUtc != file.LastWriteTimeUtc;

            var shouldRequeue = sourceChanged
                                || settings.ReprocessCompleted
                                || existing.Status is JobStatus.Failed or JobStatus.Cancelled
                                || (settings.RetryFailedOnly && existing.Status == JobStatus.Failed);

            if (!shouldRequeue)
            {
                return new EnqueueResult(EnqueueDecision.Unchanged, existing,
                    existing.Status == JobStatus.Completed ? "이미 완료된 작업입니다." : null);
            }

            existing.FileSize = file.SizeBytes;
            existing.LastWriteTimeUtc = file.LastWriteTimeUtc;
            existing.DurationSeconds = file.DurationSeconds > 0 ? file.DurationSeconds : existing.DurationSeconds;
            existing.HasAudioTrack = file.Probed ? file.HasAudioTrack : existing.HasAudioTrack;
            existing.HasEmbeddedSubtitle = file.HasEmbeddedSubtitle;
            existing.HasExternalSubtitle = file.HasExternalSubtitle;
            existing.HasKoreanSubtitle = koreanExists;
            existing.OutputPath = outputPath;
            existing.ErrorCode = null;
            existing.ErrorMessage = null;
            existing.Status = JobStatus.Pending;
            existing.CurrentStage = JobStage.None;
            existing.OverallProgress = 0d;
            existing.StageProgress = 0d;
            existing.CompletedAtUtc = null;
            existing.UpdatedAtUtc = now;

            if (sourceChanged)
            {
                // The file was re-encoded, so the stream indices the user picked may now point at a
                // different track, or at none. Falling back to the core path is safer than
                // translating "subtitle stream 3" of a container that no longer has one.
                existing.ClearSourceOverride();
            }

            return new EnqueueResult(EnqueueDecision.Requeued, existing,
                sourceChanged ? "원본 파일이 변경되어 다시 처리합니다." : null);
        }

        return new EnqueueResult(EnqueueDecision.Created, Build(file, settings, outputPath, now), null);
    }

    /// <summary>
    /// Puts a job into the same shape the pipeline leaves it in after writing the subtitle, without
    /// going through <see cref="Job.TransitionTo"/> — the state machine only admits Completed from
    /// WritingSubtitle, and nothing was written here.
    /// </summary>
    private static void MarkAlreadyDone(Job job, string outputPath, DateTime now)
    {
        job.Status = JobStatus.Completed;
        job.CurrentStage = JobStage.Done;
        job.OverallProgress = 100d;
        job.StageProgress = 100d;
        job.ErrorCode = null;
        job.ErrorMessage = null;
        job.EstimatedTimeRemaining = null;
        job.ProcessingSpeed = 0d;
        job.OutputPath = outputPath;
        job.CompletedAtUtc = now;
        job.UpdatedAtUtc = now;
    }

    private static Job Build(VideoFile file, AppSettings settings, string outputPath, DateTime now) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        VideoPath = file.FullPath,
        FileName = file.FileName,
        FileSize = file.SizeBytes,
        LastWriteTimeUtc = file.LastWriteTimeUtc,
        DurationSeconds = file.DurationSeconds,
        HasAudioTrack = file.Probed ? file.HasAudioTrack : true,
        HasEmbeddedSubtitle = file.HasEmbeddedSubtitle,
        HasExternalSubtitle = file.HasExternalSubtitle,
        HasKoreanSubtitle = file.HasKoreanExternalSubtitle,
        Status = JobStatus.Pending,
        CurrentStage = JobStage.None,
        OutputPath = outputPath,
        TranslationEngine = settings.TranslationEngine,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };
}
