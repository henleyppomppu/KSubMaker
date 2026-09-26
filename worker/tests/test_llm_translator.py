"""The llama-server engine: prompt contents, defensive JSON parsing, server lifecycle."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from conftest import make_segments
from ksubmaker_worker import errors
from ksubmaker_worker.batching import BatchOptions
from ksubmaker_worker import llm_translator as llm
from ksubmaker_worker.llm_translator import (
    SYSTEM_PROMPT_RULES,
    LlamaServer,
    LlmTranslator,
    build_system_prompt,
    choose_gpu_layers,
    free_port,
    parse_translation_json,
)

# ---------------------------------------------------------------------------
# prompt
# ---------------------------------------------------------------------------

EXPECTED_RULES = """다음은 영상 자막입니다.

규칙:
1. 모든 항목을 자연스러운 한국어로 번역한다.
2. id를 절대 변경하지 않는다.
3. 항목을 삭제하거나 합치지 않는다.
4. 새로운 정보를 추가하지 않는다.
5. 설명이나 주석을 출력하지 않는다.
6. 지정된 JSON 배열 형식으로만 반환한다.
7. 앞뒤 문맥을 고려한다.
8. 인명과 고유명사는 일관되게 번역한다."""


def test_rule_block_is_verbatim() -> None:
    assert SYSTEM_PROMPT_RULES == EXPECTED_RULES


def test_system_prompt_starts_with_the_rule_block() -> None:
    assert build_system_prompt().startswith(EXPECTED_RULES)


@pytest.mark.parametrize("style", ["natural", "literal", "polite", "casual", "preserve"])
def test_style_lines_are_appended_after_the_rules(style: str) -> None:
    prompt = build_system_prompt(style)

    assert prompt.startswith(EXPECTED_RULES)
    assert "문체:" in prompt[len(EXPECTED_RULES) :]


def test_polite_and_casual_prompts_differ() -> None:
    assert build_system_prompt("polite") != build_system_prompt("casual")


def test_glossary_is_rendered_into_the_prompt() -> None:
    prompt = build_system_prompt("natural", {"Kubernetes": "쿠버네티스"})

    assert "용어집" in prompt
    assert "Kubernetes → 쿠버네티스" in prompt


def test_glossary_is_bounded() -> None:
    huge = {f"term{i}": f"용어{i}" for i in range(500)}
    prompt = build_system_prompt("natural", huge)

    assert "term0 → 용어0" in prompt
    assert "term400" not in prompt


def test_output_format_is_always_stated() -> None:
    assert '[{"id": 1, "translation": "..."}]' in build_system_prompt()


def test_short_prompt_adds_the_retry_instruction() -> None:
    short = build_system_prompt(short=True)

    assert short.startswith(EXPECTED_RULES)
    assert "JSON 배열만 출력한다" in short
    assert "코드 블록" in short


# ---------------------------------------------------------------------------
# the target-language lock
# ---------------------------------------------------------------------------
#
# Rule 1 of the pinned block already says "자연스러운 한국어로 번역한다", and Qwen2.5 still answered
# 41% of a Japanese file in Chinese (측정 표본 B: 113 of 273 output lines Han-only 간체자). One
# positive instruction at position 1 of 8 was not enough, so the constraint is stated on its own.


# ---------------------------------------------------------------------------
# MSVC runtime
# ---------------------------------------------------------------------------
#
# ggml-base.dll and ggml-cuda.dll import MSVCP140.dll, and nothing in the portable build supplies
# it — the copies under tools\python are invisible to llama-server.exe, which is its own process
# and searches its own directory. On a machine without the redistributable the process either dies
# at load time with no stderr, or runs on the CPU backend without a word.


def test_a_healthy_machine_reports_no_missing_runtime() -> None:
    assert llm.missing_msvc_runtime(loader=lambda _name: object(), is_windows=True) == []


def test_every_unresolvable_runtime_dll_is_named() -> None:
    def refuse(name: str):
        if name == "msvcp140.dll":
            raise OSError("[WinError 126] 지정된 모듈을 찾을 수 없습니다")
        return object()

    assert llm.missing_msvc_runtime(loader=refuse, is_windows=True) == ["msvcp140.dll"]


def test_the_probe_is_a_no_op_off_windows() -> None:
    def explode(_name: str):  # pragma: no cover - must never be called
        raise AssertionError("no DLL may be probed on a non-Windows host")

    assert llm.missing_msvc_runtime(loader=explode, is_windows=False) == []


def test_the_remedy_names_the_redistributable_and_where_to_get_it() -> None:
    assert "Visual C++" in llm._MSVC_REMEDY  # noqa: SLF001 - the message is the point
    assert llm.MSVC_REDIST_URL in llm._MSVC_REMEDY  # noqa: SLF001


def test_a_loader_failure_at_startup_is_reported_as_the_missing_runtime() -> None:
    """STATUS_DLL_NOT_FOUND leaves no stderr — the process never ran.

    Without naming the cause the user was told "모델 파일이 손상되었을 수 있습니다" about a model
    file that is perfectly fine.
    """
    server = LlamaServer("model.gguf", executable="llama-server.exe")

    class Dead:
        returncode = llm._STATUS_DLL_NOT_FOUND  # noqa: SLF001
        stderr = None

        def poll(self):
            return self.returncode

    server.process = Dead()
    server.port = 1234

    with pytest.raises(errors.WorkerError) as excinfo:
        server._wait_for_health(_FakeSession(), None)  # noqa: SLF001

    assert "Visual C++" in excinfo.value.message
    assert "손상" not in excinfo.value.message


def test_the_prompt_states_korean_only_and_names_the_drift() -> None:
    prompt = build_system_prompt()

    assert "한국어" in prompt
    assert "중국어" in prompt, "the observed failure mode has to be named, not implied"
    assert "영어로 쓰지 않는다" in prompt


def test_the_prompt_states_the_direction_when_the_source_language_is_known() -> None:
    prompt = build_system_prompt(source_language="ja")

    assert "일본어(ja) → 한국어(ko)" in prompt


def test_an_unknown_source_language_does_not_break_the_direction_line() -> None:
    prompt = build_system_prompt(source_language="xx")

    assert "xx → 한국어(ko)" in prompt


def test_the_retry_prompt_repeats_the_language_lock() -> None:
    # The retry is exactly when drift matters: the first answer was already unusable.
    short = build_system_prompt(short=True)

    assert "한국어(한글)로만 쓴다" in short


def test_the_language_lock_never_disturbs_the_pinned_rule_block() -> None:
    for prompt in (
        build_system_prompt(),
        build_system_prompt("polite", {"a": "가"}, short=True, source_language="ja"),
    ):
        assert prompt.startswith(EXPECTED_RULES)


def test_the_request_names_both_languages_in_words() -> None:
    """A bare "ja" is a token the model can skip past; 일본어 is the word the rule is about."""
    engine, session = _engine(['[{"id": 1, "translation": "안녕"}]'])

    engine.translate_items([{"id": 1, "text": "こんにちは"}], source_language="ja")

    body = session.posts[0]["json"]
    system = body["messages"][0]["content"]
    user = body["messages"][1]["content"]

    assert "일본어(ja) → 한국어(ko)" in system
    assert "원본 언어: 일본어(ja)" in user
    assert "출력 언어: 한국어(ko)" in user


# ---------------------------------------------------------------------------
# response parsing
# ---------------------------------------------------------------------------


def test_parses_plain_json() -> None:
    content = '[{"id": 1, "translation": "안녕"}, {"id": 2, "translation": "잘 가"}]'
    assert parse_translation_json(content) == [
        {"id": 1, "translation": "안녕"},
        {"id": 2, "translation": "잘 가"},
    ]


def test_strips_markdown_fences() -> None:
    content = '```json\n[{"id": 1, "translation": "안녕"}]\n```'
    assert parse_translation_json(content) == [{"id": 1, "translation": "안녕"}]


def test_strips_a_bare_fence() -> None:
    content = '```\n[{"id": 1, "translation": "안녕"}]\n```'
    assert parse_translation_json(content) == [{"id": 1, "translation": "안녕"}]


def test_finds_the_array_inside_chatter() -> None:
    content = '물론입니다! 다음은 번역 결과입니다:\n[{"id": 1, "translation": "안녕"}]\n도움이 되었길 바랍니다.'
    assert parse_translation_json(content) == [{"id": 1, "translation": "안녕"}]


def test_recovers_the_complete_prefix_of_a_truncated_array() -> None:
    # A model that ran out of tokens mid-array is common; the retry then only has to ask for
    # the tail rather than the whole batch.
    content = '[{"id": 1, "translation": "안녕"}, {"id": 2, "translation": "잘'
    assert parse_translation_json(content) == [{"id": 1, "translation": "안녕"}]


def test_ids_given_as_strings_are_coerced() -> None:
    assert parse_translation_json('[{"id": "7", "translation": "일곱"}]') == [
        {"id": 7, "translation": "일곱"}
    ]


def test_alternative_key_names_are_accepted() -> None:
    assert parse_translation_json('[{"id": 1, "text": "안녕"}]') == [{"id": 1, "translation": "안녕"}]
    assert parse_translation_json('[{"id": 2, "ko": "잘 가"}]') == [{"id": 2, "translation": "잘 가"}]


def test_entries_with_no_id_are_dropped() -> None:
    content = '[{"translation": "익명"}, {"id": 1, "translation": "안녕"}]'
    assert parse_translation_json(content) == [{"id": 1, "translation": "안녕"}]


def test_brackets_inside_a_translation_do_not_confuse_the_extractor() -> None:
    content = '[{"id": 1, "translation": "대괄호 [예시] 포함"}]'
    assert parse_translation_json(content) == [{"id": 1, "translation": "대괄호 [예시] 포함"}]


@pytest.mark.parametrize(
    "content",
    ["", "   ", "죄송합니다. 번역할 수 없습니다.", "{}", "[]", "not json", "[not, valid, json"],
)
def test_unparseable_content_returns_none(content: str) -> None:
    assert parse_translation_json(content) is None


# ---------------------------------------------------------------------------
# server sizing / port
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("free_gib", "expected_at_least"),
    [(0, 0), (1, 0), (2, 0), (3, 12), (4, 20), (6, 32), (8, 48), (12, 99), (24, 99)],
)
def test_gpu_layers_scale_with_free_vram(free_gib: int, expected_at_least: int) -> None:
    layers = choose_gpu_layers(int(free_gib * 1024**3))
    assert layers == expected_at_least


def test_a_model_that_fits_comfortably_is_fully_offloaded() -> None:
    # 3 GiB model, 8 GiB free -> everything goes on the GPU.
    assert choose_gpu_layers(8 * 1024**3, 3 * 1024**3) == 99


def test_free_port_is_usable_and_ephemeral() -> None:
    port = free_port()
    assert 1024 < port < 65536


# ---------------------------------------------------------------------------
# server lifecycle
# ---------------------------------------------------------------------------


def test_missing_llama_server_raises_translation_model_not_found(tmp_path: Path) -> None:
    model = tmp_path / "model.gguf"
    model.write_bytes(b"GGUF")

    server = LlamaServer(model, executable=str(tmp_path / "no-such-llama-server"))

    with pytest.raises(errors.WorkerError) as excinfo:
        server.start(_FakeSession())

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND
    assert "모델 화면" in excinfo.value.message


def test_missing_model_file_raises_translation_model_not_found(tmp_path: Path) -> None:
    server = LlamaServer(tmp_path / "absent.gguf", executable="/bin/true")

    with pytest.raises(errors.WorkerError) as excinfo:
        server.start(_FakeSession())

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND


def test_stopping_a_server_that_never_started_is_safe(tmp_path: Path) -> None:
    LlamaServer(tmp_path / "model.gguf").stop()


def test_no_local_gguf_raises_with_the_searched_path(tmp_path: Path) -> None:
    engine = LlmTranslator(models_dir=tmp_path, session=_FakeSession())

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.load(model_id="qwen2.5-3b-instruct-q4km")

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND
    assert "qwen2.5-3b-instruct-q4km" in (excinfo.value.detail or "")


# ---------------------------------------------------------------------------
# translation over a faked HTTP server
# ---------------------------------------------------------------------------


class _FakeResponse:
    def __init__(self, payload: Any, status: int = 200) -> None:
        self.status_code = status
        self._payload = payload
        self.text = json.dumps(payload) if not isinstance(payload, str) else payload

    def json(self) -> Any:
        if isinstance(self._payload, str):
            raise ValueError("not json")
        return self._payload

    def close(self) -> None:
        return None


class _FakeSession:
    """Records requests and replies with scripted chat completions."""

    def __init__(self, replies: list[Any] | None = None) -> None:
        self.replies = replies or []
        self.posts: list[dict[str, Any]] = []
        self.gets: list[str] = []

    def get(self, url: str, **_kwargs: Any) -> _FakeResponse:
        self.gets.append(url)
        return _FakeResponse({"status": "ok"})

    def post(self, url: str, *, json: dict[str, Any], **_kwargs: Any) -> _FakeResponse:  # noqa: A002
        self.posts.append({"url": url, "json": json})
        if not self.replies:
            return _FakeResponse({"choices": [{"message": {"content": "[]"}}]})
        reply = self.replies.pop(0)
        if isinstance(reply, _FakeResponse):
            return reply
        return _FakeResponse({"choices": [{"message": {"content": reply}}]})


class _StubServer:
    def __init__(self, port: int = 12345) -> None:
        self.port = port
        self.stopped = False

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def start(self, _session: Any, _token: Any = None) -> None:
        return None

    def stop(self) -> None:
        self.stopped = True


def _engine(replies: list[Any]) -> tuple[LlmTranslator, _FakeSession]:
    session = _FakeSession(replies)
    engine = LlmTranslator(server=_StubServer(), session=session)
    return engine, session


def test_translate_items_posts_to_the_chat_endpoint() -> None:
    engine, session = _engine(['[{"id": 1, "translation": "안녕"}]'])
    result = engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert result == [{"id": 1, "translation": "안녕"}]
    assert session.posts[0]["url"].endswith("/v1/chat/completions")


def test_the_user_message_carries_the_items_as_json() -> None:
    engine, session = _engine(['[{"id": 5, "translation": "안녕"}]'])
    engine.translate_items([{"id": 5, "text": "hello"}], source_language="en")

    user = session.posts[0]["json"]["messages"][1]["content"]
    assert '"id": 5' in user
    assert '"text": "hello"' in user


def test_context_is_marked_read_only_and_kept_separate() -> None:
    engine, session = _engine(['[{"id": 2, "translation": "안녕"}]'])
    engine.translate_items(
        [{"id": 2, "text": "hello"}],
        source_language="en",
        context=[{"id": 1, "text": "previous line"}],
    )

    user = session.posts[0]["json"]["messages"][1]["content"]
    assert "previous line" in user
    assert "결과에 포함하지 않는다" in user


def test_the_system_message_is_the_rule_block() -> None:
    engine, session = _engine(['[{"id": 1, "translation": "안녕"}]'])
    engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert session.posts[0]["json"]["messages"][0]["content"].startswith(EXPECTED_RULES)


def test_a_retry_uses_the_shortened_instruction() -> None:
    engine, session = _engine(['[{"id": 1, "translation": "안녕"}]'])
    engine.translate_items([{"id": 1, "text": "hello"}], source_language="en", attempt=2)

    assert "JSON 배열만 출력한다" in session.posts[0]["json"]["messages"][0]["content"]


def test_an_unparseable_reply_yields_nothing_so_the_retry_loop_takes_over() -> None:
    engine, _ = _engine(["죄송합니다, 번역할 수 없습니다."])
    assert engine.translate_items([{"id": 1, "text": "hello"}], source_language="en") == []


def test_a_4xx_reply_is_a_translation_failure() -> None:
    # Our request is malformed. Retrying sends the identical bytes, so there is nothing to salvage.
    engine, _ = _engine([_FakeResponse({"error": "bad request"}, status=400)])

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert excinfo.value.code == errors.TRANSLATION_FAILED


def test_a_5xx_on_a_single_cue_yields_nothing_instead_of_failing_the_job() -> None:
    """The field failure (2026-08-08).

    llama.cpp answers 500 when the model's reply does not fit the chat template's own parser
    ("does not match the expected peg-native format"). Raising abandoned a job minutes in over one
    batch, and the only way forward the user found was deleting the cache — which also threw away
    the transcription and re-ran ASR. The batch retry and, failing that, the degrade path own this
    decision now.
    """
    engine, _ = _engine([_FakeResponse({"error": "server_error"}, status=500)])

    assert engine.translate_items([{"id": 1, "text": "hello"}], source_language="en") == []


def test_a_rejected_batch_is_halved_so_one_bad_cue_cannot_lose_the_rest() -> None:
    """Whole-batch surrender would push 30 cues past MOSTLY_UNTRANSLATED_RATIO and fail anyway.

    Splitting isolates whatever the model trips over: here the first half succeeds on its own and
    only the offending cue is left with nothing, which is small enough for the degrade path to
    carry through as source text.
    """
    engine, session = _engine(
        [
            _FakeResponse({"error": "server_error"}, status=500),  # the batch of four
            '[{"id": 1, "translation": "하나"}, {"id": 2, "translation": "둘"}]',  # left half
            _FakeResponse({"error": "server_error"}, status=500),  # right half
            '[{"id": 3, "translation": "셋"}]',  # right half, split again
            _FakeResponse({"error": "server_error"}, status=500),  # id 4 alone: genuinely bad
        ]
    )

    result = engine.translate_items(
        [{"id": i, "text": f"line {i}"} for i in (1, 2, 3, 4)], source_language="ja"
    )

    assert [r["id"] for r in result] == [1, 2, 3]
    assert len(session.posts) == 5, "the batch should have been halved, not abandoned"


def test_a_reply_with_no_choices_is_an_invalid_response() -> None:
    engine, _ = _engine([_FakeResponse({"unexpected": True})])

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert excinfo.value.code == errors.INVALID_TRANSLATION_RESPONSE
    assert excinfo.value.recoverable is True


def test_translating_before_load_is_an_error() -> None:
    engine = LlmTranslator(session=_FakeSession())

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "x"}], source_language="en")

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND


def test_empty_input_never_hits_the_server() -> None:
    engine, session = _engine([])
    assert engine.translate_items([], source_language="en") == []
    assert session.posts == []


def test_translate_segments_retries_only_the_missing_ids() -> None:
    engine, session = _engine(
        [
            '[{"id": 1, "translation": "하나"}]',
            '[{"id": 2, "translation": "둘"}, {"id": 3, "translation": "셋"}]',
        ]
    )

    result = engine.translate_segments(
        make_segments(3), source_language="en", options=BatchOptions(max_items=10)
    )

    assert result == {1: "하나", 2: "둘", 3: "셋"}
    second_request = session.posts[1]["json"]["messages"][1]["content"]
    assert '"id": 1' not in second_request
    assert '"id": 2' in second_request


def test_unload_stops_the_server() -> None:
    server = _StubServer()
    engine = LlmTranslator(server=server, session=_FakeSession())
    engine.unload()

    assert server.stopped is True


# ---------------------------------------------------------------------------
# GPU 로 못 띄우는 카드 — 실패 대신 CPU 로
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "detail",
    [
        # 실기 신고(GTX 1070) 의 detail 을 그대로 옮긴 것.
        "llama-server exited with 3221226505: ggml-cuda.cu:106: CUDA error",
        "no kernel image is available for execution on the device",
        "cublas64_12.dll load failed",
    ],
)
def test_a_cuda_startup_death_is_recognised(detail: str) -> None:
    """번들한 CUDA 빌드에 이 GPU 세대 커널이 없으면 llama-server 가 시작 중에 죽는다.

    CUDA 13 빌드에는 Pascal(6.x) 커널이 아예 없다. 그것을 '모델 파일이 손상되었을 수 있습니다'
    로 보고하면 사용자가 엉뚱한 곳을 찾는다.
    """
    assert llm.looks_like_gpu_startup_failure(detail)


@pytest.mark.parametrize(
    "detail",
    ["llama-server exited with 1: failed to load model: bad magic", "", None],
)
def test_an_ordinary_failure_is_not_mistaken_for_a_gpu_one(detail: str | None) -> None:
    # 오탐은 CPU 로 한 번 더 시도하는 비용이지만, 누락은 실패한 작업이다 — 느슨한 쪽이 맞다.
    assert not llm.looks_like_gpu_startup_failure(detail)


def test_a_three_gigabyte_card_never_offloads() -> None:
    """GTX 1060 3GB 가 '동작한' 이유. GPU 를 안 썼기 때문이지 지원돼서가 아니다.

    같은 파스칼이라도 VRAM 이 넉넉한 1070 은 임계값을 넘겨 오프로드하고, 그래서 죽었다.
    """
    three_gb = int(2.8 * 1024**3)
    assert llm.choose_gpu_layers(three_gb) == 0

    seven_gb = int(7.0 * 1024**3)
    assert llm.choose_gpu_layers(seven_gb) > 0

