#!/usr/bin/env python3
"""A minimal stand-in for the real KSubMaker worker.

It speaks just enough of the JSON Lines protocol for the host's WorkerProcessClient to be exercised
for real: a `ready` handshake, request/response correlation by `requestId`, deliberate garbage on
stdout, and a clean `shutdown` -> `goodbye` -> exit(0).

Nothing here imports the production worker package, so the test does not depend on faster-whisper,
torch or any other heavyweight dependency being installed.
"""

from __future__ import annotations

import json
import os
import sys

PROTOCOL_VERSION = "1.2"


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def emit_garbage(label: str) -> None:
    """A stray print / warning / progress bar, exactly what the reader loop must survive."""
    sys.stdout.write(f"[stub] not json at all: {label}\n")
    sys.stdout.write("{ truncated json\n")
    sys.stdout.flush()


def dump_environment(path: str) -> None:
    """Record the path variables the host is supposed to inject.

    Written from inside the child process on purpose: that is the only way to prove the variables
    actually crossed the process boundary rather than just being set on a ProcessStartInfo.
    """
    interesting = ("KSUBMAKER_MODELS_DIR", "KSUBMAKER_TOOLS_DIR", "HF_HOME", "PYTHONIOENCODING")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump({name: os.environ.get(name) for name in interesting}, handle, ensure_ascii=False)


def main() -> int:
    pid_file = os.environ.get("KSUBMAKER_STUB_PID_FILE")
    if pid_file:
        with open(pid_file, "w", encoding="utf-8") as handle:
            handle.write(str(os.getpid()))

    environment_file = os.environ.get("KSUBMAKER_STUB_ENV_FILE")
    if environment_file:
        dump_environment(environment_file)

    # Something noisy before the handshake: the host must not choke on it.
    emit_garbage("before-ready")

    emit(
        {
            "type": "ready",
            "protocolVersion": os.environ.get("KSUBMAKER_STUB_PROTOCOL", PROTOCOL_VERSION),
            "workerVersion": "stub-0.1",
            "pythonVersion": ".".join(str(p) for p in sys.version_info[:3]),
            "capabilities": ["stub"],
        }
    )

    while True:
        # Explicit readline rather than `for line in sys.stdin`: no read-ahead buffering, so the
        # stub answers each command as soon as the host writes it.
        raw = sys.stdin.readline()
        if raw == "":
            break

        line = raw.strip()
        if not line:
            continue

        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            emit({"type": "error", "code": "PROTOCOL_ERROR", "message": "명령을 해석하지 못했습니다."})
            continue

        command = message.get("command")
        request_id = message.get("requestId")

        # Noise between every request and its answer.
        emit_garbage(command or "unknown")

        if command == "hello":
            emit({"type": "ack", "requestId": request_id, "command": "hello"})

        elif command == "probe":
            emit(
                {
                    "videoPath": message.get("videoPath", ""),
                    "durationSeconds": 8.0,
                    "audioTracks": [
                        {"index": 0, "language": "eng", "codec": "aac", "channels": 2, "isDefault": True}
                    ],
                    "subtitleTracks": [],
                    "container": "mov,mp4,m4a",
                    "requestId": request_id,
                    # Discriminator deliberately last: the host's hand-rolled dispatch must cope.
                    "type": "probeResult",
                }
            )

        elif command == "listModels":
            emit(
                {
                    "type": "modelList",
                    "requestId": request_id,
                    "models": [
                        {
                            "modelId": "whisper-small",
                            "path": "/models/whisper-small",
                            "installed": True,
                            "verified": True,
                            "sizeBytes": 1024,
                            "downloadedBytes": 1024,
                        }
                    ],
                }
            )

        elif command == "detectHardware":
            emit(
                {
                    "type": "hardware",
                    "requestId": request_id,
                    "gpus": [],
                    "cudaAvailable": False,
                    # Protocol 1.2: a machine with no GPU at all, so no device and nothing missing.
                    "cudaDeviceDetected": False,
                    "cudaLibrariesAvailable": True,
                    "missingCudaLibraries": [],
                    "cpuName": "stub cpu",
                    "logicalCores": 2,
                    "totalRamBytes": 1024,
                    "availableRamBytes": 512,
                    "warnings": ["nvidia-smi를 찾지 못했습니다."],
                }
            )

        elif command == "process" and os.environ.get("KSUBMAKER_STUB_HANG_PROCESS"):
            # A job that never reports again — what a wedged native call looks like from the host.
            emit({"type": "started", "requestId": request_id, "jobId": message.get("jobId")})

        elif command == "process":
            job_id = message.get("jobId")
            emit({"type": "started", "requestId": request_id, "jobId": job_id})
            emit(
                {
                    "type": "progress",
                    "requestId": request_id,
                    "jobId": job_id,
                    "stage": "transcribing",
                    "stageProgress": 50.0,
                    "overallProgress": 30.0,
                }
            )
            emit(
                {
                    "type": "error",
                    "requestId": request_id,
                    "jobId": job_id,
                    "code": "TRANSCRIPTION_FAILED",
                    "message": "스텁 worker는 실제 처리를 하지 않습니다.",
                    "recoverable": False,
                }
            )

        elif command == "shutdown":
            emit({"type": "goodbye", "requestId": request_id})
            return 0

        else:
            emit(
                {
                    "type": "error",
                    "requestId": request_id,
                    "code": "PROTOCOL_ERROR",
                    "message": f"알 수 없는 명령입니다: {command}",
                    "recoverable": False,
                }
            )

    return 0


if __name__ == "__main__":
    sys.exit(main())
