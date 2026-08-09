"""ffmpeg / ffprobe wrapper.

Two rules drive this module:

1. **Never assume PATH.** A user with a random ffmpeg build on PATH must still get the copy we
   shipped and tested against, so bundled locations are probed first and PATH is the last resort.
2. **Never build a command string.** Every invocation is a list argv with ``shell=False``, which
   is what makes non-ASCII and space-laden Windows paths work without any quoting of our own.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence

from . import errors
from .cancellation import CancellationToken, kill_process
from .errors import WorkerError
from .logging_setup import get_logger

_log = get_logger("ffmpeg")

#: ffmpeg writes `time=00:01:23.45` (and occasionally `time=-00:00:00.00`) on its stderr status line.
_TIME_PATTERN = re.compile(r"time=\s*(-?)(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")
_TIME_MS_PATTERN = re.compile(r"out_time_ms=\s*(-?\d+)")

TARGET_SAMPLE_RATE = 16_000
TARGET_CHANNELS = 1


# ---------------------------------------------------------------------------
# binary discovery
# ---------------------------------------------------------------------------


def _executable_name(stem: str) -> str:
    return f"{stem}.exe" if sys.platform.startswith("win") else stem


def _candidate_roots() -> list[Path]:
    """Directories that might contain a bundled tools/ tree, most specific first.

    In the frozen build ``sys.executable`` is the worker itself and the tools sit next to it. In
    the source-tree build the package lives at ``<repo>/worker/ksubmaker_worker`` and the tools at
    ``<repo>/tools``.
    """
    roots: list[Path] = []

    override = os.environ.get("KSUBMAKER_TOOLS_DIR")
    if override:
        roots.append(Path(override))

    module_dir = Path(__file__).resolve().parent
    for base in (module_dir, module_dir.parent, module_dir.parent.parent):
        roots.append(base / "tools")

    try:
        exe_dir = Path(sys.executable).resolve().parent
    except (OSError, ValueError):  # pragma: no cover - defensive
        exe_dir = None

    if exe_dir is not None:
        roots.append(exe_dir / "tools")
        # The interpreter's own directory is only the *app* directory in the frozen build. On a
        # developer machine sys.executable is /usr/bin/python3, and treating /usr/bin as "ours"
        # would silently pick up a system ffmpeg while claiming it came from the bundle.
        if getattr(sys, "frozen", False):
            roots.append(exe_dir)

    roots.append(Path.cwd() / "tools")

    # Deduplicate while preserving order.
    seen: set[str] = set()
    unique: list[Path] = []
    for root in roots:
        key = str(root)
        if key not in seen:
            seen.add(key)
            unique.append(root)
    return unique


def find_binary(stem: str) -> str | None:
    """Locate ffmpeg/ffprobe: bundled ``tools/ffmpeg/bin`` -> ``tools`` -> app dir -> PATH."""
    filename = _executable_name(stem)

    for root in _candidate_roots():
        for candidate in (root / "ffmpeg" / "bin" / filename, root / filename):
            if candidate.is_file():
                _log.debug("%s resolved to bundled %s", stem, candidate)
                return str(candidate.resolve())

    from_path = shutil.which(stem)
    if from_path:
        # Reaching here in production means the bundle is broken; the app still runs but against
        # an untested ffmpeg build, so say so loudly.
        _log.warning("%s not found in the bundle; falling back to PATH copy %s", stem, from_path)
        return from_path

    return None


class FfmpegService:
    """Probing, audio extraction and subtitle extraction."""

    def __init__(self, ffmpeg: str | None = None, ffprobe: str | None = None) -> None:
        self._ffmpeg = ffmpeg
        self._ffprobe = ffprobe

    # -- binaries -------------------------------------------------------------

    @property
    def ffmpeg_path(self) -> str:
        if self._ffmpeg is None:
            self._ffmpeg = find_binary("ffmpeg")
        if self._ffmpeg is None:
            raise WorkerError(
                errors.FFMPEG_NOT_FOUND,
                "FFmpeg 실행 파일을 찾을 수 없습니다. 설치 폴더의 tools/ffmpeg 디렉터리를 확인하세요.",
                detail="ffmpeg not found in tools/, app directory or PATH",
            )
        return self._ffmpeg

    @property
    def ffprobe_path(self) -> str:
        if self._ffprobe is None:
            self._ffprobe = find_binary("ffprobe")
        if self._ffprobe is None:
            raise WorkerError(
                errors.FFMPEG_NOT_FOUND,
                "FFprobe 실행 파일을 찾을 수 없습니다. 설치 폴더의 tools/ffmpeg 디렉터리를 확인하세요.",
                detail="ffprobe not found in tools/, app directory or PATH",
            )
        return self._ffprobe

    def available(self) -> bool:
        try:
            return bool(self.ffmpeg_path) and bool(self.ffprobe_path)
        except WorkerError:
            return False

    # -- probe ----------------------------------------------------------------

    def probe(self, path: str) -> dict[str, Any]:
        """ffprobe a container into the ``probeResult`` payload shape.

        Never raises for a merely broken file: the failure is reported through the ``error`` key
        so the host can grey out one row instead of failing the whole scan.
        """
        source = Path(path)
        if not source.exists():
            raise WorkerError(
                errors.VIDEO_NOT_FOUND,
                f"영상 파일을 찾을 수 없습니다: {source.name}",
                detail=f"missing path {path}",
            )

        argv = [
            self.ffprobe_path,
            "-hide_banner",
            "-loglevel",
            "error",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            str(source),
        ]

        try:
            completed = subprocess.run(  # noqa: S603 - list argv, shell=False
                argv,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
                timeout=120,
            )
        except FileNotFoundError as exc:
            raise WorkerError(
                errors.FFMPEG_NOT_FOUND,
                "FFprobe 실행 파일을 찾을 수 없습니다.",
                detail=repr(exc),
            ) from exc
        except subprocess.TimeoutExpired as exc:
            raise WorkerError(
                errors.VIDEO_UNREADABLE,
                f"영상 정보를 읽는 데 너무 오래 걸립니다: {source.name}",
                detail=repr(exc),
            ) from exc

        if completed.returncode != 0:
            stderr = completed.stderr.decode("utf-8", "replace").strip()
            return {
                "videoPath": str(source),
                "durationSeconds": 0.0,
                "audioTracks": [],
                "subtitleTracks": [],
                "container": source.suffix.lstrip("."),
                "error": stderr[:500] or "ffprobe failed",
            }

        try:
            data = json.loads(completed.stdout.decode("utf-8", "replace") or "{}")
        except json.JSONDecodeError as exc:
            return {
                "videoPath": str(source),
                "durationSeconds": 0.0,
                "audioTracks": [],
                "subtitleTracks": [],
                "container": source.suffix.lstrip("."),
                "error": f"ffprobe output was not JSON: {exc}",
            }

        return _parse_probe(source, data)

    # -- audio ----------------------------------------------------------------

    def extract_audio(
        self,
        video_path: str,
        output_path: str,
        *,
        audio_track_index: int | None = None,
        duration_seconds: float | None = None,
        token: CancellationToken | None = None,
        progress: Callable[[float], None] | None = None,
    ) -> str:
        """Extract 16 kHz mono PCM WAV.

        Writes to ``output_path + '.tmp'`` and ``os.replace``s it into place, so a cancelled or
        crashed run never leaves a truncated wav that a resume would happily transcribe.
        """
        source = Path(video_path)
        if not source.exists():
            raise WorkerError(
                errors.VIDEO_NOT_FOUND,
                f"영상 파일을 찾을 수 없습니다: {source.name}",
                detail=f"missing path {video_path}",
            )

        target = Path(output_path)
        target.parent.mkdir(parents=True, exist_ok=True)
        temp = target.with_name(target.name + ".tmp")

        if duration_seconds is None or duration_seconds <= 0:
            probed = self.probe(video_path)
            duration_seconds = float(probed.get("durationSeconds") or 0.0)
            if not probed.get("audioTracks"):
                raise WorkerError(
                    errors.AUDIO_TRACK_NOT_FOUND,
                    f"영상에 오디오 트랙이 없습니다: {source.name}",
                    detail=f"no audio streams in {video_path}",
                )

        argv: list[str] = [
            self.ffmpeg_path,
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            str(source),
            "-vn",
        ]
        if audio_track_index is not None:
            argv += ["-map", f"0:a:{int(audio_track_index)}"]
        if duration_seconds is not None and duration_seconds > 0:
            argv += ["-t", f"{duration_seconds:.3f}"]
        argv += [
            "-ac",
            str(TARGET_CHANNELS),
            "-ar",
            str(TARGET_SAMPLE_RATE),
            "-c:a",
            "pcm_s16le",
            "-f",
            "wav",
            str(temp),
        ]

        _log.info("extracting audio from %s", source.name)

        try:
            self._run_with_progress(
                argv,
                total_seconds=duration_seconds,
                token=token,
                progress=progress,
                on_failure=lambda code, tail: self._audio_failure(source, code, tail),
            )
        except BaseException:
            _unlink_quietly(temp)
            raise

        if not temp.exists() or temp.stat().st_size == 0:
            _unlink_quietly(temp)
            raise WorkerError(
                errors.FFMPEG_FAILED,
                f"오디오를 추출하지 못했습니다: {source.name}",
                detail="ffmpeg exited successfully but produced no output",
            )

        os.replace(temp, target)
        _log.info("audio written to %s (%d bytes)", target, target.stat().st_size)
        return str(target)

    @staticmethod
    def _audio_failure(source: Path, returncode: int, tail: str) -> WorkerError:
        lowered = tail.lower()

        if "does not contain any stream" in lowered or (
            "stream map" in lowered and "matches no streams" in lowered
        ):
            return WorkerError(
                errors.AUDIO_TRACK_NOT_FOUND,
                f"선택한 오디오 트랙을 찾을 수 없습니다: {source.name}",
                detail=tail,
            )
        if "invalid data found" in lowered or "moov atom not found" in lowered:
            return WorkerError(
                errors.VIDEO_UNREADABLE,
                f"영상 파일이 손상되어 읽을 수 없습니다: {source.name}",
                detail=tail,
            )
        if "no space left" in lowered:
            return WorkerError(
                errors.DISK_SPACE_LOW,
                "디스크 공간이 부족하여 오디오를 추출하지 못했습니다.",
                detail=tail,
            )

        return WorkerError(
            errors.FFMPEG_FAILED,
            f"오디오 추출에 실패했습니다: {source.name}",
            detail=f"ffmpeg exited with {returncode}: {tail}",
        )

    # -- subtitles ------------------------------------------------------------

    def extract_subtitle_track(
        self,
        video_path: str,
        index: int = 0,
        *,
        token: CancellationToken | None = None,
    ) -> str:
        """Return an embedded subtitle track as SRT text.

        ``index`` is the position among subtitle streams (``0:s:N``), matching the ``index`` field
        of the probe payload's ``subtitleTracks`` entries.
        """
        source = Path(video_path)
        if not source.exists():
            raise WorkerError(
                errors.VIDEO_NOT_FOUND,
                f"영상 파일을 찾을 수 없습니다: {source.name}",
                detail=f"missing path {video_path}",
            )

        argv = [
            self.ffmpeg_path,
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            str(source),
            "-map",
            f"0:s:{int(index)}",
            "-c:s",
            "srt",
            "-f",
            "srt",
            "pipe:1",
        ]

        try:
            process = subprocess.Popen(  # noqa: S603 - list argv, shell=False
                argv,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
        except FileNotFoundError as exc:
            raise WorkerError(
                errors.FFMPEG_NOT_FOUND, "FFmpeg 실행 파일을 찾을 수 없습니다.", detail=repr(exc)
            ) from exc

        registered = token.child(process) if token is not None else _NullScope(process)
        with registered:
            try:
                stdout, stderr = process.communicate(timeout=600)
            except subprocess.TimeoutExpired as exc:
                kill_process(process)
                raise WorkerError(
                    errors.FFMPEG_FAILED,
                    f"자막 트랙 추출이 시간 초과되었습니다: {source.name}",
                    detail=repr(exc),
                ) from exc

        if token is not None and token.cancelled:
            raise errors.CancelledError()

        if process.returncode != 0:
            tail = stderr.decode("utf-8", "replace").strip()[-1500:]
            raise WorkerError(
                errors.FFMPEG_FAILED,
                f"내장 자막 트랙을 추출하지 못했습니다: {source.name}",
                detail=f"ffmpeg exited with {process.returncode}: {tail}",
            )

        text = stdout.decode("utf-8", "replace")
        if not text.strip():
            raise WorkerError(
                errors.FFMPEG_FAILED,
                f"내장 자막 트랙이 비어 있습니다: {source.name}",
                detail=f"subtitle stream 0:s:{index} produced no text",
            )

        return text

    # -- process plumbing -----------------------------------------------------

    def _run_with_progress(
        self,
        argv: Sequence[str],
        *,
        total_seconds: float,
        token: CancellationToken | None,
        progress: Callable[[float], None] | None,
        on_failure: Callable[[int, str], WorkerError],
    ) -> None:
        """Run ffmpeg, translating its stderr status lines into 0-100 progress callbacks."""
        try:
            process = subprocess.Popen(  # noqa: S603 - list argv, shell=False
                list(argv),
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                bufsize=0,
            )
        except FileNotFoundError as exc:
            raise WorkerError(
                errors.FFMPEG_NOT_FOUND, "FFmpeg 실행 파일을 찾을 수 없습니다.", detail=repr(exc)
            ) from exc
        except OSError as exc:
            raise WorkerError(
                errors.FFMPEG_FAILED, "FFmpeg를 실행하지 못했습니다.", detail=repr(exc)
            ) from exc

        tail_lines: list[str] = []
        scope = token.child(process) if token is not None else _NullScope(process)

        with scope:
            assert process.stderr is not None
            for raw in _iter_status_chunks(process.stderr):
                tail_lines.append(raw)
                if len(tail_lines) > 40:
                    del tail_lines[0]

                if token is not None and token.cancelled:
                    kill_process(process)
                    raise errors.CancelledError()

                if progress is not None and total_seconds > 0:
                    seconds = _parse_time(raw)
                    if seconds is not None:
                        progress(min(100.0, max(0.0, seconds / total_seconds * 100.0)))

            process.wait()

        if token is not None and token.cancelled:
            raise errors.CancelledError()

        if process.returncode != 0:
            raise on_failure(process.returncode, "\n".join(tail_lines)[-1500:])

        if progress is not None:
            progress(100.0)


class _NullScope:
    """Stand-in for ``CancellationToken.child`` when no token was supplied."""

    def __init__(self, process: subprocess.Popen) -> None:
        self._process = process

    def __enter__(self) -> subprocess.Popen:
        return self._process

    def __exit__(self, exc_type, exc, tb) -> bool:  # noqa: ANN001
        return False


def _iter_status_chunks(stream) -> Iterable[str]:  # noqa: ANN001
    """Yield ffmpeg status chunks.

    ffmpeg separates status updates with ``\\r`` rather than ``\\n``, so ``readline`` would block
    until the whole run finished. Reading raw bytes and splitting on both terminators is what makes
    progress arrive continuously.
    """
    buffer = bytearray()

    while True:
        chunk = stream.read(256)
        if not chunk:
            break

        buffer.extend(chunk)

        while True:
            index = -1
            for i, byte in enumerate(buffer):
                if byte in (0x0A, 0x0D):
                    index = i
                    break
            if index < 0:
                break

            line = bytes(buffer[:index]).decode("utf-8", "replace").strip()
            del buffer[: index + 1]
            if line:
                yield line

    if buffer:
        remainder = bytes(buffer).decode("utf-8", "replace").strip()
        if remainder:
            yield remainder


def _parse_time(line: str) -> float | None:
    """Seconds of media processed, from ``time=HH:MM:SS.ms`` or ``out_time_ms=``."""
    match = _TIME_PATTERN.search(line)
    if match is not None:
        sign, hours, minutes, seconds = match.groups()
        value = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        return 0.0 if sign == "-" else value

    ms_match = _TIME_MS_PATTERN.search(line)
    if ms_match is not None:
        micros = int(ms_match.group(1))
        return max(0.0, micros / 1_000_000.0)

    return None


def _parse_probe(source: Path, data: dict[str, Any]) -> dict[str, Any]:
    fmt = data.get("format") or {}
    streams = data.get("streams") or []

    duration = _to_float(fmt.get("duration"))
    audio_tracks: list[dict[str, Any]] = []
    subtitle_tracks: list[dict[str, Any]] = []

    for stream in streams:
        if not isinstance(stream, dict):
            continue

        kind = stream.get("codec_type")
        tags = stream.get("tags") or {}
        disposition = stream.get("disposition") or {}

        if kind == "audio":
            if duration <= 0:
                duration = max(duration, _to_float(stream.get("duration")))
            audio_tracks.append(
                {
                    # Index among audio streams: this is what `-map 0:a:N` selects.
                    "index": len(audio_tracks),
                    "language": tags.get("language"),
                    "title": tags.get("title"),
                    "codec": stream.get("codec_name"),
                    "channels": int(stream.get("channels") or 0),
                    "isDefault": bool(disposition.get("default")),
                }
            )
        elif kind == "subtitle":
            subtitle_tracks.append(
                {
                    "index": len(subtitle_tracks),
                    "language": tags.get("language"),
                    "title": tags.get("title"),
                    "codec": stream.get("codec_name"),
                    "isForced": bool(disposition.get("forced")),
                    "isDefault": bool(disposition.get("default")),
                }
            )

    container = fmt.get("format_name") or source.suffix.lstrip(".")

    result: dict[str, Any] = {
        "videoPath": str(source),
        "durationSeconds": round(duration, 3),
        "audioTracks": audio_tracks,
        "subtitleTracks": subtitle_tracks,
        "container": container,
    }

    if duration <= 0:
        result["error"] = "재생 시간을 확인할 수 없습니다."

    return result


def _to_float(value: Any) -> float:
    try:
        result = float(value)
    except (TypeError, ValueError):
        return 0.0
    return result if result > 0 else 0.0


def _unlink_quietly(path: Path) -> None:
    try:
        if path.exists():
            path.unlink()
    except OSError as exc:
        _log.debug("could not delete temp file %s: %r", path, exc)


#: Lazily shared instance; binary discovery is memoised on it.
_default_service: FfmpegService | None = None
_default_lock = threading.Lock()


def default_service() -> FfmpegService:
    global _default_service
    with _default_lock:
        if _default_service is None:
            _default_service = FfmpegService()
        return _default_service
