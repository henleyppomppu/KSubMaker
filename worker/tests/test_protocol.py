"""stdout must carry exactly one parseable JSON object per line and nothing else."""

from __future__ import annotations

import json
import threading

import pytest

from ksubmaker_worker import protocol


def test_emit_writes_one_line_that_parses(channel) -> None:
    protocol.emit({"type": "log", "message": "안녕하세요"})

    assert len(channel.lines()) == 1
    assert json.loads(channel.lines()[0]) == {"type": "log", "message": "안녕하세요"}


def test_output_is_not_ascii_escaped(channel) -> None:
    protocol.emit_log("한국어 메시지")
    assert "한국어 메시지" in channel.getvalue()
    assert "\\u" not in channel.getvalue()


def test_output_is_compact(channel) -> None:
    protocol.emit({"type": "log", "message": "x", "level": "info"})
    assert channel.lines()[0] == '{"type":"log","message":"x","level":"info"}'


def test_every_emitted_event_is_one_line(channel) -> None:
    protocol.emit_ready(worker_version="1.0.0", python_version="3.11.0")
    protocol.emit_ack("hello", "r1")
    protocol.emit_started(request_id="r1", job_id="j1", resumed_from_stage="translating")
    protocol.emit_progress(stage=protocol.Stages.TRANSCRIBING, stage_progress=42.5, job_id="j1")
    protocol.emit_language_detected(language="en", probability=0.98, job_id="j1")
    protocol.emit_stage_completed(stage=protocol.Stages.TRANSCRIBING, job_id="j1")
    protocol.emit_completed(output_path="/tmp/a.ko.srt", cue_count=3, job_id="j1")
    protocol.emit_error(code="UNKNOWN", message="오류", job_id="j1")
    protocol.emit_cancelled(job_id="j1")
    protocol.emit_log("메모")
    protocol.emit_hardware({"gpus": [], "cudaAvailable": False})
    protocol.emit_probe_result({"videoPath": "/tmp/a.mkv", "durationSeconds": 1.0})
    protocol.emit_model_list([{"modelId": "whisper-small"}])
    protocol.emit_download_progress(model_id="m", received_bytes=1, total_bytes=2)
    protocol.emit_download_completed(model_id="m", verified=True)
    protocol.emit_goodbye()

    events = channel.events()
    assert len(events) == 16
    assert all(isinstance(event, dict) and "type" in event for event in events)


def test_multiline_text_never_breaks_the_channel(channel) -> None:
    protocol.emit_log("첫 줄\n둘째 줄\r\n셋째 줄")

    assert len(channel.lines()) == 1
    assert json.loads(channel.lines()[0])["message"] == "첫 줄\n둘째 줄\r\n셋째 줄"


def test_events_echo_the_request_id(channel) -> None:
    protocol.emit_progress(stage=protocol.Stages.PROBING, stage_progress=0.0, request_id="abc")
    assert channel.events()[0]["requestId"] == "abc"


def test_absent_ids_are_omitted_rather_than_null(channel) -> None:
    protocol.emit_progress(stage=protocol.Stages.PROBING, stage_progress=0.0)
    event = channel.events()[0]
    assert "requestId" not in event
    assert "jobId" not in event


def test_unserialisable_payload_does_not_corrupt_the_channel(channel) -> None:
    # `default=str` handles most things; an object that raises from __str__ must still not write
    # a partial line.
    class Hostile:
        def __str__(self) -> str:
            raise RuntimeError("nope")

    with pytest.raises(RuntimeError):
        protocol.emit({"type": "log", "message": Hostile()})

    assert channel.getvalue() == ""


@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_non_finite_numbers_never_reach_the_wire(channel, value: float) -> None:
    # Bare NaN/Infinity is not JSON; System.Text.Json rejects the whole line. The event is
    # repaired rather than dropped, because losing a `completed` costs the user a whole job.
    protocol.emit_progress(stage=protocol.Stages.TRANSCRIBING, stage_progress=10.0, speed=value)

    raw = channel.getvalue()
    assert "NaN" not in raw
    assert "Infinity" not in raw

    event = channel.events()[0]
    assert event["type"] == "progress"
    assert event["speed"] is None


def test_a_non_finite_value_nested_in_a_payload_is_repaired(channel) -> None:
    protocol.emit_hardware({"gpus": [{"index": 0, "load": float("nan")}], "cudaAvailable": True})

    event = channel.events()[0]
    assert event["gpus"][0]["load"] is None
    assert event["cudaAvailable"] is True


def test_concurrent_emits_do_not_interleave(channel) -> None:
    def emit_many(index: int) -> None:
        for i in range(50):
            protocol.emit_log(f"worker {index} line {i} " + "가" * 40)

    threads = [threading.Thread(target=emit_many, args=(n,)) for n in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    lines = channel.lines()
    assert len(lines) == 400
    for line in lines:
        assert json.loads(line)["type"] == "log"


# ---------------------------------------------------------------------------
# progress arithmetic
# ---------------------------------------------------------------------------


def test_stage_weights_sum_to_one() -> None:
    assert sum(protocol.STAGE_WEIGHTS.values()) == pytest.approx(1.0)


@pytest.mark.parametrize(
    ("stage", "stage_progress", "expected"),
    [
        (protocol.Stages.PROBING, 0.0, 0.0),
        (protocol.Stages.PROBING, 100.0, 2.0),
        (protocol.Stages.EXTRACTING_AUDIO, 100.0, 10.0),
        (protocol.Stages.TRANSCRIBING, 0.0, 10.0),
        (protocol.Stages.TRANSCRIBING, 100.0, 65.0),
        (protocol.Stages.TRANSLATING, 50.0, 81.0),
        (protocol.Stages.TRANSLATING, 100.0, 97.0),
        (protocol.Stages.WRITING_SUBTITLE, 100.0, 100.0),
    ],
)
def test_overall_progress_matches_the_domain_calculator(
    stage: str, stage_progress: float, expected: float
) -> None:
    assert protocol.overall_progress(stage, stage_progress) == pytest.approx(expected)


def test_overall_progress_clamps_out_of_range_input() -> None:
    assert protocol.overall_progress(protocol.Stages.TRANSCRIBING, -50.0) == 10.0
    assert protocol.overall_progress(protocol.Stages.TRANSCRIBING, 500.0) == 65.0
    assert protocol.overall_progress("nonsense", 50.0) == 0.0


def test_progress_event_carries_both_percentages(channel) -> None:
    protocol.emit_progress(stage=protocol.Stages.TRANSLATING, stage_progress=50.0, speed=3.25)
    event = channel.events()[0]
    assert event["stageProgress"] == 50.0
    assert event["overallProgress"] == pytest.approx(81.0)
    assert event["speed"] == 3.25


# ---------------------------------------------------------------------------
# names + versions
# ---------------------------------------------------------------------------


def test_wire_names_match_the_csharp_constants() -> None:
    # 1.1: settings.outputConflictPolicy + process.subtitleLanguage.
    # 1.2: hardware.cudaDeviceDetected / cudaLibrariesAvailable / missingCudaLibraries.
    # 1.3: the extractAudio command.
    # 1.4: settings.initialPrompt.
    # 1.5: sourceMode "externalSubtitle" + process.subtitlePath.
    # 1.6: settings.saveSourceSubtitle.
    # Keep in step with ProtocolConstants.Version on the C# side.
    assert protocol.PROTOCOL_VERSION == "1.6"
    assert protocol.SourceModes.EXTERNAL_SUBTITLE == "externalSubtitle"
    assert protocol.Commands.DETECT_HARDWARE == "detectHardware"
    assert protocol.Commands.EXTRACT_AUDIO == "extractAudio"
    assert protocol.Commands.EXTRACT_AUDIO in protocol.Commands.ALL
    assert protocol.Commands.CANCEL_DOWNLOAD == "cancelDownload"
    assert protocol.Events.LANGUAGE_DETECTED == "languageDetected"
    assert protocol.Events.DOWNLOAD_COMPLETED == "downloadCompleted"
    assert protocol.Stages.EXTRACTING_AUDIO == "extractingAudio"
    assert protocol.Stages.WRITING_SUBTITLE == "writingSubtitle"
    assert protocol.SourceModes.EMBEDDED_SUBTITLE == "embeddedSubtitle"
    assert protocol.Phases.ALL == {"full", "transcribe", "translate"}


@pytest.mark.parametrize(
    ("host", "compatible"),
    [("1.0", True), ("1.4", True), ("2.0", False), ("", False), (None, False)],
)
def test_version_compatibility(host: str | None, compatible: bool) -> None:
    ok, _ = protocol.is_compatible(host)
    assert ok is compatible


def test_minor_version_drift_warns_but_stays_compatible() -> None:
    ok, warning = protocol.is_compatible("1.7")
    assert ok is True
    assert warning is not None and "1.7" in warning


def test_error_detail_is_bounded(channel) -> None:
    protocol.emit_error(code="UNKNOWN", message="오류", detail="x" * 10_000)
    assert len(channel.events()[0]["detail"]) == 4000
