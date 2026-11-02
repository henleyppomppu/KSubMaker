"""The stdin command loop: parsing, dispatch, cancellation and shutdown."""

from __future__ import annotations

import io
import json
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import errors, protocol
from ksubmaker_worker.cancellation import CancellationToken
from ksubmaker_worker.main import Worker

REPO_WORKER_DIR = str(Path(__file__).resolve().parents[1])


class FakeHandlers:
    """Records what the loop dispatched, without doing any real work."""

    def __init__(self) -> None:
        self.calls: list[str] = []
        self.model_manager = _FakeModelManager()
        self.block = threading.Event()
        self.started = threading.Event()
        self.observed_cancel = threading.Event()
        self.extract_block = threading.Event()
        self.extract_started = threading.Event()
        self.shutdown_called = False

    def hello(self, command: dict[str, Any]) -> None:
        self.calls.append("hello")
        protocol.emit_ack("hello", command.get("requestId"))

    def detect_hardware(self, command: dict[str, Any]) -> None:
        self.calls.append("detectHardware")
        protocol.emit_hardware({"gpus": [], "cudaAvailable": False}, request_id=command.get("requestId"))

    def probe(self, command: dict[str, Any]) -> None:
        self.calls.append("probe")
        protocol.emit_probe_result({"videoPath": "x", "durationSeconds": 1.0}, request_id=command.get("requestId"))

    def list_models(self, command: dict[str, Any]) -> None:
        self.calls.append("listModels")
        protocol.emit_model_list([], request_id=command.get("requestId"))

    def delete_model(self, command: dict[str, Any]) -> None:
        self.calls.append("deleteModel")
        protocol.emit_model_list([], request_id=command.get("requestId"))

    def cancel_download(self, command: dict[str, Any]) -> None:
        self.calls.append("cancelDownload")

    def download_model(self, command: dict[str, Any], token: CancellationToken) -> None:
        self.calls.append("downloadModel")
        protocol.emit_download_completed(model_id="m", request_id=command.get("requestId"))

    def verify_model(self, command: dict[str, Any], token: CancellationToken) -> None:
        self.calls.append("verifyModel")
        protocol.emit_model_list([], request_id=command.get("requestId"))

    def process(self, command: dict[str, Any], token: CancellationToken) -> None:
        self.calls.append("process")
        self.started.set()

        # Simulate a long job that polls the token, exactly as the real pipeline does.
        deadline = time.monotonic() + 10.0
        while time.monotonic() < deadline:
            if token.cancelled:
                self.observed_cancel.set()
                protocol.emit_cancelled(
                    request_id=command.get("requestId"), job_id=command.get("jobId")
                )
                return
            if self.block.wait(0.02):
                break

        protocol.emit_completed(
            output_path="/tmp/a.ko.srt",
            cue_count=1,
            request_id=command.get("requestId"),
            job_id=command.get("jobId"),
        )

    def extract_audio(self, command: dict[str, Any], token: CancellationToken) -> None:
        self.calls.append("extractAudio")
        self.extract_started.set()
        self.extract_block.wait(10.0)
        protocol.emit_completed(
            output_path="",
            cue_count=0,
            request_id=command.get("requestId"),
            job_id=command.get("jobId"),
            skipped=True,
        )

    def shutdown(self) -> None:
        self.shutdown_called = True


class _FakeModelManager:
    def __init__(self) -> None:
        self.cancelled: list[str] = []

    def cancel_download(self, model_id: str) -> bool:
        self.cancelled.append(model_id)
        return True


def _lines(*commands: dict[str, Any]) -> io.StringIO:
    return io.StringIO("".join(json.dumps(c) + "\n" for c in commands))


# ---------------------------------------------------------------------------
# startup
# ---------------------------------------------------------------------------


def test_ready_is_emitted_first(channel) -> None:
    Worker(FakeHandlers(), stdin=_lines({"command": "shutdown", "requestId": "r"})).run()

    events = channel.events()
    ready = events[0]
    assert ready["type"] == "ready"
    assert ready["protocolVersion"] == protocol.PROTOCOL_VERSION
    assert ready["workerVersion"]
    assert ready["pythonVersion"]
    assert set(ready["capabilities"]) == set(protocol.CAPABILITIES)


def test_goodbye_is_emitted_last(channel) -> None:
    Worker(FakeHandlers(), stdin=_lines({"command": "shutdown", "requestId": "r"})).run()
    assert channel.events()[-1]["type"] == "goodbye"


def test_shutdown_returns_zero_and_releases_the_handlers(channel) -> None:
    handlers = FakeHandlers()
    code = Worker(handlers, stdin=_lines({"command": "shutdown", "requestId": "r"})).run()

    assert code == 0
    assert handlers.shutdown_called is True


def test_closed_stdin_exits_cleanly(channel) -> None:
    assert Worker(FakeHandlers(), stdin=io.StringIO("")).run() == 0
    assert channel.first("goodbye") is not None


# ---------------------------------------------------------------------------
# malformed input
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "bad",
    [
        "not json at all",
        "{unclosed",
        '{"command": }',
        "[1, 2, 3]",
        '"just a string"',
        "   ",
        "\x00\x01binary garbage",
    ],
)
def test_a_malformed_line_does_not_kill_the_loop(channel, bad: str) -> None:
    stdin = io.StringIO(
        bad + "\n" + json.dumps({"command": "hello", "requestId": "r1"}) + "\n"
        + json.dumps({"command": "shutdown", "requestId": "r2"}) + "\n"
    )
    handlers = FakeHandlers()

    assert Worker(handlers, stdin=stdin).run() == 0
    assert handlers.calls == ["hello"]
    assert channel.events()[-1]["type"] == "goodbye"


def test_every_stdout_line_is_still_valid_json_after_bad_input(channel) -> None:
    stdin = io.StringIO(
        "garbage\n"
        + json.dumps({"command": "detectHardware", "requestId": "r1"})
        + "\n{\n"
        + json.dumps({"command": "shutdown", "requestId": "r2"})
        + "\n"
    )
    Worker(FakeHandlers(), stdin=stdin).run()

    for line in channel.lines():
        parsed = json.loads(line)
        assert isinstance(parsed, dict)
        assert isinstance(parsed.get("type"), str)


def test_a_line_with_no_command_field_reports_a_protocol_error(channel) -> None:
    stdin = io.StringIO(
        json.dumps({"requestId": "r1", "notACommand": True})
        + "\n"
        + json.dumps({"command": "shutdown", "requestId": "r2"})
        + "\n"
    )
    Worker(FakeHandlers(), stdin=stdin).run()

    error = channel.first("error")
    assert error is not None
    assert error["code"] == "PROTOCOL_ERROR"
    assert error["requestId"] == "r1"


def test_an_unknown_command_reports_a_protocol_error(channel) -> None:
    stdin = _lines(
        {"command": "danceTheTango", "requestId": "r1"},
        {"command": "shutdown", "requestId": "r2"},
    )
    Worker(FakeHandlers(), stdin=stdin).run()

    error = channel.first("error")
    assert error is not None and error["code"] == "PROTOCOL_ERROR"
    assert error["requestId"] == "r1"


def test_a_handler_that_throws_does_not_kill_the_loop(channel) -> None:
    handlers = FakeHandlers()

    def boom(command: dict[str, Any]) -> None:
        raise RuntimeError("handler exploded")

    handlers.probe = boom  # type: ignore[assignment]

    stdin = _lines(
        {"command": "probe", "requestId": "r1", "videoPath": "/tmp/x.mkv"},
        {"command": "hello", "requestId": "r2"},
        {"command": "shutdown", "requestId": "r3"},
    )
    assert Worker(handlers, stdin=stdin).run() == 0

    error = channel.first("error")
    assert error is not None and error["code"] == "WORKER_CRASHED"
    assert "hello" in handlers.calls


# ---------------------------------------------------------------------------
# dispatch
# ---------------------------------------------------------------------------


def test_synchronous_commands_are_dispatched(channel) -> None:
    handlers = FakeHandlers()
    stdin = _lines(
        {"command": "hello", "requestId": "r1", "protocolVersion": "1.0"},
        {"command": "detectHardware", "requestId": "r2"},
        {"command": "probe", "requestId": "r3", "videoPath": "/tmp/x.mkv"},
        {"command": "listModels", "requestId": "r4"},
        {"command": "deleteModel", "requestId": "r5", "modelId": "m", "targetDir": "/tmp"},
        {"command": "cancelDownload", "requestId": "r6", "modelId": "m"},
        {"command": "shutdown", "requestId": "r7"},
    )
    Worker(handlers, stdin=stdin).run()

    assert handlers.calls == [
        "hello",
        "detectHardware",
        "probe",
        "listModels",
        "deleteModel",
        "cancelDownload",
    ]


def test_every_reply_echoes_its_request_id(channel) -> None:
    stdin = _lines(
        {"command": "detectHardware", "requestId": "hw-1"},
        {"command": "listModels", "requestId": "lm-1"},
        {"command": "shutdown", "requestId": "sd-1"},
    )
    Worker(FakeHandlers(), stdin=stdin).run()

    assert channel.first("hardware")["requestId"] == "hw-1"
    assert channel.first("modelList")["requestId"] == "lm-1"
    assert channel.first("ack")["requestId"] == "sd-1"


def test_a_job_runs_on_a_background_thread_and_is_acked(channel) -> None:
    handlers = FakeHandlers()
    handlers.block.set()  # let the job finish immediately

    stdin = _lines(
        {"command": "process", "requestId": "r1", "jobId": "j1"},
        {"command": "shutdown", "requestId": "r2"},
    )
    Worker(handlers, stdin=stdin).run()

    ack = channel.first("ack")
    assert ack is not None and ack["command"] == "process" and ack["jobId"] == "j1"
    assert channel.first("completed") is not None


def test_a_job_sent_while_the_previous_thread_is_still_unwinding_is_accepted(channel) -> None:
    """The host sends the next ``process`` as soon as it reads ``completed``; the thread that sent
    it may not have returned yet. That used to be refused with PROTOCOL_ERROR (2026-11-02: two
    files failed in 0.0s right after a job that finished in 1.1s)."""
    started: list[str] = []

    class SlowToReturn(FakeHandlers):
        def process(self, command: dict[str, Any], token: CancellationToken) -> None:
            started.append(str(command.get("jobId")))
            protocol.emit_completed(
                output_path="", cue_count=0, request_id=command.get("requestId"), job_id=command.get("jobId")
            )
            time.sleep(0.3)  # cleanup after the terminal event, still on the job thread

    handlers = SlowToReturn()
    worker = Worker(handlers)

    worker._start_job(  # noqa: SLF001
        {"command": "process", "requestId": "r1", "jobId": "j1"},
        lambda token: handlers.process({"requestId": "r1", "jobId": "j1"}, token),
        job_id="j1",
    )
    # Immediately, like the host does on reading `completed`.
    worker._start_job(  # noqa: SLF001
        {"command": "process", "requestId": "r2", "jobId": "j2"},
        lambda token: handlers.process({"requestId": "r2", "jobId": "j2"}, token),
        job_id="j2",
    )
    worker._join_job(5.0)  # noqa: SLF001

    assert started == ["j1", "j2"]
    assert not any(e["code"] == "PROTOCOL_ERROR" for e in channel.of_type("error"))


def test_a_second_job_is_refused_while_one_is_running(channel, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(Worker, "JOB_HANDOFF_GRACE_SECONDS", 0.2)
    handlers = FakeHandlers()
    worker = Worker(
        handlers,
        stdin=_lines(
            {"command": "process", "requestId": "r1", "jobId": "j1"},
            {"command": "process", "requestId": "r2", "jobId": "j2"},
            {"command": "cancel", "requestId": "r3"},
            {"command": "shutdown", "requestId": "r4"},
        ),
    )
    worker.run()

    errors_seen = channel.of_type("error")
    assert any(e["code"] == "PROTOCOL_ERROR" and e["requestId"] == "r2" for e in errors_seen)


# ---------------------------------------------------------------------------
# cancellation
# ---------------------------------------------------------------------------


def test_cancel_stops_a_running_job(channel) -> None:
    handlers = FakeHandlers()

    stdin = _WaitingStdin(
        [
            {"command": "process", "requestId": "r1", "jobId": "j1"},
            {"command": "cancel", "requestId": "r2", "jobId": "j1"},
            {"command": "shutdown", "requestId": "r3"},
        ],
        gate=handlers.started,
        gate_after=1,
    )

    worker = Worker(handlers, stdin=stdin)
    worker.run()

    assert handlers.observed_cancel.is_set()
    assert channel.first("cancelled") is not None
    assert channel.first("completed") is None


def test_cancel_for_a_different_job_is_ignored(channel) -> None:
    handlers = FakeHandlers()

    stdin = _WaitingStdin(
        [
            {"command": "process", "requestId": "r1", "jobId": "j1"},
            {"command": "cancel", "requestId": "r2", "jobId": "some-other-job"},
        ],
        gate=handlers.started,
        gate_after=1,
    )

    worker = Worker(handlers, stdin=stdin)
    thread = threading.Thread(target=worker.run, daemon=True)
    thread.start()

    handlers.started.wait(5)
    time.sleep(0.2)
    assert not handlers.observed_cancel.is_set()

    handlers.block.set()
    worker.request_stop()
    stdin.close_now()
    thread.join(10)


def test_cancel_with_no_job_running_still_replies(channel) -> None:
    stdin = _lines(
        {"command": "cancel", "requestId": "r1"},
        {"command": "shutdown", "requestId": "r2"},
    )
    Worker(FakeHandlers(), stdin=stdin).run()

    assert channel.first("cancelled") is not None


def test_shutdown_cancels_a_running_job(channel) -> None:
    handlers = FakeHandlers()

    stdin = _WaitingStdin(
        [
            {"command": "process", "requestId": "r1", "jobId": "j1"},
            {"command": "shutdown", "requestId": "r2"},
        ],
        gate=handlers.started,
        gate_after=1,
    )

    assert Worker(handlers, stdin=stdin).run() == 0
    assert handlers.observed_cancel.is_set()
    assert channel.events()[-1]["type"] == "goodbye"


class _WaitingStdin:
    """Yields command lines, pausing after ``gate_after`` of them until ``gate`` is set.

    This is what makes "cancel arrives while a job is in flight" deterministic instead of a race
    against a sleep.
    """

    def __init__(self, commands: list[dict[str, Any]], *, gate: threading.Event, gate_after: int) -> None:
        self._commands = commands
        self._gate = gate
        self._gate_after = gate_after
        self._closed = threading.Event()

    def close_now(self) -> None:
        self._closed.set()

    def __iter__(self):  # noqa: ANN204
        for index, command in enumerate(self._commands):
            if index == self._gate_after:
                self._gate.wait(5)
            yield json.dumps(command) + "\n"

        # Keep the loop alive until the test says otherwise, mimicking an open pipe.
        self._closed.wait(10)


# ---------------------------------------------------------------------------
# end to end, as a real subprocess
# ---------------------------------------------------------------------------


def test_worker_boots_as_a_subprocess_and_writes_only_json() -> None:
    stdin = (
        '{"command":"hello","requestId":"r1","protocolVersion":"1.0"}\n'
        "this line is not json\n"
        '{"command":"listModels","requestId":"r2"}\n'
        '{"command":"shutdown","requestId":"r3"}\n'
    )

    completed = subprocess.run(  # noqa: S603 - list argv, shell=False
        [sys.executable, "-m", "ksubmaker_worker"],
        input=stdin,
        capture_output=True,
        text=True,
        timeout=120,
        env={"PYTHONPATH": REPO_WORKER_DIR, "PATH": "/usr/bin:/bin", "HOME": "/tmp"},
    )

    assert completed.returncode == 0

    events = [json.loads(line) for line in completed.stdout.splitlines() if line.strip()]
    types = [event["type"] for event in events]

    assert types[0] == "ready"
    assert types[-1] == "goodbye"
    assert "modelList" in types
    # The malformed line must have been logged to stderr, never echoed on stdout.
    assert all(isinstance(event, dict) for event in events)
    # The CUDA setup report is a diagnostic and belongs on stderr, not on the protocol channel.
    assert "cuda_setup:" in completed.stderr
    assert "cuda_setup" not in completed.stdout


# ---------------------------------------------------------------------------
# CUDA DLL registration ordering
# ---------------------------------------------------------------------------


def test_main_registers_the_cuda_dll_directories_before_the_first_command(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Ordering is load-bearing, not incidental.

    ``ctranslate2`` is imported lazily by transcriber / translator / hardware_detector, so the DLL
    search path must be extended before any of them runs. Once the Windows loader has failed to
    resolve a dependency it does not try again, so "register later" means "never".
    """
    from ksubmaker_worker import cuda_setup, main as main_module

    events: list[str] = []

    def fake_register(**_kwargs: Any) -> cuda_setup.CudaSetupReport:
        events.append("register")
        return cuda_setup.CudaSetupReport(windows=False)

    monkeypatch.setattr(cuda_setup, "ensure_registered", fake_register)
    monkeypatch.setattr(main_module, "preload_native_modules", lambda: events.append("preload"))

    class RecordingWorker:
        def __init__(self) -> None:
            events.append("worker-constructed")

        def run(self) -> int:
            events.append("run")
            return 0

        def cancel_current_job(self) -> None:  # pragma: no cover - signal handler only
            pass

        def request_stop(self) -> None:  # pragma: no cover - signal handler only
            pass

    monkeypatch.setattr(main_module, "Worker", RecordingWorker)

    assert main_module.main([]) == 0
    # preload after register (the DLL search path must be extended first) and before the worker
    # reads its first command (so no job thread ever maps a native DLL).
    assert events == ["register", "preload", "worker-constructed", "run"]


def test_preloading_imports_the_native_modules_and_never_raises(monkeypatch: pytest.MonkeyPatch) -> None:
    """A job thread once hung inside LoadLibrary importing numpy (2026-11-02). Importing on the main
    thread at startup removes the window; a missing package must still not stop the worker."""
    import builtins

    from ksubmaker_worker import main as main_module

    real_import = builtins.__import__
    seen: list[str] = []

    def fake_import(name: str, *args: Any, **kwargs: Any) -> Any:
        if name in main_module.PRELOADED_MODULES:
            seen.append(name)
            if name == "faster_whisper":
                raise ImportError("not installed")
            return None
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", fake_import)

    loaded = main_module.preload_native_modules()

    assert seen == list(main_module.PRELOADED_MODULES)
    assert loaded == ["numpy", "ctranslate2"]


# ---------------------------------------------------------------------------
# the prefetch lane (v1.3)
# ---------------------------------------------------------------------------


def test_extract_audio_runs_while_a_job_is_running(channel) -> None:
    """The premise of the whole feature.

    Every other command is serialised behind the job thread because two concurrent CUDA jobs would
    fight over VRAM. Extraction is ffmpeg — no GPU — so it gets its own lane, and if it ever ends up
    behind the job lock the feature silently stops doing anything at all.
    """
    handlers = FakeHandlers()
    worker = Worker(handlers)

    worker._start_job(  # noqa: SLF001 - driving the lane directly is the point
        {"command": "process", "requestId": "r1", "jobId": "job-1"},
        lambda token: handlers.process({"requestId": "r1", "jobId": "job-1"}, token),
        job_id="job-1",
    )
    assert handlers.started.wait(5.0), "the job never entered the processor"

    # Now, with that job deliberately still running:
    worker._start_extraction(  # noqa: SLF001
        {"command": "extractAudio", "requestId": "r2", "jobId": "job-2"}
    )

    assert handlers.extract_started.wait(5.0), "extraction was serialised behind the running job"

    handlers.extract_block.set()
    handlers.block.set()
    worker._join_extraction(5.0)  # noqa: SLF001
    worker._join_job(5.0)  # noqa: SLF001


def test_a_second_extraction_is_refused_while_one_is_running(channel) -> None:
    handlers = FakeHandlers()
    worker = Worker(handlers)

    worker._start_extraction({"command": "extractAudio", "requestId": "r1", "jobId": "job-1"})  # noqa: SLF001
    assert handlers.extract_started.wait(5.0)

    worker._start_extraction({"command": "extractAudio", "requestId": "r2", "jobId": "job-2"})  # noqa: SLF001

    # Refusing is a real answer the host can act on; dropping the request silently would leave it
    # waiting for an event that never arrives.
    error = channel.first("error")
    assert error is not None
    assert error["code"] == errors.PROTOCOL_ERROR

    handlers.extract_block.set()
    worker._join_extraction(5.0)  # noqa: SLF001


def test_cancel_stops_a_prefetch_when_no_job_is_running(channel) -> None:
    handlers = FakeHandlers()
    worker = Worker(handlers)

    worker._start_extraction({"command": "extractAudio", "requestId": "r1", "jobId": "job-1"})  # noqa: SLF001
    assert handlers.extract_started.wait(5.0)

    # Without reaching the lane this would report "no job running" and leave ffmpeg chewing on a
    # file the user has just given up on.
    worker._handle_cancel({"command": "cancel", "requestId": "r2", "jobId": "job-1"})  # noqa: SLF001

    with worker._extract_lock:  # noqa: SLF001
        token = worker._extract_token  # noqa: SLF001

    assert token is None or token.cancelled

    handlers.extract_block.set()
    worker._join_extraction(5.0)  # noqa: SLF001


def test_shutdown_waits_for_the_prefetch_lane(channel) -> None:
    handlers = FakeHandlers()
    worker = Worker(handlers)

    worker._start_extraction({"command": "extractAudio", "requestId": "r1", "jobId": "job-1"})  # noqa: SLF001
    assert handlers.extract_started.wait(5.0)

    handlers.extract_block.set()
    worker._finish()  # noqa: SLF001

    # An ffmpeg child outliving the worker would keep writing into a cache directory that no longer
    # belongs to anyone.
    with worker._extract_lock:  # noqa: SLF001
        thread = worker._extract_thread  # noqa: SLF001

    assert thread is None or not thread.is_alive()
