using System.Globalization;
using KSubMaker.App.Resources;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;

namespace KSubMaker.App.Services;

/// <summary>
/// The one place that turns a domain enum or a raw number into the Korean text shown on screen.
///
/// Both the value converters (used from XAML) and the view models (used from C#) call in here, so a
/// status can never be spelled two different ways in two different columns.
///
/// Method names carry a <c>Name</c> suffix so that a method never shadows the enum type it formats.
/// </summary>
public static class DisplayText
{
    public static string StatusName(JobStatus status) => status switch
    {
        JobStatus.Pending => Strings.JobStatusPending,
        JobStatus.Probing => Strings.JobStatusProbing,
        JobStatus.ExtractingAudio => Strings.JobStatusExtractingAudio,
        JobStatus.Transcribing => Strings.JobStatusTranscribing,
        JobStatus.Translating => Strings.JobStatusTranslating,
        JobStatus.WritingSubtitle => Strings.JobStatusWritingSubtitle,
        JobStatus.Completed => Strings.JobStatusCompleted,
        JobStatus.Failed => Strings.JobStatusFailed,
        JobStatus.Cancelled => Strings.JobStatusCancelled,
        JobStatus.Skipped => Strings.JobStatusSkipped,
        JobStatus.Paused => Strings.JobStatusPaused,
        _ => Strings.Unknown
    };

    public static string StageName(JobStage stage) => stage switch
    {
        JobStage.Probing => Strings.JobStageProbing,
        JobStage.ExtractingAudio => Strings.JobStageExtractingAudio,
        JobStage.Transcribing => Strings.JobStageTranscribing,
        JobStage.Translating => Strings.JobStageTranslating,
        JobStage.WritingSubtitle => Strings.JobStageWritingSubtitle,
        JobStage.Done => Strings.JobStageDone,
        _ => Strings.JobStageNone
    };

    public static string QueueStateName(QueueState state) => state switch
    {
        QueueState.Running => Strings.QueueStateRunning,
        QueueState.Pausing => Strings.QueueStatePausing,
        QueueState.Paused => Strings.QueueStatePaused,
        QueueState.Stopping => Strings.QueueStateStopping,
        _ => Strings.QueueStateIdle
    };

    public static string ModelKindName(ModelKind kind) => kind switch
    {
        ModelKind.Whisper => Strings.ModelKindWhisper,
        ModelKind.Translation => Strings.ModelKindTranslation,
        ModelKind.Llm => Strings.ModelKindLlm,
        _ => Strings.Unknown
    };

    /// <summary>One-line, honestly-hedged blurb shown under each category header in 모델 관리.</summary>
    public static string ModelKindHint(ModelKind kind) => kind switch
    {
        ModelKind.Whisper => Strings.ModelKindWhisperHint,
        ModelKind.Translation => Strings.ModelKindTranslationHint,
        ModelKind.Llm => Strings.ModelKindLlmHint,
        _ => string.Empty
    };

    public static string TranslationStyleName(TranslationStyle style) => style switch
    {
        TranslationStyle.Literal => Strings.TranslationStyleLiteral,
        TranslationStyle.Polite => Strings.TranslationStylePolite,
        TranslationStyle.Casual => Strings.TranslationStyleCasual,
        TranslationStyle.PreserveSourceRegister => Strings.TranslationStylePreserve,
        _ => Strings.TranslationStyleNatural
    };

    public static string TranslationEngineName(TranslationEngineKind engine) => engine switch
    {
        TranslationEngineKind.LocalLlm => Strings.TranslationEngineLlm,
        TranslationEngineKind.Fake => Strings.TranslationEngineFake,
        _ => Strings.TranslationEngineDedicated
    };

    public static string SubtitleSourceName(SubtitleSourcePreference source) => source switch
    {
        SubtitleSourcePreference.PreferExternalFile => Strings.SubtitleSourceExternalFile,
        SubtitleSourcePreference.PreferEmbeddedTrack => Strings.SubtitleSourceEmbeddedTrack,
        SubtitleSourcePreference.PreferAnySubtitle => Strings.SubtitleSourceAnySubtitle,
        SubtitleSourcePreference.AskPerFile => Strings.SubtitleSourceAsk,
        _ => Strings.SubtitleSourceAudioOnly
    };

    public static string ExistingSubtitleRuleName(ExistingSubtitleRule rule) => rule switch
    {
        ExistingSubtitleRule.SkipIfAnySubtitleExists => Strings.ExistingSubtitleSkipExternal,
        ExistingSubtitleRule.ProcessAnyway => Strings.ExistingSubtitleProcessAnyway,
        _ => Strings.ExistingSubtitleCompleteKorean
    };

    public static string OutputConflictPolicyName(OutputConflictPolicy policy) => policy switch
    {
        OutputConflictPolicy.Overwrite => Strings.OutputConflictOverwrite,
        OutputConflictPolicy.CreateNumberedCopy => Strings.OutputConflictNumbered,
        _ => Strings.OutputConflictSkip
    };

    public static string ProcessingStrategyName(ProcessingStrategy strategy) => strategy switch
    {
        ProcessingStrategy.SequentialPerFile => Strings.StrategyA,
        ProcessingStrategy.TranscribeAllThenTranslate => Strings.StrategyB,
        ProcessingStrategy.PipelinedParallel => Strings.StrategyC,
        _ => Strings.StrategyAuto
    };

    public static string PostQueueActionName(PostQueueAction action) => action switch
    {
        PostQueueAction.Sleep => Strings.PostQueueActionSleep,
        PostQueueAction.Hibernate => Strings.PostQueueActionHibernate,
        PostQueueAction.Shutdown => Strings.PostQueueActionShutdown,
        _ => Strings.PostQueueActionNone
    };

    public static string LogLevelName(string? level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "verbose" or "trace" => Strings.LogLevelTrace,
        "debug" => Strings.LogLevelDebug,
        "warning" or "warn" => Strings.LogLevelWarning,
        "error" => Strings.LogLevelError,
        "fatal" or "critical" => Strings.LogLevelFatal,
        _ => Strings.LogLevelInformation
    };

    /// <summary>ISO-639-1 code to the Korean language name shown in the settings list.</summary>
    public static string LanguageName(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();

        return normalized switch
        {
            "" or "auto" => Strings.LanguageAuto,
            "en" => Strings.LanguageEn,
            "ja" => Strings.LanguageJa,
            "zh" => Strings.LanguageZh,
            "ko" => Strings.LanguageKo,
            "es" => Strings.LanguageEs,
            "fr" => Strings.LanguageFr,
            "de" => Strings.LanguageDe,
            "ru" => Strings.LanguageRu,
            "pt" => Strings.LanguagePt,
            "it" => Strings.LanguageIt,
            "vi" => Strings.LanguageVi,
            "th" => Strings.LanguageTh,
            "id" => Strings.LanguageId,
            "hi" => Strings.LanguageHi,
            "ar" => Strings.LanguageAr,
            "tr" => Strings.LanguageTr,
            "nl" => Strings.LanguageNl,
            "pl" => Strings.LanguagePl,
            "sv" => Strings.LanguageSv,
            _ => normalized
        };
    }

    /// <summary>Language codes offered in the 입력 언어 list, in the order they are shown.</summary>
    public static IReadOnlyList<string> SupportedLanguageCodes { get; } =
    [
        "en", "ja", "zh", "ko", "es", "fr", "de", "ru", "pt", "it",
        "vi", "th", "id", "hi", "ar", "tr", "nl", "pl", "sv"
    ];

    /// <summary>Log level identifiers offered in the settings list. Stored verbatim in AppSettings.</summary>
    public static IReadOnlyList<string> LogLevels { get; } =
    [
        "Trace", "Debug", "Information", "Warning", "Error", "Fatal"
    ];

    /// <summary>1024-based size with one decimal, e.g. <c>3 GB</c>.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary><c>h:mm:ss</c>, or <c>m:ss</c> below an hour. Zero, negative and NaN render as "-".</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0d)
        {
            return Strings.Dash;
        }

        // Guards against a corrupt duration overflowing TimeSpan and taking a grid row down with it.
        var capped = Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1d);
        return Duration(TimeSpan.FromSeconds(capped));
    }

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } value || value < TimeSpan.Zero)
        {
            return Strings.Dash;
        }

        var hours = (long)value.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{hours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.CurrentCulture, $"{value.Minutes}:{value.Seconds:00}");
    }

    /// <summary>Media seconds processed per wall-clock second, e.g. <c>12.4x</c>.</summary>
    public static string Speed(double mediaSecondsPerSecond)
    {
        if (mediaSecondsPerSecond <= 0d ||
            double.IsNaN(mediaSecondsPerSecond) ||
            double.IsInfinity(mediaSecondsPerSecond))
        {
            return Strings.Dash;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{mediaSecondsPerSecond:0.0}x");
    }

    public static string Percent(double value) =>
        string.Create(CultureInfo.CurrentCulture, $"{Math.Clamp(value, 0d, 100d):0.0}%");

    public static string GigabytesOrDash(double gigabytes) =>
        gigabytes <= 0d ? Strings.Dash : string.Create(CultureInfo.CurrentCulture, $"{gigabytes:0.##} GB");

    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? Strings.Dash : value;

    /// <summary>
    /// Short label for the 자막 원본 grid column. Kept terse because it shares a row with nine other
    /// columns; the full track description lives in the picker dialog.
    /// </summary>
    public static string SubtitleSourceName(JobSourceOverride mode, int? audioTrackIndex, int? subtitleTrackIndex) =>
        mode switch
        {
            JobSourceOverride.EmbeddedSubtitle => string.Format(
                CultureInfo.CurrentCulture,
                Strings.SubtitleSourceEmbeddedShortFormat,
                subtitleTrackIndex ?? 0),

            JobSourceOverride.Audio when audioTrackIndex is { } index => string.Format(
                CultureInfo.CurrentCulture,
                Strings.SubtitleSourceAudioShortFormat,
                index),

            // Audio with no explicit track is the same pipeline as the default; saying "음성 인식"
            // twice in two different ways would suggest a difference that does not exist.
            JobSourceOverride.Audio => Strings.SubtitleSourceDefault,
            _ => Strings.SubtitleSourceDefault
        };
}
