"""faster-whisper speech recognition.

``faster_whisper`` / ``ctranslate2`` / ``torch`` are imported inside the methods that need them so
that importing this module — and running the test suite — works on a machine with none of them.

The intermediate JSON this produces is the checkpoint contract:

    {"sourceLanguage": "en", "durationSeconds": 123.4, "modelId": "...",
     "segments": [{"id": 1, "start": 1.2, "end": 4.8, "text": "...",
                   "words": [{"word": " x", "start": .., "end": .., "probability": ..}]}]}
"""

from __future__ import annotations

import gc
import io
import sys
import time
from pathlib import Path
from typing import Any, Callable, Iterable

from . import errors, hardware_detector
from .cancellation import CancellationToken
from .cuda_setup import missing_cuda_library, remedy_message
from .errors import WorkerError
from .logging_setup import get_logger
from .model_manager import find_local_model

_log = get_logger("asr")

#: Fallback faster-whisper model names, used only when nothing is installed locally.
_MODEL_NAME_ALIASES: dict[str, str] = {
    "whisper-base": "base",
    "whisper-small": "small",
    "whisper-medium": "medium",
    "whisper-large-v3": "large-v3",
    "whisper-large-v3-turbo": "large-v3-turbo",
    "auto": "small",
}

#: Compute-type downgrade ladder used by the CUDA-OOM recovery path.
COMPUTE_DOWNGRADE: dict[str, str] = {
    "float32": "float16",
    "bfloat16": "float16",
    "float16": "int8_float16",
    "int8_float16": "int8",
}

_OOM_MARKERS = (
    "out of memory",
    "cuda_error_out_of_memory",
    "cublas_status_alloc_failed",
    "failed to allocate",
    "insufficient memory",
)


def is_cuda_oom(exc: BaseException) -> bool:
    """True when ``exc`` is a GPU out-of-memory condition from torch or CTranslate2.

    Detection is textual on purpose: CTranslate2 raises a plain ``RuntimeError`` whose message is
    the only thing distinguishing OOM from any other CUDA failure.
    """
    torch_oom = getattr(getattr(sys.modules.get("torch"), "cuda", None), "OutOfMemoryError", None)
    if torch_oom is not None and isinstance(exc, torch_oom):
        return True

    if type(exc).__name__ == "OutOfMemoryError":
        return True

    text = str(exc).lower()
    return any(marker in text for marker in _OOM_MARKERS)


def cuda_library_error(exc: BaseException) -> WorkerError | None:
    """Turn CTranslate2's "Library cublas64_12.dll is not found" into an actionable error.

    Returns None when ``exc`` is anything else. Callers must consult this **before**
    :func:`_looks_missing`, whose "not found" marker would otherwise swallow the message and report
    a perfectly present model as missing.
    """
    library = missing_cuda_library(exc)
    if library is None:
        return None

    return WorkerError(
        errors.CUDA_LIBRARY_MISSING,
        remedy_message(library),
        # Not recoverable: the retry loads the same absent DLL from the same directory.
        recoverable=False,
        detail=repr(exc),
    )


class Transcriber:
    """Owns one loaded WhisperModel. Reused across jobs; :meth:`unload` frees the VRAM."""

    def __init__(self, models_dir: str | Path | None = None) -> None:
        self._models_dir = Path(models_dir) if models_dir is not None else None
        self._model: Any = None
        self._model_key: tuple[str, str, str] | None = None
        self.loaded_model_id: str | None = None
        self.loaded_compute_type: str | None = None
        self.loaded_device: str | None = None

    # -- model lifecycle -------------------------------------------------------

    def _resolve_model_source(self, model_id: str, *, allow_download: bool) -> str:
        """A local directory wins; the hub name is a fallback that would download."""
        local = find_local_model(model_id, self._models_dir)
        if local is not None:
            _log.info("using local whisper model at %s", local)
            return str(local)

        if not allow_download:
            raise WorkerError(
                errors.WHISPER_MODEL_NOT_FOUND,
                f"음성 인식 모델을 찾을 수 없습니다: {model_id}. 모델 화면에서 먼저 내려받으세요.",
                detail=f"no local model directory for {model_id} and downloads are disabled",
            )

        name = _MODEL_NAME_ALIASES.get(model_id, model_id)
        _log.warning("no local copy of %s; falling back to the hub name %r", model_id, name)
        return name

    def load(
        self,
        *,
        model_id: str = "auto",
        device: str = "auto",
        compute_type: str | None = None,
        allow_download: bool = False,
        cpu_threads: int = 0,
    ) -> Any:
        """Load (or reuse) a ``WhisperModel``.

        Model loading is the single noisiest thing in the process — CTranslate2 and huggingface_hub
        both print to stdout — so it runs with ``sys.stdout`` pointed at stderr for the duration.
        """
        resolved_device = _resolve_device(device)
        resolved_compute = compute_type or _default_compute_type(resolved_device)
        key = (model_id, resolved_device, resolved_compute)

        if self._model is not None and self._model_key == key:
            return self._model

        if self._model is not None:
            self.unload()

        source = self._resolve_model_source(model_id, allow_download=allow_download)

        try:
            from faster_whisper import WhisperModel  # noqa: PLC0415 - deliberately lazy
        except ImportError as exc:
            raise WorkerError(
                errors.WHISPER_MODEL_LOAD_FAILED,
                "음성 인식 구성 요소(faster-whisper)를 불러오지 못했습니다. 설치가 손상되었을 수 있습니다.",
                detail=repr(exc),
            ) from exc

        _log.info(
            "loading whisper model %s (device=%s, compute=%s)", source, resolved_device, resolved_compute
        )
        started = time.monotonic()

        with _stdout_to_stderr():
            try:
                model = WhisperModel(
                    source,
                    device=resolved_device,
                    compute_type=resolved_compute,
                    cpu_threads=cpu_threads,
                    local_files_only=not allow_download,
                )
            except Exception as exc:  # noqa: BLE001 - ctranslate2 raises RuntimeError for everything
                if is_cuda_oom(exc):
                    raise WorkerError(
                        errors.CUDA_OUT_OF_MEMORY,
                        "GPU 메모리가 부족하여 음성 인식 모델을 불러오지 못했습니다.",
                        recoverable=True,
                        detail=repr(exc),
                    ) from exc
                # Before _looks_missing: "…is not found or cannot be loaded" contains "not found".
                library_error = cuda_library_error(exc)
                if library_error is not None:
                    raise library_error from exc
                if _looks_missing(exc):
                    raise WorkerError(
                        errors.WHISPER_MODEL_NOT_FOUND,
                        f"음성 인식 모델을 찾을 수 없습니다: {model_id}. 모델 화면에서 먼저 내려받으세요.",
                        detail=repr(exc),
                    ) from exc
                raise WorkerError(
                    errors.WHISPER_MODEL_LOAD_FAILED,
                    f"음성 인식 모델을 불러오지 못했습니다: {model_id}",
                    detail=repr(exc),
                ) from exc

        self._model = model
        self._model_key = key
        self.loaded_model_id = model_id
        self.loaded_compute_type = resolved_compute
        self.loaded_device = resolved_device

        _log.info("whisper model ready in %.1fs", time.monotonic() - started)
        return model

    def unload(self) -> None:
        """Drop the model and actively release its memory.

        Strategy B (transcribe everything, then translate everything) only works if this really
        frees VRAM, so it does all three things: drop the reference, run the collector, and hand
        the freed blocks back to the CUDA allocator.
        """
        if self._model is None:
            return

        _log.info("unloading whisper model %s", self.loaded_model_id)

        self._model = None
        self._model_key = None
        self.loaded_model_id = None
        self.loaded_compute_type = None
        self.loaded_device = None

        gc.collect()

        torch = sys.modules.get("torch")
        if torch is None:
            try:
                import torch as torch_module  # noqa: PLC0415

                torch = torch_module
            except ImportError:
                torch = None

        if torch is not None:
            try:
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
                    torch.cuda.ipc_collect()
            except Exception as exc:  # noqa: BLE001 - a broken CUDA state must not block unload
                _log.debug("torch.cuda cleanup failed: %r", exc)

    # -- transcription ---------------------------------------------------------

    def transcribe(
        self,
        audio_path: str,
        *,
        model_id: str = "auto",
        language: str = "auto",
        device: str = "auto",
        compute_type: str | None = None,
        beam_size: int = 5,
        vad_filter: bool = True,
        word_timestamps: bool = True,
        condition_on_previous_text: bool = False,
        initial_prompt: str | None = None,
        duration_seconds: float | None = None,
        allow_download: bool = False,
        token: CancellationToken | None = None,
        on_progress: Callable[[float, float], None] | None = None,
        on_language: Callable[[str, float], None] | None = None,
    ) -> dict[str, Any]:
        """Transcribe ``audio_path`` and return the intermediate JSON shape.

        ``on_progress`` receives (percent, media-seconds-per-wall-second); ``on_language`` fires as
        soon as the language is known, which for ``auto`` is after the first 30 s window.
        """
        source = Path(audio_path)
        if not source.is_file():
            raise WorkerError(
                errors.TRANSCRIPTION_FAILED,
                "추출된 오디오 파일을 찾을 수 없습니다.",
                detail=f"missing audio file {audio_path}",
            )

        model = self.load(
            model_id=model_id,
            device=device,
            compute_type=compute_type,
            allow_download=allow_download,
        )

        if token is not None:
            token.raise_if_cancelled()

        # `initial_prompt` steers the decoder's opening context: orthography, spacing, register.
        #
        # **It reaches the first decoding window and nothing after it.** faster-whisper seeds
        # `all_tokens` with the prompt, then after each window runs
        #
        #     if not condition_on_previous_text or temperature > prompt_reset_on_temperature:
        #         prompt_reset_since = len(all_tokens)
        #
        # and `condition_on_previous_text` is off by default here (ADR-010), so the prompt is
        # dropped from the context the moment the first window finishes. On a two-hour film that
        # is roughly the first 30 seconds *of speech* — with VAD on, the first couple of minutes
        # of wall clock.
        #
        # Measured on a 2h07m Japanese film, whisper-large-v3 / cuda / float16, three runs per
        # variant (2026-08-09): the hint changed 15 of 271 lines, every one of them between 53 s
        # and 138 s, and every change was punctuation (`。` / `、` added). Everything later was
        # inside the run-to-run noise floor, which is 4–7 lines for this model on this GPU. The
        # hallucination counters — back-to-back repeats, longest repeat run, lines repeated five
        # times or more, trailing runaway — came out **identical** with and without it. Whatever
        # this is worth, it is not "동일 단어 중복 반복을 대폭 낮춘다", which is what the comment
        # here used to claim.
        #
        # `vad_parameters={"speech_pad_ms": 400}` restates faster-whisper's own default. It is
        # kept so the value is pinned if that default ever moves, not because it changes anything
        # today: with and against it the transcript differed by one line, below the noise floor.
        if initial_prompt is None:
            if language == "ko":
                initial_prompt = "한국어 자막입니다. 띄어쓰기와 맞춤법을 준수합니다."
            elif language == "ja":
                initial_prompt = "日本語の字幕です。"

        options: dict[str, Any] = {
            "beam_size": max(1, int(beam_size)),
            "vad_filter": bool(vad_filter),
            "vad_parameters": dict(speech_pad_ms=400) if vad_filter else None,
            "word_timestamps": bool(word_timestamps),
            "condition_on_previous_text": bool(condition_on_previous_text),
            "initial_prompt": initial_prompt,
            # None means "detect"; faster-whisper treats the empty string as an error.
            "language": None if not language or language == "auto" else language,
        }

        _log.info("transcribing %s with options %s", source.name, options)
        started = time.monotonic()

        try:
            with _stdout_to_stderr():
                segments_iter, info = model.transcribe(str(source), **options)
        except Exception as exc:  # noqa: BLE001
            raise self._translate_exception(exc, "음성 인식을 시작하지 못했습니다.") from exc

        detected_language = getattr(info, "language", None) or (
            language if language != "auto" else "en"
        )
        language_probability = float(getattr(info, "language_probability", 0.0) or 0.0)
        total_duration = float(
            duration_seconds or getattr(info, "duration", 0.0) or 0.0
        )

        if on_language is not None:
            on_language(detected_language, language_probability)

        segments: list[dict[str, Any]] = []

        try:
            for index, segment in enumerate(segments_iter, start=1):
                if token is not None:
                    token.raise_if_cancelled()

                segments.append(_segment_to_dict(index, segment))

                if on_progress is not None:
                    end = float(getattr(segment, "end", 0.0) or 0.0)
                    elapsed = max(1e-6, time.monotonic() - started)
                    percent = (end / total_duration * 100.0) if total_duration > 0 else 0.0
                    on_progress(min(100.0, max(0.0, percent)), end / elapsed)
        except errors.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001
            raise self._translate_exception(exc, "음성 인식에 실패했습니다.") from exc

        if total_duration <= 0 and segments:
            total_duration = float(segments[-1]["end"])

        if not segments:
            # Not an error the user can act on differently, but it must not masquerade as success:
            # an empty transcript would silently produce an empty subtitle file.
            raise WorkerError(
                errors.TRANSCRIPTION_FAILED,
                "음성을 인식하지 못했습니다. 오디오에 말소리가 없거나 볼륨이 너무 작을 수 있습니다.",
                detail=f"whisper returned no segments for {audio_path}",
            )

        _log.info(
            "transcription finished: %d segments, %.1fs of media in %.1fs",
            len(segments),
            total_duration,
            time.monotonic() - started,
        )

        return {
            "sourceLanguage": detected_language,
            "languageProbability": round(language_probability, 4),
            "durationSeconds": round(total_duration, 3),
            "modelId": model_id,
            "computeType": self.loaded_compute_type,
            "device": self.loaded_device,
            "segments": segments,
        }

    @staticmethod
    def _translate_exception(exc: BaseException, korean: str) -> WorkerError:
        if isinstance(exc, WorkerError):
            return exc
        if is_cuda_oom(exc):
            return WorkerError(
                errors.CUDA_OUT_OF_MEMORY,
                "GPU 메모리가 부족합니다. 더 작은 모델이나 낮은 정밀도로 다시 시도합니다.",
                recoverable=True,
                detail=repr(exc),
            )
        # A bare TRANSCRIPTION_FAILED here is what the user actually saw: "음성 인식을 시작하지
        # 못했습니다" with the real cause (cublas64_12.dll) buried in the log.
        library_error = cuda_library_error(exc)
        if library_error is not None:
            return library_error
        return WorkerError(errors.TRANSCRIPTION_FAILED, korean, detail=repr(exc))


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def _segment_to_dict(index: int, segment: Any) -> dict[str, Any]:
    words: list[dict[str, Any]] = []
    raw_words = getattr(segment, "words", None) or []

    for word in raw_words:
        text = getattr(word, "word", None)
        if text is None:
            continue
        entry: dict[str, Any] = {
            "word": text,
            "start": round(float(getattr(word, "start", 0.0) or 0.0), 3),
            "end": round(float(getattr(word, "end", 0.0) or 0.0), 3),
        }
        probability = getattr(word, "probability", None)
        if probability is not None:
            entry["probability"] = round(float(probability), 4)
        words.append(entry)

    return {
        "id": index,
        "start": round(float(getattr(segment, "start", 0.0) or 0.0), 3),
        "end": round(float(getattr(segment, "end", 0.0) or 0.0), 3),
        "text": (getattr(segment, "text", "") or "").strip(),
        "words": words,
    }


def _resolve_device(device: str) -> str:
    if device and device != "auto":
        return device

    try:
        import ctranslate2  # noqa: PLC0415

        if ctranslate2.get_cuda_device_count() > 0:
            return "cuda"
    except ImportError:
        pass
    except Exception as exc:  # noqa: BLE001
        _log.warning("CUDA probe failed; using CPU: %r", exc)

    return "cpu"


def _default_compute_type(device: str) -> str:
    # int8 on CPU is both faster and the only type CTranslate2 supports everywhere; float16 needs
    # a GPU. Getting this wrong is an immediate hard failure inside CTranslate2, not a slowdown.
    if device != "cuda":
        return "int8"

    # Having a CUDA device is not the same as being able to execute float16 on it. Pascal cards
    # run fp16 at a fraction of their fp32 rate, and this is the branch an "auto" user reaches —
    # the host sends computeType=null and never gets to apply its own recommendation.
    if hardware_detector.lacks_usable_float16():
        _log.info("이 GPU 세대는 float16 연산이 느려 int8로 전환합니다.")
        return "int8"

    return "float16"


def _looks_missing(exc: BaseException) -> bool:
    text = str(exc).lower()
    markers = (
        "no such file",
        "not found",
        "does not exist",
        "couldn't find",
        "could not find",
        "local_files_only",
        "offline",
    )
    return any(marker in text for marker in markers)


class _stdout_to_stderr:
    """Context manager that makes ``sys.stdout`` an alias of stderr.

    Belt and braces: :func:`protocol.install_stdout_guard` already does this process-wide, but
    model loading is the one place where a library is most likely to have captured the original
    stream, so the window is narrowed explicitly here too.
    """

    def __init__(self) -> None:
        self._saved: Any = None

    def __enter__(self) -> None:
        self._saved = sys.stdout
        sys.stdout = sys.stderr

    def __exit__(self, exc_type, exc, tb) -> bool:  # noqa: ANN001
        sys.stdout = self._saved if self._saved is not None else io.StringIO()
        return False


def segments_from_json(payload: dict[str, Any]) -> list[dict[str, Any]]:
    """Segment list out of a transcription payload, tolerating a missing key."""
    segments = payload.get("segments")
    return [s for s in segments if isinstance(s, dict)] if isinstance(segments, list) else []


def iter_text(segments: Iterable[dict[str, Any]]) -> Iterable[str]:
    for segment in segments:
        text = segment.get("text")
        if isinstance(text, str) and text.strip():
            yield text.strip()
