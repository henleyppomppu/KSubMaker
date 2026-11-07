namespace KSubMaker.Domain.Errors;

/// <summary>
/// Stable, machine-readable error identifiers shared by the C# host and the Python worker.
/// The Python side mirrors this list in <c>worker/ksubmaker_worker/errors.py</c>; the unit test
/// <c>ErrorCodeParityTests</c> keeps the two in sync.
/// </summary>
public static class ErrorCodes
{
    public const string VideoNotFound = "VIDEO_NOT_FOUND";
    public const string VideoUnreadable = "VIDEO_UNREADABLE";
    public const string AudioTrackNotFound = "AUDIO_TRACK_NOT_FOUND";

    /// <summary>
    /// The sidecar subtitle chosen as the job's source is gone. Its own code rather than
    /// <see cref="VideoNotFound"/> because the remedy differs: the file existed when the folder was
    /// scanned, so it has been moved, renamed or deleted since.
    /// </summary>
    public const string SubtitleSourceNotFound = "SUBTITLE_SOURCE_NOT_FOUND";

    /// <summary>
    /// The sidecar exists but produced no cues — a bitmap format that got through, a truncated
    /// file, or an encoding none of the candidates could decode.
    /// </summary>
    public const string SubtitleSourceUnreadable = "SUBTITLE_SOURCE_UNREADABLE";

    public const string FfmpegNotFound = "FFMPEG_NOT_FOUND";
    public const string FfmpegFailed = "FFMPEG_FAILED";
    public const string CudaNotAvailable = "CUDA_NOT_AVAILABLE";

    /// <summary>
    /// CUDA support libraries (cuBLAS for CUDA 12, cuDNN 9) are not installed or cannot be loaded.
    /// Distinct from <see cref="CudaNotAvailable"/>: the driver and the device are fine, so the app
    /// reports a GPU, but the very first model load dies inside CTranslate2.
    /// </summary>
    public const string CudaLibraryMissing = "CUDA_LIBRARY_MISSING";

    public const string CudaOutOfMemory = "CUDA_OUT_OF_MEMORY";
    public const string WhisperModelNotFound = "WHISPER_MODEL_NOT_FOUND";
    public const string WhisperModelLoadFailed = "WHISPER_MODEL_LOAD_FAILED";
    public const string TranscriptionFailed = "TRANSCRIPTION_FAILED";

    /// <summary>
    /// The audio track is silent: Whisper ran and found nothing to say. Not a failure — there is
    /// no subtitle to make — so the queue parks the job as <c>Skipped</c> and the grid reads 음성 없음.
    /// </summary>
    public const string NoSpeech = "NO_SPEECH";

    public const string TranslationModelNotFound = "TRANSLATION_MODEL_NOT_FOUND";
    public const string TranslationFailed = "TRANSLATION_FAILED";
    public const string InvalidTranslationResponse = "INVALID_TRANSLATION_RESPONSE";
    public const string OutputWriteFailed = "OUTPUT_WRITE_FAILED";
    public const string DiskSpaceLow = "DISK_SPACE_LOW";
    public const string WorkerCrashed = "WORKER_CRASHED";
    public const string OperationCancelled = "OPERATION_CANCELLED";
    public const string ModelDownloadFailed = "MODEL_DOWNLOAD_FAILED";
    public const string ModelVerificationFailed = "MODEL_VERIFICATION_FAILED";
    public const string ProtocolError = "PROTOCOL_ERROR";
    public const string Unknown = "UNKNOWN";

    public static readonly IReadOnlyList<string> All =
    [
        VideoNotFound, VideoUnreadable, AudioTrackNotFound,
        SubtitleSourceNotFound, SubtitleSourceUnreadable, FfmpegNotFound, FfmpegFailed,
        CudaNotAvailable, CudaLibraryMissing, CudaOutOfMemory, WhisperModelNotFound, WhisperModelLoadFailed,
        TranscriptionFailed, NoSpeech, TranslationModelNotFound, TranslationFailed, InvalidTranslationResponse,
        OutputWriteFailed, DiskSpaceLow, WorkerCrashed, OperationCancelled,
        ModelDownloadFailed, ModelVerificationFailed, ProtocolError, Unknown
    ];

    /// <summary>
    /// Whether the host may automatically retry the job once after this error, without user input.
    /// </summary>
    public static bool IsAutoRetryable(string? code) => code switch
    {
        CudaOutOfMemory => true,
        WorkerCrashed => true,
        FfmpegFailed => true,
        InvalidTranslationResponse => true,
        // Deliberately NOT retryable: a retry loads the same missing DLL from the same directory
        // and fails identically, one whole model load later. Only an install fixes it.
        _ => false
    };
}
