"""Error codes shared with the C# host.

This module mirrors ``src/KSubMaker.Domain/Errors/ErrorCodes.cs`` exactly: same constant names
(snake-cased) and, more importantly, the same string values. A C# unit test asserts parity
against ``ALL``, so never rename or drop an entry without changing both sides.
"""

from __future__ import annotations

from typing import Final

VIDEO_NOT_FOUND: Final = "VIDEO_NOT_FOUND"
VIDEO_UNREADABLE: Final = "VIDEO_UNREADABLE"
AUDIO_TRACK_NOT_FOUND: Final = "AUDIO_TRACK_NOT_FOUND"
#: The sidecar the host chose is gone. Its own code because the fix is different from a missing
#: video: the file was there when the folder was scanned, so it was moved, renamed or deleted since.
SUBTITLE_SOURCE_NOT_FOUND: Final = "SUBTITLE_SOURCE_NOT_FOUND"
#: The sidecar exists but yielded no cues — a bitmap format that slipped through, a truncated file,
#: or an encoding no candidate could decode into anything sane.
SUBTITLE_SOURCE_UNREADABLE: Final = "SUBTITLE_SOURCE_UNREADABLE"
FFMPEG_NOT_FOUND: Final = "FFMPEG_NOT_FOUND"
FFMPEG_FAILED: Final = "FFMPEG_FAILED"
CUDA_NOT_AVAILABLE: Final = "CUDA_NOT_AVAILABLE"
#: cuBLAS 12 / cuDNN 9 are absent or unloadable. Distinct from CUDA_NOT_AVAILABLE: the driver and
#: the device are fine, so everything reports a working GPU right up until the first model load.
CUDA_LIBRARY_MISSING: Final = "CUDA_LIBRARY_MISSING"
CUDA_OUT_OF_MEMORY: Final = "CUDA_OUT_OF_MEMORY"
WHISPER_MODEL_NOT_FOUND: Final = "WHISPER_MODEL_NOT_FOUND"
WHISPER_MODEL_LOAD_FAILED: Final = "WHISPER_MODEL_LOAD_FAILED"
TRANSCRIPTION_FAILED: Final = "TRANSCRIPTION_FAILED"
#: The audio decoded fine and Whisper ran, but there is nothing to transcribe: a silent track.
#: Not a failure — there is simply no subtitle to make — so the host shows it as 음성 없음.
NO_SPEECH: Final = "NO_SPEECH"
TRANSLATION_MODEL_NOT_FOUND: Final = "TRANSLATION_MODEL_NOT_FOUND"
TRANSLATION_FAILED: Final = "TRANSLATION_FAILED"
INVALID_TRANSLATION_RESPONSE: Final = "INVALID_TRANSLATION_RESPONSE"
OUTPUT_WRITE_FAILED: Final = "OUTPUT_WRITE_FAILED"
DISK_SPACE_LOW: Final = "DISK_SPACE_LOW"
WORKER_CRASHED: Final = "WORKER_CRASHED"
OPERATION_CANCELLED: Final = "OPERATION_CANCELLED"
MODEL_DOWNLOAD_FAILED: Final = "MODEL_DOWNLOAD_FAILED"
MODEL_VERIFICATION_FAILED: Final = "MODEL_VERIFICATION_FAILED"
PROTOCOL_ERROR: Final = "PROTOCOL_ERROR"
UNKNOWN: Final = "UNKNOWN"

#: Every code, in the same order as ``ErrorCodes.All`` on the C# side.
ALL: Final[tuple[str, ...]] = (
    VIDEO_NOT_FOUND,
    VIDEO_UNREADABLE,
    AUDIO_TRACK_NOT_FOUND,
    SUBTITLE_SOURCE_NOT_FOUND,
    SUBTITLE_SOURCE_UNREADABLE,
    FFMPEG_NOT_FOUND,
    FFMPEG_FAILED,
    CUDA_NOT_AVAILABLE,
    CUDA_LIBRARY_MISSING,
    CUDA_OUT_OF_MEMORY,
    WHISPER_MODEL_NOT_FOUND,
    WHISPER_MODEL_LOAD_FAILED,
    TRANSCRIPTION_FAILED,
    NO_SPEECH,
    TRANSLATION_MODEL_NOT_FOUND,
    TRANSLATION_FAILED,
    INVALID_TRANSLATION_RESPONSE,
    OUTPUT_WRITE_FAILED,
    DISK_SPACE_LOW,
    WORKER_CRASHED,
    OPERATION_CANCELLED,
    MODEL_DOWNLOAD_FAILED,
    MODEL_VERIFICATION_FAILED,
    PROTOCOL_ERROR,
    UNKNOWN,
)

#: Codes the host may retry once, without asking the user.
#: Must stay in step with ``ErrorCodes.IsAutoRetryable``.
#:
#: ``CUDA_LIBRARY_MISSING`` is deliberately **not** here: a retry loads the same missing DLL from
#: the same directory and fails identically, one whole model load later.
RECOVERABLE: Final[frozenset[str]] = frozenset(
    {
        CUDA_OUT_OF_MEMORY,
        WORKER_CRASHED,
        FFMPEG_FAILED,
        INVALID_TRANSLATION_RESPONSE,
    }
)


def is_auto_retryable(code: str | None) -> bool:
    """Mirror of ``ErrorCodes.IsAutoRetryable``."""
    return code is not None and code in RECOVERABLE


#: Default Korean sentence per code. Callers usually pass a more specific message; this is the
#: fallback so an error event is never sent with an empty user-facing string.
DEFAULT_MESSAGES: Final[dict[str, str]] = {
    VIDEO_NOT_FOUND: "영상 파일을 찾을 수 없습니다.",
    VIDEO_UNREADABLE: "영상 파일을 읽을 수 없습니다. 파일이 손상되었을 수 있습니다.",
    AUDIO_TRACK_NOT_FOUND: "영상에 오디오 트랙이 없습니다.",
    FFMPEG_NOT_FOUND: "FFmpeg 실행 파일을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.",
    FFMPEG_FAILED: "오디오 추출에 실패했습니다.",
    CUDA_NOT_AVAILABLE: "CUDA를 사용할 수 없습니다. CPU로 실행하거나 그래픽 드라이버를 확인하세요.",
    CUDA_LIBRARY_MISSING: (
        "CUDA 지원 라이브러리(cuBLAS 12 / cuDNN 9)를 불러오지 못했습니다. "
        "scripts\\build-worker.ps1로 워커를 다시 설치하거나 설정에서 CPU 모드로 전환하세요."
    ),
    CUDA_OUT_OF_MEMORY: "GPU 메모리가 부족합니다. 더 작은 모델이나 낮은 정밀도로 다시 시도하세요.",
    WHISPER_MODEL_NOT_FOUND: "음성 인식 모델을 찾을 수 없습니다. 모델 화면에서 먼저 내려받으세요.",
    WHISPER_MODEL_LOAD_FAILED: "음성 인식 모델을 불러오지 못했습니다.",
    TRANSCRIPTION_FAILED: "음성 인식에 실패했습니다.",
    NO_SPEECH: "음성이 없습니다. 자막으로 만들 내용이 없습니다.",
    TRANSLATION_MODEL_NOT_FOUND: "번역 모델을 찾을 수 없습니다. 모델 화면에서 먼저 내려받으세요.",
    TRANSLATION_FAILED: "번역에 실패했습니다.",
    INVALID_TRANSLATION_RESPONSE: "번역 결과 형식이 올바르지 않습니다.",
    OUTPUT_WRITE_FAILED: "자막 파일을 저장하지 못했습니다.",
    DISK_SPACE_LOW: "디스크 공간이 부족합니다.",
    WORKER_CRASHED: "AI 작업 프로세스가 예기치 않게 종료되었습니다.",
    OPERATION_CANCELLED: "작업이 취소되었습니다.",
    MODEL_DOWNLOAD_FAILED: "모델 다운로드에 실패했습니다.",
    MODEL_VERIFICATION_FAILED: "모델 파일 검증에 실패했습니다. 다시 내려받으세요.",
    PROTOCOL_ERROR: "호스트와의 통신 형식이 올바르지 않습니다.",
    UNKNOWN: "알 수 없는 오류가 발생했습니다.",
}


def describe(code: str | None) -> str:
    """Korean one-liner for a code; falls back to the UNKNOWN sentence."""
    if code is None:
        return DEFAULT_MESSAGES[UNKNOWN]
    return DEFAULT_MESSAGES.get(code, DEFAULT_MESSAGES[UNKNOWN])


class WorkerError(Exception):
    """A failure that maps onto exactly one protocol ``error`` event.

    ``message`` is Korean and user-facing; ``detail`` is English/technical and only ever reaches
    the log file.
    """

    def __init__(
        self,
        code: str,
        message: str | None = None,
        *,
        recoverable: bool | None = None,
        detail: str | None = None,
    ) -> None:
        self.code = code
        self.message = message or describe(code)
        # An explicit flag wins; otherwise the shared retryability table decides, so the worker
        # and the host never disagree about whether a retry is worth attempting.
        self.recoverable = is_auto_retryable(code) if recoverable is None else recoverable
        self.detail = detail
        super().__init__(f"{code}: {self.message}")

    def to_dict(self) -> dict[str, object]:
        payload: dict[str, object] = {
            "code": self.code,
            "message": self.message,
            "recoverable": self.recoverable,
        }
        if self.detail:
            payload["detail"] = self.detail
        return payload

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"WorkerError(code={self.code!r}, recoverable={self.recoverable!r})"


class CancelledError(WorkerError):
    """Raised when a cancellation token fires. Handled specially by the orchestrator."""

    def __init__(self, message: str | None = None) -> None:
        super().__init__(OPERATION_CANCELLED, message, recoverable=False)
