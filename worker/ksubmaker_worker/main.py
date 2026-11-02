"""The worker's command loop.

stdin is read on the **main thread**; each ``process`` / ``downloadModel`` job runs on a single
background worker thread. That split is what lets ``cancel`` and ``shutdown`` be handled while a
job is mid-inference — the alternative (running jobs inline) means a cancel sits unread in the
pipe until the thing it is cancelling has finished.

Only one job runs at a time: two concurrent CUDA jobs would fight over the same VRAM and both
would fail.

``extractAudio`` (v1.3) is the single exception, and gets a lane of its own. Pulling audio out of a
container is ffmpeg work — CPU and disk, no VRAM — so it can run while the GPU transcribes the
previous file, and the VRAM argument above simply does not apply to it. One at a time there too:
the limit is the disk, not the CPU, and a second concurrent demux would only make both slower.
"""

from __future__ import annotations

import json
import platform
import signal
import sys
import threading
from typing import Any, Mapping, TextIO

from . import __version__, cuda_setup, errors, protocol
from .cancellation import GLOBAL_PROCESSES, CancellationToken
from .commands import CommandHandlers
from .logging_setup import configure, get_logger
from .protocol import Commands

_log = get_logger("main")

#: How long ``shutdown`` waits for a running job to notice cancellation before exiting anyway.
SHUTDOWN_JOIN_TIMEOUT = 20.0

#: The same for the prefetch lane. Much shorter: killing an ffmpeg child is immediate, unlike a
#: CUDA kernel that cannot be interrupted from Python.
EXTRACT_JOIN_TIMEOUT = 5.0


class Worker:
    """Owns the command loop, the single job thread and the process-wide cancellation state."""

    def __init__(
        self,
        handlers: CommandHandlers | None = None,
        *,
        stdin: TextIO | None = None,
    ) -> None:
        self.handlers = handlers or CommandHandlers()
        self._stdin = stdin
        self._stop = threading.Event()
        self._job_lock = threading.Lock()
        self._job_thread: threading.Thread | None = None
        self._job_token: CancellationToken | None = None
        self._job_id: str | None = None

        # The prefetch lane. Deliberately its own lock and thread: taking _job_lock here would
        # serialise extraction behind the running job and defeat the entire point.
        self._extract_lock = threading.Lock()
        self._extract_thread: threading.Thread | None = None
        self._extract_token: CancellationToken | None = None
        self._extract_job_id: str | None = None

        self.exit_code = 0

    # -----------------------------------------------------------------------
    # lifecycle
    # -----------------------------------------------------------------------

    def run(self) -> int:
        """Read commands until stdin closes or ``shutdown`` arrives. Returns the exit code."""
        stream = self._stdin if self._stdin is not None else sys.stdin

        protocol.emit_ready(
            worker_version=__version__,
            python_version=platform.python_version(),
            capabilities=protocol.CAPABILITIES,
        )
        _log.info("worker %s ready (protocol %s)", __version__, protocol.PROTOCOL_VERSION)

        try:
            for line in stream:
                if self._stop.is_set():
                    break

                command = self._parse(line)
                if command is None:
                    continue

                try:
                    self._dispatch(command)
                except Exception as exc:  # noqa: BLE001 - one bad command must not kill the loop
                    _log.exception("command dispatch failed")
                    protocol.emit_error(
                        code=errors.WORKER_CRASHED,
                        message="명령을 처리하지 못했습니다.",
                        recoverable=True,
                        detail=repr(exc),
                        request_id=command.get("requestId"),
                    )

                if self._stop.is_set():
                    break
        except KeyboardInterrupt:
            _log.info("interrupted; shutting down")
            self.request_stop()
        except (OSError, ValueError) as exc:
            # stdin died under us: the host went away, so exit cleanly rather than crash.
            _log.warning("stdin read failed: %r", exc)
            self.request_stop()

        self._finish()
        return self.exit_code

    def _finish(self) -> None:
        self._cancel_job()
        self._join_job(SHUTDOWN_JOIN_TIMEOUT)

        # After the job: an ffmpeg child that outlives the worker would keep writing into a cache
        # directory nobody owns any more. It dies fast, so it gets a short leash rather than the
        # full timeout.
        self._join_extraction(EXTRACT_JOIN_TIMEOUT)

        try:
            self.handlers.shutdown()
        except Exception as exc:  # noqa: BLE001
            _log.warning("handler shutdown failed: %r", exc)

        GLOBAL_PROCESSES.terminate_all()

        protocol.emit_goodbye()
        _log.info("worker stopped")

    def request_stop(self) -> None:
        """Ask the loop to exit after the current command."""
        self._stop.set()

    def cancel_current_job(self) -> None:
        """Cancel whatever is running. Safe to call from a signal handler."""
        self._cancel_job()

    # -----------------------------------------------------------------------
    # parsing / dispatch
    # -----------------------------------------------------------------------

    @staticmethod
    def _parse(line: str) -> dict[str, Any] | None:
        """Parse one stdin line. A malformed line is logged and skipped, never fatal."""
        text = (line or "").strip()
        if not text:
            return None

        if text[0] != "{":
            _log.warning("ignoring non-JSON input line: %.120r", text)
            return None

        try:
            payload = json.loads(text)
        except json.JSONDecodeError as exc:
            _log.warning("ignoring malformed input line (%s): %.120r", exc, text)
            return None

        if not isinstance(payload, dict):
            _log.warning("ignoring input line that is not a JSON object: %.120r", text)
            return None

        command = payload.get("command")
        if not isinstance(command, str) or not command:
            _log.warning("ignoring input line with no command field: %.120r", text)
            protocol.emit_error(
                code=errors.PROTOCOL_ERROR,
                message="명령 형식이 올바르지 않습니다.",
                detail="missing 'command' field",
                request_id=payload.get("requestId") if isinstance(payload.get("requestId"), str) else None,
            )
            return None

        return payload

    def _dispatch(self, command: Mapping[str, Any]) -> None:
        name = str(command.get("command"))
        request_id = command.get("requestId")

        if name == Commands.HELLO:
            self.handlers.hello(command)

        elif name == Commands.DETECT_HARDWARE:
            self.handlers.detect_hardware(command)

        elif name == Commands.PROBE:
            self.handlers.probe(command)

        elif name == Commands.LIST_MODELS:
            self.handlers.list_models(command)

        elif name == Commands.DELETE_MODEL:
            self.handlers.delete_model(command)

        elif name == Commands.CANCEL_DOWNLOAD:
            self.handlers.cancel_download(command)

        elif name == Commands.PROCESS:
            self._start_job(
                command,
                lambda token: self.handlers.process(command, token),
                job_id=str(command.get("jobId") or ""),
            )

        elif name == Commands.EXTRACT_AUDIO:
            self._start_extraction(command)

        elif name == Commands.DOWNLOAD_MODEL:
            self._start_job(
                command,
                lambda token: self.handlers.download_model(command, token),
                job_id=str(command.get("modelId") or ""),
            )

        elif name == Commands.VERIFY_MODEL:
            self._start_job(
                command,
                lambda token: self.handlers.verify_model(command, token),
                job_id=str(command.get("modelId") or ""),
            )

        elif name == Commands.CANCEL:
            protocol.emit_ack(name, request_id)
            self._handle_cancel(command)

        elif name == Commands.SHUTDOWN:
            protocol.emit_ack(name, request_id)
            self.request_stop()

        else:
            _log.warning("unknown command %r", name)
            protocol.emit_error(
                code=errors.PROTOCOL_ERROR,
                message=f"알 수 없는 명령입니다: {name}",
                detail=f"unknown command {name!r}",
                request_id=request_id,
            )

    # -----------------------------------------------------------------------
    # job thread
    # -----------------------------------------------------------------------

    def _start_job(self, command: Mapping[str, Any], body: Any, *, job_id: str) -> None:
        request_id = command.get("requestId")

        with self._job_lock:
            if self._job_thread is not None and self._job_thread.is_alive():
                protocol.emit_error(
                    code=errors.PROTOCOL_ERROR,
                    message="이미 실행 중인 작업이 있습니다. 완료된 뒤 다시 시도하세요.",
                    detail=f"job {self._job_id!r} is still running",
                    request_id=request_id,
                    job_id=job_id or None,
                )
                return

            token = CancellationToken(job_id or "job")
            self._job_token = token
            self._job_id = job_id or None

            def target() -> None:
                try:
                    body(token)
                except Exception as exc:  # noqa: BLE001 - the thread must never die silently
                    _log.exception("job thread crashed")
                    protocol.emit_error(
                        code=errors.WORKER_CRASHED,
                        message="AI 작업 중 예기치 않은 오류가 발생했습니다.",
                        recoverable=True,
                        detail=repr(exc),
                        request_id=request_id,
                        job_id=job_id or None,
                    )

            thread = threading.Thread(target=target, name=f"ksm-job-{job_id or 'x'}", daemon=True)
            self._job_thread = thread

        protocol.emit_ack(str(command.get("command")), request_id, job_id or None)
        thread.start()

    # -----------------------------------------------------------------------
    # extraction lane (v1.3)
    # -----------------------------------------------------------------------

    def _start_extraction(self, command: Mapping[str, Any]) -> None:
        """Run one prefetch extraction on the lane thread, alongside any running job."""
        request_id = command.get("requestId")
        job_id = str(command.get("jobId") or "")

        with self._extract_lock:
            if self._extract_thread is not None and self._extract_thread.is_alive():
                # One at a time. The host sends the next one when this reports back, so refusing
                # is a real answer rather than a dropped request.
                protocol.emit_error(
                    code=errors.PROTOCOL_ERROR,
                    message="이미 진행 중인 음성 추출이 있습니다.",
                    detail=f"extraction for {self._extract_job_id!r} is still running",
                    request_id=request_id,
                    job_id=job_id or None,
                )
                return

            token = CancellationToken(job_id or "extract")
            self._extract_token = token
            self._extract_job_id = job_id or None

            def target() -> None:
                try:
                    self.handlers.extract_audio(command, token)
                except Exception:  # noqa: BLE001 - the lane must never die silently
                    _log.exception("extraction thread crashed")
                    protocol.emit_error(
                        code=errors.WORKER_CRASHED,
                        message="음성 추출 중 예기치 않은 오류가 발생했습니다.",
                        recoverable=True,
                        request_id=request_id,
                        job_id=job_id or None,
                    )
                finally:
                    with self._extract_lock:
                        self._extract_token = None
                        self._extract_job_id = None

            thread = threading.Thread(target=target, name=f"ksm-extract-{job_id or 'x'}", daemon=True)
            self._extract_thread = thread

        protocol.emit_ack(Commands.EXTRACT_AUDIO, request_id)
        thread.start()

    def _cancel_extraction(self, job_id: str | None = None) -> bool:
        """Cancel the running extraction. With a job id, only if it is that job's."""
        with self._extract_lock:
            token = self._extract_token
            current = self._extract_job_id

        if token is None:
            return False

        if job_id and current and job_id != current:
            return False

        token.cancel()
        return True

    def _join_extraction(self, timeout: float) -> None:
        with self._extract_lock:
            thread = self._extract_thread

        if thread is not None and thread.is_alive():
            thread.join(timeout)
            if thread.is_alive():
                _log.warning("extraction thread did not stop in time; exiting anyway")

    def _handle_cancel(self, command: Mapping[str, Any]) -> None:
        requested = command.get("jobId")

        with self._job_lock:
            token = self._job_token
            current = self._job_id

        # Before the job checks: this job may have no *job* running and still have a prefetch
        # extraction in flight. Cancelling only the job would leave that ffmpeg writing audio for a
        # file the user just cancelled.
        wanted = requested if isinstance(requested, str) and requested else None
        cancelled_extraction = self._cancel_extraction(wanted)

        if token is None:
            if cancelled_extraction:
                _log.info("cancel stopped the prefetch extraction for %s", wanted or "?")
            else:
                _log.info("cancel received with no job running")
            protocol.emit_cancelled(
                request_id=command.get("requestId"),
                job_id=requested if isinstance(requested, str) else None,
            )
            return

        if isinstance(requested, str) and requested and current and requested != current:
            if cancelled_extraction:
                # Not a stray cancel after all — it matched the prefetch lane.
                return

            _log.info("cancel for %s ignored; %s is running", requested, current)
            protocol.emit_log(
                f"취소할 작업을 찾지 못했습니다: {requested}",
                "warn",
                request_id=command.get("requestId"),
            )
            return

        # The job thread turns the token into a `cancelled` event; the download path does the
        # same. Emitting one here too would double-report.
        token.cancel()

        # Anything the model manager started outside a job thread also has to stop.
        if isinstance(requested, str) and requested:
            self.handlers.model_manager.cancel_download(requested)

    def _cancel_job(self) -> None:
        with self._job_lock:
            token = self._job_token
        if token is not None:
            token.cancel()

        # "Cancel whatever is running" has to mean the prefetch too, or a shutdown leaves an ffmpeg
        # running against a file the user just gave up on.
        self._cancel_extraction()

    def _join_job(self, timeout: float) -> None:
        with self._job_lock:
            thread = self._job_thread

        if thread is not None and thread.is_alive():
            _log.info("waiting up to %.0fs for the running job to stop", timeout)
            thread.join(timeout)
            if thread.is_alive():
                # Nothing more we can do: a CUDA kernel cannot be interrupted from Python.
                _log.warning("job thread did not stop in time; exiting anyway")


# ---------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------


def _install_signal_handlers(worker: Worker) -> None:
    def handler(signum: int, _frame: Any) -> None:
        _log.info("signal %s received; cancelling and shutting down", signum)
        worker.cancel_current_job()
        worker.request_stop()

    for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        signum = getattr(signal, name, None)
        if signum is None:
            continue
        try:
            signal.signal(signum, handler)
        except (ValueError, OSError, RuntimeError) as exc:
            # Not the main thread, or a platform without this signal: not fatal.
            _log.debug("could not install a handler for %s: %r", name, exc)


#: Native-extension packages imported up front, in this order. numpy first: ctranslate2 imports it.
PRELOADED_MODULES: tuple[str, ...] = ("numpy", "ctranslate2", "faster_whisper")


def preload_native_modules() -> list[str]:
    """Import the heavy native packages on the main thread, before any command is read.

    Observed on a user machine (2026-11-02): a job thread wedged for 10+ minutes inside
    ``LoadLibraryExW`` for ``numpy/_core/_multiarray_umath.pyd`` — reached via
    ``transcriber._resolve_device`` → ``import ctranslate2`` — while the prefetch lane was starting
    ffmpeg on another thread and the main thread sat in a blocking ``ReadFile`` on stdin. No
    progress event, no exit; the host's idle watchdog only fires after 15 minutes. The lazy imports
    existed to keep startup cheap, but together they cost ~0.15s; doing them here means no DLL is
    ever mapped while other threads are creating processes or holding the loader.

    A failure is logged and ignored: the lazy import at the point of use still raises the real,
    user-facing error (missing package, missing CUDA DLL) exactly as before.
    """
    loaded: list[str] = []
    for name in PRELOADED_MODULES:
        try:
            __import__(name)
            loaded.append(name)
        except Exception as exc:  # noqa: BLE001 - diagnostics only; see docstring
            _log.warning("preloading %s failed; it will be imported on first use: %r", name, exc)
    _log.info("preloaded native modules: %s", ", ".join(loaded) or "-")
    return loaded


def main(argv: list[str] | None = None) -> int:
    """Process entry point. Returns the exit code.

    The order of the first three calls is load-bearing and is asserted by ``test_main_loop``:

    1. ``install_stdout_guard`` — everything below may import a library that prints on import.
    2. ``configure`` — so the CUDA report below has somewhere to go.
    3. ``cuda_setup.ensure_registered`` — **before any command is handled.** The imports of
       ``ctranslate2`` / ``faster_whisper`` in transcriber.py, translator.py and
       hardware_detector.py are all lazy, so no CUDA DLL has been resolved yet; once one has, the
       Windows loader will not look again. Registering here, rather than relying on whichever
       module happens to import first, is what makes the ordering deliberate instead of accidental.
    """
    protocol.install_stdout_guard()
    configure()

    report = cuda_setup.ensure_registered()
    # stderr only: this is a diagnostic, and stdout is the protocol channel (AGENTS.md §4).
    _log.info("%s", report.summary())
    if report.windows and report.missing:
        _log.warning(
            "CUDA support libraries missing: %s. GPU jobs will fail until they are installed "
            "(scripts/build-worker.ps1).",
            ", ".join(report.missing),
        )

    preload_native_modules()

    args = list(argv if argv is not None else sys.argv[1:])
    if "--version" in args:
        protocol.emit_log(f"ksubmaker-worker {__version__} (protocol {protocol.PROTOCOL_VERSION})")
        return 0

    worker = Worker()
    _install_signal_handlers(worker)

    try:
        return worker.run()
    except Exception as exc:  # noqa: BLE001 - a crash here still has to report through the protocol
        _log.exception("fatal worker error")
        protocol.emit_error(
            code=errors.WORKER_CRASHED,
            message="AI 작업 프로세스가 예기치 않게 종료되었습니다.",
            recoverable=True,
            detail=repr(exc),
        )
        protocol.emit_goodbye()
        return 1


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
