namespace KSubMaker.Domain.Settings;

/// <summary>
/// The full user-configurable surface of the application. Persisted as flat key/value rows in the
/// <c>AppSettings</c> table so adding a property never requires a schema migration.
/// </summary>
public sealed class AppSettings
{
    // ---- scanning --------------------------------------------------------
    public string LastFolder { get; set; } = string.Empty;
    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludeHiddenFolders { get; set; }
    public bool ReprocessCompleted { get; set; }
    public bool RetryFailedOnly { get; set; }

    // ---- speech recognition ---------------------------------------------
    /// <summary>ISO-639-1 code, or "auto" for detection.</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>Whisper model id, or "auto" to use the hardware recommendation.</summary>
    public string WhisperModel { get; set; } = "auto";

    /// <summary>Null means "follow the hardware recommendation".</summary>
    public ComputeType? ComputeType { get; set; }

    public int BeamSize { get; set; } = 5;
    public bool VadFilter { get; set; } = true;
    public bool WordTimestamps { get; set; } = true;

    /// <summary>
    /// faster-whisper's <c>condition_on_previous_text</c>. Off by default: leaving it on is the main
    /// cause of runaway repeated subtitles on long videos.
    /// </summary>
    public bool ConditionOnPreviousText { get; set; }

    /// <summary>
    /// Optional hint prepended to Whisper's decoder context — proper nouns, recurring character
    /// names, a sample of the register you want. Null or blank keeps the worker's built-in
    /// per-language hint.
    /// </summary>
    public string? InitialPrompt { get; set; }

    // ---- translation -----------------------------------------------------
    public TranslationEngineKind TranslationEngine { get; set; } = TranslationEngineKind.LocalTranslationModel;
    public string TranslationModel { get; set; } = "auto";
    public string LlmModel { get; set; } = "auto";
    public TranslationStyle TranslationStyle { get; set; } = TranslationStyle.Natural;
    public bool SkipTranslationForSameLanguage { get; set; } = true;
    public int TestDurationSeconds { get; set; } = 0;

    public int TranslationBatchMaxItems { get; set; } = 30;
    public int TranslationBatchMaxChars { get; set; } = 2500;
    public int TranslationBatchMaxSeconds { get; set; } = 180;

    /// <summary>How many already-translated lines are passed as read-only context to the next batch.</summary>
    public int TranslationContextLines { get; set; } = 3;

    /// <summary>Proper-noun glossary: source term → fixed Korean rendering.</summary>
    public Dictionary<string, string> Glossary { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- subtitles / output ---------------------------------------------
    /// <summary>What a job translates from. See <see cref="SubtitleSourcePreference"/>.</summary>
    public SubtitleSourcePreference SubtitleSource { get; set; } = SubtitleSourcePreference.AudioOnly;

    /// <summary>Whether a file that already has a subtitle is processed at all.</summary>
    public ExistingSubtitleRule ExistingSubtitleRule { get; set; } = ExistingSubtitleRule.CompleteIfKoreanExists;
    public OutputConflictPolicy OutputConflictPolicy { get; set; } = OutputConflictPolicy.Skip;

    /// <summary>Suffix inserted before <c>.srt</c>. The resulting name is <c>{video}.ko.srt</c>.</summary>
    public string OutputSuffix { get; set; } = "ko";

    /// <summary>
    /// Where finished subtitles go. Empty (the default) writes each SRT next to its source video.
    /// When set, the source folder structure is mirrored beneath this directory — a file at
    /// <c>D:\videos\showA\ep1.mkv</c> lands at <c>{OutputDirectory}\videos\showA\ep1.ko.srt</c> —
    /// so two files with the same name in different source folders never collide.
    /// </summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Also keep the recognised source text as its own SRT, in the same folder as the Korean one
    /// and tagged with the detected language (<c>movie.ja.srt</c>).
    ///
    /// <para>Off by default: it is an extra file most users do not want, and the transcript is in
    /// the job cache regardless. Turning it on also makes the next run cheaper — the sidecar
    /// becomes a usable translation source, so ASR can be skipped entirely.</para>
    /// </summary>
    public bool SaveSourceSubtitle { get; set; }

    public int MaxLinesPerCue { get; set; } = 2;
    public int MaxCharsPerLine { get; set; } = 22;
    public double MinCueDurationSeconds { get; set; } = 1.0;
    public double MaxCueDurationSeconds { get; set; } = 7.0;
    public int MinCueGapMilliseconds { get; set; } = 50;
    public bool MergeShortCues { get; set; } = true;

    // ---- execution -------------------------------------------------------
    public ProcessingStrategy ProcessingStrategy { get; set; } = ProcessingStrategy.Auto;

    /// <summary>0 means "decide from hardware". GPU stages are serialised regardless of this value.</summary>
    public int MaxParallelCpuTasks { get; set; }

    /// <summary>
    /// How many upcoming files may have their audio extracted while the current one is being
    /// transcribed. 0 disables the lookahead.
    ///
    /// <para>Demuxing is CPU and disk work, so it overlaps the GPU stages for free — unlike
    /// processing strategy C, which needs both models resident at once and is therefore only
    /// offered above 16GB of VRAM.</para>
    ///
    /// <para>The default is deliberately small. Throughput converges once the extractor merely
    /// stays ahead of the consumer, which a depth of one already does when extraction takes a
    /// minute and transcription takes many; going deeper buys nothing and costs disk, at roughly
    /// 115MB of wav per hour of video. Raise it only when the source files live on storage slow or
    /// contended enough that extraction cannot keep up.</para>
    /// </summary>
    public int AudioPrefetchDepth { get; set; } = 1;

    public bool AutoRetryOnRecoverableError { get; set; } = true;

    /// <summary>
    /// What to do to the machine once the queue drains on its own. Never acted on after a manual
    /// 중단 or 일시정지, and always behind a cancellable countdown. See <see cref="PostQueueAction"/>.
    /// </summary>
    public PostQueueAction PostQueueAction { get; set; } = PostQueueAction.None;

    /// <summary>
    /// When true, a failed or cancelled job anywhere in the run calls off
    /// <see cref="PostQueueAction"/> so the user comes back to a running machine and can see what
    /// broke. Defaults to the cautious choice.
    /// </summary>
    public bool PostQueueActionOnlyWhenAllSucceeded { get; set; } = true;

    // ---- paths -----------------------------------------------------------
    /// <summary>Empty means the default under %LOCALAPPDATA%\KSubMaker.</summary>
    public string CacheDirectory { get; set; } = string.Empty;
    public string ModelDirectory { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;

    // ---- diagnostics -----------------------------------------------------
    public string LogLevel { get; set; } = "Information";

    /// <summary>Replaces directory components with <c>***</c> in log output.</summary>
    public bool MaskPathsInLogs { get; set; }

    /// <summary>
    /// Runs the pipeline with the deterministic fake transcriber/translator. Used for smoke-testing the
    /// whole chain without a GPU or any downloaded model.
    /// </summary>
    public bool FakeAiMode { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Glossary = new Dictionary<string, string>(Glossary, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
