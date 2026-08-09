# 모델 관리

KSubMaker는 모델을 **번들하지 않습니다.** 프로그램은 작고, 사용자가 자기 하드웨어에 맞는
모델만 내려받습니다. 이 문서는 카탈로그의 실제 내용, 저장 위치, 다운로드·이어받기·검증·삭제가
어떻게 동작하는지, 그리고 인터넷이 없는 PC에 수동으로 넣는 방법을 설명합니다.

정본 코드: `src/KSubMaker.Domain/Models/ModelCatalog.cs`,
`src/KSubMaker.Infrastructure/Models/HttpModelManager.cs`,
`worker/ksubmaker_worker/model_manager.py`.

---

## 1. 모델 카탈로그

`ModelCatalog.BuiltIn()`에 정의된 9개가 전부입니다. **모델 추가는 이 파일의 데이터 변경이지,
파이프라인 코드 변경이 아닙니다** — 그것이 "모델 이름을 하드코딩하지 말라"는 요구사항의 구현
형태입니다.

크기 열의 값은 `ApproxSizeBytes`(MiB 단위 상수)이며, 다운로드 전 "예상 디스크 용량" 열에
쓰이는 근사치입니다. VRAM 열은 `VramGbByComputeType`의 값입니다.

### 1.1 음성 인식 (Whisper, CTranslate2 변환본)

| 모델 id | 표시 이름 | Hugging Face 저장소 | 예상 크기 | VRAM float16 | VRAM int8_float16 | VRAM int8 | 라이선스 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `whisper-base` | Whisper base (CTranslate2) | `Systran/faster-whisper-base` | 145 MiB | 0.7 GB | 0.5 GB | 0.4 GB | MIT (가중치: OpenAI Whisper, MIT) |
| `whisper-small` | Whisper small (CTranslate2) | `Systran/faster-whisper-small` | 484 MiB | 1.6 GB | 1.0 GB | 0.9 GB | MIT |
| `whisper-medium` | Whisper medium (CTranslate2) | `Systran/faster-whisper-medium` | 1,530 MiB | 4.3 GB | 2.6 GB | 2.2 GB | MIT |
| `whisper-large-v3` | Whisper large-v3 (CTranslate2) | `Systran/faster-whisper-large-v3` | 3,090 MiB | 5.5 GB | 3.4 GB | 3.1 GB | MIT |
| `whisper-large-v3-turbo` | Whisper large-v3-turbo (CTranslate2) | `deepdml/faster-whisper-large-v3-turbo-ct2` | 1,620 MiB | 3.1 GB | 2.0 GB | 1.8 GB | MIT |

구성 파일(5개 모델 공통): `config.json`, `model.bin`, `tokenizer.json`, `vocabulary.txt`

카탈로그에 적힌 한국어 설명:

* **base** — 가장 가벼운 모델. 정확도는 낮지만 저사양 GPU와 CPU에서도 동작합니다.
* **small** — VRAM 4GB 이하 환경 권장. 속도와 정확도의 균형이 좋습니다.
* **medium** — VRAM 6GB 환경 권장. int8_float16으로 실행하면 안정적입니다.
* **large-v3** — 최고 품질. VRAM 12GB 이상에서 float16으로 실행하는 것을 권장합니다.
* **large-v3-turbo** — large-v3에 가까운 정확도에 약 4배 빠른 속도. VRAM 8GB 환경의 기본값입니다.

### 1.2 번역 (NLLB-200, CTranslate2 변환본)

| 모델 id | 표시 이름 | Hugging Face 저장소 | 예상 크기 | VRAM float16 | VRAM int8_float16 | VRAM int8 | 라이선스 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `nllb-200-distilled-600M` | NLLB-200 distilled 600M (CTranslate2) | `entai2965/nllb-200-distilled-600B-ctranslate2` | 2,460 MiB | 1.6 GB | 1.0 GB | 0.9 GB | **CC-BY-NC-4.0 (비상업적 사용)** |
| `nllb-200-distilled-1.3B` | NLLB-200 distilled 1.3B (CTranslate2) | `entai2965/nllb-200-distilled-1.3B-ctranslate2` | 5,400 MiB | 3.2 GB | 1.9 GB | 1.7 GB | **CC-BY-NC-4.0 (비상업적 사용)** |

구성 파일: `config.json`, `model.bin`, `shared_vocabulary.json`, `sentencepiece.bpe.model`,
`tokenizer.json`, `special_tokens_map.json`, `tokenizer_config.json`

#### 일본어 전용 — Kotoba-Whisper v2.0

| 모델 id | 표시 이름 | Hugging Face 저장소 | 예상 크기 | VRAM(대략) | 라이선스 |
| --- | --- | --- | --- | --- | --- |
| `kotoba-whisper-v2.0` | Kotoba-Whisper v2.0 — 일본어 전용 (CTranslate2) | `kotoba-tech/kotoba-whisper-v2.0-faster` | 1,446 MiB | 1.8–3.0 GB | MIT |

OpenAI Whisper를 일본어 음성으로 추가 학습한 파인튜닝입니다. `large-v3`의 **절반 크기**라 더
빠르고 VRAM도 적게 씁니다. 파일 구성이 large-v3 세대와 같아(`model.bin`, `vocabulary.json`,
`preprocessor_config.json` …) 별도 처리 없이 그대로 로드됩니다.

> **자동 권장에서는 절대 선택되지 않습니다.** 하드웨어 권장은 음성 인식을 시작하기 **전에**
> 정해지므로 원본 언어를 알 수 없습니다. 일본어 전용 모델을 자동으로 물리면 다른 언어 영상에서
> 오히려 정확도가 떨어집니다. 폴더가 일본어 영상이라는 것을 아는 사용자가 직접 고르는 선택지입니다.
> (`HardwareRecommendationPolicyTests`가 이것을 고정합니다.)

> 🛑 **단어 단위 타임스탬프를 지원하지 않습니다. 자동으로 꺼집니다.**
>
> 이 변환본의 `config.json`은 `alignment_heads`에 **large-v3의 값을 그대로** 담고 있습니다 —
> 디코더 레이어 7·10·12·13·16·17·19·21·24·25를 가리킵니다. 그런데 distil 구조라 이 모델의
> 디코더는 **2층뿐**입니다. 단어 타임스탬프를 켜면 faster-whisper가 없는 레이어의
> cross-attention을 요구하고, CTranslate2가 배열 밖을 읽어 **워커 프로세스가
> ACCESS_VIOLATION(0xC0000005)으로 즉사**합니다. 네이티브 크래시라 파이썬 예외 처리로 잡히지
> 않습니다.
>
> 실기에서 실제로 발생했습니다(종료 코드 -1073741819, 인식 시작 22초 후). 그래서
> `ModelDescriptor.SupportsWordTimestamps = false`로 표시하고 `WorkerJobProcessor`가 실행 시
> 강제로 끕니다. 설정에서 켜 두어도 이 모델을 쓰는 동안에는 무시됩니다.
>
> 대가는 자막 줄 나누기입니다 — `split_segments`가 단어 타임스탬프 대신 문자 비율로 근사하므로
> 긴 세그먼트를 자르는 위치가 조금 거칠어집니다. 크래시보다는 낫습니다.

> ⚠️ **인식 품질은 아직 측정하지 않았습니다.** 일본어 특화라는 설계상의 근거만 있습니다.

> ⚠️ **NLLB-200은 기본 번역 모델이며 CC-BY-NC-4.0 — 비상업적 사용만 허용됩니다.**
> 상업적 용도로 자막을 만들려면 번역 엔진을 로컬 LLM으로 바꾸거나 다른 모델을 써야 합니다.
> 다만 로컬 LLM 쪽도 라이선스를 확인하세요 — Qwen2.5는 Apache-2.0이지만 아래 경고대로 품질
> 문제가 있고, Gemma 3는 Apache-2.0이 아니라 Gemma Terms of Use(사용 제한 조항이 있습니다)입니다.
> 자세한 내용은 [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md).

### 1.3 로컬 LLM (GGUF)

| 모델 id | 표시 이름 | Hugging Face 저장소 | 파일 | 예상 크기 | VRAM(대략) | 라이선스 |
| --- | --- | --- | --- | --- | --- | --- |
| `gemma-3-4b-it-q4km` | Gemma 3 4B Instruct (GGUF Q4_K_M) | `unsloth/gemma-3-4b-it-GGUF` | `gemma-3-4b-it-Q4_K_M.gguf` | 2,374 MiB | 3.0–3.6 GB | Gemma Terms of Use |
| `gemma-3-12b-it-q4km` | Gemma 3 12B Instruct (GGUF Q4_K_M) | `unsloth/gemma-3-12b-it-GGUF` | `gemma-3-12b-it-Q4_K_M.gguf` | 6,963 MiB | 7.6–8.6 GB | Gemma Terms of Use |
| `qwen2.5-3b-instruct-q4km` | Qwen2.5 3B Instruct (GGUF Q4_K_M) | `Qwen/Qwen2.5-3B-Instruct-GGUF` | `qwen2.5-3b-instruct-q4_k_m.gguf` | 2,020 MiB | 2.4–3.0 GB | Apache-2.0 |
| `qwen2.5-7b-instruct-q4km` | Qwen2.5 7B Instruct (GGUF Q4_K_M) | `Qwen/Qwen2.5-7B-Instruct-GGUF` | `qwen2.5-7b-instruct-q4_k_m.gguf` | 4,680 MiB | 5.2–6.2 GB | Apache-2.0 |

* **Gemma 3 4B** — 일→한 자막용 기본 LLM. VRAM 12GB에서 음성 인식 모델과 함께 올라갑니다.
* **Gemma 3 12B** — 품질 우선. 12GB에서는 `whisper-large-v3`와 동시에 못 올라가 방식 B로 내려가므로
  VRAM 16GB 이상에서만 권장합니다.
* **Qwen2.5 3B / 7B** — 설치는 가능하지만 **권장하지 않습니다.**

> ⚠️ **Qwen2.5는 일본어 자막에서 중국어로 샙니다.** 실측(측정 표본 B, `qwen2.5-7b-instruct-q4km`):
> 출력 273줄 중 **113줄이 간체자 중국어**(`请多关照`, `翻开教科书第39页` 등)였고 41줄은 번역되지
> 않은 일본어로 남아 **57%가 한국어가 아니었습니다.** 시스템 프롬프트 규칙 1이 "자연스러운
> 한국어로 번역한다"인데도 그렇습니다. 중국어 중심 모델이 원문이 영어가 아닐 때 보이는 성향이라
> 프롬프트로 눌러지지 않습니다. 카탈로그에는 남겨 두지만(이미 내려받은 사용자에게서 모델을
> 빼앗는 편이 더 나쁩니다) 하드웨어 권장은 더 이상 Qwen을 고르지 않습니다.

> **EXAONE은 후보가 아닙니다.** 한국어에 강한 모델을 찾을 때 가장 먼저 떠오르지만, LG가
> **영어·한국어 이중 언어** 모델로 공개했고 모델 카드에 일본어 언급이 없습니다. 원문이 일본어인
> 이 작업에서는 원문을 못 읽는 모델이라 Qwen보다 나쁩니다.

> **Google 공식 `google/gemma-3-*-qat-*-gguf` 저장소는 `gated: "manual"`입니다.** 다운로더에
> 토큰이 없어 401이 납니다. 그래서 같은 양자화를 게이팅 없이 공개하는 unsloth 미러를 씁니다 —
> `deepdml`·`entai2965` 변환본을 쓰는 것과 같은 이유입니다.

이 두 모델은 **`llama-server` 실행 파일이 있어야만** 쓸 수 있습니다. 받는 방법은 §7.

---

## 2. 저장 위치

| 대상 | 경로 |
| --- | --- |
| 모델 루트(기본) | `%LOCALAPPDATA%\KSubMaker\models` |
| 모델 하나 | `%LOCALAPPDATA%\KSubMaker\models\{modelId}\` |
| 무결성 매니페스트 | `%LOCALAPPDATA%\KSubMaker\models\{modelId}\.ksubmaker-manifest.json` |
| 다운로드 중인 조각 | 같은 폴더의 `{파일명}.part` |

경로는 `IAppPaths.ModelDirectory(modelId)` 한 곳에서만 만들어집니다. 모델 id는 디렉터리 이름으로
그대로 쓰이므로 `AppPaths.Sanitize`가 경로 구분자와 금지 문자를 `_`로 바꿉니다 —
`faster-whisper/large` 같은 id가 중첩 폴더를 만드는 사고를 막습니다.

설정 화면(**설정 → 경로**)에서 모델 폴더를 다른 드라이브로 옮길 수 있습니다.

옮긴 위치는 워커에도 그대로 전달됩니다. `WorkerProcessClient`가 워커 프로세스를 띄울 때
환경 변수 세 개를 넣습니다.

| 변수 | 값 | 워커에서 읽는 곳 |
| --- | --- | --- |
| `KSUBMAKER_MODELS_DIR` | `IAppPaths.ModelsDirectory` | `model_manager.models_root()` — 모델 관리자·인식기·번역기가 모두 여기서 출발합니다 |
| `KSUBMAKER_TOOLS_DIR` | `IAppPaths.ToolsDirectory` | `ffmpeg_service._candidate_roots()`, `llm_translator.find_llama_server()` |
| `HF_HOME` | `<모델 폴더>/.hf-cache` | huggingface_hub. 허브 폴백 다운로드가 사용자 프로필이 아니라 고른 폴더 안에 남게 합니다 |

프로토콜 필드가 아니라 환경 변수인 이유는, 워커가 작업이 도착하기 **전에** 모델 위치를 알아야
하기 때문입니다 — `listModels`/`verifyModel`은 작업 밖에서 돌고, 엔진들은 워커가 시작할 때
만들어집니다.

호스트가 넣는 값이 기존 시스템 환경 변수보다 **우선합니다.** 경로의 정본은 설정 화면입니다.
워커를 직접(개발용으로) 실행할 때는 그 환경 변수를 손으로 지정하면 됩니다.

---

## 3. 다운로드

**모델 화면**(메인 창의 "모델 관리")에서 모델을 고르고 **다운로드**를 누르면 시작됩니다.
구현은 C# 쪽 `HttpModelManager`입니다(워커의 `downloadModel` 명령도 같은 일을 하도록 구현되어
있지만 현재 호스트는 이 C# 경로만 씁니다).

### 3.1 흐름

```
① 저장소 트리 조회
   GET https://huggingface.co/api/models/{repositoryId}/tree/main?recursive=1
   → 파일별 크기와 (LFS인 경우) SHA-256

② 파일마다
   GET https://huggingface.co/{repositoryId}/resolve/main/{path}
   Range: bytes={이미 받은 바이트}-          ← 이어받기
   → {파일명}.part 에 스트리밍

③ .part 전체를 SHA-256으로 해싱
   원격 digest가 있으면 비교 → 불일치면 실패(MODEL_VERIFICATION_FAILED)
   원격 digest가 없으면 계산 결과를 그대로 매니페스트에 기록

④ .part → 최종 파일명으로 이동

⑤ 모든 파일이 끝나면 .ksubmaker-manifest.json 작성 (원자적)
⑥ 데이터베이스의 ModelInstallation 행 갱신 (Installed=true, Verified=true, …)
```

* URL은 **반드시 HTTPS**입니다. `_require_https` / `BuildUri`가 강제합니다.
* 진행률은 파일 단위가 아니라 **모델 단위**로 보고됩니다.
* 무기한 타임아웃 대신 **읽기 정체 감시**를 씁니다(60초 동안 한 바이트도 안 오면 연결이 죽은
  것으로 판정). 3GB 전송에 벽시계 타임아웃을 거는 것은 의미가 없기 때문입니다.
* `HttpClient.Timeout = Timeout.InfiniteTimeSpan`이고, User-Agent를 붙입니다(일부 Hugging Face
  엣지 노드가 UA 없는 요청을 거부합니다).

### 3.2 이어받기

`.part` 파일은 **일부러 남깁니다.** 취소하거나 프로그램이 죽어도 지우지 않습니다. 그것이
다음 시도를 "처음부터"가 아니라 "이어받기"로 만드는 유일한 근거이고, 3GB 파일에서는 이 차이가
큽니다.

한 가지 안전장치가 있습니다. **원격 digest를 모르는 파일의 `.part`는 이어받지 않습니다.**
digest 없이 이어받으면 그 사이 원격 내용이 바뀌었을 때 두 버전이 섞인 파일이 만들어지고,
검증할 방법도 없습니다. 그런 경우 조각을 버리고 처음부터 받습니다.

### 3.3 왜 작은 파일에는 원격 digest가 없는가

Hugging Face 트리 API는 **LFS 항목에만** 진짜 SHA-256을 공개합니다. 일반 git blob의 `oid`는
git 오브젝트 헤더 + 내용의 SHA-**1**이라 파일 해시와 비교할 수 없습니다. 그래서:

* `model.bin`, `*.gguf` 같은 큰 가중치 → LFS → 원격 SHA-256과 대조
* `config.json`, `tokenizer.json` 같은 작은 파일 → 원격 digest 없음 → **로컬에서 해싱해
  매니페스트에 기록**

이는 오류가 아니라 알려진 한계이며, 코드 주석에도 그렇게 적혀 있습니다. 검증할 가치가 있는
큰 파일은 전부 LFS입니다. 그리고 로컬 해시를 기록해 두는 덕분에 **나중의 오프라인 재검증은
모든 파일에 대해 의미가 있습니다.**

### 3.4 다운로드 취소

모델 화면의 취소 버튼(또는 창을 닫으면) `CancellationToken`이 발화합니다. `.part`는 남습니다.
같은 모델의 다운로드를 다시 시작하면 그 지점부터 이어받습니다.

---

## 4. 매니페스트와 검증

### 4.1 `.ksubmaker-manifest.json`

```json
{
  "ModelId": "whisper-small",
  "RepositoryId": "Systran/faster-whisper-small",
  "Files": [
    { "RelativePath": "config.json",     "SizeBytes": 2249,      "Sha256": "3a1f…" },
    { "RelativePath": "model.bin",       "SizeBytes": 483546112, "Sha256": "9c07…" },
    { "RelativePath": "tokenizer.json",  "SizeBytes": 2202267,   "Sha256": "e5b2…" },
    { "RelativePath": "vocabulary.txt",  "SizeBytes": 460138,    "Sha256": "77da…" }
  ],
  "CreatedAtUtc": "2026-08-02T04:11:23.4517Z"
}
```

원자적으로 씁니다(임시 파일 → 이동). 매니페스트 자체의 SHA-256은 데이터베이스의
`ModelInstallation.Sha256` 열에 저장됩니다.

### 4.2 검증은 완전히 오프라인

모델 화면의 **검증** 버튼(`IModelManager.VerifyAsync`)이 하는 일:

1. `.ksubmaker-manifest.json`을 읽습니다. 없으면 즉시 실패("검증에 필요한 매니페스트가 없습니다").
2. 매니페스트의 각 항목에 대해:
   - 파일이 존재하는가
   - **크기가 일치하는가** (해싱 전에 빠르게 걸러 냅니다)
   - SHA-256이 일치하는가
3. 결과를 데이터베이스의 `Verified`/`VerifiedAtUtc`에 기록합니다.

**네트워크를 전혀 쓰지 않습니다.** 인터넷 없는 PC에 수동으로 넣은 모델도 검증할 수 있어야
하기 때문입니다(§6).

`worker/ksubmaker_worker/model_manager.py`의 `verify()`도 같은 규칙이며, 취소 토큰을 지원해
큰 파일을 해싱하는 중에도 멈출 수 있습니다.

### 4.3 "설치됨" 판정

`IsInstalledAsync`는 **매니페스트가 있고, 거기에 적힌 파일이 전부 존재할 때만** true입니다.
파일이 있는지만 보는 것보다 엄격한 이유는, 취소된 다운로드가 남긴 빈 디렉터리가 "설치됨"으로
보이면 사용자가 다운로드 버튼 대신 "모델 로드 실패"를 만나게 되기 때문입니다.

워커 쪽 `find_local_model`은 같은 이유로 `model.bin`/`model.safetensors`/`*.gguf` 중 하나가
실제로 있는지를 확인하고, 허브 스타일 중첩 레이아웃(`models/<id>/snapshots/<sha>/`)도 한 단계
탐색해 줍니다.

### 4.4 설정 화면의 설치 상태

**설정 → 음성 인식 / 번역**의 모델 목록도 같은 판정을 씁니다. 각 항목 뒤에 **설치됨** 또는
**미설치**가 붙고, 저장할 때 고른 모델이 없으면 확인 대화상자가 뜹니다.

원래는 아무 표시가 없었습니다. 사용자가 한 번도 내려받지 않은 `whisper-small`을 고를 수 있었고,
그 사실을 알려 주는 것은 한참 뒤 큐가 돌기 시작한 다음의 `WHISPER_MODEL_NOT_FOUND` 하나뿐이었
습니다.

규칙은 `KSubMaker.Domain.Models.ModelSelectionValidator`에 있습니다(순수 함수, `net10.0`이라
Linux CI에서 테스트됩니다 — `ModelSelectionValidatorTests`).

| 상황 | 경고 | 왜 |
| --- | --- | --- |
| 모델이 `auto` | 없음 | `ResolveModelIdAsync`가 실제로 설치된 것 중에서만 고릅니다 |
| Whisper 모델 미설치 | 경고 | 음성 인식은 언제나 돕니다 |
| 번역 모델 미설치, 엔진이 **로컬 번역 모델** | 경고 | 그 모델을 올립니다 |
| 번역 모델 미설치, 엔진이 **로컬 LLM** | 없음 | NLLB 를 올리지 않습니다 |
| LLM 모델 미설치, 엔진이 **로컬 LLM** | 경고 | 그 모델을 올립니다 |
| 엔진이 **Fake** 또는 Fake AI 모드 | 없음 | 아무 모델도 올리지 않습니다 |

경고는 저장을 **막지 않습니다.** 지금 막 내려받으려는 사람이나 모델 폴더를 옮기는 중인 사람이
있고, 그들에게는 이 조합이 정상입니다.

---

## 5. 삭제

모델 화면의 **삭제** 버튼은 `%LOCALAPPDATA%\KSubMaker\models\{modelId}\` 디렉터리를 통째로
재귀 삭제하고 데이터베이스 행을 지웁니다. 다른 프로그램이 파일을 잡고 있으면
`MODEL_DOWNLOAD_FAILED`와 함께 "다른 프로그램이 파일을 사용 중일 수 있습니다"가 나옵니다 —
대개 KSubMaker 자신이 그 모델을 로드한 상태입니다. 큐를 멈추고 다시 시도하세요.

---

## 6. `auto`는 어떻게 결정되는가

설정에서 모델을 `auto`로 두면 두 단계를 거칩니다.

### 6.1 하드웨어 → 권장 모델 (`HardwareRecommendationPolicy.Recommend`)

CUDA를 쓸 수 있을 때, **주 GPU의 전체 VRAM** 기준:

| VRAM | Whisper 모델 | 정밀도 | 빔 | 번역 모델 | LLM 모델 |
| --- | --- | --- | --- | --- | --- |
| 16 GB 이상 | `whisper-large-v3` | float16 | 5 | `nllb-200-distilled-1.3B` | `gemma-3-12b-it-q4km` |
| 12 GB 이상 | `whisper-large-v3` | float16 | 5 | `nllb-200-distilled-1.3B` | `gemma-3-4b-it-q4km` |
| 10 GB 이상 | `whisper-large-v3-turbo` | int8_float16 | 5 | `nllb-200-distilled-600M` | `gemma-3-4b-it-q4km` |
| 8 GB 이상 | `whisper-large-v3-turbo` | int8_float16 | 5 | `nllb-200-distilled-600M` | `gemma-3-4b-it-q4km` |
| 6 GB 이상 | `whisper-medium` | int8_float16 | 5 | `nllb-200-distilled-600M` | `gemma-3-4b-it-q4km` |
| 4 GB 이상 | `whisper-small` | int8_float16 | 3 | `nllb-200-distilled-600M` | `gemma-3-4b-it-q4km` |
| 4 GB 미만 | `whisper-small` | int8 | 1 | `nllb-200-distilled-600M` | `gemma-3-4b-it-q4km` |

CUDA를 못 쓰면(GPU 없음 또는 CUDA 로드 실패) **CPU 폴백**입니다:
RAM 16GB 이상이면 `whisper-medium`, 아니면 `whisper-small`. 정밀도는 `int8`, 빔은 1,
번역 모델은 `nllb-200-distilled-600M`, 처리 방식은 B로 고정됩니다.

정책은 **일부러 보수적**입니다. 낮게 잡혀서 끝나는 편이, 높게 잡혀서 파일 묶음 중간에 CUDA
OOM으로 죽는 것보다 낫습니다. 여기서 나오는 모든 값은 설정 화면에서 덮어쓸 수 있습니다.

동시 상주 가능 여부도 여기서 나옵니다:
`Whisper VRAM + 번역 VRAM + 1.0GB(드라이버·컴포지터 여유) ≤ 전체 VRAM`이면 두 모델을 동시에
올릴 수 있다고 보고 처리 방식 A를 고릅니다. 아니면 B입니다. 16GB 이상이면서 여유가 2.5GB
이상 남으면 C입니다.

### 6.2 권장 모델 → 실제 모델 (`IModelManager.ResolveModelIdAsync`)

1. 사용자가 명시적으로 모델을 골랐고 그것이 카탈로그에 있고 종류가 맞으면 → **그것을 씁니다.**
   설치되어 있지 않아도 그렇습니다(다운로드를 유도할지는 호출자가 결정).
2. `auto`이면 권장 모델을 봅니다. **설치되어 있으면** 그것.
3. 권장 모델이 설치되어 있지 않으면 → **해당 종류에서 설치된 것 중 가장 큰 모델**로 폴백합니다.
   큰 모델이 느리지만 정확하고, 이 시점의 유일한 강한 제약은 "설치되어 있는가"입니다.
   폴백이 일어나면 로그에 남습니다.
4. 그 종류의 모델이 하나도 설치되어 있지 않으면 `null` → 작업이 `WHISPER_MODEL_NOT_FOUND`
   또는 `TRANSLATION_MODEL_NOT_FOUND`로 실패합니다.

---

## 7. `llama-server` 받기 (로컬 LLM 엔진용 — 기본 미포함)

번역 엔진을 **로컬 LLM**으로 바꾸면 워커가 `llama-server` 실행 파일을 필요로 합니다. GGUF
모델 파일만으로는 동작하지 않습니다. 이 실행 파일은 **기본 배포에 포함되지 않는 선택
구성 요소**입니다.

### 7.1 스크립트로 받기 (권장)

```powershell
# 저장소 루트에서, PowerShell
.\scripts\fetch-llama.ps1
```

기본적으로 llama.cpp의 GitHub 릴리스에서 Windows x64 CUDA 자산을 찾아 `tools/llama/`에
풉니다. 재현 가능한 빌드를 원하면 태그와 SHA-256을 고정하세요.

```powershell
.\scripts\fetch-llama.ps1 -Tag b4321 -Sha256 <64자리 해시> -Force
```

### 7.2 수동으로 받기

1. <https://github.com/ggml-org/llama.cpp> 의 **Releases** 페이지를 엽니다.
2. Windows x64 CUDA 자산을 고릅니다. 파일명은 릴리스마다 다르며 대체로
   `llama-<태그>-bin-win-cuda-*-x64.zip` 형태입니다. CUDA 툴킷 버전이 여러 개면 설치된
   NVIDIA 드라이버가 지원하는 것을 고르세요(드라이버가 새 것이면 아무거나 됩니다).
3. 압축을 풀고 아래 구조가 되도록 복사합니다. **`llama-server.exe`와 그 DLL들이 같은 폴더에
   있어야 합니다.**

```
<설치 폴더 또는 저장소 루트>\tools\llama\
    llama-server.exe
    ggml-cuda.dll          (릴리스 구성에 따라 이름·개수가 다릅니다)
    ggml-base.dll
    llama.dll
    …
```

4. GGUF 모델을 모델 화면에서 내려받습니다(`Qwen2.5 3B/7B Instruct`).
5. **설정 → 번역 → 번역 엔진**을 "로컬 LLM"으로 바꿉니다.

### 7.3 워커가 `llama-server`를 찾는 순서

`llm_translator.find_llama_server()`:

1. `%KSUBMAKER_TOOLS_DIR%\llama\llama-server.exe`
2. 패키지 기준 상위 세 단계의 `tools\llama\` (소스 트리 실행 시)
3. `<파이썬 실행 파일 폴더>\tools\llama\`
4. 현재 작업 디렉터리의 `tools\llama\`
5. 각 후보에 대해 `llama\llama-server.exe`, `llama\bin\llama-server.exe`, `llama-server.exe`
6. 마지막 수단으로 PATH

찾지 못하면 `TRANSLATION_MODEL_NOT_FOUND`와 함께 "로컬 LLM 실행 파일(llama-server)을 찾을 수
없습니다. 모델 화면에서 로컬 LLM 구성 요소를 설치한 뒤 다시 시도하세요."가 나옵니다.

### 7.4 워커가 서버를 띄우는 방식

```
llama-server --model <gguf 경로> --host 127.0.0.1 --port <임시 포트> \
             --n-gpu-layers <자동 계산> --ctx-size 8192 --log-disable
```

* 포트는 OS에게 받은 임시 포트입니다. 인스턴스가 여러 개여도 충돌하지 않습니다.
* `--n-gpu-layers`는 여유 VRAM에서 계산합니다(`choose_gpu_layers`):
  10GB 이상 → 99(전부), 8GB → 48, 6GB → 32, 4GB → 20, 3GB → 12, **3GB 미만 → 0(CPU만)**.
  일부만 올려 스필하면 안 올린 것보다 느리기 때문입니다.
* `/health`가 200을 줄 때까지 최대 180초 기다립니다. 그 사이 프로세스가 죽으면 stderr 꼬리를
  담아 `TRANSLATION_FAILED`를 냅니다.
* 서버 프로세스는 취소 토큰과 `GLOBAL_PROCESSES`에 등록되고 Windows Job Object 안에 있으므로,
  작업이 끝나거나 앱이 죽으면 반드시 함께 종료됩니다.

---

## 8. 인터넷이 없는 PC에 수동 설치

모델 화면의 다운로드는 인터넷이 필요합니다. 폐쇄망 PC라면 **인터넷이 되는 다른 PC에서 파일을
받아 복사**한 뒤 검증하면 됩니다.

### 8.1 파일 받기

인터넷이 되는 PC에서, 원하는 모델의 저장소 페이지를 열고 §1의 "구성 파일" 목록에 있는 파일을
전부 받습니다. 브라우저로 받아도 되고 `huggingface-cli`를 써도 됩니다.

```bash
# 예: whisper-large-v3-turbo
huggingface-cli download deepdml/faster-whisper-large-v3-turbo-ct2 \
    config.json model.bin tokenizer.json vocabulary.txt \
    --local-dir ./whisper-large-v3-turbo

# 예: 번역 모델
huggingface-cli download entai2965/nllb-200-distilled-600B-ctranslate2 \
    config.json model.bin shared_vocabulary.json sentencepiece.bpe.model \
    tokenizer.json special_tokens_map.json tokenizer_config.json \
    --local-dir ./nllb-200-distilled-600M

# 예: GGUF (파일 하나)
huggingface-cli download Qwen/Qwen2.5-3B-Instruct-GGUF \
    qwen2.5-3b-instruct-q4_k_m.gguf \
    --local-dir ./qwen2.5-3b-instruct-q4km
```

직접 URL로 받아도 됩니다:
`https://huggingface.co/{저장소}/resolve/main/{파일명}`

### 8.2 폴더 이름 규칙 (중요)

폴더 이름은 **카탈로그의 모델 id와 정확히 같아야 합니다.** 저장소 이름이 아닙니다.

```
%LOCALAPPDATA%\KSubMaker\models\
    whisper-large-v3-turbo\
        config.json
        model.bin
        tokenizer.json
        vocabulary.txt
    nllb-200-distilled-600M\
        config.json
        model.bin
        shared_vocabulary.json
        sentencepiece.bpe.model
        tokenizer.json
        special_tokens_map.json
        tokenizer_config.json
    qwen2.5-3b-instruct-q4km\
        qwen2.5-3b-instruct-q4_k_m.gguf
```

### 8.3 매니페스트 만들기

파일만 복사하면 **`IsInstalledAsync`가 false를 돌려줍니다** — 매니페스트가 없기 때문입니다.
모델 화면에서 "설치되지 않음"으로 보이고, 검증 버튼도 "매니페스트가 없습니다"라고 합니다.

두 가지 방법이 있습니다.

**(a) 매니페스트를 손으로 만들기.** 아래 PowerShell을 모델 폴더에서 실행하면 형식에 맞는
`.ksubmaker-manifest.json`이 만들어집니다.

```powershell
$dir   = 'C:\Users\<사용자>\AppData\Local\KSubMaker\models\whisper-large-v3-turbo'
$model = 'whisper-large-v3-turbo'
$repo  = 'deepdml/faster-whisper-large-v3-turbo-ct2'

$files = Get-ChildItem -LiteralPath $dir -File |
    Where-Object { $_.Name -ne '.ksubmaker-manifest.json' -and $_.Extension -ne '.part' } |
    ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.Name
            SizeBytes    = $_.Length
            Sha256       = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

[pscustomobject]@{
    ModelId      = $model
    RepositoryId = $repo
    Files        = @($files)
    CreatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $dir '.ksubmaker-manifest.json') -Encoding UTF8
```

그다음 KSubMaker를 켜고 모델 화면에서 **검증**을 누르면 "정상"으로 표시됩니다.

**(b) 파일이 있는 상태에서 다운로드 버튼 누르기.** 인터넷이 조금이라도 되는 환경이라면
이쪽이 더 간단합니다. 다운로더는 이미 있는 파일의 크기와 해시를 확인하고, 맞으면 **다시
내려받지 않고** 매니페스트만 만듭니다. 저장소 트리 API 호출(수 KB)만 필요합니다.

### 8.4 확인

* 모델 화면에서 상태가 "설치됨"이고 검증이 "정상"인지 봅니다.
* 또는 설정 → 실행에서 **Fake AI 모드를 끄고** 짧은 영상 하나로 시험합니다.
* 로그(`%LOCALAPPDATA%\KSubMaker\logs\`)에 `WHISPER_MODEL_NOT_FOUND`가 없어야 합니다.

---

## 9. 디스크 용량 계획

전부 설치하면 약 **21 GiB**입니다. 실제로는 필요한 것만 받으면 됩니다.

| 사용 시나리오 | 받을 모델 | 대략 용량 |
| --- | --- | --- |
| 최소 (CPU, 시험용) | `whisper-small` + `nllb-200-distilled-600M` | 약 2.9 GiB |
| VRAM 8 GB 권장 조합 | `whisper-large-v3-turbo` + `nllb-200-distilled-600M` | 약 4.0 GiB |
| VRAM 12 GB 이상 권장 조합 | `whisper-large-v3` + `nllb-200-distilled-1.3B` | 약 8.3 GiB |
| 로컬 LLM 추가 (7B) | 위 + `qwen2.5-7b-instruct-q4km` + `llama-server` | 위 + 약 4.7 GiB |

다운로드 중에는 `.part` 파일이 최종 파일과 **동시에 존재하지 않으므로**(이동 방식) 추가 여유
공간은 최대 파일 하나 크기면 충분합니다. 다만 작업 캐시(`cache/{jobId}/audio.wav`)가 별도로
필요합니다 — 16kHz 모노 PCM은 시간당 약 110 MB입니다.
