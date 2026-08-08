using System.Text.Json.Serialization;

namespace KSubMaker.WorkerProtocol;

/// <summary>Base for every host → worker message. One JSON object per stdin line.</summary>
public abstract record WorkerCommand
{
    /// <summary>Discriminator; always written as the <c>command</c> field.</summary>
    [JsonPropertyName("command")]
    public abstract string Command { get; }

    /// <summary>Correlates responses with this request. The worker echoes it on every reply.</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = ProtocolConstants.Version;
}

public sealed record HelloCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.Hello;

    [JsonPropertyName("hostVersion")]
    public string? HostVersion { get; init; }
}

public sealed record DetectHardwareCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.DetectHardware;
}

public sealed record ProbeCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.Probe;

    [JsonPropertyName("videoPath")]
    public required string VideoPath { get; init; }
}

/// <summary>Where the source text comes from for a given job.</summary>
public static class SourceModes
{
    /// <summary>Run ASR over the audio track. The MVP core path.</summary>
    public const string Audio = "audio";

    /// <summary>Extract an embedded subtitle track and translate it instead.</summary>
    public const string EmbeddedSubtitle = "embeddedSubtitle";
}

/// <summary>
/// Wire values for <see cref="WorkerJobSettings.OutputConflictPolicy"/>. They mirror
/// <c>subtitle_writer.CONFLICT_*</c> in the Python worker; the names differ from the C# enum member
/// names on purpose, because the wire vocabulary must stay stable even if the enum is renamed.
/// </summary>
public static class OutputConflictPolicies
{
    /// <summary>Leave the existing file alone and report <c>skipped</c>.</summary>
    public const string Skip = "skip";

    public const string Overwrite = "overwrite";

    /// <summary>Write <c>name (2).ko.srt</c> next to the existing file.</summary>
    public const string Numbered = "numbered";
}

/// <summary>Per-job knobs handed to the worker. Mirrors the user-visible settings screen.</summary>
public sealed record WorkerJobSettings
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("whisperModel")]
    public string WhisperModel { get; init; } = "auto";

    [JsonPropertyName("computeType")]
    public string? ComputeType { get; init; }

    [JsonPropertyName("device")]
    public string Device { get; init; } = "auto";

    [JsonPropertyName("beamSize")]
    public int BeamSize { get; init; } = 5;

    [JsonPropertyName("vadFilter")]
    public bool VadFilter { get; init; } = true;

    [JsonPropertyName("wordTimestamps")]
    public bool WordTimestamps { get; init; } = true;

    [JsonPropertyName("conditionOnPreviousText")]
    public bool ConditionOnPreviousText { get; init; }

    /// <summary><c>local-translation</c>, <c>local-llm</c> or <c>fake</c>.</summary>
    [JsonPropertyName("translationEngine")]
    public string TranslationEngine { get; init; } = "local-translation";

    [JsonPropertyName("translationModel")]
    public string TranslationModel { get; init; } = "auto";

    [JsonPropertyName("llmModel")]
    public string LlmModel { get; init; } = "auto";

    [JsonPropertyName("translationStyle")]
    public string TranslationStyle { get; init; } = "natural";

    [JsonPropertyName("skipTranslationForSameLanguage")]
    public bool SkipTranslationForSameLanguage { get; init; } = true;

    [JsonPropertyName("batchMaxItems")]
    public int BatchMaxItems { get; init; } = 30;

    [JsonPropertyName("batchMaxChars")]
    public int BatchMaxChars { get; init; } = 2500;

    [JsonPropertyName("batchMaxSeconds")]
    public int BatchMaxSeconds { get; init; } = 180;

    [JsonPropertyName("contextLines")]
    public int ContextLines { get; init; } = 3;

    [JsonPropertyName("glossary")]
    public Dictionary<string, string> Glossary { get; init; } = [];

    // ---- subtitle formatting --------------------------------------------
    [JsonPropertyName("maxLinesPerCue")]
    public int MaxLinesPerCue { get; init; } = 2;

    [JsonPropertyName("maxCharsPerLine")]
    public int MaxCharsPerLine { get; init; } = 22;

    [JsonPropertyName("minCueDurationSeconds")]
    public double MinCueDurationSeconds { get; init; } = 1.0;

    [JsonPropertyName("maxCueDurationSeconds")]
    public double MaxCueDurationSeconds { get; init; } = 7.0;

    [JsonPropertyName("minCueGapMilliseconds")]
    public int MinCueGapMilliseconds { get; init; } = 50;

    [JsonPropertyName("mergeShortCues")]
    public bool MergeShortCues { get; init; } = true;

    /// <summary>
    /// What the worker does when the target <c>*.ko.srt</c> already exists. One of
    /// <see cref="OutputConflictPolicies"/>. Defaults to <c>skip</c> so an older host that omits the
    /// field keeps the previous (safe) behaviour.
    /// </summary>
    [JsonPropertyName("outputConflictPolicy")]
    public string OutputConflictPolicy { get; init; } = OutputConflictPolicies.Skip;

    [JsonPropertyName("autoRetryOnRecoverableError")]
    public bool AutoRetryOnRecoverableError { get; init; } = true;
}

public sealed record ProcessCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.Process;

    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("videoPath")]
    public required string VideoPath { get; init; }

    [JsonPropertyName("outputPath")]
    public required string OutputPath { get; init; }

    /// <summary>Directory that holds this job's checkpoints (<c>cache/{jobId}</c>).</summary>
    [JsonPropertyName("checkpointDir")]
    public required string CheckpointDir { get; init; }

    [JsonPropertyName("settings")]
    public required WorkerJobSettings Settings { get; init; }

    /// <summary><see cref="SourceModes"/>.</summary>
    [JsonPropertyName("sourceMode")]
    public string SourceMode { get; init; } = SourceModes.Audio;

    /// <summary>Null means "let FFmpeg choose the default track".</summary>
    [JsonPropertyName("audioTrackIndex")]
    public int? AudioTrackIndex { get; init; }

    [JsonPropertyName("subtitleTrackIndex")]
    public int? SubtitleTrackIndex { get; init; }

    /// <summary>
    /// Language of the embedded subtitle track named by <see cref="SubtitleTrackIndex"/>, as an
    /// ISO-639-1/2 code. Only meaningful when <see cref="SourceMode"/> is
    /// <see cref="SourceModes.EmbeddedSubtitle"/>.
    ///
    /// Sent because container metadata is routinely wrong or absent: without it the worker has to
    /// assume English, and a Japanese track translated as if it were English produces confident
    /// nonsense. Null keeps the worker's own fallback.
    /// </summary>
    [JsonPropertyName("subtitleLanguage")]
    public string? SubtitleLanguage { get; init; }

    /// <summary>When true the worker reuses any checkpoint it finds instead of starting over.</summary>
    [JsonPropertyName("resume")]
    public bool Resume { get; init; } = true;

    /// <summary>Which part of the pipeline to run: <c>full</c> | <c>transcribe</c> | <c>translate</c>.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "full";
}

/// <summary>
/// <b>v1.3.</b> Extract one file's audio into its checkpoint directory, ahead of the job that will
/// need it.
///
/// <para>The one command the worker accepts while a <see cref="ProcessCommand"/> is running.
/// Everything else is serialised because two CUDA jobs would fight over the same VRAM; this shells
/// out to ffmpeg and allocates none, so the pump can keep the CPU busy demuxing file N+1 while the
/// GPU transcribes file N.</para>
///
/// <para>There is no separate "use the prefetched audio" command: the worker writes the same
/// <c>audio.wav</c> and checkpoint stanza a job would have written itself, so the job simply finds
/// the extraction stage already done. That also means a prefetch that never happened costs nothing
/// but the time it would have saved.</para>
/// </summary>
public sealed record ExtractAudioCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.ExtractAudio;

    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("videoPath")]
    public required string VideoPath { get; init; }

    /// <summary>Same <c>cache/{jobId}</c> directory the matching <see cref="ProcessCommand"/> uses.</summary>
    [JsonPropertyName("checkpointDir")]
    public required string CheckpointDir { get; init; }

    /// <summary>Null means "let FFmpeg choose the default track".</summary>
    [JsonPropertyName("audioTrackIndex")]
    public int? AudioTrackIndex { get; init; }

    /// <summary>
    /// The audio-affecting settings this extraction was made under, recorded so the job can tell
    /// whether the prefetched wav still matches what the user is asking for. Same fingerprint the
    /// job writes; a mismatch discards the wav rather than transcribing the wrong track.
    /// </summary>
    [JsonPropertyName("settings")]
    public required WorkerJobSettings Settings { get; init; }

    /// <summary><see cref="SourceModes"/>. Only <see cref="SourceModes.Audio"/> extracts anything.</summary>
    [JsonPropertyName("sourceMode")]
    public string SourceMode { get; init; } = SourceModes.Audio;
}

public sealed record CancelCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.Cancel;

    /// <summary>Null cancels whatever is currently running.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }
}

public sealed record ListModelsCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.ListModels;
}

public sealed record DownloadModelCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.DownloadModel;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("repositoryId")]
    public required string RepositoryId { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<string> Files { get; init; }

    [JsonPropertyName("targetDir")]
    public required string TargetDir { get; init; }
}

public sealed record CancelDownloadCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.CancelDownload;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }
}

public sealed record VerifyModelCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.VerifyModel;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("targetDir")]
    public required string TargetDir { get; init; }
}

public sealed record DeleteModelCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.DeleteModel;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("targetDir")]
    public required string TargetDir { get; init; }
}

public sealed record ShutdownCommand : WorkerCommand
{
    public override string Command => ProtocolConstants.Commands.Shutdown;
}
