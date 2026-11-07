"""The error-code table must stay identical to ``ErrorCodes.cs``."""

from __future__ import annotations

import pytest

from ksubmaker_worker import errors

#: The documented list, spelled out literally rather than derived from ``errors.ALL`` — otherwise
#: this test would happily accept a rename on both sides at once.
DOCUMENTED = [
    "VIDEO_NOT_FOUND",
    "VIDEO_UNREADABLE",
    "AUDIO_TRACK_NOT_FOUND",
    "SUBTITLE_SOURCE_NOT_FOUND",
    "SUBTITLE_SOURCE_UNREADABLE",
    "FFMPEG_NOT_FOUND",
    "FFMPEG_FAILED",
    "CUDA_NOT_AVAILABLE",
    "CUDA_LIBRARY_MISSING",
    "CUDA_OUT_OF_MEMORY",
    "WHISPER_MODEL_NOT_FOUND",
    "WHISPER_MODEL_LOAD_FAILED",
    "TRANSCRIPTION_FAILED",
    "NO_SPEECH",
    "TRANSLATION_MODEL_NOT_FOUND",
    "TRANSLATION_FAILED",
    "INVALID_TRANSLATION_RESPONSE",
    "OUTPUT_WRITE_FAILED",
    "DISK_SPACE_LOW",
    "WORKER_CRASHED",
    "OPERATION_CANCELLED",
    "MODEL_DOWNLOAD_FAILED",
    "MODEL_VERIFICATION_FAILED",
    "PROTOCOL_ERROR",
    "UNKNOWN",
]


def test_all_matches_the_documented_list_in_order() -> None:
    assert list(errors.ALL) == DOCUMENTED


def test_every_code_is_exposed_as_a_module_constant() -> None:
    for code in DOCUMENTED:
        assert getattr(errors, code) == code


def test_no_extra_codes_leaked_in() -> None:
    exported = {
        value
        for name, value in vars(errors).items()
        if name.isupper() and isinstance(value, str) and name == value
    }
    assert exported == set(DOCUMENTED)


@pytest.mark.parametrize(
    "code",
    ["CUDA_OUT_OF_MEMORY", "WORKER_CRASHED", "FFMPEG_FAILED", "INVALID_TRANSLATION_RESPONSE"],
)
def test_recoverable_set_matches_is_auto_retryable(code: str) -> None:
    assert code in errors.RECOVERABLE
    assert errors.is_auto_retryable(code)


@pytest.mark.parametrize(
    "code",
    # CUDA_LIBRARY_MISSING is in this list on purpose: retrying reloads the same absent DLL.
    ["VIDEO_NOT_FOUND", "OPERATION_CANCELLED", "UNKNOWN", "CUDA_LIBRARY_MISSING", None],
)
def test_non_retryable_codes(code: str | None) -> None:
    assert not errors.is_auto_retryable(code)


def test_every_code_has_a_korean_default_message() -> None:
    for code in errors.ALL:
        message = errors.describe(code)
        assert message
        # Every default message must contain Hangul: an English fallback would leak into the UI.
        assert any("가" <= ch <= "힣" for ch in message), code


def test_worker_error_defaults_recoverability_from_the_table() -> None:
    recoverable = errors.WorkerError(errors.CUDA_OUT_OF_MEMORY)
    assert recoverable.recoverable is True

    fatal = errors.WorkerError(errors.VIDEO_NOT_FOUND)
    assert fatal.recoverable is False


def test_worker_error_explicit_flag_wins() -> None:
    error = errors.WorkerError(errors.VIDEO_NOT_FOUND, recoverable=True)
    assert error.recoverable is True


def test_worker_error_to_dict_omits_absent_detail() -> None:
    payload = errors.WorkerError(errors.UNKNOWN, "메시지").to_dict()
    assert payload == {"code": "UNKNOWN", "message": "메시지", "recoverable": False}

    with_detail = errors.WorkerError(errors.UNKNOWN, "메시지", detail="boom").to_dict()
    assert with_detail["detail"] == "boom"


def test_cancelled_error_is_a_worker_error_with_the_cancel_code() -> None:
    error = errors.CancelledError()
    assert isinstance(error, errors.WorkerError)
    assert error.code == errors.OPERATION_CANCELLED
    assert error.recoverable is False
