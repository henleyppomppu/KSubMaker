namespace KSubMaker.WorkerProtocol;

/// <summary>
/// Wire constants shared with <c>worker/ksubmaker_worker/protocol.py</c>.
/// Any change here is a protocol change: bump <see cref="Version"/> and follow the rules in
/// AGENTS.md ("Worker 프로토콜 변경 규칙").
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// Semantic protocol version. Major mismatch = refuse to run; minor mismatch = warn and continue.
    ///
    /// 1.1 added two optional fields, both of which default to the 1.0 behaviour when absent:
    /// <c>settings.outputConflictPolicy</c> and <c>process.subtitleLanguage</c>.
    ///
    /// 1.2 added three optional fields to the <c>hardware</c> event —
    /// <c>cudaDeviceDetected</c>, <c>cudaLibrariesAvailable</c> and <c>missingCudaLibraries</c> —
    /// which split "a CUDA device exists" from "the cuBLAS/cuDNN libraries a model load needs are
    /// actually there". <c>cudaAvailable</c> keeps its name and becomes the conjunction of the two,
    /// so a 1.1 host reading only that field gets the *safer* answer, not a wrong one.
    ///
    /// 1.3 added the <c>extractAudio</c> command. It is the only command the worker will run
    /// <i>while</i> a <c>process</c> job is already running: it shells out to ffmpeg and touches no
    /// GPU, so the VRAM argument that serialises everything else does not apply to it. A 1.2 worker
    /// answers it with <c>PROTOCOL_ERROR</c>, which the host treats as "prefetch unavailable" and
    /// carries on — extraction then simply happens inside the job as before.
    /// </summary>
    public const string Version = "1.6";

    public static class Commands
    {
        public const string Hello = "hello";
        public const string DetectHardware = "detectHardware";
        public const string Probe = "probe";
        public const string Process = "process";

        /// <summary><b>v1.3.</b> Extract one file's audio ahead of time; runs alongside a job.</summary>
        public const string ExtractAudio = "extractAudio";
        public const string Cancel = "cancel";
        public const string ListModels = "listModels";
        public const string DownloadModel = "downloadModel";
        public const string CancelDownload = "cancelDownload";
        public const string VerifyModel = "verifyModel";
        public const string DeleteModel = "deleteModel";
        public const string Shutdown = "shutdown";
    }

    public static class Events
    {
        public const string Ready = "ready";
        public const string Ack = "ack";
        public const string Started = "started";
        public const string Progress = "progress";
        public const string LanguageDetected = "languageDetected";
        public const string StageCompleted = "stageCompleted";
        public const string Completed = "completed";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
        public const string Log = "log";
        public const string Hardware = "hardware";
        public const string ProbeResult = "probeResult";
        public const string ModelList = "modelList";
        public const string DownloadProgress = "downloadProgress";
        public const string DownloadCompleted = "downloadCompleted";
        public const string Goodbye = "goodbye";
    }

    /// <summary>Stage names on the wire. Must match <c>JobStage</c> when lower-camel-cased.</summary>
    public static class Stages
    {
        public const string Probing = "probing";
        public const string ExtractingAudio = "extractingAudio";
        public const string Transcribing = "transcribing";
        public const string Translating = "translating";
        public const string WritingSubtitle = "writingSubtitle";
    }
}
