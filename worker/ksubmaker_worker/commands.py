"""Command handlers, including the ``process`` pipeline orchestration.

Pipeline: ``probing -> extractingAudio -> transcribing -> translating -> writingSubtitle``,
honouring ``phase`` (``transcribe`` stops after ASR and checkpoints; ``translate`` resumes from
the checkpoint) and resuming from whatever a previous run already finished.
"""

from __future__ import annotations

import threading
import time
from pathlib import Path
from typing import Any, Callable, Mapping

from . import errors, hardware_detector, protocol
from .batching import Batch, BatchOptions, split_batches, translate_with_retry
from .cancellation import CancellationToken
from .checkpoint import (
    AUDIO,
    AUDIO_SETTINGS_KEY,
    TRANSCRIPTION,
    TRANSCRIPTION_SETTINGS_KEY,
    TRANSLATION_SETTINGS_KEY,
    CheckpointStore,
    missing_ids,
    stale_artifacts,
)
from .errors import CancelledError, WorkerError
from .ffmpeg_service import FfmpegService
from .logging_setup import get_logger
from .model_manager import ModelManager, models_root
from .protocol import Phases, SourceModes, Stages
from .subtitle_postprocessor import FormattingOptions, build_cues, split_segments
from .subtitle_writer import parse_srt, write_subtitle_file
from .transcriber import COMPUTE_DOWNGRADE, Transcriber, is_cuda_oom
from .translator import FakeTranslator, NllbTranslator

_log = get_logger("commands")

#: Default engine ids when settings say "auto".
DEFAULT_WHISPER_MODEL = "whisper-small"
DEFAULT_TRANSLATION_MODEL = "nllb-200-distilled-600M"
#: Gemma, not Qwen: Qwen2.5 answered 41% of a Japanese file in Chinese. See ModelCatalog.
DEFAULT_LLM_MODEL = "gemma-3-4b-it-q4km"

#: How many batches between partial-translation checkpoint writes. Writing every batch would
#: fsync a growing JSON file dozens of times a minute for no benefit.
_CHECKPOINT_EVERY_BATCHES = 3


# ---------------------------------------------------------------------------
# Settings fingerprints
# ---------------------------------------------------------------------------
#
# What each cached artefact depends on. The rule for membership is "would the user expect a
# different result after changing this?" — not "does this touch the stage at all". Performance
# knobs are deliberately excluded: invalidating an hour of ASR because someone nudged the batch
# size would make the checkpoint useless, and `device` / `computeType` are worse still, because
# the CUDA OOM ladder rewrites `computeType` mid-run and every resume would then look stale.
#
# Mirrored by `KSubMaker.Application.Processing.InProcessJobProcessor`; the two must agree, since
# both write the same `job.json`.


def audio_fingerprint(command: Mapping[str, Any]) -> dict[str, Any]:
    """What ``audio.wav`` depends on, beyond the source file itself."""
    return {
        "sourceMode": str(command.get("sourceMode") or SourceModes.AUDIO),
        "audioTrackIndex": _optional_int(command.get("audioTrackIndex")),
    }


def transcription_fingerprint(
    command: Mapping[str, Any], settings: Mapping[str, Any]
) -> dict[str, Any]:
    """What ``transcription.json`` depends on."""
    return {
        "whisperModel": _or_default(settings.get("whisperModel"), DEFAULT_WHISPER_MODEL),
        "language": str(settings.get("language") or "auto"),
        "beamSize": int(settings.get("beamSize", 5) or 5),
        "vadFilter": bool(settings.get("vadFilter", True)),
        "wordTimestamps": bool(settings.get("wordTimestamps", True)),
        "conditionOnPreviousText": bool(settings.get("conditionOnPreviousText", False)),
        "initialPrompt": settings.get("initialPrompt"),
        # Only meaningful in embeddedSubtitle mode, but recorded unconditionally so the shape of
        # the fingerprint does not depend on the mode.
        "subtitleTrackIndex": _optional_int(command.get("subtitleTrackIndex")),
        "subtitleLanguage": str(command.get("subtitleLanguage") or ""),
    }


def translation_fingerprint(settings: Mapping[str, Any]) -> dict[str, Any]:
    """What ``translation.partial.json`` depends on.

    The *resolved* model id, not the raw fields: recording both ``translationModel`` and
    ``llmModel`` would throw away good NLLB output whenever the unused LLM model changed.
    """
    engine = str(settings.get("translationEngine") or "local-translation")
    return {
        "engine": engine,
        "model": _resolve_translation_model(engine, settings),
        "style": str(settings.get("translationStyle") or "natural"),
        "glossary": {str(k): str(v) for k, v in dict(settings.get("glossary") or {}).items()},
    }


def _resolve_translation_model(engine: str, settings: Mapping[str, Any]) -> str:
    if engine == "local-llm":
        return _or_default(settings.get("llmModel"), DEFAULT_LLM_MODEL)
    if engine == "fake":
        return "fake"
    return _or_default(settings.get("translationModel"), DEFAULT_TRANSLATION_MODEL)


def _reached_stage(store: CheckpointStore, stage: str) -> bool:
    """Whether the recorded ``completedStage`` is at or past ``stage``.

    An unknown stage name (a checkpoint from a build with a different pipeline) reads as "not
    reached", which costs a redo rather than trusting a file we cannot place in the order.
    """
    job = store.load_job()
    if not job:
        return False

    recorded = str(job.get("completedStage") or "")
    if recorded not in Stages.ORDER or stage not in Stages.ORDER:
        return False

    return Stages.ORDER.index(recorded) >= Stages.ORDER.index(stage)


def _optional_int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


class CommandHandlers:
    """Holds the long-lived engine instances and executes one command at a time."""

    def __init__(
        self,
        *,
        ffmpeg: FfmpegService | None = None,
        transcriber: Transcriber | None = None,
        translator_factory: Callable[[str, Mapping[str, Any]], Any] | None = None,
        model_manager: ModelManager | None = None,
        models_dir: str | Path | None = None,
    ) -> None:
        self.models_dir = Path(models_dir) if models_dir is not None else models_root()
        self.ffmpeg = ffmpeg or FfmpegService()
        self.transcriber = transcriber or Transcriber(self.models_dir)
        self.model_manager = model_manager or ModelManager(self.models_dir)
        self._translator_factory = translator_factory
        self._translator: Any = None
        self._translator_kind: str | None = None

        # One lock per checkpoint directory, guarding audio extraction.
        #
        # The prefetch lane and the job thread can both want the same job's wav: the host starts
        # file N while the prefetch it launched for file N is still running. Both would write the
        # same audio.wav.tmp and one would win a torn race. Serialising on the directory makes the
        # loser find the finished wav and reuse it, so the work is shared rather than corrupted.
        self._audio_locks: dict[str, threading.Lock] = {}
        self._audio_locks_guard = threading.Lock()

    # -----------------------------------------------------------------------
    # simple commands
    # -----------------------------------------------------------------------

    def hello(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        reported = command.get("protocolVersion")
        compatible, warning = protocol.is_compatible(reported)

        if warning:
            _log.warning("protocol version check: %s", warning)
            # "error" is reserved for an actual major mismatch. A host that simply did not report
            # its version is only unverifiable, not incompatible.
            level = "error" if reported and not compatible else "warn"
            protocol.emit_log(warning, level, request_id=request_id)

        protocol.emit_ack(protocol.Commands.HELLO, request_id)

    def detect_hardware(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        try:
            payload = hardware_detector.detect()
        except Exception as exc:  # noqa: BLE001 - detect() promises never to raise; belt and braces
            _log.error("hardware detection failed: %r", exc)
            payload = {
                "gpus": [],
                "cudaAvailable": False,
                "cudaDeviceDetected": False,
                # Nothing was probed, so nothing is known to be missing. False here would put a
                # "CUDA 라이브러리가 없습니다" warning on a machine that may be perfectly fine.
                "cudaLibrariesAvailable": True,
                "missingCudaLibraries": [],
                "cudaVersion": None,
                "cpuName": None,
                "logicalCores": 0,
                "totalRamBytes": 0,
                "availableRamBytes": 0,
                "warnings": ["하드웨어 정보를 확인하지 못했습니다."],
            }
        protocol.emit_hardware(payload, request_id=request_id)

    def probe(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        video_path = command.get("videoPath")

        if not isinstance(video_path, str) or not video_path:
            protocol.emit_error(
                code=errors.PROTOCOL_ERROR,
                message="영상 경로가 지정되지 않았습니다.",
                request_id=request_id,
            )
            return

        try:
            payload = self.ffmpeg.probe(video_path)
        except WorkerError as exc:
            payload = {
                "videoPath": video_path,
                "durationSeconds": 0.0,
                "audioTracks": [],
                "subtitleTracks": [],
                "error": exc.message,
            }

        protocol.emit_probe_result(payload, request_id=request_id)

    # -----------------------------------------------------------------------
    # models
    # -----------------------------------------------------------------------

    def list_models(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        try:
            models = self.model_manager.list_models()
        except OSError as exc:
            _log.warning("model listing failed: %r", exc)
            models = []
        protocol.emit_model_list(models, request_id=request_id)

    def download_model(self, command: Mapping[str, Any], token: CancellationToken) -> None:
        request_id = command.get("requestId")
        model_id = str(command.get("modelId") or "")
        repository_id = str(command.get("repositoryId") or "")
        files = command.get("files") or []
        target_dir = command.get("targetDir")

        if not model_id or not repository_id or not isinstance(files, list):
            protocol.emit_error(
                code=errors.PROTOCOL_ERROR,
                message="모델 다운로드 요청 형식이 올바르지 않습니다.",
                request_id=request_id,
                detail=f"modelId={model_id!r} repositoryId={repository_id!r} files={files!r}",
            )
            return

        last_emitted = [0.0]

        def on_progress(received: int, total: int, current: str, speed: float) -> None:
            now = time.monotonic()
            # Throttle: a 3 GB download at 256 KiB chunks would otherwise emit 12k events.
            if now - last_emitted[0] < 0.5 and received < total:
                return
            last_emitted[0] = now
            protocol.emit_download_progress(
                model_id=model_id,
                received_bytes=received,
                total_bytes=total,
                current_file=current,
                speed_bytes_per_second=speed,
                request_id=request_id,
            )

        try:
            result = self.model_manager.download(
                model_id=model_id,
                repository_id=repository_id,
                files=[str(f) for f in files],
                target_dir=target_dir,
                token=token,
                on_progress=on_progress,
            )
        except WorkerError as exc:
            protocol.emit_error(
                code=exc.code,
                message=exc.message,
                recoverable=exc.recoverable,
                detail=exc.detail,
                request_id=request_id,
            )
            return

        protocol.emit_download_completed(request_id=request_id, **result)

    def cancel_download(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        model_id = str(command.get("modelId") or "")

        cancelled = self.model_manager.cancel_download(model_id)
        protocol.emit_ack(protocol.Commands.CANCEL_DOWNLOAD, request_id)

        if not cancelled:
            protocol.emit_log(
                f"진행 중인 다운로드를 찾지 못했습니다: {model_id}", "warn", request_id=request_id
            )

    def verify_model(self, command: Mapping[str, Any], token: CancellationToken) -> None:
        request_id = command.get("requestId")
        model_id = str(command.get("modelId") or "")

        try:
            result = self.model_manager.verify(model_id, command.get("targetDir"), token)
        except CancelledError:
            protocol.emit_cancelled(request_id=request_id)
            return
        except WorkerError as exc:
            protocol.emit_error(
                code=exc.code,
                message=exc.message,
                recoverable=exc.recoverable,
                detail=exc.detail,
                request_id=request_id,
            )
            return

        protocol.emit_model_list([result], request_id=request_id)

    def delete_model(self, command: Mapping[str, Any]) -> None:
        request_id = command.get("requestId")
        model_id = str(command.get("modelId") or "")

        try:
            result = self.model_manager.delete(model_id, command.get("targetDir"))
        except WorkerError as exc:
            protocol.emit_error(
                code=exc.code,
                message=exc.message,
                recoverable=exc.recoverable,
                detail=exc.detail,
                request_id=request_id,
            )
            return

        protocol.emit_model_list([result], request_id=request_id)

    # -----------------------------------------------------------------------
    # process
    # -----------------------------------------------------------------------

    def process(self, command: Mapping[str, Any], token: CancellationToken) -> None:
        """Run the pipeline for one job, emitting protocol events as it goes.

        Never raises: every outcome becomes exactly one terminal event (``completed``, ``error``
        or ``cancelled``).
        """
        request_id = command.get("requestId")
        job_id = command.get("jobId")
        started_at = time.monotonic()

        try:
            self._process_inner(command, token, started_at)
        except CancelledError:
            _log.info("job %s cancelled", job_id)
            protocol.emit_cancelled(request_id=request_id, job_id=job_id)
        except WorkerError as exc:
            _log.error("job %s failed: %s (%s)", job_id, exc.code, exc.detail)
            protocol.emit_error(
                code=exc.code,
                message=exc.message,
                recoverable=exc.recoverable,
                detail=exc.detail,
                request_id=request_id,
                job_id=job_id,
            )
        except Exception as exc:  # noqa: BLE001 - the loop must survive any engine bug
            _log.exception("job %s crashed", job_id)
            protocol.emit_error(
                code=errors.WORKER_CRASHED,
                message="AI 작업 중 예기치 않은 오류가 발생했습니다.",
                recoverable=True,
                detail=repr(exc),
                request_id=request_id,
                job_id=job_id,
            )

    def _audio_lock(self, checkpoint_dir: str | Path) -> threading.Lock:
        """The extraction lock for one checkpoint directory, created on first use."""
        key = str(Path(checkpoint_dir).resolve()) if checkpoint_dir else str(checkpoint_dir)
        with self._audio_locks_guard:
            lock = self._audio_locks.get(key)
            if lock is None:
                lock = threading.Lock()
                self._audio_locks[key] = lock
            return lock

    def _audio_is_reusable(
        self, store: CheckpointStore, fingerprint: Mapping[str, Any]
    ) -> bool:
        """Whether the wav on disk was made from this source under these audio settings."""
        if not store.has_audio() or not _reached_stage(store, Stages.EXTRACTING_AUDIO):
            return False

        recorded = (store.load_job() or {}).get(AUDIO_SETTINGS_KEY)

        # A record with no fingerprint was written by an older build. Same call as everywhere else:
        # trust it rather than re-demux a two-hour film on a guess.
        return recorded is None or dict(recorded) == dict(fingerprint)

    # -- prefetch (v1.3) -------------------------------------------------------

    def extract_audio(self, command: Mapping[str, Any], token: CancellationToken) -> None:
        """Extract one job's audio ahead of the job itself, on the prefetch lane.

        Writes exactly what :meth:`_transcribe` would have written — the wav plus the checkpoint
        stanza that marks the extraction stage done — so there is no "use the prefetch" path to go
        wrong: the job simply finds the work already done and skips it.

        Never raises. A prefetch is an optimisation, and every way it can fail (unreadable file, no
        audio track, a race with the user deleting the job) has to cost nothing more than the time
        it would have saved.
        """
        request_id = command.get("requestId")
        job_id = str(command.get("jobId") or "")
        started_at = time.monotonic()

        try:
            extracted = self._extract_audio_inner(command, token)
        except CancelledError:
            _log.info("prefetch for %s cancelled", job_id)
            protocol.emit_cancelled(request_id=request_id, job_id=job_id or None)
            return
        except WorkerError as exc:
            _log.info("prefetch for %s failed: %s (%s)", job_id, exc.code, exc.detail)
            protocol.emit_error(
                code=exc.code,
                message=exc.message,
                # Always recoverable: the job will extract the audio itself when it gets there.
                recoverable=True,
                detail=exc.detail,
                request_id=request_id,
                job_id=job_id or None,
            )
            return
        except Exception as exc:  # noqa: BLE001 - the lane must survive anything
            _log.exception("prefetch for %s crashed", job_id)
            protocol.emit_error(
                code=errors.WORKER_CRASHED,
                message="음성 추출 중 예기치 않은 오류가 발생했습니다.",
                recoverable=True,
                detail=repr(exc),
                request_id=request_id,
                job_id=job_id or None,
            )
            return

        protocol.emit_completed(
            output_path="",
            cue_count=0,
            request_id=request_id,
            job_id=job_id or None,
            elapsed_seconds=time.monotonic() - started_at,
            # skipped carries its usual meaning — "the work was not needed". The host uses it to
            # tell a real extraction apart from a wav an earlier run had already left behind.
            skipped=not extracted,
        )

    def _extract_audio_inner(
        self, command: Mapping[str, Any], token: CancellationToken
    ) -> bool:
        """Returns True when ffmpeg actually ran, False when the wav was already usable."""
        job_id = str(command.get("jobId") or "")
        video_path = str(command.get("videoPath") or "")
        checkpoint_dir = str(command.get("checkpointDir") or "")
        source_mode = str(command.get("sourceMode") or SourceModes.AUDIO)

        if not video_path or not checkpoint_dir:
            raise WorkerError(
                errors.PROTOCOL_ERROR,
                "음성 추출 요청에 필요한 경로가 누락되었습니다.",
                detail=f"videoPath={video_path!r} checkpointDir={checkpoint_dir!r}",
            )

        if source_mode != SourceModes.AUDIO:
            # An embedded-subtitle job never reads audio, so there is nothing worth doing early.
            _log.debug("prefetch skipped for %s: sourceMode=%s", job_id, source_mode)
            return False

        if not Path(video_path).exists():
            raise WorkerError(
                errors.VIDEO_NOT_FOUND,
                f"영상 파일을 찾을 수 없습니다: {Path(video_path).name}",
                detail=f"missing {video_path}",
            )

        store = CheckpointStore(checkpoint_dir, job_id, video_path)

        # Same order the job uses. A checkpoint left over from a different cut of this file has to
        # go before anything is reused, and clear() takes the stale wav with it.
        if store.load_job() is not None and not store.is_valid_for_source():
            _log.info("prefetch for %s: source changed; clearing the checkpoint", job_id)
            store.clear()

        fingerprint = audio_fingerprint(command)

        if self._audio_is_reusable(store, fingerprint):
            _log.info("prefetch for %s: audio already present", job_id)
            return False

        token.raise_if_cancelled()

        probed = self.ffmpeg.probe(video_path)
        if not probed.get("audioTracks"):
            raise WorkerError(
                errors.AUDIO_TRACK_NOT_FOUND,
                f"영상에 오디오 트랙이 없습니다: {Path(video_path).name}",
                detail=f"probe found no audio streams in {video_path}",
            )

        audio_path = store.audio_path()

        # Held across the extraction so the job thread cannot start a second ffmpeg onto the same
        # temp file. If the job got here first we come out of the wait with its wav already done.
        with self._audio_lock(store.directory):
            if self._audio_is_reusable(store, fingerprint):
                _log.info("prefetch for %s: the job extracted it first", job_id)
                return False

            token.raise_if_cancelled()
            _log.info("prefetching audio for %s -> %s", job_id, audio_path)

            # No progress events: this file is not the row the user is watching, and reporting
            # against a job the queue has not started would move the wrong progress bar.
            self.ffmpeg.extract_audio(
                video_path,
                str(audio_path),
                audio_track_index=command.get("audioTrackIndex"),
                duration_seconds=float(probed.get("durationSeconds") or 0.0),
                token=token,
            )

            self._record_extracted_audio(store, audio_path, fingerprint)
            return True

    def _record_extracted_audio(
        self, store: CheckpointStore, audio_path: Path, fingerprint: Mapping[str, Any]
    ) -> None:
        """Mark the extraction stage done without losing anything already recorded."""
        existing = store.load_job() or {}
        recorded_stage = str(existing.get("completedStage") or "")

        # Never walk the recorded stage backwards. A job that was already transcribed and is being
        # re-run can be prefetched again, and rewriting extractingAudio over transcribing would
        # throw away the knowledge that the expensive stage is done.
        stage = (
            recorded_stage
            if recorded_stage in Stages.ORDER
            and Stages.ORDER.index(recorded_stage) > Stages.ORDER.index(Stages.EXTRACTING_AUDIO)
            else Stages.EXTRACTING_AUDIO
        )

        store.save_job(
            completed_stage=stage,
            audio_path=str(audio_path),
            detected_language=existing.get("detectedLanguage"),
            whisper_model=existing.get("whisperModel"),
            audio_settings=fingerprint,
            transcription_settings=existing.get(TRANSCRIPTION_SETTINGS_KEY),
            translation_settings=existing.get(TRANSLATION_SETTINGS_KEY),
        )

    def _process_inner(
        self, command: Mapping[str, Any], token: CancellationToken, started_at: float
    ) -> None:
        request_id = command.get("requestId")
        job_id = str(command.get("jobId") or "")
        video_path = str(command.get("videoPath") or "")
        output_path = str(command.get("outputPath") or "")
        checkpoint_dir = str(command.get("checkpointDir") or "")
        settings: Mapping[str, Any] = command.get("settings") or {}
        source_mode = str(command.get("sourceMode") or SourceModes.AUDIO)
        phase = str(command.get("phase") or Phases.FULL)
        resume = bool(command.get("resume", True))

        if phase not in Phases.ALL:
            raise WorkerError(
                errors.PROTOCOL_ERROR,
                f"알 수 없는 처리 단계입니다: {phase}",
                detail=f"unknown phase {phase!r}",
            )

        if not video_path or not output_path or not checkpoint_dir:
            raise WorkerError(
                errors.PROTOCOL_ERROR,
                "작업 요청에 필요한 경로가 누락되었습니다.",
                detail=f"videoPath={video_path!r} outputPath={output_path!r} checkpointDir={checkpoint_dir!r}",
            )

        if not Path(video_path).exists():
            raise WorkerError(
                errors.VIDEO_NOT_FOUND,
                f"영상 파일을 찾을 수 없습니다: {Path(video_path).name}",
                detail=f"missing {video_path}",
            )

        store = CheckpointStore(checkpoint_dir, job_id, video_path)

        fingerprints = {
            "audio_settings": audio_fingerprint(command),
            "transcription_settings": transcription_fingerprint(command, settings),
            "translation_settings": translation_fingerprint(settings),
        }

        if resume and not store.is_valid_for_source():
            if store.load_job() is not None:
                _log.info("source changed since the checkpoint; starting over")
                protocol.emit_log(
                    "원본 파일이 변경되어 처음부터 다시 처리합니다.",
                    "warn",
                    request_id=request_id,
                    job_id=job_id,
                )
            store.clear()
            store.clear_audio()
        elif not resume:
            store.clear()
            store.clear_audio()
        else:
            self._discard_stale_artifacts(
                store, fingerprints, request_id=request_id, job_id=job_id
            )

        transcription = store.load_transcription() if resume else None
        partial = store.load_partial_translation() if resume else {}

        # The host uses this to say "이어서 진행합니다" and to move its own progress bar forward.
        resumed_from = Stages.TRANSLATING if transcription is not None else None
        protocol.emit_started(request_id=request_id, job_id=job_id, resumed_from_stage=resumed_from)

        emit = _Emitter(request_id, job_id)

        # -- stage 1: probe ---------------------------------------------------
        token.raise_if_cancelled()
        emit.progress(Stages.PROBING, 0.0)
        probed = self.ffmpeg.probe(video_path)
        duration = float(probed.get("durationSeconds") or 0.0)
        emit.progress(Stages.PROBING, 100.0)
        emit.stage_completed(Stages.PROBING)

        # -- stages 2 & 3: transcript ----------------------------------------
        if transcription is None:
            if phase == Phases.TRANSLATE:
                raise WorkerError(
                    errors.TRANSCRIPTION_FAILED,
                    "이어서 번역할 음성 인식 결과가 없습니다. 먼저 음성 인식을 실행하세요.",
                    detail=f"translate phase requested but no transcription checkpoint in {checkpoint_dir}",
                )

            if source_mode == SourceModes.EMBEDDED_SUBTITLE:
                transcription = self._from_embedded_subtitle(
                    command, emit, token, duration=duration
                )
            else:
                transcription = self._transcribe(
                    command,
                    emit,
                    token,
                    store=store,
                    probed=probed,
                    settings=settings,
                    fingerprints=fingerprints,
                )

            store.save_transcription(transcription)
            store.save_job(
                completed_stage=Stages.TRANSCRIBING,
                audio_path=str(store.audio_path()) if source_mode == SourceModes.AUDIO else None,
                detected_language=transcription.get("sourceLanguage"),
                whisper_model=transcription.get("modelId"),
                **fingerprints,
            )
        else:
            _log.info("reusing the transcription checkpoint (%d segments)", len(transcription.get("segments", [])))
            emit.progress(Stages.EXTRACTING_AUDIO, 100.0)
            emit.stage_completed(Stages.EXTRACTING_AUDIO)
            language = transcription.get("sourceLanguage")
            if language:
                protocol.emit_language_detected(
                    language=str(language),
                    probability=float(transcription.get("languageProbability") or 0.0),
                    request_id=request_id,
                    job_id=job_id,
                )
            emit.progress(Stages.TRANSCRIBING, 100.0)

        emit.stage_completed(Stages.TRANSCRIBING)

        segments = [s for s in transcription.get("segments", []) if isinstance(s, dict)]
        if not segments:
            raise WorkerError(
                errors.TRANSCRIPTION_FAILED,
                "음성 인식 결과가 비어 있습니다.",
                detail="transcription contains no segments",
            )

        if phase == Phases.TRANSCRIBE:
            # Strategy B's first pass: stop here, leaving a checkpoint the translate pass resumes.
            _log.info("phase=transcribe: stopping after ASR with a checkpoint")
            protocol.emit_completed(
                output_path=output_path,
                cue_count=len(segments),
                request_id=request_id,
                job_id=job_id,
                source_language=str(transcription.get("sourceLanguage") or ""),
                whisper_model=str(transcription.get("modelId") or ""),
                elapsed_seconds=time.monotonic() - started_at,
                skipped=True,
            )
            return

        # -- stage 4: translate ----------------------------------------------
        translations = self._translate(
            segments=segments,
            transcription=transcription,
            settings=settings,
            emit=emit,
            token=token,
            store=store,
            already=partial,
        )
        emit.stage_completed(Stages.TRANSLATING)

        # -- stage 5: write ---------------------------------------------------
        token.raise_if_cancelled()
        emit.progress(Stages.WRITING_SUBTITLE, 0.0)

        options = FormattingOptions.from_settings(settings)
        cues = build_cues(segments, translations, options)

        if not cues:
            raise WorkerError(
                errors.OUTPUT_WRITE_FAILED,
                "저장할 자막이 없습니다. 번역 결과가 비어 있습니다.",
                detail=f"build_cues produced nothing from {len(segments)} segments",
            )

        conflict_policy = str(settings.get("outputConflictPolicy") or "skip")
        written, reason = write_subtitle_file(cues, output_path, conflict_policy)

        store.save_finalization(
            output_path=written,
            cue_count=len(cues),
            skipped=written is None,
            reason=reason,
        )
        store.save_job(
            completed_stage=Stages.WRITING_SUBTITLE,
            audio_path=str(store.audio_path()) if source_mode == SourceModes.AUDIO else None,
            detected_language=transcription.get("sourceLanguage"),
            whisper_model=transcription.get("modelId"),
            **fingerprints,
        )

        emit.progress(Stages.WRITING_SUBTITLE, 100.0)
        emit.stage_completed(Stages.WRITING_SUBTITLE)

        if reason:
            protocol.emit_log(reason, "info", request_id=request_id, job_id=job_id)

        protocol.emit_completed(
            output_path=written or output_path,
            cue_count=len(cues),
            request_id=request_id,
            job_id=job_id,
            source_language=str(transcription.get("sourceLanguage") or ""),
            whisper_model=str(transcription.get("modelId") or ""),
            translation_engine=str(settings.get("translationEngine") or ""),
            translation_model=self._translation_model_id(settings),
            elapsed_seconds=time.monotonic() - started_at,
            skipped=written is None,
        )

    def _discard_stale_artifacts(
        self,
        store: CheckpointStore,
        fingerprints: Mapping[str, Mapping[str, Any]],
        *,
        request_id: Any,
        job_id: str,
    ) -> None:
        """Drop the cached artefacts whose settings changed since they were produced.

        This is what makes 재시도 mean "redo it with what I just changed". Without it a job that
        was 80% translated by NLLB kept those lines verbatim after the user switched to the LLM
        engine, and the resulting file was half one engine and half the other — with nothing on
        screen to say so.
        """
        stale = stale_artifacts(
            store.load_job(),
            audio=fingerprints["audio_settings"],
            transcription=fingerprints["transcription_settings"],
            translation=fingerprints["translation_settings"],
        )
        if not stale:
            return

        # Coarsest first: clearing the transcript also clears the translation, so the narrower
        # calls would be redundant work (and a confusing second log line).
        if AUDIO in stale:
            store.clear_audio()
            store.clear_transcription()
            message = "음성 추출 설정이 바뀌어 음성 인식부터 다시 실행합니다."
        elif TRANSCRIPTION in stale:
            store.clear_transcription()
            message = "음성 인식 설정이 바뀌어 음성 인식부터 다시 실행합니다."
        else:
            store.clear_translation()
            message = "번역 설정이 바뀌어 번역을 처음부터 다시 실행합니다."

        # Before anything runs, so a failure partway through the redo still leaves a record that
        # matches the current settings and the next attempt resumes instead of starting over.
        store.refresh_settings(**fingerprints)

        _log.info("discarding stale artefacts %s", sorted(stale))
        protocol.emit_log(message, "info", request_id=request_id, job_id=job_id)

    # -- transcription --------------------------------------------------------

    def _transcribe(
        self,
        command: Mapping[str, Any],
        emit: "_Emitter",
        token: CancellationToken,
        *,
        store: CheckpointStore,
        probed: Mapping[str, Any],
        settings: Mapping[str, Any],
        fingerprints: Mapping[str, Mapping[str, Any]],
    ) -> dict[str, Any]:
        video_path = str(command.get("videoPath"))
        duration = float(probed.get("durationSeconds") or 0.0)

        if not probed.get("audioTracks"):
            raise WorkerError(
                errors.AUDIO_TRACK_NOT_FOUND,
                f"영상에 오디오 트랙이 없습니다: {Path(video_path).name}",
                detail=f"probe found no audio streams in {video_path}",
            )

        # -- extract ---------------------------------------------------------
        audio_path = store.audio_path()

        # Reuse the wav when a previous run — or the prefetch lane — got as far as extracting it.
        # Everything that could invalidate it has already been checked by the caller: the source
        # fingerprint, and the audio fingerprint that clears this file when the track selection
        # changed. So an ASR failure — the common reason to be back here — no longer re-demuxes a
        # two-hour film.
        #
        # The lock is what makes the prefetch safe. The host can start this job while the prefetch
        # it launched for the same file is still running; without serialising here both would drive
        # ffmpeg at the same audio.wav.tmp. Whoever waits finds the finished wav and reuses it.
        audio_fp = fingerprints["audio_settings"]

        # Announce the stage **before** taking the lock.
        #
        # The prefetch lane can already be extracting this very file, and on a large source that
        # wait runs into minutes. With the first progress event inside the lock nothing was sent
        # while waiting, so the row froze at the 2.00% that finishing 단계 Probing leaves behind and
        # read as a hang — the reported symptom was exactly "2%에서 멈춘다". The work was fine; only
        # the reporting was missing.
        emit.progress(Stages.EXTRACTING_AUDIO, 0.0)

        audio_lock = self._audio_lock(store.directory)

        if not audio_lock.acquire(blocking=False):
            # Say *why* it is waiting. "음성 추출 중 0%" that never moves is the thing that looked
            # broken; naming the reason turns it into an obviously-temporary state.
            _log.info("waiting for another extraction of %s to finish", audio_path)
            emit.progress(
                Stages.EXTRACTING_AUDIO,
                0.0,
                message="다른 곳에서 같은 파일의 음성을 추출하는 중입니다. 끝나면 그 결과를 재사용합니다.",
            )
            audio_lock.acquire()

        try:
            if self._audio_is_reusable(store, audio_fp):
                _log.info("reusing the extracted audio at %s", audio_path)
                emit.progress(Stages.EXTRACTING_AUDIO, 100.0)
            else:
                emit.progress(Stages.EXTRACTING_AUDIO, 0.0)

                self.ffmpeg.extract_audio(
                    video_path,
                    str(audio_path),
                    audio_track_index=command.get("audioTrackIndex"),
                    duration_seconds=duration,
                    token=token,
                    progress=lambda pct: emit.progress(Stages.EXTRACTING_AUDIO, pct),
                )

                emit.progress(Stages.EXTRACTING_AUDIO, 100.0)

                # Recorded before ASR runs, so a crash during transcription still leaves a
                # checkpoint that says the wav is complete. Without this save the reuse above can
                # never fire.
                store.save_job(
                    completed_stage=Stages.EXTRACTING_AUDIO,
                    audio_path=str(audio_path),
                    **fingerprints,
                )
        finally:
            # Released on cancellation and on an ffmpeg failure too. Leaking it would wedge every
            # later attempt at this file behind a lock nobody holds a reference to any more.
            audio_lock.release()

        emit.stage_completed(Stages.EXTRACTING_AUDIO)

        # -- transcribe with OOM recovery -------------------------------------
        emit.progress(Stages.TRANSCRIBING, 0.0)

        model_id = _or_default(settings.get("whisperModel"), DEFAULT_WHISPER_MODEL)
        compute_type = settings.get("computeType") or None
        device = str(settings.get("device") or "auto")

        def run(current_compute: str | None) -> dict[str, Any]:
            return self.transcriber.transcribe(
                str(audio_path),
                model_id=model_id,
                language=str(settings.get("language") or "auto"),
                device=device,
                compute_type=current_compute,
                beam_size=int(settings.get("beamSize", 5) or 5),
                vad_filter=bool(settings.get("vadFilter", True)),
                word_timestamps=bool(settings.get("wordTimestamps", True)),
                condition_on_previous_text=bool(settings.get("conditionOnPreviousText", False)),
                # 호스트/UI에서 전송한 custom initialPrompt 수신 지원
                initial_prompt=settings.get("initialPrompt"),
                duration_seconds=duration,
                token=token,
                on_progress=lambda pct, speed: emit.progress(
                    Stages.TRANSCRIBING, pct, speed=speed
                ),
                on_language=lambda lang, prob: emit.language(lang, prob),
            )

        result = self._with_oom_recovery(
            run,
            compute_type=compute_type,
            emit=emit,
            settings=settings,
            what="음성 인식",
        )

        emit.progress(Stages.TRANSCRIBING, 100.0)

        # Split over-long segments now, while word timestamps still exist.
        result["segments"] = split_segments(
            result.get("segments", []),
            max_chars=90,
            max_duration_seconds=float(settings.get("maxCueDurationSeconds", 7.0) or 7.0),
        )
        return result

    def _from_embedded_subtitle(
        self,
        command: Mapping[str, Any],
        emit: "_Emitter",
        token: CancellationToken,
        *,
        duration: float,
    ) -> dict[str, Any]:
        """Use an existing subtitle track as the source text instead of running ASR."""
        video_path = str(command.get("videoPath"))
        index = int(command.get("subtitleTrackIndex") or 0)

        emit.progress(Stages.EXTRACTING_AUDIO, 0.0)
        text = self.ffmpeg.extract_subtitle_track(video_path, index, token=token)
        emit.progress(Stages.EXTRACTING_AUDIO, 100.0)
        emit.stage_completed(Stages.EXTRACTING_AUDIO)

        segments = parse_srt(text)
        if not segments:
            raise WorkerError(
                errors.TRANSCRIPTION_FAILED,
                "내장 자막 트랙에서 읽을 수 있는 자막을 찾지 못했습니다.",
                detail=f"parse_srt produced nothing for track {index} of {video_path}",
            )

        emit.progress(Stages.TRANSCRIBING, 100.0)

        # No detected language: the track's own tag is the best available hint, and the host may
        # not have sent one. Fall back to English, which is what the NLLB mapper defaults to.
        language = str(command.get("subtitleLanguage") or "en")
        emit.language(language, 1.0)

        return {
            "sourceLanguage": language,
            "languageProbability": 1.0,
            "durationSeconds": duration or (segments[-1]["end"] if segments else 0.0),
            "modelId": "embeddedSubtitle",
            "segments": segments,
        }

    # -- translation ----------------------------------------------------------

    def _translate(
        self,
        *,
        segments: list[dict[str, Any]],
        transcription: Mapping[str, Any],
        settings: Mapping[str, Any],
        emit: "_Emitter",
        token: CancellationToken,
        store: CheckpointStore,
        already: Mapping[int, str],
    ) -> dict[int, str]:
        emit.progress(Stages.TRANSLATING, 0.0)

        translations: dict[int, str] = {int(k): v for k, v in already.items() if str(v).strip()}
        
        source_language = str(transcription.get("sourceLanguage") or "en")
        skip_same_lang = bool(settings.get("skipTranslationForSameLanguage", True))
        # 원본 음성 언어가 한국어(ko)이고 동일 언어 번역 생략 옵션이 활성화된 경우 번역 패스
        if skip_same_lang and source_language == "ko":
            _log.info("원본 언어가 한국어(ko)이고 동일 언어 번역 생략 옵션이 활성화되어 있어 번역 단계를 생략합니다.")
            for segment in segments:
                seg_id = int(segment.get("id", 0) or 0)
                translations[seg_id] = (segment.get("text") or "").strip()
            emit.progress(Stages.TRANSLATING, 100.0)
            return translations

        pending_ids = set(missing_ids(segments, translations))

        if not pending_ids:
            _log.info("every segment is already translated; skipping the translate stage")
            emit.progress(Stages.TRANSLATING, 100.0)
            return translations

        if translations:
            _log.info(
                "resuming translation: %d done, %d remaining", len(translations), len(pending_ids)
            )

        pending = [s for s in segments if int(s.get("id", 0) or 0) in pending_ids]

        engine_kind = str(settings.get("translationEngine") or "local-translation")
        self._ensure_translator(engine_kind, settings)

        source_language = str(transcription.get("sourceLanguage") or "en")
        style = str(settings.get("translationStyle") or "natural")
        glossary = dict(settings.get("glossary") or {})
        options = BatchOptions.from_settings(settings)

        batches = split_batches(pending, options)
        total = len(batches)
        done_before = len(translations)
        target_total = done_before + len(pending)

        for position, batch in enumerate(batches, start=1):
            token.raise_if_cancelled()

            def run(
                items: list[dict[str, Any]],
                context: list[dict[str, Any]],
                attempt: int,
            ) -> list[dict[str, Any]]:
                # Resolved per call, not captured: the OOM ladder may have replaced the engine
                # with a reloaded one, and a stale reference here would call an unloaded model.
                return _call_engine(
                    self._require_translator(),
                    items,
                    context=context,
                    attempt=attempt,
                    source_language=source_language,
                    style=style,
                    glossary=glossary,
                    token=token,
                )

            result = self._translate_batch_with_recovery(
                batch, run, emit=emit, settings=settings, engine_kind=engine_kind
            )
            translations.update(result)

            if position % _CHECKPOINT_EVERY_BATCHES == 0 or position == total:
                store.save_partial_translation(translations)

            percent = (len(translations) / target_total * 100.0) if target_total else 100.0
            emit.progress(
                Stages.TRANSLATING,
                min(100.0, percent),
                message=f"{position}/{total} 배치 번역 완료",
            )

        store.save_partial_translation(translations)
        emit.progress(Stages.TRANSLATING, 100.0)
        return translations

    def _translate_batch_with_recovery(
        self,
        batch: Batch,
        run: Any,
        *,
        emit: "_Emitter",
        settings: Mapping[str, Any],
        engine_kind: str,
    ) -> dict[int, str]:
        """One batch, with the CUDA-OOM ladder applied around it.

        Halving is a *split*, never a truncation: the second half becomes another sub-batch that
        the same retry translates. Dropping it would silently lose cues from the finished file.
        """
        parts: list[Batch] = [batch]

        def degraded(ids: tuple[int, ...]) -> None:
            # Surfaced to the host as a log event rather than swallowed into the worker's own file:
            # the user is entitled to know that part of this subtitle is still in the source
            # language, and the job is going to finish successfully either way.
            emit.log(f"번역되지 않은 자막 {len(ids)}개는 원문을 그대로 사용했습니다.", "warn")

        def attempt(_compute: str | None) -> dict[int, str]:
            collected: dict[int, str] = {}
            for part in parts:
                collected.update(translate_with_retry(part, run, on_degraded=degraded))
            return collected

        def halve() -> bool:
            split: list[Batch] = []
            for part in parts:
                split.extend(split_batch_in_half(part))

            if len(split) == len(parts):
                return False

            parts[:] = split
            return True

        return self._with_oom_recovery(
            attempt,
            compute_type=settings.get("computeType") or None,
            emit=emit,
            settings=settings,
            what="번역",
            on_halve_batch=halve,
            reload_engine=lambda compute: self._reload_translator(engine_kind, settings, compute),
        )

    def _require_translator(self) -> Any:
        """The currently loaded engine.

        Raises rather than silently returning None: a None here would surface much later as an
        unhelpful AttributeError in the middle of a batch.
        """
        if self._translator is None:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "번역 엔진이 준비되지 않았습니다.",
                detail="translation requested before the engine was loaded",
            )
        return self._translator

    def _ensure_translator(self, kind: str, settings: Mapping[str, Any]) -> Any:
        if self._translator is not None and self._translator_kind == kind:
            return self._translator

        if self._translator is not None:
            _unload(self._translator)
            self._translator = None

        self._translator = self._build_translator(kind, settings)
        self._translator_kind = kind
        return self._translator

    def _build_translator(self, kind: str, settings: Mapping[str, Any], compute: str | None = None) -> Any:
        if self._translator_factory is not None:
            return self._translator_factory(kind, settings)

        if kind == "fake":
            return FakeTranslator()

        if kind == "local-llm":
            from .llm_translator import LlmTranslator  # noqa: PLC0415 - keeps requests lazy

            engine = LlmTranslator(models_dir=self.models_dir)
            engine.load(
                model_id=_or_default(settings.get("llmModel"), DEFAULT_LLM_MODEL),
                free_vram_bytes=hardware_detector.largest_free_vram_bytes(),
            )
            return engine

        engine = NllbTranslator(self.models_dir)
        engine.load(
            model_id=_or_default(settings.get("translationModel"), DEFAULT_TRANSLATION_MODEL),
            device=str(settings.get("device") or "auto"),
            compute_type=compute or settings.get("computeType") or None,
        )
        return engine

    def _reload_translator(self, kind: str, settings: Mapping[str, Any], compute: str | None) -> None:
        if self._translator is not None:
            _unload(self._translator)
        self._translator = self._build_translator(kind, settings, compute)
        self._translator_kind = kind

    def _translation_model_id(self, settings: Mapping[str, Any]) -> str:
        kind = str(settings.get("translationEngine") or "local-translation")
        return _resolve_translation_model(kind, settings)

    # -----------------------------------------------------------------------
    # CUDA OOM recovery
    # -----------------------------------------------------------------------

    def _with_oom_recovery(
        self,
        run: Callable[[str | None], Any],
        *,
        compute_type: str | None,
        emit: "_Emitter",
        settings: Mapping[str, Any],
        what: str,
        on_halve_batch: Callable[[], bool] | None = None,
        reload_engine: Callable[[str | None], None] | None = None,
    ) -> Any:
        """Run ``run``, and on CUDA OOM apply the recovery ladder before failing.

        Order, exactly as specified: free memory -> halve the batch size -> downgrade the compute
        type (float16 -> int8_float16 -> int8) -> advise a smaller model -> retry **once**.
        The single retry is deliberate: a machine that OOMs twice is not going to succeed on a
        third identical attempt, and the user is waiting.
        """
        current = compute_type

        try:
            return run(current)
        except WorkerError as exc:
            # CancelledError is a WorkerError too, and its code is never CUDA_OUT_OF_MEMORY, so a
            # cancelled job propagates instead of being "recovered" from.
            if exc.code != errors.CUDA_OUT_OF_MEMORY:
                raise
            first = exc
        except Exception as exc:  # noqa: BLE001 - a raw ctranslate2/torch OOM
            if not is_cuda_oom(exc):
                raise
            first = WorkerError(
                errors.CUDA_OUT_OF_MEMORY,
                "GPU 메모리가 부족합니다.",
                recoverable=True,
                detail=repr(exc),
            )

        _log.warning("%s hit CUDA OOM; starting recovery", what)
        emit.log(f"GPU 메모리가 부족하여 {what} 설정을 낮추고 다시 시도합니다.", "warn")

        # 1. free whatever is resident.
        self.transcriber.unload()
        if self._translator is not None:
            _unload(self._translator)

        # 2. halve the batch size.
        if on_halve_batch is not None and on_halve_batch():
            emit.log("배치 크기를 절반으로 줄였습니다.", "info")

        # 3. downgrade the compute type.
        downgraded = COMPUTE_DOWNGRADE.get((current or "float16").lower())
        if downgraded is not None:
            current = downgraded
            emit.log(f"연산 정밀도를 {current}(으)로 낮췄습니다.", "info")
        else:
            emit.log("이미 가장 낮은 정밀도입니다.", "info")

        # 4. tell the user what to change if this retry also fails.
        emit.log(
            "GPU 메모리가 계속 부족하면 설정에서 더 작은 모델을 선택하세요.",
            "warn",
        )

        if reload_engine is not None:
            try:
                reload_engine(current)
            except WorkerError as exc:
                _log.error("engine reload after OOM failed: %r", exc)
                raise first from exc

        # 5. retry exactly once.
        try:
            return run(current)
        except WorkerError as exc:
            if exc.code == errors.CUDA_OUT_OF_MEMORY:
                raise WorkerError(
                    errors.CUDA_OUT_OF_MEMORY,
                    "GPU 메모리가 부족합니다. 설정에서 더 작은 모델을 선택하거나 "
                    "정밀도를 int8로 낮춘 뒤 다시 시도하세요.",
                    recoverable=True,
                    detail=exc.detail,
                ) from exc
            raise
        except Exception as exc:  # noqa: BLE001
            if is_cuda_oom(exc):
                raise WorkerError(
                    errors.CUDA_OUT_OF_MEMORY,
                    "GPU 메모리가 부족합니다. 설정에서 더 작은 모델을 선택하거나 "
                    "정밀도를 int8로 낮춘 뒤 다시 시도하세요.",
                    recoverable=True,
                    detail=repr(exc),
                ) from exc
            raise

    # -----------------------------------------------------------------------

    def shutdown(self) -> None:
        """Release every model. Called on ``shutdown`` and on a fatal signal."""
        try:
            self.transcriber.unload()
        except Exception as exc:  # noqa: BLE001
            _log.warning("transcriber unload failed: %r", exc)

        if self._translator is not None:
            _unload(self._translator)
            self._translator = None
            self._translator_kind = None


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


class _Emitter:
    """Binds requestId/jobId once so the pipeline code stays readable."""

    def __init__(self, request_id: str | None, job_id: str | None) -> None:
        self.request_id = request_id
        self.job_id = job_id

    def progress(
        self,
        stage: str,
        percent: float,
        *,
        speed: float | None = None,
        message: str | None = None,
    ) -> None:
        protocol.emit_progress(
            stage=stage,
            stage_progress=percent,
            request_id=self.request_id,
            job_id=self.job_id,
            speed=speed,
            message=message,
        )

    def stage_completed(self, stage: str) -> None:
        protocol.emit_stage_completed(
            stage=stage, request_id=self.request_id, job_id=self.job_id
        )

    def language(self, language: str, probability: float) -> None:
        protocol.emit_language_detected(
            language=language,
            probability=probability,
            request_id=self.request_id,
            job_id=self.job_id,
        )

    def log(self, message: str, level: str = "info") -> None:
        protocol.emit_log(message, level, request_id=self.request_id, job_id=self.job_id)


def _call_engine(
    engine: Any,
    items: list[dict[str, Any]],
    *,
    context: list[dict[str, Any]],
    attempt: int,
    source_language: str,
    style: str,
    glossary: dict[str, str],
    token: CancellationToken,
) -> list[dict[str, Any]]:
    """Call whichever engine we have, passing context only to engines that accept it."""
    kwargs: dict[str, Any] = {
        "source_language": source_language,
        "style": style,
        "glossary": glossary,
        "token": token,
    }

    try:
        import inspect  # noqa: PLC0415

        parameters = inspect.signature(engine.translate_items).parameters
        if "context" in parameters:
            kwargs["context"] = context
        if "attempt" in parameters:
            kwargs["attempt"] = attempt
    except (TypeError, ValueError):
        # A callable without an introspectable signature: pass the common arguments only.
        pass

    return engine.translate_items(items, **kwargs)


def split_batch_in_half(batch: Batch) -> list[Batch]:
    """Split a batch into two smaller ones so a retry asks the model for less at a time.

    Returns ``[batch]`` unchanged when there is nothing left to split — a single segment cannot be
    made smaller, and pretending otherwise would loop forever.
    """
    if len(batch.segments) < 2:
        return [batch]

    cut = len(batch.segments) // 2
    first = Batch(index=batch.index, segments=batch.segments[:cut], context=batch.context)
    # The tail's context is the head it now follows, which keeps the LLM's continuity intact.
    second = Batch(
        index=batch.index,
        segments=batch.segments[cut:],
        context=batch.segments[max(0, cut - 3) : cut],
    )
    return [first, second]


def _unload(engine: Any) -> None:
    unload = getattr(engine, "unload", None)
    if callable(unload):
        try:
            unload()
        except Exception as exc:  # noqa: BLE001 - unload is best-effort cleanup
            _log.warning("engine unload failed: %r", exc)


def _or_default(value: Any, default: str) -> str:
    text = str(value or "").strip()
    return default if not text or text == "auto" else text
