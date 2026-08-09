"""The alternative translation engine: a bundled ``llama-server`` (llama.cpp).

No Ollama, no external daemon: the worker spawns ``tools/llama/llama-server`` itself, bound to
127.0.0.1 on an ephemeral free port, waits for ``/health`` and then talks OpenAI-compatible
``/v1/chat/completions`` over plain ``requests``. The server is a child of this process, so it
dies with the job — a stray 7B model holding 6 GB of VRAM is not an acceptable failure mode.
"""

from __future__ import annotations

import json
import os
import re
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Sequence

from . import errors
from .batching import BatchOptions, split_batches, translate_with_retry
from .cancellation import GLOBAL_PROCESSES, CancellationToken, kill_process
from .errors import WorkerError
from .logging_setup import get_logger
from .model_manager import find_local_file, model_directory, models_root

_log = get_logger("llm")

#: The exact rule list from the specification. Style- and glossary-specific lines are appended
#: after this block; the block itself must not be reworded.
SYSTEM_PROMPT_RULES = """다음은 영상 자막입니다.

규칙:
1. 모든 항목을 자연스러운 한국어로 번역한다.
2. id를 절대 변경하지 않는다.
3. 항목을 삭제하거나 합치지 않는다.
4. 새로운 정보를 추가하지 않는다.
5. 설명이나 주석을 출력하지 않는다.
6. 지정된 JSON 배열 형식으로만 반환한다.
7. 앞뒤 문맥을 고려한다.
8. 인명과 고유명사는 일관되게 번역한다."""

_STYLE_INSTRUCTIONS: dict[str, str] = {
    "natural": "문체: 자연스러운 한국어 구어체로 번역한다.",
    "literal": "문체: 원문 구조를 최대한 유지하여 직역에 가깝게 번역한다.",
    "polite": "문체: 존댓말(합니다체)로 통일한다.",
    "casual": "문체: 반말로 통일한다.",
    "preserve": "문체: 원문의 말투와 격식 수준을 그대로 유지한다.",
}

_OUTPUT_FORMAT = '출력 형식: [{"id": 1, "translation": "..."}]'

#: Appended straight after the rule block, which is pinned verbatim by the specification and by
#: ``test_rule_block_is_verbatim`` — so the target-language constraint has to live here rather than
#: be worked into rule 1.
#:
#: Why it exists: rule 1 already says "자연스러운 한국어로 번역한다" and Qwen2.5 still answered 41%
#: of a Japanese file in Chinese (측정 표본 B: 113 of 273 output lines were Han-only 간체자). A single
#: positive instruction buried at position 1 of 8 was not enough. This states the target language on
#: its own, names the failure modes actually observed, and repeats on every retry.
_LANGUAGE_LOCK = (
    "출력 언어(가장 중요):\n"
    "- translation 값은 **한국어**로만 쓴다. 한글로 적는다.\n"
    "- 중국어(간체·번체)로 쓰지 않는다. 영어로 쓰지 않는다.\n"
    "- 원문 언어를 그대로 옮겨 적지 않는다. 번역한다.\n"
    "- 예외는 번역할 수 없는 고유명사뿐이며, 그 경우에도 한글 표기를 우선한다."
)

#: ISO-639-1 -> Korean name. A bare "ja" in the prompt is a token the model can ignore; "일본어"
#: is the word the instruction is actually about. Unknown codes fall through unchanged.
_LANGUAGE_NAMES: dict[str, str] = {
    "ja": "일본어",
    "en": "영어",
    "zh": "중국어",
    "zh-cn": "중국어(간체)",
    "zh-tw": "중국어(번체)",
    "ko": "한국어",
    "es": "스페인어",
    "fr": "프랑스어",
    "de": "독일어",
    "ru": "러시아어",
    "th": "태국어",
    "vi": "베트남어",
    "id": "인도네시아어",
}


def describe_language(code: str | None) -> str:
    """``ja`` -> ``일본어(ja)``. Unknown or blank codes render as-is."""
    if not code:
        return "알 수 없음"

    normalised = code.strip().lower().replace("_", "-")
    name = _LANGUAGE_NAMES.get(normalised) or _LANGUAGE_NAMES.get(normalised.split("-")[0])
    return f"{name}({code})" if name else code


_SHORT_INSTRUCTION = (
    "JSON 배열만 출력한다. 형식: [{\"id\": 1, \"translation\": \"...\"}]. "
    "설명, 주석, 코드 블록 표시를 붙이지 않는다. 모든 id를 빠짐없이 포함한다. "
    "translation 값은 한국어(한글)로만 쓴다."
)

_HEALTH_TIMEOUT_SECONDS = 180.0
_REQUEST_TIMEOUT_SECONDS = 600.0


def build_system_prompt(
    style: str = "natural",
    glossary: dict[str, str] | None = None,
    *,
    short: bool = False,
    source_language: str | None = None,
) -> str:
    """The rule block, plus the language lock, style/glossary/format lines.

    ``source_language`` turns the constraint into a direction the model can act on — "일본어 →
    한국어" rather than a target stated in isolation. ``short`` adds the retry instruction.
    """
    parts = [SYSTEM_PROMPT_RULES, ""]

    if source_language:
        parts.append(f"번역 방향: {describe_language(source_language)} → 한국어(ko)")

    parts += [_LANGUAGE_LOCK, "", _STYLE_INSTRUCTIONS.get(style, _STYLE_INSTRUCTIONS["natural"])]

    if glossary:
        # Bounded: a 500-term glossary would eat the whole context window.
        pairs = list(glossary.items())[:40]
        rendered = ", ".join(f"{source} → {target}" for source, target in pairs)
        parts.append(f"용어집(반드시 이 번역을 사용한다): {rendered}")

    parts.append(_OUTPUT_FORMAT)

    if short:
        parts.append(_SHORT_INSTRUCTION)

    return "\n".join(parts)


def find_llama_server() -> str | None:
    """Locate the bundled ``llama-server``; PATH is the last resort, as with ffmpeg."""
    name = "llama-server.exe" if sys.platform.startswith("win") else "llama-server"

    roots: list[Path] = []
    override = os.environ.get("KSUBMAKER_TOOLS_DIR")
    if override:
        roots.append(Path(override))

    module_dir = Path(__file__).resolve().parent
    for base in (module_dir, module_dir.parent, module_dir.parent.parent):
        roots.append(base / "tools")

    try:
        roots.append(Path(sys.executable).resolve().parent / "tools")
    except (OSError, ValueError):  # pragma: no cover
        pass

    roots.append(Path.cwd() / "tools")

    for root in roots:
        for candidate in (root / "llama" / name, root / "llama" / "bin" / name, root / name):
            if candidate.is_file():
                return str(candidate.resolve())

    import shutil  # noqa: PLC0415 - only needed on the fallback path

    return shutil.which("llama-server")


#: MSVC runtime DLLs the bundled llama.cpp links against. Windows resolves them from System32 only
#: when the Visual C++ Redistributable is installed — they are **not** in the llama.cpp archive, and
#: the copies that ship under tools\python are invisible to llama-server.exe, which is its own
#: process and searches its own directory.
_MSVC_RUNTIME_DLLS: tuple[str, ...] = ("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll")

#: NTSTATUS STATUS_DLL_NOT_FOUND, as Python reports it for a process the loader could not start.
_STATUS_DLL_NOT_FOUND = -1073741515

MSVC_REDIST_URL = "https://aka.ms/vs/17/release/vc_redist.x64.exe"

_MSVC_REMEDY = (
    "Microsoft Visual C++ 재배포 가능 패키지(x64)가 설치되어 있지 않습니다. "
    f"{MSVC_REDIST_URL} 에서 내려받아 설치한 뒤 다시 시도하세요."
)


def missing_msvc_runtime(
    *,
    loader: Callable[[str], Any] | None = None,
    is_windows: bool | None = None,
) -> list[str]:
    """MSVC runtime DLLs that cannot be resolved, in the order they were probed.

    Empty on a healthy machine and on every non-Windows host. Probing by **name** is deliberate:
    that is exactly how the Windows loader will look for them when it loads ggml, so a name that
    resolves here resolves there too.

    Why this exists: ``ggml-base.dll`` and ``ggml-cuda.dll`` import ``MSVCP140.dll``, and nothing in
    the portable build supplies it. On a machine without the redistributable llama-server either
    refuses to start or — worse — starts and silently drops to the CPU backend, which is
    indistinguishable from "the local LLM is just slow".
    """
    windows = sys.platform.startswith("win") if is_windows is None else is_windows
    if not windows:
        return []

    if loader is None:
        import ctypes  # noqa: PLC0415 - Windows branch only

        loader = getattr(ctypes, "WinDLL", None)
        if loader is None:  # pragma: no cover - non-Windows interpreter
            return []

    missing: list[str] = []
    for name in _MSVC_RUNTIME_DLLS:
        try:
            loader(name)
        except Exception:  # noqa: BLE001 - the Windows loader surfaces more than OSError
            missing.append(name)

    return missing


def free_port() -> int:
    """Ask the OS for an ephemeral port and immediately release it.

    Racy in principle; in practice the window is microseconds and the alternative (a fixed port)
    breaks the moment two KSubMaker instances run at once.
    """
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        return int(sock.getsockname()[1])


def choose_gpu_layers(free_vram_bytes: int, model_size_bytes: int = 0) -> int:
    """How many transformer layers to offload, from the free VRAM.

    99 means "all of them" to llama.cpp. Below ~3 GB free we stay on the CPU entirely: a partial
    offload that spills is slower than not offloading at all.
    """
    gib = free_vram_bytes / (1024**3)

    if gib <= 0:
        return 0
    if model_size_bytes and free_vram_bytes > model_size_bytes * 1.3:
        return 99
    if gib >= 10:
        return 99
    if gib >= 8:
        return 48
    if gib >= 6:
        return 32
    if gib >= 4:
        return 20
    if gib >= 3:
        return 12
    return 0


class LlamaServer:
    """Owns one ``llama-server`` child process."""

    def __init__(
        self,
        model_path: str | Path,
        *,
        executable: str | None = None,
        n_gpu_layers: int | None = None,
        context_size: int = 8192,
        extra_args: Sequence[str] | None = None,
    ) -> None:
        self.model_path = Path(model_path)
        self.executable = executable
        self.n_gpu_layers = n_gpu_layers
        self.context_size = context_size
        self.extra_args = list(extra_args or [])
        self.port: int | None = None
        self.process: subprocess.Popen[bytes] | None = None
        self._lock = threading.Lock()

    @property
    def base_url(self) -> str:
        if self.port is None:
            raise WorkerError(
                errors.TRANSLATION_FAILED,
                "번역 서버가 시작되지 않았습니다.",
                detail="base_url read before start()",
            )
        return f"http://127.0.0.1:{self.port}"

    def start(self, session: Any, token: CancellationToken | None = None) -> None:
        with self._lock:
            if self.process is not None and self.process.poll() is None:
                return

            executable = self.executable or find_llama_server()
            if executable is None:
                raise WorkerError(
                    errors.TRANSLATION_MODEL_NOT_FOUND,
                    "로컬 LLM 실행 파일(llama-server)이 설치되어 있지 않습니다. "
                    "설정에서 번역 엔진을 '로컬 번역 모델(NLLB)'로 변경하시거나, 모델 화면에서 로컬 LLM 구성 요소를 먼저 내려받으세요.",
                    detail="llama-server not found in tools/llama, app directory or PATH",
                )

            if not self.model_path.is_file():
                raise WorkerError(
                    errors.TRANSLATION_MODEL_NOT_FOUND,
                    f"LLM 모델 파일을 찾을 수 없습니다: {self.model_path.name}. "
                    "모델 화면에서 먼저 내려받으세요.",
                    detail=f"missing gguf at {self.model_path}",
                )

            # Checked before spawning: a missing redistributable either kills the process at load
            # time with no stderr to report, or lets it run on the CPU backend without a word. Both
            # are far cheaper to explain here than to diagnose from the symptom.
            absent = missing_msvc_runtime()
            if absent:
                raise WorkerError(
                    errors.TRANSLATION_FAILED,
                    f"로컬 LLM을 실행할 수 없습니다. {_MSVC_REMEDY}",
                    detail=f"missing MSVC runtime: {', '.join(absent)}",
                )

            self.port = free_port()
            layers = self.n_gpu_layers if self.n_gpu_layers is not None else 0

            argv = [
                executable,
                "--model",
                str(self.model_path),
                "--host",
                "127.0.0.1",
                "--port",
                str(self.port),
                "--n-gpu-layers",
                str(layers),
                "--ctx-size",
                str(self.context_size),
                "--log-disable",
                *self.extra_args,
            ]

            _log.info(
                "starting llama-server on port %d with %d GPU layers", self.port, layers
            )

            try:
                self.process = subprocess.Popen(  # noqa: S603 - list argv, shell=False
                    argv,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.PIPE,
                )
            except FileNotFoundError as exc:
                raise WorkerError(
                    errors.TRANSLATION_MODEL_NOT_FOUND,
                    "로컬 LLM 실행 파일(llama-server)을 실행하지 못했습니다. "
                    "모델 화면에서 로컬 LLM 구성 요소를 설치하세요.",
                    detail=repr(exc),
                ) from exc
            except OSError as exc:
                raise WorkerError(
                    errors.TRANSLATION_FAILED,
                    "번역 서버를 시작하지 못했습니다.",
                    detail=repr(exc),
                ) from exc

            GLOBAL_PROCESSES.add(self.process)
            if token is not None:
                token.register_process(self.process)

        self._wait_for_health(session, token)

    def _wait_for_health(self, session: Any, token: CancellationToken | None) -> None:
        deadline = time.monotonic() + _HEALTH_TIMEOUT_SECONDS
        url = f"{self.base_url}/health"

        while time.monotonic() < deadline:
            if token is not None and token.cancelled:
                self.stop()
                raise errors.CancelledError()

            process = self.process
            if process is not None and process.poll() is not None:
                tail = _read_stderr_tail(process)

                # The loader refused to start it. There is no stderr in this case — the process
                # never ran — so without naming the cause the user sees "모델 파일이 손상되었을
                # 수 있습니다" for a perfectly good model file.
                if process.returncode == _STATUS_DLL_NOT_FOUND:
                    raise WorkerError(
                        errors.TRANSLATION_FAILED,
                        f"로컬 LLM 실행 파일을 시작하지 못했습니다. {_MSVC_REMEDY}",
                        detail=f"llama-server exited with STATUS_DLL_NOT_FOUND: {tail}",
                    )

                raise WorkerError(
                    errors.TRANSLATION_FAILED,
                    "번역 서버가 시작 도중 종료되었습니다. 모델 파일이 손상되었을 수 있습니다.",
                    detail=f"llama-server exited with {process.returncode}: {tail}",
                )

            try:
                response = session.get(url, timeout=5)
                if int(getattr(response, "status_code", 0)) == 200:
                    _log.info("llama-server is ready on port %s", self.port)
                    return
            except Exception as exc:  # noqa: BLE001 - connection refused while it boots
                _log.debug("health probe not ready yet: %r", exc)

            time.sleep(0.5)

        self.stop()
        raise WorkerError(
            errors.TRANSLATION_FAILED,
            "번역 서버가 응답하지 않습니다. 잠시 후 다시 시도하세요.",
            detail=f"llama-server did not become healthy within {_HEALTH_TIMEOUT_SECONDS:.0f}s",
        )

    def stop(self) -> None:
        with self._lock:
            process = self.process
            self.process = None
            self.port = None

        if process is not None:
            GLOBAL_PROCESSES.remove(process)
            kill_process(process)
            _log.info("llama-server stopped")


class LlmTranslator:
    """Translation through the local llama-server."""

    def __init__(
        self,
        *,
        models_dir: str | Path | None = None,
        server: LlamaServer | None = None,
        session: Any = None,
        temperature: float = 0.2,
    ) -> None:
        self._models_dir = Path(models_dir) if models_dir is not None else models_root()
        self._server = server
        self._session = session
        self.temperature = temperature
        self.loaded_model_id: str | None = None

    # -- lifecycle -------------------------------------------------------------

    def _ensure_session(self) -> Any:
        if self._session is None:
            try:
                import requests  # noqa: PLC0415 - lazy
            except ImportError as exc:
                raise WorkerError(
                    errors.TRANSLATION_FAILED,
                    "번역 통신 구성 요소를 불러오지 못했습니다. 설치가 손상되었을 수 있습니다.",
                    detail=repr(exc),
                ) from exc
            self._session = requests.Session()
        return self._session

    def load(
        self,
        *,
        model_id: str = "auto",
        free_vram_bytes: int = 0,
        context_size: int = 8192,
        token: CancellationToken | None = None,
    ) -> None:
        session = self._ensure_session()

        if self._server is None:
            gguf = self._resolve_model_file(model_id)
            size = gguf.stat().st_size if gguf.is_file() else 0
            self._server = LlamaServer(
                gguf,
                n_gpu_layers=choose_gpu_layers(free_vram_bytes, size),
                context_size=context_size,
            )

        self._server.start(session, token)
        self.loaded_model_id = model_id

    def _resolve_model_file(self, model_id: str) -> Path:
        gguf = find_local_file(model_id, self._models_dir)
        if gguf is not None:
            return gguf

        # Report the path we looked in: "model not found" without a location is unactionable.
        raise WorkerError(
            errors.TRANSLATION_MODEL_NOT_FOUND,
            f"LLM 모델을 찾을 수 없습니다: {model_id}. 모델 화면에서 먼저 내려받으세요.",
            detail=f"no .gguf under {model_directory(model_id, self._models_dir)}",
        )

    def unload(self) -> None:
        if self._server is not None:
            self._server.stop()
        self.loaded_model_id = None

    # -- translation -----------------------------------------------------------

    def _handle_server_rejection(
        self,
        items: Sequence[dict[str, Any]],
        status: int,
        detail: str,
        *,
        source_language: str,
        style: str,
        glossary: dict[str, str] | None,
        context: Sequence[dict[str, Any]] | None,
        attempt: int,
        token: CancellationToken | None,
    ) -> list[dict[str, Any]]:
        """A 5xx means the server is alive but would not accept *this* batch. Narrow it, don't die.

        Seen in the field (2026-08-08)::

            HTTP 500 {"error":{"code":500,"type":"server_error","message":
              "The model produced output that does not match the expected peg-native format"}}

        That string lives in the bundled llama.cpp, not in this code: the server parses the model's
        reply against the chat template's own grammar and answers 500 when it does not fit. The
        request was fine; one generation was not.

        Raising here abandoned a job that was minutes in over a single batch, and the user's only
        way out was deleting the cache — which threw away the transcription too and re-ran ASR.
        It also skipped the batch-level retry that switches to a shorter system prompt on the
        second attempt, so the one mechanism that might have produced different output never ran.

        **Halving the batch is what makes a content-dependent failure survivable.** If one cue is
        what the model trips over, splitting isolates it in log2(n) requests; everything around it
        translates normally and only that cue falls through to :func:`_degrade_or_reject`, which
        keeps its source text. Returning an empty list for the whole batch instead would push 30
        cues past ``MOSTLY_UNTRANSLATED_RATIO`` and fail the job all over again.
        """
        _log.warning(
            "translation server rejected a batch of %d (HTTP %d): %s", len(items), status, detail
        )

        if len(items) <= 1:
            # Nothing left to narrow. One unusable cue is exactly what the degrade path is for.
            return []

        if token is not None:
            token.raise_if_cancelled()

        middle = len(items) // 2
        halves: list[dict[str, Any]] = []

        for half in (items[:middle], items[middle:]):
            halves.extend(
                self.translate_items(
                    half,
                    source_language=source_language,
                    style=style,
                    glossary=glossary,
                    context=context,
                    attempt=attempt,
                    token=token,
                )
            )

        return halves

    def translate_items(
        self,
        items: Sequence[dict[str, Any]],
        *,
        source_language: str = "en",
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        context: Sequence[dict[str, Any]] | None = None,
        attempt: int = 1,
        token: CancellationToken | None = None,
    ) -> list[dict[str, Any]]:
        """One chat completion for one batch, parsed defensively."""
        if not items:
            return []

        if self._server is None:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "LLM 번역 서버가 준비되지 않았습니다.",
                detail="translate_items called before load()",
            )

        if token is not None:
            token.raise_if_cancelled()

        session = self._ensure_session()
        system = build_system_prompt(
            style, glossary, short=attempt > 1, source_language=source_language
        )
        user = self._build_user_message(items, context, source_language)

        payload = {
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            # Low but non-zero: greedy decoding makes a stuck model repeat itself forever.
            "temperature": self.temperature,
            "top_p": 0.9,
            "stream": False,
            "n_predict": -1,
        }

        try:
            response = session.post(
                f"{self._server.base_url}/v1/chat/completions",
                json=payload,
                timeout=_REQUEST_TIMEOUT_SECONDS,
            )
        except Exception as exc:  # noqa: BLE001 - requests raises a family of errors
            raise WorkerError(
                errors.TRANSLATION_FAILED,
                "번역 서버와 통신하지 못했습니다.",
                detail=repr(exc),
            ) from exc

        status = int(getattr(response, "status_code", 0))

        if 500 <= status < 600:
            return self._handle_server_rejection(
                items,
                status,
                _safe_text(response)[:500],
                source_language=source_language,
                style=style,
                glossary=glossary,
                context=context,
                attempt=attempt,
                token=token,
            )

        if status != 200:
            # 4xx is our own request being malformed. Retrying sends the identical bytes, so there
            # is nothing to gain by degrading — fail loudly and let the detail name the problem.
            raise WorkerError(
                errors.TRANSLATION_FAILED,
                f"번역 서버가 오류를 반환했습니다 (HTTP {status}).",
                detail=_safe_text(response)[:500],
            )

        try:
            body = response.json()
            content = body["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError, ValueError) as exc:
            raise WorkerError(
                errors.INVALID_TRANSLATION_RESPONSE,
                "번역 서버 응답을 이해하지 못했습니다.",
                recoverable=True,
                detail=f"{exc!r}: {_safe_text(response)[:500]}",
            ) from exc

        parsed = parse_translation_json(content)
        if parsed is None:
            _log.warning("unparseable LLM response on attempt %d: %r", attempt, content[:300])
            # Returning an empty list rather than raising lets the shared retry loop shorten the
            # instruction and ask again for exactly the missing ids.
            return []

        return parsed

    @staticmethod
    def _build_user_message(
        items: Sequence[dict[str, Any]],
        context: Sequence[dict[str, Any]] | None,
        source_language: str,
    ) -> str:
        parts: list[str] = [
            f"원본 언어: {describe_language(source_language)}",
            "출력 언어: 한국어(ko)",
        ]

        if context:
            rendered = json.dumps(
                [{"id": c["id"], "text": c.get("text", "")} for c in context],
                ensure_ascii=False,
            )
            parts.append(
                "다음은 바로 앞의 문맥이다. 참고만 하고 결과에 포함하지 않는다:\n" + rendered
            )

        payload = json.dumps(
            [{"id": int(i["id"]), "text": str(i.get("text", "") or "")} for i in items],
            ensure_ascii=False,
        )
        parts.append("번역할 항목:\n" + payload)

        return "\n\n".join(parts)

    # -- batch orchestration ---------------------------------------------------

    def translate_segments(
        self,
        segments: Sequence[dict[str, Any]],
        *,
        source_language: str,
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        options: BatchOptions | None = None,
        token: CancellationToken | None = None,
        on_batch_done: Any = None,
    ) -> dict[int, str]:
        batches = split_batches(segments, options)
        translations: dict[int, str] = {}

        for batch in batches:
            if token is not None:
                token.raise_if_cancelled()

            def run(
                items: list[dict[str, Any]],
                context: list[dict[str, Any]],
                attempt: int,
            ) -> list[dict[str, Any]]:
                return self.translate_items(
                    items,
                    source_language=source_language,
                    style=style,
                    glossary=glossary,
                    context=context,
                    attempt=attempt,
                    token=token,
                )

            translations.update(translate_with_retry(batch, run, token=token))

            if on_batch_done is not None:
                on_batch_done(batch, translations)

        return translations


# ---------------------------------------------------------------------------
# response parsing
# ---------------------------------------------------------------------------

_FENCE_PATTERN = re.compile(r"^\s*```(?:json)?\s*|\s*```\s*$", re.IGNORECASE)


def parse_translation_json(content: str) -> list[dict[str, Any]] | None:
    """Extract ``[{"id":.., "translation":..}]`` from a chat response.

    Chat models wrap JSON in markdown fences, prefix it with "물론입니다!", or append a note. The
    strategy is: strip fences, take the outermost bracketed span, parse, and keep only entries
    that actually look like translations.
    """
    if not content or not content.strip():
        return None

    text = _FENCE_PATTERN.sub("", content.strip())

    start = text.find("[")
    if start < 0:
        return None

    end = text.rfind("]")
    if end > start:
        candidate = text[start : end + 1]
    else:
        # No closing bracket at all: the model ran out of tokens mid-array. Hand the tail to the
        # repair pass, which salvages the objects that did complete.
        candidate = text[start:]

    try:
        parsed = json.loads(candidate)
    except json.JSONDecodeError:
        repaired = _repair(candidate)
        if repaired is None:
            return None
        parsed = repaired

    if not isinstance(parsed, list):
        return None

    result: list[dict[str, Any]] = []
    for entry in parsed:
        if not isinstance(entry, dict):
            continue
        if "id" not in entry:
            continue

        try:
            entry_id = int(entry["id"])
        except (TypeError, ValueError):
            continue

        translation = entry.get("translation")
        if translation is None:
            translation = entry.get("text") or entry.get("ko") or ""

        result.append({"id": entry_id, "translation": str(translation)})

    return result or None


def _repair(candidate: str) -> list[Any] | None:
    """Salvage a truncated array by parsing the objects that are complete.

    A model that ran out of tokens mid-array is common; recovering the complete prefix means the
    retry only has to ask for the tail.
    """
    objects: list[Any] = []
    depth = 0
    start = -1
    in_string = False
    escaped = False

    for index, char in enumerate(candidate):
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue

        if char == '"':
            in_string = True
        elif char == "{":
            if depth == 0:
                start = index
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0 and start >= 0:
                try:
                    objects.append(json.loads(candidate[start : index + 1]))
                except json.JSONDecodeError:
                    pass
                start = -1

    return objects or None


def _safe_text(response: Any) -> str:
    try:
        return str(getattr(response, "text", "") or "")
    except Exception:  # noqa: BLE001 - a mock without .text must not mask the real error
        return ""


def _read_stderr_tail(process: subprocess.Popen, limit: int = 1500) -> str:
    stream = getattr(process, "stderr", None)
    if stream is None:
        return ""
    try:
        data = stream.read() or b""
    except (OSError, ValueError):
        return ""
    if isinstance(data, bytes):
        return data.decode("utf-8", "replace")[-limit:]
    return str(data)[-limit:]
