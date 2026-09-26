"""The ``process`` orchestration: stages, phases, resume and CUDA OOM recovery.

Everything expensive is faked: no ffmpeg child, no Whisper, no translation model.
"""

from __future__ import annotations

import json
import threading
import time
import wave
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import errors, protocol
from ksubmaker_worker.cancellation import CancellationToken
from ksubmaker_worker.checkpoint import CheckpointStore
from ksubmaker_worker.commands import CommandHandlers, split_batch_in_half
from ksubmaker_worker.batching import Batch
from ksubmaker_worker.protocol import Stages

# ---------------------------------------------------------------------------
# fakes
# ---------------------------------------------------------------------------


class FakeFfmpeg:
    """Stands in for ffmpeg: reports a probe and writes a token wav file."""

    def __init__(self, *, audio_tracks: int = 1, duration: float = 30.0, subtitle_text: str | None = None) -> None:
        self.audio_tracks = audio_tracks
        self.duration = duration
        self.subtitle_text = subtitle_text
        self.extracted: list[str] = []
        #: One entry per extraction: the (duration_seconds, trim_seconds) it was asked for.
        self.lengths: list[tuple[float | None, float | None]] = []

    def probe(self, path: str) -> dict[str, Any]:
        return {
            "videoPath": path,
            "durationSeconds": self.duration,
            "audioTracks": [
                {"index": i, "language": "eng", "codec": "aac", "channels": 2, "isDefault": i == 0}
                for i in range(self.audio_tracks)
            ],
            "subtitleTracks": [] if self.subtitle_text is None else [{"index": 0, "codec": "subrip"}],
            "container": "matroska",
        }

    def extract_audio(self, video_path, output_path, *, audio_track_index=None, duration_seconds=None, trim_seconds=None, token=None, progress=None):  # noqa: ANN001, ANN201
        self.lengths.append((duration_seconds, trim_seconds))
        Path(output_path).parent.mkdir(parents=True, exist_ok=True)
        with wave.open(output_path, "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(16_000)
            handle.writeframes(b"\x00\x00" * 160)

        if progress is not None:
            for pct in (25.0, 50.0, 100.0):
                progress(pct)

        self.extracted.append(output_path)
        return output_path

    def extract_subtitle_track(self, video_path, index=0, *, token=None):  # noqa: ANN001, ANN201
        if self.subtitle_text is None:
            raise errors.WorkerError(errors.FFMPEG_FAILED, "자막 트랙 없음")
        return self.subtitle_text


class FakeTranscriber:
    """Returns a canned transcript; can be told to raise CUDA OOM a set number of times."""

    def __init__(self, *, oom_times: int = 0, segments: list[dict[str, Any]] | None = None) -> None:
        self.oom_times = oom_times
        self.calls: list[dict[str, Any]] = []
        self.unload_count = 0
        #: 감지 언어. 원어 자막 파일 이름이 이것을 따르므로 테스트가 바꿀 수 있어야 한다.
        self.language = "en"
        self._segments = segments or [
            {"id": 1, "start": 0.0, "end": 2.0, "text": "Hello there.", "words": []},
            {"id": 2, "start": 2.5, "end": 5.0, "text": "General Kenobi.", "words": []},
            {"id": 3, "start": 5.5, "end": 9.0, "text": "You are a bold one.", "words": []},
        ]

    def transcribe(self, audio_path, **kwargs):  # noqa: ANN001, ANN201
        self.calls.append(kwargs)

        if self.oom_times > 0:
            self.oom_times -= 1
            raise errors.WorkerError(
                errors.CUDA_OUT_OF_MEMORY, "GPU 메모리 부족", recoverable=True, detail="fake OOM"
            )

        on_language = kwargs.get("on_language")
        if on_language is not None:
            on_language(self.language, 0.99)

        on_progress = kwargs.get("on_progress")
        if on_progress is not None:
            on_progress(50.0, 3.0)
            on_progress(100.0, 3.0)

        return {
            "sourceLanguage": self.language,
            "languageProbability": 0.99,
            "durationSeconds": 9.0,
            "modelId": kwargs.get("model_id", "whisper-small"),
            "segments": [dict(s) for s in self._segments],
        }

    def unload(self) -> None:
        self.unload_count += 1


class FakeEngine:
    """Translation engine that prefixes each cue and can fail on demand."""

    def __init__(self, *, oom_times: int = 0) -> None:
        self.oom_times = oom_times
        self.batches: list[list[int]] = []
        self.unload_count = 0

    def translate_items(self, items, *, source_language="en", style="natural", glossary=None, token=None, **_: Any):  # noqa: ANN001, ANN201
        self.batches.append([i["id"] for i in items])

        if self.oom_times > 0:
            self.oom_times -= 1
            raise errors.WorkerError(
                errors.CUDA_OUT_OF_MEMORY, "GPU 메모리 부족", recoverable=True, detail="fake OOM"
            )

        return [{"id": i["id"], "translation": f"번역 {i['text']}"} for i in items]

    def unload(self) -> None:
        self.unload_count += 1


def _handlers(
    ffmpeg: FakeFfmpeg | None = None,
    transcriber: FakeTranscriber | None = None,
    engine: FakeEngine | None = None,
    models_dir: Path | None = None,
) -> tuple[CommandHandlers, FakeFfmpeg, FakeTranscriber, FakeEngine]:
    ffmpeg = ffmpeg or FakeFfmpeg()
    transcriber = transcriber or FakeTranscriber()
    engine = engine or FakeEngine()

    handlers = CommandHandlers(
        ffmpeg=ffmpeg,  # type: ignore[arg-type]
        transcriber=transcriber,  # type: ignore[arg-type]
        translator_factory=lambda _kind, _settings: engine,
        models_dir=models_dir,
    )
    return handlers, ffmpeg, transcriber, engine


SETTINGS: dict[str, Any] = {
    "language": "auto",
    "whisperModel": "whisper-small",
    "device": "cpu",
    "beamSize": 5,
    "translationEngine": "fake",
    "translationStyle": "natural",
    "glossary": {},
    "batchMaxItems": 30,
    "maxLinesPerCue": 2,
    "maxCharsPerLine": 22,
}


def _command(tmp_path: Path, **overrides: Any) -> dict[str, Any]:
    video = tmp_path / "movie.mkv"
    if not video.exists():
        video.write_bytes(b"pretend this is a video" * 50)

    command: dict[str, Any] = {
        "command": "process",
        "requestId": "req-1",
        "jobId": "job-1",
        "videoPath": str(video),
        "outputPath": str(tmp_path / "movie.ko.srt"),
        "checkpointDir": str(tmp_path / "cache" / "job-1"),
        "settings": dict(SETTINGS),
        "sourceMode": "audio",
        "phase": "full",
        "resume": True,
    }
    command.update(overrides)
    return command


# ---------------------------------------------------------------------------
# happy path
# ---------------------------------------------------------------------------


def test_full_pipeline_emits_the_expected_event_sequence(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    types = [event["type"] for event in channel.events()]
    assert types[0] == "started"
    assert types[-1] == "completed"

    stages = [event["stage"] for event in channel.of_type("stageCompleted")]
    assert stages == [
        Stages.PROBING,
        Stages.EXTRACTING_AUDIO,
        Stages.TRANSCRIBING,
        Stages.TRANSLATING,
        Stages.WRITING_SUBTITLE,
    ]

    assert channel.first("languageDetected")["language"] == "en"


def test_the_output_file_is_written(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    completed = channel.first("completed")
    assert completed is not None
    assert completed["cueCount"] >= 1
    assert completed["skipped"] is False

    output = Path(completed["outputPath"])
    assert output.is_file()

    text = output.read_text(encoding="utf-8-sig")
    assert "-->" in text
    assert "번역" in text


def test_progress_is_monotonic_and_reaches_one_hundred(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    overall = [event["overallProgress"] for event in channel.of_type("progress")]
    assert overall == sorted(overall)
    assert overall[-1] == pytest.approx(100.0)
    assert all(0.0 <= value <= 100.0 for value in overall)


def test_every_event_echoes_the_request_and_job_id(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    for event in channel.events():
        assert event["requestId"] == "req-1"
        assert event["jobId"] == "job-1"


def test_completed_reports_the_engine_metadata(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    completed = channel.first("completed")
    assert completed["sourceLanguage"] == "en"
    assert completed["whisperModel"] == "whisper-small"
    assert completed["translationEngine"] == "fake"


# ---------------------------------------------------------------------------
# failures
# ---------------------------------------------------------------------------


def test_missing_video_reports_video_not_found(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    command = _command(tmp_path, videoPath=str(tmp_path / "gone.mkv"))
    handlers.process(command, CancellationToken("t"))

    error = channel.first("error")
    assert error is not None and error["code"] == errors.VIDEO_NOT_FOUND
    assert error["recoverable"] is False


def test_no_audio_track_reports_audio_track_not_found(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers(ffmpeg=FakeFfmpeg(audio_tracks=0))
    handlers.process(_command(tmp_path), CancellationToken("t"))

    assert channel.first("error")["code"] == errors.AUDIO_TRACK_NOT_FOUND


def test_an_unknown_phase_is_a_protocol_error(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path, phase="teleport"), CancellationToken("t"))

    assert channel.first("error")["code"] == errors.PROTOCOL_ERROR


def test_missing_paths_are_a_protocol_error(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path, outputPath=""), CancellationToken("t"))

    assert channel.first("error")["code"] == errors.PROTOCOL_ERROR


def test_an_unexpected_engine_crash_becomes_worker_crashed(tmp_path: Path, channel) -> None:
    class Exploding(FakeTranscriber):
        def transcribe(self, audio_path, **kwargs):  # noqa: ANN001, ANN201
            raise ZeroDivisionError("this should never happen")

    handlers, _, _, _ = _handlers(transcriber=Exploding())
    handlers.process(_command(tmp_path), CancellationToken("t"))

    error = channel.first("error")
    assert error["code"] == errors.WORKER_CRASHED
    assert error["recoverable"] is True


def test_cancellation_produces_a_cancelled_event_not_an_error(tmp_path: Path, channel) -> None:
    token = CancellationToken("t")

    class CancellingTranscriber(FakeTranscriber):
        def transcribe(self, audio_path, **kwargs):  # noqa: ANN001, ANN201
            token.cancel()
            token.raise_if_cancelled()

    handlers, _, _, _ = _handlers(transcriber=CancellingTranscriber())
    handlers.process(_command(tmp_path), token)

    assert channel.first("cancelled") is not None
    assert channel.first("error") is None
    assert channel.first("completed") is None


# ---------------------------------------------------------------------------
# phases
# ---------------------------------------------------------------------------


def test_transcribe_phase_stops_after_asr_and_checkpoints(tmp_path: Path, channel) -> None:
    handlers, _, _, engine = _handlers()
    command = _command(tmp_path, phase="transcribe")
    handlers.process(command, CancellationToken("t"))

    completed = channel.first("completed")
    assert completed is not None and completed["skipped"] is True
    assert engine.batches == []
    assert not Path(command["outputPath"]).exists()

    store = CheckpointStore(command["checkpointDir"], "job-1", command["videoPath"])
    assert store.load_transcription() is not None


def test_translate_phase_resumes_from_the_checkpoint(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, _ = _handlers()

    # Pass one: transcribe only.
    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    assert len(transcriber.calls) == 1

    # Pass two: translate, which must not re-run ASR.
    handlers.process(_command(tmp_path, phase="translate"), CancellationToken("t"))

    assert len(transcriber.calls) == 1
    completed = [e for e in channel.of_type("completed") if not e["skipped"]]
    assert completed and Path(completed[-1]["outputPath"]).is_file()


def test_translate_phase_without_a_checkpoint_fails_clearly(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path, phase="translate"), CancellationToken("t"))

    error = channel.first("error")
    assert error["code"] == errors.TRANSCRIPTION_FAILED
    assert "음성 인식" in error["message"]


# ---------------------------------------------------------------------------
# lines the engine will not translate
# ---------------------------------------------------------------------------


class BlankingEngine(FakeEngine):
    """Returns an empty translation for the listed ids, deterministically and forever.

    Exactly what NLLB does with a Japanese ``♪`` cue, and — before this behaviour existed — enough
    to fail a whole job three identical requests later.
    """

    def __init__(self, *blank_ids: int) -> None:
        super().__init__()
        self.blank_ids = set(blank_ids)

    def translate_items(self, items, *, source_language="en", style="natural", glossary=None, token=None, **_: Any):  # noqa: ANN001, ANN201
        self.batches.append([i["id"] for i in items])
        return [
            {"id": i["id"], "translation": "" if i["id"] in self.blank_ids else f"번역 {i['text']}"}
            for i in items
        ]


def test_a_symbol_only_cue_never_reaches_the_engine_and_still_reaches_the_file(
    tmp_path: Path, channel
) -> None:
    transcriber = FakeTranscriber(
        segments=[
            {"id": 1, "start": 0.0, "end": 2.0, "text": "♪", "words": []},
            {"id": 2, "start": 2.5, "end": 5.0, "text": "こんにちは", "words": []},
            {"id": 3, "start": 5.5, "end": 9.0, "text": "！？", "words": []},
        ]
    )
    handlers, _, _, engine = _handlers(transcriber=transcriber)
    # Cue merging off, so one segment stays one cue and the count below means what it says.
    command = _command(tmp_path, settings={**SETTINGS, "mergeShortCues": False})

    handlers.process(command, CancellationToken("t"))

    assert engine.batches == [[2]], "only the cue with words in it is sent to the model"

    text = Path(command["outputPath"]).read_text(encoding="utf-8-sig")
    for expected in ("♪", "번역 こんにちは", "！？"):
        assert expected in text
    assert channel.first("completed")["cueCount"] == 3


def test_a_deterministically_blank_cue_degrades_and_the_job_still_completes(
    tmp_path: Path, channel
) -> None:
    handlers, _, _, engine = _handlers(engine=BlankingEngine(2))
    command = _command(tmp_path)

    handlers.process(command, CancellationToken("t"))

    assert channel.first("error") is None, "one untranslatable line must not fail the job"
    assert channel.first("completed") is not None

    # Asked once for the whole batch, once for the straggler, and then stopped: the answer was
    # byte-identical, so a third request could only waste time.
    assert engine.batches == [[1, 2, 3], [2]]

    text = Path(command["outputPath"]).read_text(encoding="utf-8-sig")
    assert "General Kenobi." in text, "the source text stands in for the cue that would not translate"
    assert "번역 Hello there." in text


def test_the_degraded_cue_count_is_reported_to_the_host(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers(engine=BlankingEngine(2))

    handlers.process(_command(tmp_path), CancellationToken("t"))

    logs = [e for e in channel.of_type("log") if "원문을 그대로" in e["message"]]
    assert logs, "the user has to be told part of the file is still in the source language"
    assert "1개" in logs[0]["message"]
    # The protocol's level vocabulary is debug/info/warn/error — "warning" is not one of them.
    assert logs[0]["level"] == "warn"


def test_a_mostly_blank_batch_still_fails_the_job(tmp_path: Path, channel) -> None:
    transcriber = FakeTranscriber(
        segments=[
            {"id": i, "start": i * 2.0, "end": (i * 2.0) + 1.5, "text": f"line {i}", "words": []}
            for i in range(1, 11)
        ]
    )
    handlers, _, _, _ = _handlers(
        transcriber=transcriber, engine=BlankingEngine(*range(2, 11))
    )

    handlers.process(_command(tmp_path), CancellationToken("t"))

    error = channel.first("error")
    assert error is not None
    assert error["code"] == errors.INVALID_TRANSLATION_RESPONSE
    assert error["recoverable"] is True


# ---------------------------------------------------------------------------
# resume
# ---------------------------------------------------------------------------


def test_a_second_run_reuses_the_transcription(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, _ = _handlers()

    handlers.process(_command(tmp_path), CancellationToken("t"))
    handlers.process(_command(tmp_path, outputPath=str(tmp_path / "second.ko.srt")), CancellationToken("t"))

    assert len(transcriber.calls) == 1


def test_partial_translations_are_reused_and_only_the_rest_translated(tmp_path: Path, channel) -> None:
    handlers, _, _, engine = _handlers()
    command = _command(tmp_path)

    # Seed a checkpoint as if a previous run had died after translating segment 1.
    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    store = CheckpointStore(command["checkpointDir"], "job-1", command["videoPath"])
    store.save_partial_translation({1: "이미 번역됨"})

    handlers.process(command, CancellationToken("t"))

    assert engine.batches == [[2, 3]]
    text = Path(command["outputPath"]).read_text(encoding="utf-8-sig")
    assert "이미 번역됨" in text


def test_a_changed_source_invalidates_the_checkpoint(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, _ = _handlers()
    command = _command(tmp_path)

    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    assert len(transcriber.calls) == 1

    Path(command["videoPath"]).write_bytes(b"a completely different video file, much longer" * 100)

    handlers.process(command, CancellationToken("t"))

    assert len(transcriber.calls) == 2
    assert any("원본 파일이 변경" in e.get("message", "") for e in channel.of_type("log"))


def test_resume_false_starts_over(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, _ = _handlers()

    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    handlers.process(_command(tmp_path, resume=False), CancellationToken("t"))

    assert len(transcriber.calls) == 2


# ---------------------------------------------------------------------------
# settings drift
#
# What 재시도 has to mean. A run that reuses the transcript but *not* the stale translation is
# the whole point: these all assert on `transcriber.calls` as well as `engine.batches`, because
# discarding the wrong tier is exactly the failure being guarded against.
# ---------------------------------------------------------------------------


def _seed_partial(tmp_path: Path, handlers: Any, command: dict[str, Any]) -> CheckpointStore:
    """Run ASR, then pretend a previous run translated segment 1."""
    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    store = CheckpointStore(command["checkpointDir"], "job-1", command["videoPath"])
    store.save_partial_translation({1: "예전 번역"})
    return store


def test_a_changed_translation_model_redoes_the_translation_but_not_the_asr(
    tmp_path: Path, channel
) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, translationEngine="local-translation"))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    # Every segment retranslated — including the one the old engine had already done.
    assert engine.batches == [[1, 2, 3]]
    # ...and the expensive stage was not touched.
    assert len(transcriber.calls) == 1
    assert "예전 번역" not in Path(command["outputPath"]).read_text(encoding="utf-8-sig")
    assert any("번역 설정이 바뀌어" in e.get("message", "") for e in channel.of_type("log"))


def test_a_changed_style_redoes_the_translation(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, translationStyle="polite"))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    assert engine.batches == [[1, 2, 3]]
    assert len(transcriber.calls) == 1


def test_a_changed_glossary_redoes_the_translation(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, glossary={"Kenobi": "케노비"}))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    assert engine.batches == [[1, 2, 3]]
    assert len(transcriber.calls) == 1


def test_unchanged_settings_still_resume_the_partial_translation(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path)
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    # The regression this pairs with: a retry after a transient failure must not throw away work.
    assert engine.batches == [[2, 3]]
    assert len(transcriber.calls) == 1
    assert "예전 번역" in Path(command["outputPath"]).read_text(encoding="utf-8-sig")


def test_a_changed_whisper_model_redoes_the_asr_and_the_translation(
    tmp_path: Path, channel
) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, whisperModel="whisper-large-v3"))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    assert len(transcriber.calls) == 2
    # Segment ids are renumbered by a new ASR run, so the old translation cannot be trusted.
    assert engine.batches == [[1, 2, 3]]
    assert any("음성 인식 설정이 바뀌어" in e.get("message", "") for e in channel.of_type("log"))


class FailAfterEngine(FakeEngine):
    """Translates ``fail_after`` batches, then dies for good."""

    def __init__(self, *, fail_after: int) -> None:
        super().__init__()
        self.fail_after = fail_after

    def translate_items(self, items, *, source_language="en", style="natural", glossary=None, token=None, **_: Any):  # noqa: ANN001, ANN201
        if len(self.batches) >= self.fail_after:
            self.batches.append([i["id"] for i in items])
            raise errors.WorkerError(errors.TRANSLATION_FAILED, "번역 실패", detail="fake")
        return super().translate_items(items, source_language=source_language, style=style, glossary=glossary, token=token)


def test_a_failure_after_a_settings_change_resumes_rather_than_restarting(
    tmp_path: Path, channel
) -> None:
    segments = [
        {"id": i, "start": float(i), "end": i + 0.9, "text": f"Line {i}.", "words": []}
        for i in range(1, 10)
    ]
    engine = FailAfterEngine(fail_after=5)
    handlers, _, transcriber, _ = _handlers(
        transcriber=FakeTranscriber(segments=segments), engine=engine
    )

    polite = dict(SETTINGS, translationStyle="polite", batchMaxItems=1)
    command = _command(tmp_path, settings=polite)
    _seed_partial(tmp_path, handlers, command)

    # First attempt under the new 문체: the stale line is discarded, five one-item batches land
    # (three of them checkpointed), then the engine dies for good.
    handlers.process(command, CancellationToken("t"))
    assert channel.first("error") is not None

    engine.batches.clear()
    engine.fail_after = 99
    handlers.process(command, CancellationToken("t"))

    # The settings did not change *again*, so the checkpointed batches must survive. With the old
    # fingerprint still in job.json this would read as drift a second time and redo all nine.
    assert engine.batches == [[4], [5], [6], [7], [8], [9]]
    assert len(transcriber.calls) == 1


def test_a_changed_batch_size_changes_nothing(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, batchMaxItems=2))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    # A performance knob. Invalidating cached work on it would make the checkpoint useless.
    assert len(transcriber.calls) == 1
    assert engine.batches == [[2, 3]]


def test_a_changed_compute_type_changes_nothing(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    command = _command(tmp_path, settings=dict(SETTINGS, computeType="int8"))
    _seed_partial(tmp_path, handlers, command)

    handlers.process(command, CancellationToken("t"))

    # The CUDA OOM ladder rewrites computeType mid-run; if it were part of the fingerprint every
    # resume after a downgrade would look stale and redo the ASR it just paid for.
    assert len(transcriber.calls) == 1
    assert engine.batches == [[2, 3]]


# ---------------------------------------------------------------------------
# audio reuse
# ---------------------------------------------------------------------------


def test_a_rerun_after_a_failed_asr_reuses_the_extracted_audio(tmp_path: Path, channel) -> None:
    transcriber = FakeTranscriber(oom_times=99)
    handlers, ffmpeg, _, _ = _handlers(transcriber=transcriber)

    # First run: extraction succeeds, ASR never does.
    handlers.process(_command(tmp_path), CancellationToken("t"))
    assert len(ffmpeg.extracted) == 1

    handlers.process(_command(tmp_path), CancellationToken("t"))

    # Re-demuxing a two-hour film because Whisper fell over is pure waste.
    assert len(ffmpeg.extracted) == 1


def test_a_changed_audio_track_re_extracts(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers(ffmpeg=FakeFfmpeg(audio_tracks=3))

    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    handlers.process(_command(tmp_path, audioTrackIndex=2), CancellationToken("t"))

    assert len(ffmpeg.extracted) == 2
    assert any("음성 추출 설정이 바뀌어" in e.get("message", "") for e in channel.of_type("log"))


def test_a_changed_source_re_extracts_the_audio(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()
    command = _command(tmp_path)

    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    Path(command["videoPath"]).write_bytes(b"a completely different video" * 100)

    handlers.process(command, CancellationToken("t"))

    # The cached wav is the *old* film's audio. Reusing it would caption the wrong movie.
    assert len(ffmpeg.extracted) == 2


def test_resume_false_re_extracts_the_audio(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.process(_command(tmp_path, phase="transcribe"), CancellationToken("t"))
    handlers.process(_command(tmp_path, resume=False), CancellationToken("t"))

    assert len(ffmpeg.extracted) == 2


# ---------------------------------------------------------------------------
# conflict policy
# ---------------------------------------------------------------------------


def test_an_existing_output_is_skipped_by_default(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    command = _command(tmp_path)
    Path(command["outputPath"]).write_text("기존 자막", encoding="utf-8")

    handlers.process(command, CancellationToken("t"))

    completed = channel.first("completed")
    assert completed["skipped"] is True
    assert Path(command["outputPath"]).read_text(encoding="utf-8") == "기존 자막"


def test_overwrite_policy_is_honoured(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    settings = dict(SETTINGS, outputConflictPolicy="overwrite")
    command = _command(tmp_path, settings=settings)
    Path(command["outputPath"]).write_text("기존 자막", encoding="utf-8")

    handlers.process(command, CancellationToken("t"))

    assert channel.first("completed")["skipped"] is False
    assert "번역" in Path(command["outputPath"]).read_text(encoding="utf-8-sig")


def test_numbered_policy_writes_a_new_file_and_keeps_the_old_one(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    settings = dict(SETTINGS, outputConflictPolicy="numbered")
    command = _command(tmp_path, settings=settings)

    existing = Path(command["outputPath"])
    existing.write_text("기존 자막", encoding="utf-8")

    handlers.process(command, CancellationToken("t"))

    completed = channel.first("completed")
    assert completed["skipped"] is False

    numbered = existing.with_name(f"{existing.stem} (2){existing.suffix}")
    assert completed["outputPath"] == str(numbered)
    assert numbered.is_file()
    # The user's original file is untouched: "번호 붙이기" is never a disguised overwrite.
    assert existing.read_text(encoding="utf-8") == "기존 자막"


@pytest.mark.parametrize("policy", ["skip", None, "", "  ", "unknown-policy"])
def test_an_unknown_or_missing_policy_falls_back_to_skip(tmp_path: Path, channel, policy) -> None:
    handlers, _, _, _ = _handlers()
    settings = dict(SETTINGS)
    if policy is not None:
        settings["outputConflictPolicy"] = policy

    command = _command(tmp_path, settings=settings)
    Path(command["outputPath"]).write_text("기존 자막", encoding="utf-8")

    handlers.process(command, CancellationToken("t"))

    assert channel.first("completed")["skipped"] is True
    assert Path(command["outputPath"]).read_text(encoding="utf-8") == "기존 자막"


# ---------------------------------------------------------------------------
# embedded subtitle source mode
# ---------------------------------------------------------------------------


def test_embedded_subtitle_mode_skips_asr(tmp_path: Path, channel) -> None:
    srt = "1\n00:00:01,000 --> 00:00:03,000\nHello there.\n\n2\n00:00:04,000 --> 00:00:06,000\nGeneral Kenobi.\n"
    handlers, _, transcriber, engine = _handlers(ffmpeg=FakeFfmpeg(subtitle_text=srt))

    command = _command(tmp_path, sourceMode="embeddedSubtitle", subtitleTrackIndex=0)
    handlers.process(command, CancellationToken("t"))

    assert transcriber.calls == []
    assert engine.batches == [[1, 2]]
    assert Path(command["outputPath"]).is_file()


def test_embedded_subtitle_mode_uses_the_track_language_the_host_sent(tmp_path: Path, channel) -> None:
    srt = "1\n00:00:01,000 --> 00:00:03,000\nこんにちは。\n"
    handlers, _, _, _ = _handlers(ffmpeg=FakeFfmpeg(subtitle_text=srt))

    command = _command(
        tmp_path, sourceMode="embeddedSubtitle", subtitleTrackIndex=2, subtitleLanguage="ja"
    )
    handlers.process(command, CancellationToken("t"))

    assert channel.first("languageDetected")["language"] == "ja"
    assert channel.first("completed")["sourceLanguage"] == "ja"


def test_embedded_subtitle_mode_falls_back_to_english_without_a_language(tmp_path: Path, channel) -> None:
    srt = "1\n00:00:01,000 --> 00:00:03,000\nHello there.\n"
    handlers, _, _, _ = _handlers(ffmpeg=FakeFfmpeg(subtitle_text=srt))

    handlers.process(
        _command(tmp_path, sourceMode="embeddedSubtitle", subtitleTrackIndex=0),
        CancellationToken("t"),
    )

    assert channel.first("languageDetected")["language"] == "en"


# ---------------------------------------------------------------------------
# external subtitle source mode (v1.5)
# ---------------------------------------------------------------------------

_SIDECAR = "1\n00:00:01,000 --> 00:00:03,000\nこんにちは。\n\n2\n00:00:04,000 --> 00:00:06,000\n元気ですか。\n"


def _sidecar(tmp_path: Path, name: str, text: str = _SIDECAR, encoding: str = "utf-8") -> Path:
    path = tmp_path / name
    path.write_bytes(text.encode(encoding))
    return path


def test_an_external_subtitle_is_translated_without_running_asr(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, transcriber, engine = _handlers()

    command = _command(
        tmp_path,
        sourceMode="externalSubtitle",
        subtitlePath=str(_sidecar(tmp_path, "movie.ja.srt")),
        subtitleLanguage="ja",
    )
    handlers.process(command, CancellationToken("t"))

    assert transcriber.calls == [], "ASR must not run"
    assert ffmpeg.lengths == [], "and neither must audio extraction"
    assert engine.batches == [[1, 2]]
    assert Path(command["outputPath"]).is_file()
    assert channel.first("completed")["sourceLanguage"] == "ja"


def test_a_shift_jis_sidecar_is_decoded_rather_than_mangled(tmp_path: Path, channel) -> None:
    """Sidecars carry no encoding declaration and Japanese ones are routinely CP932.

    Read as UTF-8 this raises rather than yielding replacement characters, which is the point of
    decoding strictly: mojibake would be translated into confident nonsense instead of failing.
    """
    handlers, _, _, engine = _handlers()

    command = _command(
        tmp_path,
        sourceMode="externalSubtitle",
        subtitlePath=str(_sidecar(tmp_path, "movie.ja.srt", encoding="cp932")),
        subtitleLanguage="ja",
    )
    handlers.process(command, CancellationToken("t"))

    assert engine.batches == [[1, 2]]

    # The fake engine echoes the source text back with a prefix, so the written subtitle is proof
    # the kana survived the decode rather than arriving as U+FFFD.
    written = Path(command["outputPath"]).read_text(encoding="utf-8")
    assert "こんにちは" in written
    assert "�" not in written


def test_a_missing_sidecar_reports_its_own_error_code(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    handlers.process(
        _command(
            tmp_path,
            sourceMode="externalSubtitle",
            subtitlePath=str(tmp_path / "movie.ja.srt"),
        ),
        CancellationToken("t"),
    )

    error = channel.first("error")
    assert error is not None
    assert error["code"] == errors.SUBTITLE_SOURCE_NOT_FOUND
    assert error["recoverable"] is False


def test_a_sidecar_with_no_cues_is_reported_as_unreadable(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    handlers.process(
        _command(
            tmp_path,
            sourceMode="externalSubtitle",
            subtitlePath=str(_sidecar(tmp_path, "movie.ja.srt", text="이건 자막이 아닙니다\n")),
        ),
        CancellationToken("t"),
    )

    assert channel.first("error")["code"] == errors.SUBTITLE_SOURCE_UNREADABLE


def test_swapping_the_sidecar_discards_the_cached_transcription(tmp_path: Path, channel) -> None:
    """The path is in the transcription fingerprint, so a different file is different text."""
    handlers, _, _, _ = _handlers()

    base = dict(
        sourceMode="externalSubtitle",
        subtitleLanguage="ja",
    )
    first = _command(tmp_path, subtitlePath=str(_sidecar(tmp_path, "movie.ja.srt")), **base)
    handlers.process(first, CancellationToken("t"))

    store = CheckpointStore(tmp_path / "cache" / "job-1")
    assert store.load_job()["transcriptionSettings"]["subtitlePath"].endswith("movie.ja.srt")

    second = _command(tmp_path, subtitlePath=str(_sidecar(tmp_path, "movie.en.srt")), **base)
    handlers.process(second, CancellationToken("t"))

    assert store.load_job()["transcriptionSettings"]["subtitlePath"].endswith("movie.en.srt")


def test_the_audio_path_fingerprint_is_unchanged_by_the_new_key(tmp_path: Path, channel) -> None:
    """A 1.4 checkpoint must keep matching, or every finished job re-runs ASR (§6.13).

    The key is therefore conditional — present only when there is a path — unlike every other
    entry in the fingerprint.
    """
    handlers, _, _, _ = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    recorded = CheckpointStore(tmp_path / "cache" / "job-1").load_job()["transcriptionSettings"]
    assert "subtitlePath" not in recorded


def test_prefetch_skips_external_subtitle_jobs(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(
        _extract_command(tmp_path, sourceMode="externalSubtitle"), CancellationToken("t")
    )

    assert ffmpeg.extracted == []


# ---------------------------------------------------------------------------
# CUDA OOM recovery
# ---------------------------------------------------------------------------


def test_transcription_oom_is_recovered_by_downgrading_and_retrying(tmp_path: Path, channel) -> None:
    transcriber = FakeTranscriber(oom_times=1)
    handlers, _, _, _ = _handlers(transcriber=transcriber)

    settings = dict(SETTINGS, computeType="float16")
    handlers.process(_command(tmp_path, settings=settings), CancellationToken("t"))

    # 1. memory was freed
    assert transcriber.unload_count >= 1
    # 2. the compute type was downgraded on the retry
    assert transcriber.calls[0]["compute_type"] == "float16"
    assert transcriber.calls[1]["compute_type"] == "int8_float16"
    # 3. the user was told what to change if it happens again
    logs = " ".join(e["message"] for e in channel.of_type("log"))
    assert "더 작은 모델" in logs
    assert "int8_float16" in logs
    # 4. the job completed rather than failing
    assert channel.first("completed") is not None
    assert channel.first("error") is None


@pytest.mark.parametrize(
    ("start", "expected"),
    [("float32", "float16"), ("float16", "int8_float16"), ("int8_float16", "int8"), ("bfloat16", "float16")],
)
def test_the_compute_ladder_goes_one_step_at_a_time(
    tmp_path: Path, channel, start: str, expected: str
) -> None:
    transcriber = FakeTranscriber(oom_times=1)
    handlers, _, _, _ = _handlers(transcriber=transcriber)

    handlers.process(_command(tmp_path, settings=dict(SETTINGS, computeType=start)), CancellationToken("t"))

    assert transcriber.calls[1]["compute_type"] == expected


def test_only_one_automatic_retry_is_attempted(tmp_path: Path, channel) -> None:
    transcriber = FakeTranscriber(oom_times=5)
    handlers, _, _, _ = _handlers(transcriber=transcriber)

    handlers.process(_command(tmp_path, settings=dict(SETTINGS, computeType="float16")), CancellationToken("t"))

    assert len(transcriber.calls) == 2

    error = channel.first("error")
    assert error["code"] == errors.CUDA_OUT_OF_MEMORY
    assert error["recoverable"] is True
    assert "더 작은 모델" in error["message"]


def test_already_at_int8_says_so_and_still_retries_once(tmp_path: Path, channel) -> None:
    transcriber = FakeTranscriber(oom_times=1)
    handlers, _, _, _ = _handlers(transcriber=transcriber)

    handlers.process(_command(tmp_path, settings=dict(SETTINGS, computeType="int8")), CancellationToken("t"))

    logs = " ".join(e["message"] for e in channel.of_type("log"))
    assert "가장 낮은 정밀도" in logs
    assert transcriber.calls[1]["compute_type"] == "int8"
    assert channel.first("completed") is not None


def test_translation_oom_halves_the_batch_and_retries(tmp_path: Path, channel) -> None:
    engine = FakeEngine(oom_times=1)
    handlers, _, _, _ = _handlers(engine=engine)

    handlers.process(_command(tmp_path), CancellationToken("t"))

    # First attempt saw all three ids; the retry sent two smaller requests instead.
    assert engine.batches[0] == [1, 2, 3]
    assert all(len(call) < 3 for call in engine.batches[1:])

    logs = " ".join(e["message"] for e in channel.of_type("log"))
    assert "배치 크기를 절반" in logs
    assert channel.first("completed") is not None


def test_halving_a_batch_never_loses_a_cue(tmp_path: Path, channel) -> None:
    # Regression: truncating the batch made the job report success while silently dropping every
    # cue in the second half.
    engine = FakeEngine(oom_times=1)
    handlers, _, _, _ = _handlers(engine=engine)
    command = _command(tmp_path)

    handlers.process(command, CancellationToken("t"))

    assert sorted(i for call in engine.batches[1:] for i in call) == [1, 2, 3]
    assert channel.first("completed")["cueCount"] == 3

    text = Path(command["outputPath"]).read_text(encoding="utf-8-sig")
    for expected in ("Hello there.", "General Kenobi.", "You are a bold one."):
        assert expected in text


def test_the_retry_uses_the_reloaded_engine_not_a_stale_reference(tmp_path: Path, channel) -> None:
    first = FakeEngine(oom_times=1)
    second = FakeEngine()
    built: list[FakeEngine] = []

    def factory(_kind: str, _settings: dict[str, Any]) -> FakeEngine:
        engine = first if not built else second
        built.append(engine)
        return engine

    handlers = CommandHandlers(
        ffmpeg=FakeFfmpeg(),  # type: ignore[arg-type]
        transcriber=FakeTranscriber(),  # type: ignore[arg-type]
        translator_factory=factory,
    )
    handlers.process(_command(tmp_path), CancellationToken("t"))

    # The OOM ladder rebuilt the engine; the retry must go to the new one.
    assert len(built) == 2
    assert first.batches == [[1, 2, 3]]
    assert sorted(i for call in second.batches for i in call) == [1, 2, 3]
    assert channel.first("completed")["cueCount"] == 3


def test_a_non_oom_error_is_not_retried(tmp_path: Path, channel) -> None:
    class Failing(FakeTranscriber):
        def __init__(self) -> None:
            super().__init__()
            self.attempts = 0

        def transcribe(self, audio_path, **kwargs):  # noqa: ANN001, ANN201
            self.attempts += 1
            raise errors.WorkerError(errors.WHISPER_MODEL_NOT_FOUND, "모델 없음")

    transcriber = Failing()
    handlers, _, _, _ = _handlers(transcriber=transcriber)
    handlers.process(_command(tmp_path), CancellationToken("t"))

    assert transcriber.attempts == 1
    assert channel.first("error")["code"] == errors.WHISPER_MODEL_NOT_FOUND


def test_split_batch_in_half_partitions_without_loss() -> None:
    batch = Batch(index=4, segments=[{"id": i, "text": "a"} for i in range(1, 6)])
    parts = split_batch_in_half(batch)

    assert len(parts) == 2
    assert [s["id"] for p in parts for s in p.segments] == [1, 2, 3, 4, 5]


def test_split_batch_in_half_gives_the_tail_its_context() -> None:
    batch = Batch(index=0, segments=[{"id": i, "text": "a"} for i in range(1, 9)])
    _, tail = split_batch_in_half(batch)

    assert [c["id"] for c in tail.context] == [2, 3, 4]


def test_a_single_segment_batch_cannot_be_split() -> None:
    batch = Batch(index=0, segments=[{"id": 1, "text": "a"}])
    assert split_batch_in_half(batch) == [batch]


# ---------------------------------------------------------------------------
# simple commands
# ---------------------------------------------------------------------------


def test_probe_emits_a_probe_result(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.probe({"requestId": "p1", "videoPath": str(tmp_path / "movie.mkv")})

    result = channel.first("probeResult")
    assert result is not None and result["requestId"] == "p1"
    assert result["durationSeconds"] == 30.0


def test_probe_without_a_path_is_a_protocol_error(channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.probe({"requestId": "p1"})

    assert channel.first("error")["code"] == errors.PROTOCOL_ERROR


def test_detect_hardware_never_fails(channel, monkeypatch: pytest.MonkeyPatch) -> None:
    handlers, _, _, _ = _handlers()

    def boom() -> dict[str, Any]:
        raise RuntimeError("no /proc here")

    monkeypatch.setattr("ksubmaker_worker.hardware_detector.detect", boom)
    handlers.detect_hardware({"requestId": "h1"})

    event = channel.first("hardware")
    assert event is not None
    assert event["cudaAvailable"] is False
    assert event["warnings"]
    # Nothing was probed, so nothing is *known* to be missing. Claiming otherwise would put a
    # "CUDA 라이브러리가 없습니다" warning on a machine that may be perfectly healthy.
    assert event["cudaLibrariesAvailable"] is True
    assert event["missingCudaLibraries"] == []


def test_the_hardware_event_carries_the_protocol_1_2_cuda_fields(channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.detect_hardware({"requestId": "h2"})

    event = channel.first("hardware")
    assert event is not None
    for key in ("cudaAvailable", "cudaDeviceDetected", "cudaLibrariesAvailable", "missingCudaLibraries"):
        assert key in event, f"{key} is part of the 1.2 hardware contract"

    # The invariant the host relies on: available == device AND libraries.
    assert event["cudaAvailable"] == (event["cudaDeviceDetected"] and event["cudaLibrariesAvailable"])


def test_hello_warns_on_an_incompatible_protocol_version(channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.hello({"requestId": "r1", "protocolVersion": "9.0"})

    log = channel.first("log")
    assert log is not None and log["level"] == "error"
    assert channel.first("ack") is not None


def test_hello_only_warns_when_the_host_reported_no_version(channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.hello({"requestId": "r1"})

    log = channel.first("log")
    assert log is not None and log["level"] == "warn"
    assert channel.first("ack") is not None


def test_hello_is_quiet_on_a_matching_version(channel) -> None:
    handlers, _, _, _ = _handlers()
    handlers.hello({"requestId": "r1", "protocolVersion": protocol.PROTOCOL_VERSION})

    assert channel.of_type("log") == []
    assert channel.first("ack")["command"] == "hello"


def test_shutdown_unloads_every_model(tmp_path: Path, channel) -> None:
    handlers, _, transcriber, engine = _handlers()
    handlers.process(_command(tmp_path), CancellationToken("t"))

    handlers.shutdown()

    assert transcriber.unload_count >= 1
    assert engine.unload_count >= 1


# ---------------------------------------------------------------------------
# checkpoint side effects
# ---------------------------------------------------------------------------


def test_the_pipeline_leaves_a_complete_checkpoint(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()
    command = _command(tmp_path)
    handlers.process(command, CancellationToken("t"))

    directory = Path(command["checkpointDir"])
    names = {p.name for p in directory.iterdir()}

    assert {"job.json", "transcription.json", "translation.partial.json", "finalization.json"} <= names
    assert not any(name.endswith(".tmp") for name in names)

    final = json.loads((directory / "finalization.json").read_text(encoding="utf-8"))
    assert final["cueCount"] >= 1
    assert final["outputPath"] == command["outputPath"]


# ---------------------------------------------------------------------------
# relocatable models directory
# ---------------------------------------------------------------------------


def test_the_models_directory_comes_from_the_environment_at_startup(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """The host injects ``KSUBMAKER_MODELS_DIR`` into the worker process.

    It has to be picked up in ``__init__`` rather than per job: ``listModels``/``verifyModel`` and
    the transcriber's model resolution all run outside a ``process`` command.
    """
    relocated = tmp_path / "elsewhere" / "models"
    monkeypatch.setenv("KSUBMAKER_MODELS_DIR", str(relocated))

    handlers = CommandHandlers()

    assert handlers.models_dir == relocated
    assert handlers.model_manager.models_dir == relocated
    assert handlers.transcriber._models_dir == relocated


def test_an_explicit_models_dir_still_wins_over_the_environment(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setenv("KSUBMAKER_MODELS_DIR", str(tmp_path / "from-env"))

    handlers = CommandHandlers(models_dir=tmp_path / "explicit")

    assert handlers.models_dir == tmp_path / "explicit"


# ---------------------------------------------------------------------------
# cross-language wire compatibility
# ---------------------------------------------------------------------------


#: A ``process`` line exactly as ``WorkerProtocolSerializer.SerializeCommand`` writes it (compact,
#: camelCase, non-ASCII unescaped). Kept as raw text rather than a dict so a field renamed on the C#
#: side has to be renamed here too, which is the only thing that makes this a parity test. The three
#: path placeholders are substituted, not formatted: str.format would choke on the JSON braces.
_CSHARP_PROCESS_LINE = (
    '{"command":"process","requestId":"3d0a","protocolVersion":"1.2","jobId":"job-1",'
    '"videoPath":"@VIDEO@","outputPath":"@OUTPUT@","checkpointDir":"@CACHE@",'
    '"settings":{"language":"auto","whisperModel":"whisper-small","device":"cpu","beamSize":5,'
    '"vadFilter":true,"wordTimestamps":true,"conditionOnPreviousText":false,'
    '"translationEngine":"fake","translationModel":"auto","llmModel":"auto",'
    '"translationStyle":"natural","batchMaxItems":30,"batchMaxChars":2500,"batchMaxSeconds":180,'
    '"contextLines":3,"glossary":{"Sherlock":"셜록"},"maxLinesPerCue":2,"maxCharsPerLine":22,'
    '"minCueDurationSeconds":1.0,"maxCueDurationSeconds":7.0,"minCueGapMilliseconds":50,'
    '"mergeShortCues":true,"outputConflictPolicy":"overwrite","autoRetryOnRecoverableError":true},'
    '"sourceMode":"embeddedSubtitle","subtitleTrackIndex":2,"subtitleLanguage":"ja",'
    '"resume":true,"phase":"full"}'
)


def _json_path(path: Path) -> str:
    """Backslashes doubled, the way System.Text.Json writes a Windows path."""
    return str(path).replace("\\", "\\\\")


def test_a_command_serialised_by_the_host_is_understood_field_for_field(tmp_path: Path, channel) -> None:
    srt = "1\n00:00:01,000 --> 00:00:03,000\nこんにちは。\n"
    handlers, _, transcriber, _ = _handlers(ffmpeg=FakeFfmpeg(subtitle_text=srt))

    video = tmp_path / "movie.mkv"
    video.write_bytes(b"pretend this is a video" * 50)
    output = tmp_path / "movie.ko.srt"
    output.write_text("기존 자막", encoding="utf-8")

    line = (
        _CSHARP_PROCESS_LINE.replace("@VIDEO@", _json_path(video))
        .replace("@OUTPUT@", _json_path(output))
        .replace("@CACHE@", _json_path(tmp_path / "cache" / "job-1"))
    )

    handlers.process(json.loads(line), CancellationToken("t"))

    completed = channel.first("completed")
    assert completed is not None, channel.events()

    # sourceMode + subtitleTrackIndex: ASR never ran.
    assert transcriber.calls == []
    # subtitleLanguage: the track's own language, not the English fallback.
    assert completed["sourceLanguage"] == "ja"
    # outputConflictPolicy: the pre-existing file was replaced rather than skipped.
    assert completed["skipped"] is False
    assert output.read_text(encoding="utf-8-sig") != "기존 자막"


# ---------------------------------------------------------------------------
# extractAudio (v1.3): the prefetch lane
# ---------------------------------------------------------------------------


def _extract_command(tmp_path: Path, **overrides: Any) -> dict[str, Any]:
    video = tmp_path / "movie.mkv"
    if not video.exists():
        video.write_bytes(b"pretend this is a video" * 50)

    command: dict[str, Any] = {
        "command": "extractAudio",
        "requestId": "req-x",
        "jobId": "job-1",
        "videoPath": str(video),
        "checkpointDir": str(tmp_path / "cache" / "job-1"),
        "settings": dict(SETTINGS),
        "sourceMode": "audio",
    }
    command.update(overrides)
    return command


def test_prefetch_writes_a_wav_and_reports_completed(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    assert len(ffmpeg.extracted) == 1
    assert (tmp_path / "cache" / "job-1" / "audio.wav").stat().st_size > 0
    assert channel.first("completed") is not None


def test_the_job_reuses_a_prefetched_wav_instead_of_demuxing_again(tmp_path: Path, channel) -> None:
    """The entire point of the lane: work done early is work the job does not repeat."""
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))
    assert len(ffmpeg.extracted) == 1

    handlers.process(_command(tmp_path), CancellationToken("t"))

    # Still one: the job found the extraction stage already done and skipped straight to ASR.
    assert len(ffmpeg.extracted) == 1
    assert channel.first("completed") is not None


def test_prefetch_records_the_audio_fingerprint_so_the_job_trusts_the_wav(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    handlers.extract_audio(_extract_command(tmp_path, audioTrackIndex=2), CancellationToken("t"))

    job = CheckpointStore(tmp_path / "cache" / "job-1").load_job()
    assert job is not None
    assert job["completedStage"] == Stages.EXTRACTING_AUDIO
    # Without this the job cannot tell the wav apart from one made off a different track — or, since
    # the test-duration limit landed, from one that was trimmed to the first N seconds.
    assert job["audioSettings"] == {
        "sourceMode": "audio",
        "audioTrackIndex": 2,
        "testDurationSeconds": 0,
    }


def test_a_trimmed_wav_is_not_mistaken_for_a_full_one(tmp_path: Path, channel) -> None:
    """The test-duration limit cuts the wav short, so it has to be part of the audio fingerprint.

    Without it a run that extracted only the first 30 seconds would leave a wav the next full run
    happily reuses, and the subtitle would stop a third of the way through the film with nothing
    saying why.
    """
    handlers, _, _, _ = _handlers()

    command = _extract_command(tmp_path)
    command["settings"] = dict(command.get("settings") or {}, testDurationSeconds=30)
    handlers.extract_audio(command, CancellationToken("t"))

    job = CheckpointStore(tmp_path / "cache" / "job-1").load_job()
    assert job is not None
    assert job["audioSettings"]["testDurationSeconds"] == 30


@pytest.mark.parametrize("run", ["process", "prefetch"])
def test_an_ordinary_run_asks_ffmpeg_for_the_whole_track(tmp_path: Path, channel, run: str) -> None:
    """Neither lane may pass a trim length it only knows as "how long the container claims to be".

    The probed duration is a progress denominator. Handing it to ffmpeg as ``-t`` as well made
    every normal extraction a trimmed one, correct exactly as long as the container's own figure
    is — and silently short whenever it is not.
    """
    handlers, ffmpeg, _, _ = _handlers()

    if run == "process":
        handlers.process(_command(tmp_path), CancellationToken("t"))
    else:
        handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    assert ffmpeg.lengths == [(30.0, None)]


@pytest.mark.parametrize("run", ["process", "prefetch"])
def test_a_test_duration_reaches_ffmpeg_as_a_trim(tmp_path: Path, channel, run: str) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    if run == "process":
        command = _command(tmp_path)
    else:
        command = _extract_command(tmp_path)
    command["settings"] = dict(command.get("settings") or {}, testDurationSeconds=10)

    if run == "process":
        handlers.process(command, CancellationToken("t"))
    else:
        handlers.extract_audio(command, CancellationToken("t"))

    # The job lane measures the rest of the pipeline against the trimmed length too — the
    # transcript covers 10 seconds, not 30 — so it has already clamped its denominator. The
    # prefetch lane has no progress to report and leaves the clamping to extract_audio.
    expected_denominator = 10.0 if run == "process" else 30.0
    assert ffmpeg.lengths == [(expected_denominator, 10.0)]


def test_the_hosts_initial_prompt_reaches_the_transcriber_and_the_fingerprint(
    tmp_path: Path, channel
) -> None:
    """v1.4. The worker read this field for a while before any host sent it.

    Both halves matter: the prompt changes what Whisper writes, so a transcript made under a
    different one must not be reused.
    """
    handlers, _, transcriber, _ = _handlers()

    command = _command(tmp_path)
    command["settings"] = dict(command["settings"], initialPrompt="登場人物: 佐藤, 鈴木。")
    handlers.process(command, CancellationToken("t"))

    assert transcriber.calls[0]["initial_prompt"] == "登場人物: 佐藤, 鈴木。"

    job = CheckpointStore(tmp_path / "cache" / "job-1").load_job()
    assert job is not None
    assert job["transcriptionSettings"]["initialPrompt"] == "登場人物: 佐藤, 鈴木。"


def test_a_host_that_sends_no_prompt_leaves_the_built_in_hint_alone(
    tmp_path: Path, channel
) -> None:
    """And records ``None``, which is what every pre-1.4 checkpoint already holds.

    Dropping the key instead would have made the fingerprint of every existing cache mismatch and
    re-run ASR on work that was already correct.
    """
    handlers, _, transcriber, _ = _handlers()

    handlers.process(_command(tmp_path), CancellationToken("t"))

    assert transcriber.calls[0]["initial_prompt"] is None

    job = CheckpointStore(tmp_path / "cache" / "job-1").load_job()
    assert job is not None
    assert job["transcriptionSettings"]["initialPrompt"] is None


def test_the_source_subtitle_is_saved_beside_the_translation(tmp_path: Path, channel) -> None:
    """v1.6. 전사는 어차피 캐시에 있다 — 이 옵션은 사용자가 볼 수 있는 곳에 사본을 둘지만 정한다."""
    handlers, _, _, _ = _handlers()

    command = _command(tmp_path)
    command["settings"] = dict(command["settings"], saveSourceSubtitle=True, language="ja")
    handlers.process(command, CancellationToken("t"))

    # FakeTranscriber 가 감지 언어로 en 을 보고하므로 이름도 그것을 따른다.
    assert (tmp_path / "movie.en.srt").is_file()
    assert Path(command["outputPath"]).is_file(), "번역본은 그대로 있어야 한다"


def test_the_source_subtitle_is_not_saved_unless_asked(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    handlers.process(_command(tmp_path), CancellationToken("t"))

    assert not (tmp_path / "movie.en.srt").exists()


def test_the_source_subtitle_never_replaces_the_translation(tmp_path: Path, channel) -> None:
    """원어가 한국어이고 접미사가 기본값이면 두 이름이 같아진다 — 그때는 쓰지 않는다.

    쓰면 번역본을 그 입력으로 덮어쓴다. 되돌릴 수 없는 손실이라 조용히 건너뛰는 쪽이 옳다.
    """
    handlers, _, transcriber, _ = _handlers()
    transcriber.language = "ko"

    command = _command(tmp_path)
    command["outputPath"] = str(tmp_path / "movie.ko.srt")
    # 동일 언어 생략을 꺼야 번역이 실제로 돌아 원문과 구분되는 내용이 나온다.
    command["settings"] = dict(
        command["settings"], saveSourceSubtitle=True, skipTranslationForSameLanguage=False
    )
    handlers.process(command, CancellationToken("t"))

    written = Path(command["outputPath"]).read_text(encoding="utf-8")
    assert "번역" in written, "번역본이 원문으로 덮어써지면 안 된다"


def test_the_source_subtitle_never_overwrites_the_sidecar_it_read(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    sidecar = tmp_path / "movie.en.srt"
    # 이스케이프 시퀀스를 소스에 써 넣지 않는다 (§6.15) — 줄바꿈은 join 으로 만든다.
    sidecar.write_text(
        chr(10).join(["1", "00:00:01,000 --> 00:00:03,000", "Original.", ""]),
        encoding="utf-8",
    )
    before = sidecar.read_bytes()

    command = _command(
        tmp_path,
        sourceMode="externalSubtitle",
        subtitlePath=str(sidecar),
        subtitleLanguage="en",
    )
    command["settings"] = dict(command["settings"], saveSourceSubtitle=True)
    handlers.process(command, CancellationToken("t"))

    assert sidecar.read_bytes() == before, "읽고 있던 파일을 다시 쓰면 안 된다"


def test_a_failure_to_save_the_source_subtitle_does_not_fail_the_job(tmp_path: Path, channel) -> None:
    """번역본은 이미 디스크에 있다. 편의 파일 하나 때문에 끝난 작업을 실패로 만들 이유가 없다."""
    handlers, _, _, _ = _handlers()

    command = _command(tmp_path)
    command["settings"] = dict(command["settings"], saveSourceSubtitle=True)
    # 쓰려는 이름 자리에 디렉터리를 만들어 두면 쓰기가 실패한다.
    (tmp_path / "movie.en.srt").mkdir()

    handlers.process(command, CancellationToken("t"))

    assert channel.first("completed") is not None
    assert channel.first("error") is None


def test_a_prefetch_for_a_different_track_is_redone_by_the_job(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(_extract_command(tmp_path, audioTrackIndex=2), CancellationToken("t"))
    handlers.process(_command(tmp_path, audioTrackIndex=0), CancellationToken("t"))

    # The prefetched wav is the wrong track's audio, so reusing it would transcribe the wrong
    # language entirely. Two extractions is the correct answer here.
    assert len(ffmpeg.extracted) == 2


def test_prefetching_twice_extracts_once(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))
    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    assert len(ffmpeg.extracted) == 1


def test_prefetch_does_not_walk_the_completed_stage_backwards(tmp_path: Path, channel) -> None:
    """A re-run of an already transcribed job must not lose the expensive stage."""
    handlers, _, transcriber, _ = _handlers()

    handlers.process(_command(tmp_path), CancellationToken("t"))
    calls_after_first_run = len(transcriber.calls)

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    job = CheckpointStore(tmp_path / "cache" / "job-1").load_job()
    assert job is not None
    assert job["completedStage"] == Stages.WRITING_SUBTITLE

    # And the transcription is still reused rather than redone.
    handlers.process(_command(tmp_path), CancellationToken("t"))
    assert len(transcriber.calls) == calls_after_first_run


def test_prefetch_skips_embedded_subtitle_jobs(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.extract_audio(
        _extract_command(tmp_path, sourceMode="embeddedSubtitle"), CancellationToken("t")
    )

    # Such a job never reads audio, so extracting any would be pure waste.
    assert ffmpeg.extracted == []


def test_a_missing_video_fails_the_prefetch_without_raising(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers()

    handlers.extract_audio(
        _extract_command(tmp_path, videoPath=str(tmp_path / "gone.mkv")), CancellationToken("t")
    )

    error = channel.first("error")
    assert error is not None
    assert error["code"] == errors.VIDEO_NOT_FOUND
    # Always recoverable: the job will extract its own audio when the queue reaches it.
    assert error["recoverable"] is True


def test_a_video_without_audio_fails_the_prefetch_recoverably(tmp_path: Path, channel) -> None:
    handlers, _, _, _ = _handlers(ffmpeg=FakeFfmpeg(audio_tracks=0))

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    error = channel.first("error")
    assert error is not None
    assert error["code"] == errors.AUDIO_TRACK_NOT_FOUND


def test_a_cancelled_prefetch_reports_cancelled(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()
    token = CancellationToken("t")
    token.cancel()

    handlers.extract_audio(_extract_command(tmp_path), token)

    assert ffmpeg.extracted == []
    assert channel.first("cancelled") is not None


def test_prefetch_clears_a_checkpoint_made_from_a_different_cut(tmp_path: Path, channel) -> None:
    handlers, ffmpeg, _, _ = _handlers()

    handlers.process(_command(tmp_path), CancellationToken("t"))

    # Same name, different file: every timecode in the old transcript is now wrong.
    video = tmp_path / "movie.mkv"
    video.write_bytes(b"a completely different and much longer video file" * 90)

    handlers.extract_audio(_extract_command(tmp_path), CancellationToken("t"))

    store = CheckpointStore(tmp_path / "cache" / "job-1")
    assert store.load_transcription() is None, "the stale transcript must not survive"
    assert len(ffmpeg.extracted) == 2


class SlowFfmpeg(FakeFfmpeg):
    """Extraction that takes long enough for a second caller to collide with it."""

    def __init__(self, delay: float = 0.3) -> None:
        super().__init__()
        self.delay = delay
        self.concurrent = 0
        self.max_concurrent = 0
        self._guard = threading.Lock()

    def extract_audio(self, video_path, output_path, **kwargs):  # noqa: ANN001, ANN201
        with self._guard:
            self.concurrent += 1
            self.max_concurrent = max(self.max_concurrent, self.concurrent)
        try:
            time.sleep(self.delay)
            return super().extract_audio(video_path, output_path, **kwargs)
        finally:
            with self._guard:
                self.concurrent -= 1


def test_a_prefetch_and_its_job_never_demux_the_same_file_at_once(tmp_path: Path, channel) -> None:
    """The race the per-directory lock exists for.

    The host starts file N while the prefetch it launched for file N is still running. Both would
    drive ffmpeg at the same audio.wav.tmp, and the loser leaves a torn wav that Whisper turns into
    an empty transcript rather than an error.
    """
    ffmpeg = SlowFfmpeg()
    handlers, _, _, _ = _handlers(ffmpeg=ffmpeg)

    prefetch = threading.Thread(
        target=lambda: handlers.extract_audio(_extract_command(tmp_path), CancellationToken("p"))
    )
    job = threading.Thread(
        target=lambda: handlers.process(_command(tmp_path), CancellationToken("j"))
    )

    prefetch.start()
    time.sleep(0.05)  # let the prefetch get inside the extraction
    job.start()

    prefetch.join(30.0)
    job.join(30.0)

    assert ffmpeg.max_concurrent == 1, "two ffmpeg runs targeted one audio.wav.tmp"
    assert len(ffmpeg.extracted) == 1, "the second caller should have reused the first one's wav"


def test_the_job_reports_the_extraction_stage_before_waiting_on_the_lock(
    tmp_path: Path, channel
) -> None:
    """The reported symptom: "2%에서 멈춘다".

    2.00% is exactly what finishing 단계 Probing leaves on the bar, and the first progress event of
    the extraction used to sit *inside* the lock. So a job that arrived while the prefetch lane was
    still demuxing the same file sent nothing at all for the length of that extraction — minutes on
    a large source — and the row looked wedged. Nothing was actually wrong; the reporting was.

    The lock behaviour itself is covered by the test above. This one only asserts that the wait is
    visible, which is the part that was missing.
    """
    ffmpeg = SlowFfmpeg()
    handlers, _, _, _ = _handlers(ffmpeg=ffmpeg)

    prefetch = threading.Thread(
        target=lambda: handlers.extract_audio(_extract_command(tmp_path), CancellationToken("p"))
    )
    prefetch.start()
    time.sleep(0.05)  # let the prefetch get inside the extraction, so the job has to wait

    job = threading.Thread(
        target=lambda: handlers.process(_command(tmp_path), CancellationToken("j"))
    )
    job.start()

    prefetch.join(30.0)
    job.join(30.0)

    # Every extraction progress event is the job's: the prefetch lane deliberately reports none,
    # because it is not the row the user is watching.
    extraction_events = [
        event
        for event in channel.of_type("progress")
        if event.get("stage") == Stages.EXTRACTING_AUDIO
    ]

    assert extraction_events, "the extraction stage was never reported while the job waited"

    # The row must leave 2% as soon as the job takes the stage, not once the lock frees up.
    assert extraction_events[0].get("stageProgress") == 0.0

    # And the wait has to say why, otherwise "0% that never moves" reads the same as the freeze.
    assert any(
        "추출하는 중" in str(event.get("message") or "") for event in extraction_events
    ), "the job never explained that it was waiting for another extraction"
