"""The stdio wire protocol.

Mirrors ``src/KSubMaker.WorkerProtocol/ProtocolConstants.cs``, ``Commands.cs`` and ``Events.cs``.
Field names on the wire are camelCase.

The single rule that shapes this module: **stdout carries protocol JSON and nothing else**.
:func:`install_stdout_guard` moves the real stdout out of reach and points ``sys.stdout`` at
stderr, so a library that prints a progress bar corrupts the log instead of the channel.
"""

from __future__ import annotations

import json
import math
import sys
import threading
from typing import IO, Any, Final

#: 1.1 added ``settings.outputConflictPolicy`` and ``process.subtitleLanguage``. Both are optional
#: and both fall back to the 1.0 behaviour, so a 1.0 host still works against a 1.1 worker.
#:
#: 1.2 added ``cudaDeviceDetected``, ``cudaLibrariesAvailable`` and ``missingCudaLibraries`` to the
#: ``hardware`` event. ``cudaAvailable`` keeps its name and becomes the conjunction of the first
#: two, so a 1.1 host that reads only that field gets the safer answer rather than a wrong one.
#:
#: 1.3 added the ``extractAudio`` command — the one command that may run *while* a ``process`` job
#: is running. Everything else is serialised because two concurrent CUDA jobs would fight over the
#: same VRAM; this one only shells out to ffmpeg. A 1.2 worker rejects it with ``PROTOCOL_ERROR``
#: and the host falls back to extracting inside the job, which is what it always did.
#:
#: 1.4 added ``settings.initialPrompt``. The worker had read the field since before any host sent
#: it; null keeps the built-in per-language hint, which is what every 1.3 host effectively asked
#: for, so the fingerprint recorded by a 1.3 run still matches a 1.4 one.
#:
#: 1.5 added ``sourceMode: "externalSubtitle"`` and ``process.subtitlePath``. A 1.4 worker rejects
#: the mode, and the host's own default policy never selects it, so nothing changes for a host that
#: does not ask for it.
#: 1.6 added ``settings.saveSourceSubtitle``. Off by default and it only writes an extra file,
#: so a 1.5 host that never sends it behaves exactly as before.
PROTOCOL_VERSION: Final = "1.6"


class Commands:
    """Host -> worker message names."""

    HELLO: Final = "hello"
    DETECT_HARDWARE: Final = "detectHardware"
    PROBE: Final = "probe"
    PROCESS: Final = "process"
    #: v1.3. Extract one file's audio ahead of the job that will need it. Unlike every other
    #: command this may run alongside PROCESS: it shells out to ffmpeg and touches no GPU.
    EXTRACT_AUDIO: Final = "extractAudio"
    CANCEL: Final = "cancel"
    LIST_MODELS: Final = "listModels"
    DOWNLOAD_MODEL: Final = "downloadModel"
    CANCEL_DOWNLOAD: Final = "cancelDownload"
    VERIFY_MODEL: Final = "verifyModel"
    DELETE_MODEL: Final = "deleteModel"
    SHUTDOWN: Final = "shutdown"

    ALL: Final[frozenset[str]] = frozenset(
        {
            HELLO,
            DETECT_HARDWARE,
            PROBE,
            PROCESS,
            EXTRACT_AUDIO,
            CANCEL,
            LIST_MODELS,
            DOWNLOAD_MODEL,
            CANCEL_DOWNLOAD,
            VERIFY_MODEL,
            DELETE_MODEL,
            SHUTDOWN,
        }
    )


class Events:
    """Worker -> host message names."""

    READY: Final = "ready"
    ACK: Final = "ack"
    STARTED: Final = "started"
    PROGRESS: Final = "progress"
    LANGUAGE_DETECTED: Final = "languageDetected"
    STAGE_COMPLETED: Final = "stageCompleted"
    COMPLETED: Final = "completed"
    ERROR: Final = "error"
    CANCELLED: Final = "cancelled"
    LOG: Final = "log"
    HARDWARE: Final = "hardware"
    PROBE_RESULT: Final = "probeResult"
    MODEL_LIST: Final = "modelList"
    DOWNLOAD_PROGRESS: Final = "downloadProgress"
    DOWNLOAD_COMPLETED: Final = "downloadCompleted"
    GOODBYE: Final = "goodbye"


class Stages:
    """Stage names on the wire; lower-camel-cased ``JobStage``."""

    PROBING: Final = "probing"
    EXTRACTING_AUDIO: Final = "extractingAudio"
    TRANSCRIBING: Final = "transcribing"
    TRANSLATING: Final = "translating"
    WRITING_SUBTITLE: Final = "writingSubtitle"

    ORDER: Final[tuple[str, ...]] = (
        PROBING,
        EXTRACTING_AUDIO,
        TRANSCRIBING,
        TRANSLATING,
        WRITING_SUBTITLE,
    )


class SourceModes:
    AUDIO: Final = "audio"
    EMBEDDED_SUBTITLE: Final = "embeddedSubtitle"

    #: 1.5. Translate a sidecar file (``movie.ja.srt``) instead of running ASR. The host picks
    #: which sidecar and sends its path; the worker never globs the directory itself, so the
    #: ranking rule lives in exactly one language.
    EXTERNAL_SUBTITLE: Final = "externalSubtitle"


class Phases:
    FULL: Final = "full"
    TRANSCRIBE: Final = "transcribe"
    TRANSLATE: Final = "translate"

    ALL: Final[frozenset[str]] = frozenset({FULL, TRANSCRIBE, TRANSLATE})


#: Wall-clock share of each stage. Copied from ``KSubMaker.Domain.Jobs.ProgressCalculator``.
STAGE_WEIGHTS: Final[dict[str, float]] = {
    Stages.PROBING: 0.02,
    Stages.EXTRACTING_AUDIO: 0.08,
    Stages.TRANSCRIBING: 0.55,
    Stages.TRANSLATING: 0.32,
    Stages.WRITING_SUBTITLE: 0.03,
}

#: Capabilities advertised in the ``ready`` event.
CAPABILITIES: Final[tuple[str, ...]] = ("asr", "translate", "llm", "probe", "hardware", "models")


def overall_progress(stage: str, stage_progress: float) -> float:
    """Overall 0-100 for ``stage`` at ``stage_progress`` (0-100).

    Same arithmetic as ``ProgressCalculator.Overall`` including the two-decimal rounding, so the
    host's progress bar never jumps when it recomputes the value locally.
    """
    if stage not in STAGE_WEIGHTS:
        return 0.0

    clamped = min(100.0, max(0.0, stage_progress)) / 100.0
    completed = 0.0
    for name in Stages.ORDER:
        if name == stage:
            break
        completed += STAGE_WEIGHTS[name]

    value = (completed + STAGE_WEIGHTS[stage] * clamped) * 100.0
    return round(min(100.0, max(0.0, value)), 2)


# ---------------------------------------------------------------------------
# stdout channel
# ---------------------------------------------------------------------------

_emit_lock = threading.Lock()

#: The real stdout, captured before anything gets a chance to reassign ``sys.stdout``.
_channel: IO[str] = sys.stdout
_guard_installed = False


def install_stdout_guard() -> IO[str]:
    """Take ownership of the real stdout and point ``sys.stdout`` at stderr.

    After this call a stray ``print`` anywhere in the process (a model loader, a progress bar, a
    deprecation notice) lands on stderr where it is harmless. Idempotent.
    """
    global _channel, _guard_installed

    if not _guard_installed:
        _channel = sys.stdout
        sys.stdout = sys.stderr
        _guard_installed = True

    return _channel


def set_channel(stream: IO[str]) -> None:
    """Redirect protocol output. Tests use this; production code calls it never."""
    global _channel
    with _emit_lock:
        _channel = stream


def get_channel() -> IO[str]:
    return _channel


def _dumps(event: Any) -> str:
    """Compact JSON, strictly valid.

    ``allow_nan=False`` matters: Python happily writes bare ``NaN``/``Infinity``, which are not
    JSON and which System.Text.Json rejects outright — one NaN in a ``speed`` field would turn the
    whole event into an unparseable line on the host.
    """
    return json.dumps(
        event, ensure_ascii=False, separators=(",", ":"), default=str, allow_nan=False
    )


def _sanitize(value: Any) -> Any:
    """Replace non-finite floats with None, recursively."""
    if isinstance(value, float):
        return None if math.isnan(value) or math.isinf(value) else value
    if isinstance(value, dict):
        return {key: _sanitize(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_sanitize(item) for item in value]
    return value


def emit(event: dict[str, Any]) -> None:
    """Write exactly one compact JSON line to the protocol channel and flush it.

    Serialisation happens before anything is written, so a failure can never leave a half-written
    line on the channel. A non-finite number is repaired and the event still goes out; anything
    else unserialisable becomes a minimal error event, because an event that silently vanished
    would leave the host waiting for a reply that never arrives.
    """
    try:
        line = _dumps(event)
    except ValueError as exc:
        # Almost always a NaN/Infinity that leaked out of a division. Repair rather than drop:
        # losing a `completed` event costs the user a whole job.
        print(f"[protocol] repairing a non-finite value: {exc!r}", file=sys.stderr, flush=True)
        try:
            line = _dumps(_sanitize(event))
        except (TypeError, ValueError) as repair_exc:  # pragma: no cover - defensive
            line = _fallback_line(event, repair_exc)
    except TypeError as exc:  # pragma: no cover - default=str catches nearly everything
        line = _fallback_line(event, exc)

    _write_line(line)


def _fallback_line(event: dict[str, Any], exc: BaseException) -> str:
    print(f"[protocol] event serialisation failed: {exc!r}", file=sys.stderr, flush=True)
    return json.dumps(
        {
            "type": Events.ERROR,
            "code": "PROTOCOL_ERROR",
            "message": "이벤트를 전송하지 못했습니다.",
            "recoverable": False,
            "detail": f"unserialisable {event.get('type', 'event')!r}: {exc!r}"[:500],
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


def _write_line(line: str) -> None:
    with _emit_lock:
        try:
            _channel.write(line + "\n")
            _channel.flush()
        except (OSError, ValueError) as exc:  # closed pipe: the host is gone
            print(f"[protocol] stdout write failed: {exc!r}", file=sys.stderr, flush=True)


def _base(event_type: str, request_id: str | None, job_id: str | None) -> dict[str, Any]:
    event: dict[str, Any] = {"type": event_type}
    if request_id is not None:
        event["requestId"] = request_id
    if job_id is not None:
        event["jobId"] = job_id
    return event


# ---------------------------------------------------------------------------
# typed emitters
# ---------------------------------------------------------------------------


def emit_ready(
    *,
    worker_version: str,
    python_version: str,
    capabilities: tuple[str, ...] | list[str] = CAPABILITIES,
    request_id: str | None = None,
) -> None:
    event = _base(Events.READY, request_id, None)
    event["protocolVersion"] = PROTOCOL_VERSION
    event["workerVersion"] = worker_version
    event["pythonVersion"] = python_version
    event["capabilities"] = list(capabilities)
    emit(event)


def emit_ack(command: str, request_id: str | None, job_id: str | None = None) -> None:
    event = _base(Events.ACK, request_id, job_id)
    event["command"] = command
    emit(event)


def emit_started(
    *,
    request_id: str | None,
    job_id: str | None,
    resumed_from_stage: str | None = None,
) -> None:
    event = _base(Events.STARTED, request_id, job_id)
    if resumed_from_stage:
        event["resumedFromStage"] = resumed_from_stage
    emit(event)


def emit_progress(
    *,
    stage: str,
    stage_progress: float,
    request_id: str | None = None,
    job_id: str | None = None,
    speed: float | None = None,
    message: str | None = None,
) -> None:
    clamped = min(100.0, max(0.0, stage_progress))
    event = _base(Events.PROGRESS, request_id, job_id)
    event["stage"] = stage
    event["stageProgress"] = round(clamped, 2)
    event["overallProgress"] = overall_progress(stage, clamped)
    if speed is not None:
        event["speed"] = round(speed, 3)
    if message:
        event["message"] = message
    emit(event)


def emit_language_detected(
    *,
    language: str,
    probability: float,
    request_id: str | None = None,
    job_id: str | None = None,
) -> None:
    event = _base(Events.LANGUAGE_DETECTED, request_id, job_id)
    event["language"] = language
    event["probability"] = round(float(probability), 4)
    emit(event)


def emit_stage_completed(
    *, stage: str, request_id: str | None = None, job_id: str | None = None
) -> None:
    event = _base(Events.STAGE_COMPLETED, request_id, job_id)
    event["stage"] = stage
    emit(event)


def emit_completed(
    *,
    output_path: str,
    cue_count: int,
    request_id: str | None = None,
    job_id: str | None = None,
    source_language: str | None = None,
    whisper_model: str | None = None,
    translation_engine: str | None = None,
    translation_model: str | None = None,
    elapsed_seconds: float = 0.0,
    skipped: bool = False,
) -> None:
    event = _base(Events.COMPLETED, request_id, job_id)
    event["outputPath"] = output_path
    event["cueCount"] = int(cue_count)
    if source_language:
        event["sourceLanguage"] = source_language
    if whisper_model:
        event["whisperModel"] = whisper_model
    if translation_engine:
        event["translationEngine"] = translation_engine
    if translation_model:
        event["translationModel"] = translation_model
    event["elapsedSeconds"] = round(float(elapsed_seconds), 3)
    event["skipped"] = bool(skipped)
    emit(event)


def emit_error(
    *,
    code: str,
    message: str,
    recoverable: bool = False,
    detail: str | None = None,
    request_id: str | None = None,
    job_id: str | None = None,
) -> None:
    event = _base(Events.ERROR, request_id, job_id)
    event["code"] = code
    event["message"] = message
    event["recoverable"] = bool(recoverable)
    if detail:
        # Bounded: a multi-megabyte traceback would stall the host's line reader.
        event["detail"] = detail[:4000]
    emit(event)


def emit_cancelled(*, request_id: str | None = None, job_id: str | None = None) -> None:
    emit(_base(Events.CANCELLED, request_id, job_id))


def emit_log(
    message: str,
    level: str = "info",
    *,
    request_id: str | None = None,
    job_id: str | None = None,
) -> None:
    event = _base(Events.LOG, request_id, job_id)
    event["level"] = level
    event["message"] = message
    emit(event)


def emit_hardware(payload: dict[str, Any], *, request_id: str | None = None) -> None:
    event = _base(Events.HARDWARE, request_id, None)
    event.update(payload)
    event["type"] = Events.HARDWARE
    emit(event)


def emit_probe_result(payload: dict[str, Any], *, request_id: str | None = None) -> None:
    event = _base(Events.PROBE_RESULT, request_id, None)
    event.update(payload)
    event["type"] = Events.PROBE_RESULT
    emit(event)


def emit_model_list(models: list[dict[str, Any]], *, request_id: str | None = None) -> None:
    event = _base(Events.MODEL_LIST, request_id, None)
    event["models"] = models
    emit(event)


def emit_download_progress(
    *,
    model_id: str,
    received_bytes: int,
    total_bytes: int,
    current_file: str | None = None,
    speed_bytes_per_second: float = 0.0,
    request_id: str | None = None,
) -> None:
    event = _base(Events.DOWNLOAD_PROGRESS, request_id, None)
    event["modelId"] = model_id
    event["receivedBytes"] = int(received_bytes)
    event["totalBytes"] = int(total_bytes)
    percent = (received_bytes / total_bytes * 100.0) if total_bytes > 0 else 0.0
    event["percent"] = round(min(100.0, max(0.0, percent)), 2)
    if current_file:
        event["currentFile"] = current_file
    event["speedBytesPerSecond"] = round(float(speed_bytes_per_second), 2)
    emit(event)


def emit_download_completed(
    *,
    model_id: str,
    path: str | None = None,
    verified: bool = False,
    total_bytes: int = 0,
    cancelled: bool = False,
    request_id: str | None = None,
) -> None:
    event = _base(Events.DOWNLOAD_COMPLETED, request_id, None)
    event["modelId"] = model_id
    if path:
        event["path"] = path
    event["verified"] = bool(verified)
    event["totalBytes"] = int(total_bytes)
    event["cancelled"] = bool(cancelled)
    emit(event)


def emit_goodbye(*, request_id: str | None = None) -> None:
    emit(_base(Events.GOODBYE, request_id, None))


def is_compatible(host_version: str | None) -> tuple[bool, str | None]:
    """Same rule as ``WorkerProtocolSerializer.IsCompatible``: major must match, minor may drift."""
    if not host_version or not host_version.strip():
        return False, "호스트가 프로토콜 버전을 보고하지 않았습니다."

    host_major = host_version.split(".")[0]
    own_major = PROTOCOL_VERSION.split(".")[0]

    if host_major != own_major:
        return False, (
            f"프로토콜 버전이 호환되지 않습니다. 호스트 {host_version}, Worker {PROTOCOL_VERSION}."
        )

    if host_version != PROTOCOL_VERSION:
        return True, (
            f"프로토콜 부 버전이 다릅니다. 호스트 {host_version}, Worker {PROTOCOL_VERSION}."
        )

    return True, None
