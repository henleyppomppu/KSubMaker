"""Transcriber behaviour that is testable without faster-whisper or a GPU."""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import errors
from ksubmaker_worker.cancellation import CancellationToken
from ksubmaker_worker.transcriber import (
    COMPUTE_DOWNGRADE,
    Transcriber,
    _looks_missing,
    _resolve_device,
    _segment_to_dict,
    cuda_library_error,
    is_cuda_oom,
    segments_from_json,
)

# ---------------------------------------------------------------------------
# OOM classification
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "message",
    [
        "CUDA out of memory. Tried to allocate 2.00 GiB",
        "RuntimeError: CUDA failed with error out of memory",
        "CUBLAS_STATUS_ALLOC_FAILED",
        "Failed to allocate device memory",
        "Insufficient memory on device 0",
        "CUDA_ERROR_OUT_OF_MEMORY",
    ],
)
def test_oom_messages_are_recognised(message: str) -> None:
    assert is_cuda_oom(RuntimeError(message)) is True


@pytest.mark.parametrize(
    "message",
    ["file not found", "invalid compute type", "CUDA driver version is insufficient", ""],
)
def test_other_failures_are_not_oom(message: str) -> None:
    assert is_cuda_oom(RuntimeError(message)) is False


def test_a_class_named_out_of_memory_error_is_recognised() -> None:
    class OutOfMemoryError(Exception):
        pass

    assert is_cuda_oom(OutOfMemoryError("no detail at all")) is True


def test_the_torch_oom_type_is_recognised(monkeypatch: pytest.MonkeyPatch) -> None:
    class FakeOom(Exception):
        pass

    class FakeCuda:
        OutOfMemoryError = FakeOom

    class FakeTorch:
        cuda = FakeCuda

    monkeypatch.setitem(sys.modules, "torch", FakeTorch)
    assert is_cuda_oom(FakeOom("silence")) is True


def test_the_compute_downgrade_ladder_terminates() -> None:
    current = "float32"
    seen = [current]

    while current in COMPUTE_DOWNGRADE:
        current = COMPUTE_DOWNGRADE[current]
        assert current not in seen, "the downgrade ladder loops"
        seen.append(current)

    assert seen == ["float32", "float16", "int8_float16", "int8"]
    assert "int8" not in COMPUTE_DOWNGRADE


# ---------------------------------------------------------------------------
# model resolution
# ---------------------------------------------------------------------------


def test_a_local_directory_wins_over_the_hub_name(tmp_path: Path) -> None:
    directory = tmp_path / "whisper-small"
    directory.mkdir()
    (directory / "model.bin").write_bytes(b"weights")

    transcriber = Transcriber(tmp_path)
    assert transcriber._resolve_model_source("whisper-small", allow_download=False) == str(directory)  # noqa: SLF001


def test_no_local_model_and_no_download_raises(tmp_path: Path) -> None:
    transcriber = Transcriber(tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber._resolve_model_source("whisper-large-v3", allow_download=False)  # noqa: SLF001

    assert excinfo.value.code == errors.WHISPER_MODEL_NOT_FOUND
    assert "모델 화면" in excinfo.value.message


def test_the_hub_alias_is_used_only_when_downloads_are_allowed(tmp_path: Path) -> None:
    transcriber = Transcriber(tmp_path)
    assert transcriber._resolve_model_source("whisper-large-v3", allow_download=True) == "large-v3"  # noqa: SLF001
    assert transcriber._resolve_model_source("auto", allow_download=True) == "small"  # noqa: SLF001


def test_an_unknown_id_passes_through_as_a_hub_name(tmp_path: Path) -> None:
    transcriber = Transcriber(tmp_path)
    assert transcriber._resolve_model_source("acme/custom-ct2", allow_download=True) == "acme/custom-ct2"  # noqa: SLF001


# ---------------------------------------------------------------------------
# unload
# ---------------------------------------------------------------------------


def test_unload_on_a_cold_transcriber_is_a_no_op() -> None:
    Transcriber().unload()


def test_unload_drops_the_model_and_empties_the_cuda_cache(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[str] = []

    class FakeCuda:
        @staticmethod
        def is_available() -> bool:
            return True

        @staticmethod
        def empty_cache() -> None:
            calls.append("empty_cache")

        @staticmethod
        def ipc_collect() -> None:
            calls.append("ipc_collect")

    class FakeTorch:
        cuda = FakeCuda

    monkeypatch.setitem(sys.modules, "torch", FakeTorch)

    transcriber = Transcriber()
    transcriber._model = object()  # noqa: SLF001
    transcriber._model_key = ("m", "cuda", "float16")  # noqa: SLF001
    transcriber.loaded_model_id = "m"

    transcriber.unload()

    # Strategy B depends on this genuinely releasing VRAM, not just dropping the reference.
    assert transcriber._model is None  # noqa: SLF001
    assert transcriber.loaded_model_id is None
    assert calls == ["empty_cache", "ipc_collect"]


def test_unload_survives_a_broken_cuda_state(monkeypatch: pytest.MonkeyPatch) -> None:
    class FakeCuda:
        @staticmethod
        def is_available() -> bool:
            raise RuntimeError("CUDA context is toast")

    class FakeTorch:
        cuda = FakeCuda

    monkeypatch.setitem(sys.modules, "torch", FakeTorch)

    transcriber = Transcriber()
    transcriber._model = object()  # noqa: SLF001
    transcriber.unload()

    assert transcriber._model is None  # noqa: SLF001


# ---------------------------------------------------------------------------
# transcription plumbing
# ---------------------------------------------------------------------------


class FakeWord:
    def __init__(self, word: str, start: float, end: float, probability: float | None = 0.9) -> None:
        self.word = word
        self.start = start
        self.end = end
        self.probability = probability


class FakeSegment:
    def __init__(self, start: float, end: float, text: str, words: list[FakeWord] | None = None) -> None:
        self.start = start
        self.end = end
        self.text = text
        self.words = words or []


class FakeInfo:
    def __init__(self, language: str = "en", probability: float = 0.98, duration: float = 10.0) -> None:
        self.language = language
        self.language_probability = probability
        self.duration = duration


class FakeModel:
    def __init__(self, segments: list[FakeSegment], info: FakeInfo | None = None) -> None:
        self.segments = segments
        self.info = info or FakeInfo()
        self.options: dict[str, Any] = {}

    def transcribe(self, path: str, **kwargs: Any):  # noqa: ANN201
        self.options = kwargs
        return iter(self.segments), self.info


def _prepared(model: FakeModel, tmp_path: Path) -> tuple[Transcriber, str]:
    audio = tmp_path / "audio.wav"
    audio.write_bytes(b"RIFF----WAVEfmt ")

    transcriber = Transcriber(tmp_path)
    transcriber._model = model  # noqa: SLF001
    transcriber._model_key = ("whisper-small", "cpu", "int8")  # noqa: SLF001
    transcriber.loaded_compute_type = "int8"
    transcriber.loaded_device = "cpu"
    # load() would rebuild the model; short-circuit it so no real weights are needed.
    transcriber.load = lambda **_kwargs: model  # type: ignore[method-assign]

    return transcriber, str(audio)


def test_transcribe_returns_the_intermediate_json_shape(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(0.0, 2.0, " Hello there. ", [FakeWord(" Hello", 0.0, 0.5)])])
    transcriber, audio = _prepared(model, tmp_path)

    result = transcriber.transcribe(audio, model_id="whisper-small")

    assert result["sourceLanguage"] == "en"
    assert result["durationSeconds"] == 10.0
    assert result["modelId"] == "whisper-small"
    assert result["segments"] == [
        {
            "id": 1,
            "start": 0.0,
            "end": 2.0,
            "text": "Hello there.",
            "words": [{"word": " Hello", "start": 0.0, "end": 0.5, "probability": 0.9}],
        }
    ]


def test_options_are_forwarded_to_the_model(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(0.0, 1.0, "x")])
    transcriber, audio = _prepared(model, tmp_path)

    transcriber.transcribe(
        audio,
        language="ja",
        beam_size=3,
        vad_filter=True,
        word_timestamps=False,
        condition_on_previous_text=True,
    )

    assert model.options == {
        "beam_size": 3,
        "vad_filter": True,
        # Padding the VAD segments keeps sentence-final morphemes from being clipped.
        "vad_parameters": {"speech_pad_ms": 400},
        "word_timestamps": False,
        "condition_on_previous_text": True,
        # Seeds the decoder with a language-appropriate hint. See the tests below.
        "initial_prompt": "日本語の字幕です。",
        "language": "ja",
    }


def test_a_language_with_a_hint_gets_one_and_the_others_get_none(tmp_path: Path) -> None:
    """initial_prompt seeds Whisper's decoder, so a hint in the wrong language is worse than none.

    Only the two languages with a written hint may receive one; everything else must stay None so
    a French or Spanish video is not primed with Korean orthography rules.
    """
    for language, expected in (("ko", "한국어"), ("ja", "日本語"), ("en", None), ("fr", None)):
        model = FakeModel([FakeSegment(0.0, 1.0, "x")])
        transcriber, audio = _prepared(model, tmp_path)

        transcriber.transcribe(audio, language=language)

        prompt = model.options["initial_prompt"]
        if expected is None:
            assert prompt is None, f"{language} must not be primed with another language's hint"
        else:
            assert prompt is not None and expected in prompt


def test_an_explicit_prompt_wins_over_the_built_in_hint(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(0.0, 1.0, "x")])
    transcriber, audio = _prepared(model, tmp_path)

    transcriber.transcribe(audio, language="ja", initial_prompt="고유명사: 셜록, 베이커가")

    assert model.options["initial_prompt"] == "고유명사: 셜록, 베이커가"


def test_vad_parameters_are_omitted_when_the_filter_is_off(tmp_path: Path) -> None:
    # Padding is a property of the VAD; handing faster-whisper parameters for a filter that is not
    # running would be meaningless.
    model = FakeModel([FakeSegment(0.0, 1.0, "x")])
    transcriber, audio = _prepared(model, tmp_path)

    transcriber.transcribe(audio, language="ja", vad_filter=False)

    assert model.options["vad_parameters"] is None


def test_auto_language_becomes_none(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(0.0, 1.0, "x")])
    transcriber, audio = _prepared(model, tmp_path)

    transcriber.transcribe(audio, language="auto")

    assert model.options["language"] is None


def test_language_is_reported_as_soon_as_it_is_known(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(0.0, 1.0, "x")], FakeInfo("ja", 0.87))
    transcriber, audio = _prepared(model, tmp_path)

    seen: list[tuple[str, float]] = []
    transcriber.transcribe(audio, on_language=lambda lang, prob: seen.append((lang, prob)))

    assert seen == [("ja", 0.87)]


def test_progress_streams_as_segments_arrive(tmp_path: Path) -> None:
    model = FakeModel(
        [FakeSegment(0.0, 2.5, "a"), FakeSegment(2.5, 5.0, "b"), FakeSegment(5.0, 10.0, "c")],
        FakeInfo(duration=10.0),
    )
    transcriber, audio = _prepared(model, tmp_path)

    percents: list[float] = []
    transcriber.transcribe(audio, on_progress=lambda pct, _speed: percents.append(pct))

    assert percents == [25.0, 50.0, 100.0]


def test_cancellation_stops_mid_stream(tmp_path: Path) -> None:
    model = FakeModel([FakeSegment(i, i + 1.0, f"line {i}") for i in range(10)])
    transcriber, audio = _prepared(model, tmp_path)

    token = CancellationToken("t")

    def cancel_after_two(pct: float, _speed: float) -> None:
        if pct >= 20.0:
            token.cancel()

    with pytest.raises(errors.CancelledError):
        transcriber.transcribe(audio, token=token, on_progress=cancel_after_two)


def test_an_empty_transcript_is_no_speech_not_an_empty_success(tmp_path: Path) -> None:
    # An empty result would silently produce an empty subtitle file — but a silent track is not a
    # failure either, so it gets its own code the host shows as 음성 없음.
    transcriber, audio = _prepared(FakeModel([]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(audio)

    assert excinfo.value.code == errors.NO_SPEECH
    assert excinfo.value.recoverable is False, "retrying a silent track finds the same silence"


def test_a_missing_audio_file_is_an_error(tmp_path: Path) -> None:
    transcriber, _ = _prepared(FakeModel([FakeSegment(0.0, 1.0, "x")]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(str(tmp_path / "absent.wav"))

    assert excinfo.value.code == errors.TRANSCRIPTION_FAILED


def test_an_oom_during_streaming_is_classified(tmp_path: Path) -> None:
    class OomModel(FakeModel):
        def transcribe(self, path, **kwargs):  # noqa: ANN001, ANN201
            def generator():  # noqa: ANN202
                yield FakeSegment(0.0, 1.0, "fine")
                raise RuntimeError("CUDA out of memory while decoding")

            return generator(), FakeInfo()

    transcriber, audio = _prepared(OomModel([]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(audio)

    assert excinfo.value.code == errors.CUDA_OUT_OF_MEMORY
    assert excinfo.value.recoverable is True


# ---------------------------------------------------------------------------
# missing CUDA support libraries
# ---------------------------------------------------------------------------

#: Verbatim from the user's log (RTX 3080 Ti, driver CUDA 13.1). This shipped as a bare
#: TRANSCRIPTION_FAILED "음성 인식을 시작하지 못했습니다", which named neither the cause nor the fix.
CUBLAS_MESSAGE = "Library cublas64_12.dll is not found or cannot be loaded"


def test_a_missing_cublas_becomes_its_own_error_code() -> None:
    error = cuda_library_error(RuntimeError(CUBLAS_MESSAGE))

    assert error is not None
    assert error.code == errors.CUDA_LIBRARY_MISSING
    assert "cublas64_12.dll" in error.message
    assert error.recoverable is False, "a retry loads the same absent DLL"


def test_an_unrelated_failure_is_not_a_library_error() -> None:
    assert cuda_library_error(RuntimeError("invalid compute type")) is None


def test_the_library_message_would_otherwise_be_read_as_a_missing_model() -> None:
    """Regression guard for the ordering in load(): "not found or cannot be loaded" contains
    "not found", so the CUDA check has to run before the model-missing heuristic."""
    assert _looks_missing(RuntimeError(CUBLAS_MESSAGE)) is True
    assert cuda_library_error(RuntimeError(CUBLAS_MESSAGE)) is not None


def test_a_missing_library_during_transcription_is_classified(tmp_path: Path) -> None:
    class BrokenModel(FakeModel):
        def transcribe(self, path, **kwargs):  # noqa: ANN001, ANN201
            raise RuntimeError(CUBLAS_MESSAGE)

    transcriber, audio = _prepared(BrokenModel([]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(audio)

    assert excinfo.value.code == errors.CUDA_LIBRARY_MISSING
    assert excinfo.value.recoverable is False
    assert "build-worker.ps1" in excinfo.value.message


def test_a_missing_library_while_streaming_is_classified(tmp_path: Path) -> None:
    class BrokenModel(FakeModel):
        def transcribe(self, path, **kwargs):  # noqa: ANN001, ANN201
            def generator():  # noqa: ANN202
                yield FakeSegment(0.0, 1.0, "fine")
                raise RuntimeError("Library cudnn64_9.dll is not found or cannot be loaded")

            return generator(), FakeInfo()

    transcriber, audio = _prepared(BrokenModel([]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(audio)

    assert excinfo.value.code == errors.CUDA_LIBRARY_MISSING
    assert "cudnn64_9.dll" in excinfo.value.message


def test_a_cublas_oom_is_still_an_oom_not_a_missing_library(tmp_path: Path) -> None:
    """CUBLAS_STATUS_ALLOC_FAILED names cuBLAS but is recoverable; mixing the two up would
    disable the whole compute-type downgrade ladder."""

    class OomModel(FakeModel):
        def transcribe(self, path, **kwargs):  # noqa: ANN001, ANN201
            raise RuntimeError("cublas error: CUBLAS_STATUS_ALLOC_FAILED")

    transcriber, audio = _prepared(OomModel([]), tmp_path)

    with pytest.raises(errors.WorkerError) as excinfo:
        transcriber.transcribe(audio)

    assert excinfo.value.code == errors.CUDA_OUT_OF_MEMORY
    assert excinfo.value.recoverable is True


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def test_segment_conversion_rounds_and_trims() -> None:
    segment = FakeSegment(1.23456, 4.98765, "  spaced out  ", [FakeWord(" x", 1.23456, 1.5, 0.912345)])
    result = _segment_to_dict(7, segment)

    assert result["id"] == 7
    assert result["start"] == 1.235
    assert result["end"] == 4.988
    assert result["text"] == "spaced out"
    assert result["words"][0]["probability"] == 0.9123


def test_a_word_without_a_probability_omits_the_key() -> None:
    result = _segment_to_dict(1, FakeSegment(0.0, 1.0, "x", [FakeWord(" x", 0.0, 0.5, None)]))
    assert "probability" not in result["words"][0]


def test_device_resolution_respects_an_explicit_choice() -> None:
    assert _resolve_device("cpu") == "cpu"
    assert _resolve_device("cuda") == "cuda"


def test_device_auto_falls_back_to_cpu_without_cuda(monkeypatch: pytest.MonkeyPatch) -> None:
    class FakeCt2:
        @staticmethod
        def get_cuda_device_count() -> int:
            return 0

    monkeypatch.setitem(sys.modules, "ctranslate2", FakeCt2)
    assert _resolve_device("auto") == "cpu"


def test_device_auto_picks_cuda_when_available(monkeypatch: pytest.MonkeyPatch) -> None:
    class FakeCt2:
        @staticmethod
        def get_cuda_device_count() -> int:
            return 1

    monkeypatch.setitem(sys.modules, "ctranslate2", FakeCt2)
    assert _resolve_device("auto") == "cuda"


def test_segments_from_json_tolerates_junk() -> None:
    assert segments_from_json({}) == []
    assert segments_from_json({"segments": "not a list"}) == []
    assert segments_from_json({"segments": [{"id": 1}, "junk"]}) == [{"id": 1}]
