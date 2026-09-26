# KSubMaker 작업 인계 문서

Claude Code가 이 저장소에서 작업을 이어갈 때 **가장 먼저 읽는 문서**입니다.
저장소에 이미 있는 내용은 반복하지 않고, 링크만 겁니다. 여기에 적은 것은
**코드를 읽어서는 알 수 없는 것들** — 실기에서 무엇이 깨졌고 왜 그렇게 고쳤는지,
지금 무엇이 실제로 검증됐고 무엇이 아닌지입니다.

| 먼저 볼 곳 | 내용 |
| --- | --- |
| [`AGENTS.md`](AGENTS.md) | 계층 규칙, C#/Python 코딩 규칙, 프로토콜 변경 절차, 커밋 금지 항목 |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 구조, 데이터 흐름, 처리 방식 A/B/C, 알려진 제한사항 |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | ADR 31건. **되돌리기 전에 반드시 확인** |
| [`docs/WORKER_PROTOCOL.md`](docs/WORKER_PROTOCOL.md) | JSON Lines 프로토콜 v1.5 전체 명세 |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | 오류 코드 24개별 증상 → 원인 → 해결 |

---

## 1. 지금 상태

폴더 안 영상 → faster-whisper 원문 인식 → 한국어 번역 → `*.ko.srt`.
WPF(.NET 10) UI와 Python AI Worker를 **별도 프로세스**로 분리하고 stdio JSON Lines로 통신합니다.

- C# 191파일 · Python 39파일 · XAML 8파일 · 커밋 64개 (2026-09-26 실측)
- 버전 **1.1.0** · 프로토콜 **v1.5** · EF Core 마이그레이션 3건
- 테스트 **C# 1,903(단위 1,718 + 통합 185) / Python 754**, Release 빌드 경고 0

> **테스트 수치 주의.** 위 "전부 통과"는 **Linux/CI 기준**입니다. 실기(Windows)에서 그대로
> 돌리면 **C# 6건 · Python 9건이 실패하고 통합 테스트 52건이 스킵**됩니다(2026-09-26 재확인). 전부 POSIX를 가정한
> 테스트가 Windows에서 깨지는 것이지 제품 결함이 아닙니다 — C#은 경로 구분자
> (`"/videos\movie.ko.srt"` vs `"\videos\..."`, `JobFactoryTests`·`FullPipelineTests`·
> `RetryAndConflictPolicyTests`), Python은 `/usr/bin:/bin`·`/tmp` 하드코딩
> (`test_ffmpeg_service`·`test_hardware_detector`·`test_main_loop`).
> **자기 변경을 판단하기 전에 이 기준선부터 잡으세요.** `git archive HEAD | tar -x`로 HEAD를
> 따로 풀어 돌려보면 됩니다. 새로 늘어난 실패만 내 책임입니다.

원격은 `https://github.com/henleyppomppu/KSubMaker.git`, 브랜치 `main`.

> 저장소를 옮기면서 **이력을 버리고 현재 상태로 다시 시작했습니다.** 그 전의 커밋 11건은 이
> 저장소에 없습니다 — 아래 §6의 버그 기록이 그 이력을 대신하는 유일한 자료이므로 지우지 마세요.

---

## 2. 검증 명령 — 작업 후 반드시 전부 통과시킬 것

```powershell
dotnet build KSubMaker.sln -c Release      # 경고 0 유지 — 이건 실기에서도 그대로
dotnet test  KSubMaker.sln -c Release      # Windows 기준선: 실패 6 · 스킵 52 (§1 주의 참고)
python -m pytest worker/tests              # Windows 기준선: 실패 9
```

Linux/CI에서도 `EnableWindowsTargeting=true` 덕분에 **WPF까지 컴파일 검증**됩니다(실행은 Windows 전용).
설치 없이 파이썬 테스트만 돌리려면 `PYTHONPATH=worker python3 -m pytest worker/tests -q`.

**실기에서 파이썬 테스트를 돌리려면** 임베디드 런타임에 pytest를 먼저 넣어야 합니다(런타임은
앱 구동용이라 기본으로는 없습니다). `tools/python/`은 `.gitignore`에 있으므로 커밋에 영향은
없습니다.

```powershell
& .\tools\python\python.exe -m pip install pytest
$env:PYTHONPATH="$PWD\worker"; & .\tools\python\python.exe -m pytest worker\tests -q
```

**"통과"를 "검증됨"으로 착각하지 마세요.** GPU·모델이 필요한 경로는 어떤 자동 테스트도 건드리지 않습니다.
그건 `scripts\smoke-gpu.ps1`이 담당하고, 사람이 직접 돌려야 합니다.

---

## 3. 실기 환경 (2026-08-02 기준)

| 항목 | 값 |
| --- | --- |
| PC | Windows, 한국어 로캘(**CP949**), **PowerShell 5.1** |
| GPU | RTX 3080 Ti 12GB, 드라이버 591.86, CUDA 13.1 |
| CPU / RAM | Ryzen 9 5950X (32스레드) / 128GB |
| 소스 | `D:\Workspace\KSubMaker` |
| 앱 설치 | `C:\Dev\KSubMaker` (`tools\python` 임베디드 런타임 구성 완료) |
| 설치된 모델 | `whisper-medium`, `nllb-200-distilled-1.3B`, `qwen2.5-3b-instruct-q4km` |

사용자 환경이 **CP949 + PowerShell 5.1**이라는 점이 아래 함정 대부분의 원인입니다.
`pwsh`(7)로 검증하면 전부 통과하지만 실기에서 깨집니다.

---

## 4. 반드시 지켜야 할 함정 — 전부 실기에서 한 번씩 깨진 것들

각 항목에 **가드 테스트**가 붙어 있습니다. 테스트를 지우거나 완화하지 마세요.

### 4.1 `.ps1`과 `.iss`는 UTF-8 **BOM**으로 저장
PowerShell 5.1은 BOM이 없으면 파일을 ANSI(CP949)로 읽습니다. CP949 선행 바이트(0x81–0xFE)가
다음 1바이트를 무조건 삼키는데 UTF-8 한글은 3바이트라 정렬이 밀리고, 결국 문자열 리터럴의
닫는 따옴표까지 먹어 **실행이 아니라 파싱에서** 실패합니다. Inno Setup 6도 동일.
`pwsh`는 BOM 없는 UTF-8을 잘 읽으므로 파싱 검사로는 절대 안 잡힙니다.
→ 가드: `tests/.../Packaging/ScriptEncodingTests.cs` (바이트 직접 검사, 신규 스크립트 자동 포함)

### 4.2 파이프라인 결과는 `@()`로 **전체**를 감쌀 것
```powershell
$x = @($obj.items) | Where-Object { $_ }   # 틀림 — 비면 $null, $null.Count 는 오류
$x = @(@($obj.items) | Where-Object { $_ }) # 맞음
```
`@()`가 속성에만 걸리면 뒤따르는 파이프라인이 다시 벗겨냅니다. pwsh 7은 스칼라와 `$null`에도
합성 `.Count`를 주므로 여기서도 안 잡힙니다.
→ 가드: `tests/.../Packaging/PowerShellArrayUnwrapTests.cs`

### 4.3 Windows GPU에는 cuBLAS 12 + cuDNN 9 DLL이 **따로** 필요
`ctranslate2>=4.5`가 `cublas64_12.dll`·`cudnn64_9.dll`에 링크되는데 pip 휠에도 NVIDIA
**드라이버**에도 없습니다(툴킷 라이브러리라서). 드라이버가 CUDA 13.1을 보고해도 없을 수 있습니다.
게다가 pip이 넣는 `site-packages/nvidia/*/bin`은 DLL 검색 경로가 아니고, CPython 3.8+가
`SetDefaultDllDirectories`를 호출해 **PATH 추가도 무효**입니다.
→ `build-worker.ps1`이 `nvidia-cublas-cu12>=12.9,<13` / `nvidia-cudnn-cu12>=9.24,<10`을 설치(약 1.8GB),
`worker/ksubmaker_worker/cuda_setup.py`가 ctranslate2 import **전에** `os.add_dll_directory()`로 등록.
**버전 상한을 제거하지 마세요** — cuDNN 10은 깨끗이 설치된 뒤 로드에서만 실패합니다.

**디렉터리 등록만으로는 부족합니다 — DLL을 실제로 로드해야 합니다 (2026-08-07 실측, 수정됨).**
`os.add_dll_directory()`로 등록해도 CTranslate2는 `translate_batch`에서
`Library cublas64_12.dll is not found`로 죽습니다. 파일도 있고 등록도 성공했는데 해석이 안 됩니다.
DLL을 **절대 경로로 프로세스에 올려야** 그다음 CTranslate2의 이름 해석이 성공합니다.
같은 스크립트에서 그 한 단계 차이로 실패/성공이 갈리는 것을 반복 확인했습니다.

수정 전까지 GPU 경로는 **우연히** 동작하고 있었습니다 — `hardware_detector._detect_cuda()`가
기동 시 `probe_support_libraries()`를 부르고 그 부작용으로 DLL이 올라갔기 때문입니다. 그 사실은
어디에도 적혀 있지 않았고, 그 probe는 `if device_detected:` 안에 있습니다.

→ 이제 `ensure_registered()`가 등록 후 `preload_support_libraries()`로 로드까지 합니다.
**워커 밖에서 `NllbTranslator`/`Transcriber`를 직접 쓰는 스크립트는 `ensure_registered()`를
반드시 부르세요** — `register_cuda_dll_directories()`만 부르면 로드가 빠집니다.
→ 가드: `test_cuda_setup.py::test_ensure_registered_loads_the_dlls_and_not_only_the_directories`
(로드 순서까지 고정 — cuBLASLt가 그것에 의존하는 cuBLAS보다 먼저 올라가야 합니다).

### 4.4 외부 저장소의 파일 목록을 하드코딩하지 말 것
모델 카탈로그에 HF 파일명을 박아뒀다가 3건이 틀려 404가 났습니다(large-v3는 `vocabulary.json`,
NLLB 600M 저장소 id 오타, Qwen 7B q4_k_m은 2조각 분할).
→ hub tree API로 발견하고 `IncludePattern`으로 선택. 정적 목록은 **오프라인 폴백 전용**이며
`tests/fixtures/huggingface/`의 픽스처와 온라인 테스트가 실제 저장소와의 일치를 고정합니다.

### 4.5 하드웨어 감지는 "쓸 수 있는가"를 물어야 함
`ctranslate2.get_cuda_device_count()`는 드라이버만 있으면 통과합니다. 그것만 보고 `CUDA=true`라고
보고했다가 모델 로드에서 죽었습니다. 지금은 지원 라이브러리를 **실제로 로드해 보고** 둘 다
성공해야 `cudaAvailable=true`입니다.

### 4.6 그 외
- **C#/Python 패리티**: 오류 코드(`ErrorCodeParityTests`)와 번역 대상 판정(`TranslatableTextParityTests`)은
  두 언어 구현이 갈라지지 않도록 픽스처로 묶여 있습니다. 한쪽만 고치면 테스트가 잡습니다.
- **리소스 문자열**: `Strings.resx`와 손으로 쓴 `Strings.Designer.cs`를 **둘 다** 고쳐야 합니다
  (`StringResourceParityTests`).
- **불변 규칙**: 번역이 타임코드를 절대 바꾸지 않습니다. 타임코드는 항상 ASR에서만 나옵니다.

---

## 5. 실기에서 검증된 것 / 안 된 것

**검증됨** — 앱 실행, DB 마이그레이션, 폴더 스캔, GPU·VRAM 감지, `os.add_dll_directory()`로
CTranslate2가 cuBLAS/cuDNN을 실제 로드(`지원 라이브러리: True`), 임베디드 파이썬 워커 기동과
프로토콜 핸드셰이크, 모델 다운로드·검증(medium / nllb-1.3B / qwen-3B), 일본어 음성 인식과
언어 감지(ja 97~99%), 체크포인트 재개.

**2026-08-02 추가로 검증됨:**
- **Qwen 7B 분할 GGUF 다운로드**(`00001-of-00002` + `00002-of-00002`). §4.4의 hub tree API
  발견 + `IncludePattern` 방식이 분할 파일에서 실제로 동작한다는 뜻입니다.
- **로컬 LLM 엔진 전 경로.** `llama-server`가 배포되어 있고 기동·번역까지 됩니다. 예전에
  "미배포"로 적혀 있던 항목입니다.
- **NLLB 대비 LLM 번역 속도.** Qwen 7B가 NLLB 1.3B보다 **체감상 매우 느립니다.** 구조적으로
  당연합니다 — NLLB는 CTranslate2 인코더-디코더가 배치를 병렬로 굴리는 반면, LLM은 자기회귀
  디코딩으로 토큰을 하나씩 만들고, 자막 본문뿐 아니라 `[{"id":1,"translation":…}]` JSON 뼈대와
  매 배치의 시스템 프롬프트까지 전부 생성·처리합니다. 파라미터도 5.4배입니다. 자막 줄당 5~20배는
  정상 범위로 보세요. 그보다 더 느리면 VRAM 부족으로 CPU 폴백된 것을 의심하고
  (`choose_gpu_layers`는 여유 3GB 미만이면 오프로드를 0으로 떨어뜨립니다) 로그의
  `starting llama-server on port … with N GPU layers`에서 N이 99인지 확인하세요.
- **WPF 화면 일부** — 상태 열이 `취소됨`으로 갱신되고, 적용 불가 동작의 버튼이 회색이 되고,
  알림 대화 상자가 뜨는 것까지 스크린샷으로 확인했습니다.

**2026-08-07 추가로 검증됨:**
- **전 구간 성공.** 폴더 스캔부터 `*.ko.srt` 생성까지 한 건이 끝까지 돌았습니다. §7의 최우선
  항목이었습니다. **단, 이것은 §6.10 수정이 반영되기 *전*의 빌드입니다** — 아래 미검증 항목을
  보세요. 파이프라인이 완주한다는 것과 번역이 맞다는 것은 별개입니다.

**미검증** — 아래는 코드와 로그로부터 추론했을 뿐 실제로 본 적이 없습니다. 사실로 단정하지 마세요.
**2026-08-07 추가로 검증됨 (2):**
- **일어 잔류의 원인이 §6.10으로 확정됐습니다.** 완료 산출물 4편(자막 본문 2,179줄)에서
  383줄(**17.6%**)이 일본어로 남아 있었고, 캐시 대조 결과 그중 341줄이 `번역 == 원문`
  (=`_degrade_or_reject` 폴백)이었습니다. 폴백된 줄은 `当` `空` `目` `そ` `ね` 같은 **단발 문자**와
  `あー` `ねえってば` 같은 **감탄사**가 압도적입니다. 전부 `has_translatable_content`를
  통과하므로(글자가 있으니) 엔진에 갔고, 빈 응답이 왔고, 결정론적이라 3회 재시도가 같은 결과를
  냈고, 원문 유지로 떨어졌습니다. §6.10 수정으로 이 폴백이 59→3으로 줄어드는 것을 확인했습니다.
- **GPU 번역 경로 정상.** `device="cuda"`/`float16`으로 4편 2,606세그먼트를 **25초**에
  번역했습니다(CPU/int8 326초의 13배). 여기까지 오는 과정에서 §4.3의 결함(등록만 하고 로드는
  안 하던 것)을 찾아 고쳤고, 이제 `ensure_registered()` 한 번으로 GPU 번역이 됩니다.
  **한때 "멈춤"으로 보였던 것도 같은 뿌리**입니다.

**2026-08-09 추가로 검증됨 — PR #2(`initial_prompt` + VAD 패딩)의 실제 효과:**

주장은 "동일 단어 중복 반복(할루시네이션) 및 어휘 오인식률을 **대폭** 낮춥니다"였고 근거는
없었습니다. 2시간 07분 일본어 영상 하나로 재봤습니다 — whisper-large-v3 / cuda / float16 /
beam 5 / VAD on / `conditionOnPreviousText=false`, 변수 하나만 바꾸고 나머지는 전부 고정,
동일 설정 반복 실행으로 **노이즈 바닥을 먼저 측정**했습니다(같은 GPU에서 같은 설정으로 두 번
돌려도 271줄 중 **4~7줄이 달라집니다** — cuBLAS 비결정성).

| | 271줄 중 다른 줄 | 판정 |
| --- | ---: | --- |
| 동일 설정 반복 (노이즈 바닥) | 4 ~ 7 | — |
| `initial_prompt` 있음/없음 | **20** | 유의미 |
| `speech_pad_ms=400` 있음/없음 | **1** | 노이즈 이하 = 효과 없음 |

- **`vad_parameters={"speech_pad_ms": 400}`은 faster-whisper의 기본값**입니다
  (`VadOptions.speech_pad_ms = 400`). 넣으나 빼나 같습니다. 기본값이 나중에 바뀔 때를 대비해
  값을 고정해 두는 의미는 있으므로 남겼습니다.
- **`initial_prompt`은 첫 디코딩 윈도우에만 닿습니다.** faster-whisper는 `all_tokens`를
  프롬프트로 채운 뒤 매 윈도우 끝에서
  `if not condition_on_previous_text ...: prompt_reset_since = len(all_tokens)`를 실행하는데,
  이 저장소는 ADR-010에 따라 그것을 꺼 둡니다. 실제로 달라진 20줄 중 **15줄이 53초~138초
  구간에 몰려 있고**, 나머지 5줄의 시각(4046·4128·4129·4207초)은 **노이즈 실행에서 달라진
  줄과 정확히 겹칩니다.**
- 그 15줄의 변화는 전부 **문장부호**입니다 — 프롬프트가 `。`로 끝나므로 받아쓰기에도 `。`·`、`가
  붙습니다. `マジでやればいいじゃん` → `マジでやればいいじゃん。` 류.
- **할루시네이션 지표는 세 변형이 전부 동일**했습니다. 연속 반복 2건, 최장 연속 반복 2회,
  5회 이상 반복된 줄 1개, 말미 폭주 0. 이 표본에는 애초에 줄일 할루시네이션이 없었습니다.
- **교훈: 노이즈 바닥을 먼저 재세요.** 바닥을 안 재고 "20줄이 달라졌다"만 봤으면 패딩의
  1줄 차이도 효과로 읽었을 겁니다. §6.10에서 합성 표본으로 오판했던 것과 같은 종류의 실수입니다.
- 이 한계는 §6.13에서 추가한 사용자 인식 힌트에도 그대로 적용됩니다. UI 설명·프로토콜 문서·
  `transcriber.py` 주석에 "도입부에만 적용된다"고 적어 뒀습니다.

**미검증**
- **자막 품질을 사람이 읽고 평가한 적은 없습니다.** 위 수치는 "일본어가 남았는가"를 기계적으로
  센 것이지 번역이 좋은지를 판단한 것이 아닙니다. 실제로 §6.10 수정본이 더 나쁜 줄도 있습니다
  (`お兄ちゃん！` "형님!" → "내 동생!").
- **§6.11 시작 전 모델 확인·다운로드가 실기 미검증입니다.** 실기에 이미 모델이 깔려 있어
  대화상자가 뜨는 조건이 만들어지지 않습니다. 설정에서 미설치 모델(`whisper-large-v3`)을 고르고
  시작을 누르면 재현됩니다.
- **NVIDIA 이외의 GPU는 전부 CPU로 떨어집니다** — 아래 §5.1.
- **번역 품질 비교(NLLB 1.3B vs Qwen 7B).** 둘 다 돌아가는 것은 확인됐지만 결과물을 나란히
  놓고 본 적이 없습니다. 코드 기준으로 LLM 쪽이 유리한 지점은 분명합니다 — 문맥 3줄 전달,
  문체를 프롬프트로 실제 지시(NLLB는 문장 **끝 어미만** 사후 치환하는 근사치), 용어집을
  활용까지 반영. 반대로 LLM은 JSON 형식 위반·환각 위험이 있어 `parse_translation_json`에
  복구 코드가 붙어 있습니다. **어느 쪽이 나은지는 실측 전까지 단정하지 마세요.**
- NLLB가 실제로 `♪`·`。` 같은 줄에 빈 문자열을 반환하는지 (§6.4 수정의 전제)
- `cudnn64_9.dll` 하나만 로드 확인하면 하위 라이브러리도 따라오는지
- WPF 화면 나머지 — 진행 중 단계별 상태 열 전이, 선택 항목 제거가 캐시까지 지우는지
- `whisper-large-v3` 다운로드(카탈로그 수정 후 재시도 안 해봄)
- 처리 방식 B/C
- **설치 프로그램은 이제 컴파일까지만 검증됐습니다** (2026-09-05, ISCC 6.7.3, 197초,
  1.53 GiB). 실제 설치·업그레이드·제거를 돌려 본 적은 없습니다 — GPU 경고, VC++ 재배포
  패키지 설치, 제거 시 `%LOCALAPPDATA%\KSubMaker` 유지 여부가 전부 미검증입니다.

### 5.1 NVIDIA가 아닌 GPU (AMD·Intel Arc)

**설계상 GPU 가속 경로가 없습니다. 전부 CPU로 동작합니다** — 실패하지는 않지만 느립니다.
코드를 읽으면 그렇게 되어 있고, 실기로 확인한 적은 없습니다(장비가 없음).

- `hardware_detector._detect_gpus()`는 **`nvidia-smi`만** 찾습니다. 없으면 `gpus = []`이고
  "NVIDIA GPU를 찾지 못했습니다. CPU로 실행하면 처리 속도가 매우 느립니다" 경고가 붙습니다.
  AMD GPU는 목록에 아예 안 나옵니다 — 감지 실패가 아니라 **묻지도 않습니다.**
- `cudaAvailable=false` → `HardwareRecommendationPolicy.CpuFallback`이 RAM 16GB 이상이면
  whisper-medium, 미만이면 small + NLLB 600M + `Int8` + 방식 B를 권장하고, 근거 문구는
  "NVIDIA GPU가 감지되지 않았습니다. CPU 모드로 동작하며 영상 길이 대비 5~15배"입니다.
- 워커의 `_resolve_device("auto")`는 `ctranslate2.get_cuda_device_count()`를 보는데 CTranslate2
  공식 휠은 **CUDA 전용**(ROCm 빌드 없음)이라 0을 돌려줍니다 → `cpu` + `int8`.
  `BuildWorkerSettings`가 `Device = "auto"`를 하드코딩하므로 사용자가 GPU를 강제할 방법도
  없습니다. 즉 안전하게 CPU로 갑니다.
- 번들된 llama.cpp는 CUDA 빌드(`ggml-cuda.dll`)지만 CPU 백엔드 DLL이 마이크로아키텍처별로 전부
  들어 있고(`ggml-cpu-zen4.dll`·`ggml-cpu-piledriver.dll` 등 AMD **CPU**용 포함) ggml은 백엔드를
  런타임에 동적으로 등록합니다. CUDA 백엔드 로드가 실패하면 건너뛰고 CPU로 도는 것이 설계입니다.
  다만 `choose_gpu_layers`가 `largest_free_vram_bytes()=0`을 받아 오프로드 0층이 되므로 **로컬
  LLM 번역은 전부 CPU**입니다. Qwen 7B는 이 조건에서 실용적이지 않습니다.

**한 가지 실제 함정.** 설정에 `computeType`이 `float16`으로 **명시 저장돼 있으면** CPU에서
CTranslate2가 즉시 하드 실패합니다(`WHISPER_MODEL_LOAD_FAILED`). CUDA OOM 사다리는
`is_cuda_oom`으로만 트리거되므로 이건 복구되지 않습니다. `AppSettings.ComputeType` 기본값이
null(=워커가 `int8`로 결정)이라 평소에는 안 걸리지만, NVIDIA 기계에서 저장한 설정을 그대로
들고 오면 밟습니다.

ROCm/DirectML/Vulkan을 지원하려면 CTranslate2를 대체해야 하므로 §4.3급이 아니라 아키텍처 변경입니다.

---

## 6. 이 세션에서 고친 버그 — 재발 방지 맥락

같은 실수를 반복하지 않도록 남깁니다. 상세는 `git log`.

1. **체크포인트 무효화 널 리프팅** — `checkpoint?.CompletedStage < X`가 널일 때 false가 되어,
   영상을 교체해도 **이전 영상의 자막을 재사용**했습니다. 널 병합 비교는 이 코드베이스에서 금지.
2. **`Progress<T>` 경쟁 상태** — 콜백이 스레드풀로 넘어가 순서가 뒤바뀌고 완료 이후에도 도착해,
   완료된 작업이 "음성 인식 중 65%"로 남았습니다. 큐는 `InlineProgress<T>` + 게이트를 씁니다.
3. **`'Probing' → 'Pending'` 예외** — `ReportProgress`가 `Status`를 갱신하지 않아 작업이 실행 내내
   `Probing`에 머물렀고, 자동 재시도가 던진 예외가 `UNKNOWN` 실패로 둔갑했습니다. 즉
   **복구 가능한 오류의 자동 재시도가 전혀 동작하지 않았습니다.** 상태 표를 규칙 기반으로
   재작성(활성 단계 전진 허용, 역방향 금지, `Completed`는 `WritingSubtitle`에서만 도달).
4. **번역 한 줄로 작업 전체 폐기** — NLLB가 기호·구두점 줄에 빈 문자열을 결정론적으로 반환하는데
   같은 입력을 3번 재시도한 뒤 134초짜리 작업을 실패시켰습니다. 글자·숫자 없는 줄은 번역기에
   보내지 않고, 남은 빈 번역은 원문 유지 후 진행. 하드 실패는 id 오염이나 대부분이 빈 응답일 때로 제한.
5. **선택 UX** — "선택 없음"과 "선택했지만 적용 불가"를 같은 메시지로 뭉개, 실패한 작업을 선택하고
   취소를 누르면 "먼저 선택하세요"가 떴습니다.

이후 세션(2026-08-02):

6. **설정을 바꿔도 옛 번역이 남음** — 재시도는 캐시를 건드리지 않고 워커는 `resume=true` 고정이라,
   무효화가 **원본 파일 변경**에만 걸려 있었습니다. NLLB로 80% 번역된 작업의 엔진을 LLM으로 바꿔
   재시도하면 앞 80%는 NLLB, 뒤 20%만 LLM인 파일이 **아무 표시 없이** 나왔습니다. `job.json`에
   산출물별 설정 지문(`audioSettings` / `transcriptionSettings` / `translationSettings`)을 기록해
   **바뀐 것 아래로만** 버립니다. 상세는 `docs/ARCHITECTURE.md §8.1`.
   - 성능 손잡이는 지문에서 **제외**. 특히 `computeType`은 CUDA OOM 사다리가 실행 중에 바꾸므로,
     넣었다면 다운그레이드 후 모든 이어하기가 "설정 바뀜"이 되어 방금 끝낸 ASR을 다시 돌았습니다.
   - **버릴 것을 지운 직후 지문을 새로 씁니다**(`refresh_settings`). 작업 완료 시점으로 미뤘더니
     새 설정으로 돌리다 실패한 다음 재시도가 **또** 폐기 판정을 내려 매번 0에서 시작했습니다.
   - 곁들여 파이썬 워커에 **음성추출 재사용이 아예 없던 것**도 고쳤습니다(C#에는 있었음).
   - **C# `InProcessJobProcessor`에는 아직 없습니다.** 양쪽 다 안전하게 퇴화하므로 깨지지는
     않지만(모르는 필드 무시 / 없는 지문은 "일치"), Fake AI 모드는 설정을 바꿔도 옛 번역이 남습니다.
7. **취소된 작업 + 시작 = "선택할 게 없다"** — 147건을 취소하고 앱을 재실행한 뒤 한 건을 체크하고
   시작을 누르니 "시작할 수 있는 작업이 없습니다"가 떴습니다. 동작은 설계대로(취소는 종료 상태,
   되살리는 건 재시도의 일)였지만, **`StartAsync`만 `JobSelectionResolver`를 거치지 않아** 5번에서
   없앤 결함이 그대로 남아 있었습니다. `JobAction.Start` 추가, `ResolveStart`는 강조 행 폴백 없음
   (아무것도 체크 안 한 시작은 "큐를 돌려라"라는 뜻이므로).
8. **종료 경합으로 창 닫기 예외** — `ShutdownMode=OnMainWindowClose`에서 애플리케이션 종료 경로가
   `ignoreCancel=true`로 창을 닫아, 비동기 정리가 끝난 뒤의 `Close()`가 던졌습니다. 종료할 때마다
   "예상치 못한 오류" 창이 뜨던 원인.

기능 추가 1건(같은 세션):

9. **음성 미리 추출 레인** (프로토콜 v1.3, `extractAudio`). 다음 파일의 음성을 미리 뽑아 GPU가
   쉬지 않게 합니다. 처리 방식 A/B/C와 **직교**합니다 — 방식 C가 인식·번역을 겹쳐 VRAM 16GB를
   요구하는 것과 달리, 추출은 ffmpeg라 VRAM을 안 쓰므로 3080 Ti 12GB에서도 CPU 전용에서도
   동작합니다. 상세는 `docs/ARCHITECTURE.md §6.1`.
   - 인계 장치가 없는 것이 설계입니다. 워커가 작업이 스스로 썼을 `audio.wav`와 체크포인트를
     그대로 남기므로, 뒤 작업은 추출이 끝나 있는 것을 발견해 건너뜁니다. 실패해도 손해는
     아꼈을 시간뿐이라 모든 오류가 `recoverable`입니다.
   - **깊이를 제한한 이유**는 디스크입니다. 전체 시간은 추출이 소비자를 앞서기만 하면 수렴하므로
     깊이 1과 무제한의 **처리량이 같습니다.** 반면 2시간짜리 147개를 전부 미리 뽑으면 약 34GB가
     쌓입니다. 기본 1, 상한 32, 0이면 끔.
   - **직접 겪은 함정 둘.** ① 펌프의 `finally`에서 이미 dispose된 `CancellationTokenSource`를
     `Cancel()`해 예외가 나면서 `RaiseState(Idle)`을 건너뛰어, **큐 테스트 8건이 한꺼번에
     10초 타임아웃**했습니다. CTS 소유권을 펌프로 올려 해결. ② 레인이 `PendingSnapshot()`을
     쓰면 펌프가 선두를 집는 순간 그게 목록에서 빠져 인덱스 0이 밀리고, 레인이 파일 하나를
     조용히 건너뜁니다. 테스트가 3번 중 2번 실패해서 잡았습니다 — `UnfinishedInQueueOrder()`로 교체.
   - 미리 추출과 그 작업이 같은 wav에 동시에 ffmpeg를 걸 수 있어 **체크포인트 디렉터리별 잠금**을
     둡니다. 가드 테스트(`test_a_prefetch_and_its_job_never_demux_the_same_file_at_once`)는 잠금을
     빼면 실제로 `max_concurrent == 2`로 실패하는 것을 확인했습니다.
   - **실기 미검증.** 자동 테스트만 통과한 상태입니다.

이후 세션(2026-08-07):

10. **NLLB에 소스 언어가 전달되지 않아 일본어를 영어로 번역** — 사용자가 "번역 품질이 떨어지고
    일어 그대로 폴백되는 비율이 높다"고 알려와 찾은 것입니다. `translate_items`가
    `to_nllb_code("ja")` → `jpn_Jpan`을 **계산해 놓고 모델에 전달하는 경로가 없었습니다.**
    NLLB는 소스 언어를 시퀀스 첫 토큰으로 읽고 그 토큰은 토크나이저의 `src_lang`이 만드는데,
    `src_lang`을 대입하는 코드가 저장소에 없었습니다. 우리가 쓰는 CT2 변환본은
    `tokenizer_config.json`에 `src_lang`이 **null**이라 `NllbTokenizer`의 자체 기본값
    `eng_Latn`이 그대로 쓰였습니다.
    - `source_code`를 쓰던 유일한 자리인 `translate_batch(source_lang=...)`는 CTranslate2
      NMT Translator에 **없는 파라미터**라 `TypeError`로 빠지는 죽은 코드였습니다.
      "토크나이저가 이미 언어 토큰을 냈으니 빼도 안전하다"는 주석이 틀린 전제였습니다.
    - **실측으로 확인됨 (2026-08-07).** 실제 산출물의 전사(387세그먼트, 측정 표본 A)를 `src_lang`만
      바꿔 두 번 번역했습니다. 디바이스·컴퓨트·모델·배치 설정 전부 동일(CPU/int8)이라 차이의
      원인은 소스 언어 토큰 하나뿐입니다.

      | | 수정 전(`eng_Latn`) | 수정 후(`jpn_Jpan`) |
      | --- | ---: | ---: |
      | 일본어로 남은 줄 | 61 (**15.8%**) | 3 (**0.8%**) |
      | 원문 그대로(폴백) | 59 | 3 |
      | 정상 한국어 | 305 | 384 |

      재현 조건의 15.8%가 실제 원본 파일의 18.2%와 거의 일치해 재현이 충실함도 확인됩니다.
      단발 문자 `部`는 수정 전 `部` 그대로 → 수정 후 "부장". 잔류만이 아니라 **의미 오류도**
      고쳐집니다 — 직장 내 성희롱을 언급하는 대사가 수정 전에는 뜻이 정반대인 문장으로
      번역됐습니다. (원문 대사는 표본 출처를 특정할 수 있어 옮기지 않습니다.)
    - **⚠️ 검증 방법에 대한 교훈.** 처음에 깨끗한 표준 문장 21줄로 테스트해 "가설이 반증됐다"고
      결론 내렸다가 틀렸습니다. 그 표본에서는 수정 전에도 전부 정상 번역됐기 때문입니다.
      **NLLB는 흔한 완성 문장에서는 소스 언어가 틀려도 버팁니다. 무너지는 것은 한 글자짜리
      가나·한자, 감탄사, 파편적 대사** — 즉 자막의 상당 부분입니다. 합성 표본으로 판단하지
      말고 **실제 전사(`transcription.json`)로 재현하세요.** 백업이 있으면 그게 제일 빠릅니다.
    - 매 호출마다 설정합니다. 엔진 하나가 큐 전체를 처리하므로 언어가 섞인 폴더에서 첫 파일의
      언어를 물려받으면 안 됩니다.
    - **캐시 함정.** 코드 수정이라 `translationSettings` 지문이 안 바뀝니다. 끝난 작업을 그냥
      재시도하면 옛 번역을 그대로 재사용해 수정 전후가 구분되지 않습니다. 번역 설정을 하나
      바꾸거나 체크포인트를 지우세요.
    - `FakeTokenizer`가 실제 동작(`src_lang`을 첫 토큰으로, `eng_Latn`에서 시작)을 재현하도록
      바꾸고 가드를 넣었습니다. 그 전까지 이 버그는 **테스트를 전부 통과하고 있었습니다.**

11. **모델이 없으면 시작 전에 묻고 권장 모델을 내려받기** (기능 추가). 첫 실행에서 모델이 없으면
    경고 없이 큐가 돌기 시작해 파일마다 음성 추출을 끝낸 뒤 실패했습니다. 가드가 전부 비어
    있었습니다 — `ModelSelectionValidator.FindMissing`은 설정 **저장 시에만** 돌고 `"auto"`를
    면제하며, **`IModelManager.ResolveModelIdAsync`는 호출부가 0건**이었습니다(이 상황을 위한
    방어 로직 전체가 그 안에 있는데 아무도 부르지 않음. 심지어 "auto는 런타임에 그게 해석한다"고
    적은 테스트 주석까지 있었습니다). 그래서 `"auto"`는 워커의 하드코딩 `whisper-small`로
    떨어졌습니다 — 3080 Ti에 large-v3를 권장해 놓고 실제로는 small이 갔다는 뜻입니다.
    - 판단은 `ModelSelectionValidator.Resolve`(Domain, 순수)에 두고 UI는 표시만 합니다.
      App은 `net10.0-windows`라 Linux 테스트에서 못 건드리기 때문입니다.
    - **해석 결과가 실행까지 전달되는 것이 핵심입니다.** `EnsureModelsAsync`가 bool이 아니라
      설정 스냅샷을 돌려줍니다 — 권장 large-v3를 받아 놓고 워커에 `"auto"`를 보내면
      `whisper-small`을 찾다 죽습니다. 원본이 아니라 `Clone()`에 씁니다(실행용 스냅샷이지
      설정 변경이 아님).
    - 하드웨어 감지나 모델 상태 조회가 실패하면 **확인 없이 예전대로 진행**합니다. 워커가
      여전히 (늦게) 보고하므로, 조회 실패로 큐를 막는 쪽이 더 큰 퇴행입니다.
    - `Progress<T>` 콜백이 완료 후 도착해 상태 메시지를 되돌리는 §6.2형 경합은 게이트로,
      취소 CTS는 §6.9①의 함정대로 소유권을 명확히 해서 막았습니다.

이후 세션(2026-08-09):

12. **진행률용 길이가 `-t`를 겸해 오디오를 조용히 잘라내고 있었음** — `extract_audio`의
    `duration_seconds` 하나가 두 가지 일을 했습니다. ffmpeg의 `time=`을 퍼센트로 바꾸는
    **분모**이면서, 동시에 `-t`로 나가는 **절삭 길이**였습니다. 호출부가
    `테스트 길이 > 0 ? 테스트 길이 : 컨테이너 길이`를 넘기므로 **평범한 실행도 전부
    `-t <컨테이너가 주장하는 길이>`로 돌고 있었습니다.**
    - 컨테이너의 길이가 정확한 동안에는 무해합니다. 짧게 보고하는 순간(헤더 추정치가 어긋난
      VBR, 인덱스를 다시 쓰지 않고 복사한 스트림) wav가 잘리고, **증상은 "자막이 영화보다
      먼저 끝난다" 하나뿐**이라 여기까지 거슬러 올라오기가 사실상 불가능합니다.
    - `trim_seconds`를 별도 파라미터로 뽑았습니다. 진행률은 둘 중 짧은 쪽을 분모로 쓰므로
      절삭 실행도 100%에서 끝납니다.
    - **실기에서 이 결함을 밟은 적은 없습니다.** MP4/MKV는 길이를 정확히 보고하므로 지금 이
      저장소가 다루는 파일에서는 드러나지 않습니다. 코드 리뷰로 찾은 것입니다.
    → 가드: `test_the_progress_length_alone_never_trims_the_output`,
    `test_an_ordinary_run_asks_ffmpeg_for_the_whole_track[process/prefetch]`
13. **`initialPrompt` — 워커만 알고 호스트는 모르던 필드** (프로토콜 1.4). 워커가 이 설정을
    읽고 **전사 지문에까지 기록**해 왔는데 `WorkerJobSettings`에 해당 필드가 없어 아무도
    보내지 않았습니다. 주석만 "호스트/UI에서 전송한 custom initialPrompt 수신 지원"이라고
    적혀 있었습니다.
    - **지문에서 키를 빼는 쪽을 고르지 마세요.** 지문 비교가 dict 상등이라 키를 빼면 기존
      캐시가 전부 불일치가 되어 이미 끝난 ASR을 다시 돌립니다. 반대로 호스트를 채웠습니다 —
      값이 없으면 `null`이고 그건 1.3 호스트가 남긴 체크포인트와 그대로 일치합니다.
    - 같은 이유로 **지문에 키를 새로 넣는 것은 항상 전체 캐시 무효화**입니다. 실제로
      `initialPrompt`를 지문에 넣은 커밋(`25590ff`)이 기존 완료 작업 7건의 전사를 전부
      버리게 만들었습니다. 그 경우는 프롬프트가 실제로 결과를 바꾸므로 **재실행이 옳지만**,
      지문에 무언가를 넣을 때는 그 비용을 의도한 것인지 확인하세요.
    - `ksubmaker_worker/__init__.py`의 `PROTOCOL_VERSION`은 protocol.py를 베낀 두 번째
      사본이었고 **세 번의 개정 동안 `"1.0"`에 머물러** 있었습니다. 재수출로 바꿨습니다.

14. **설치 프로그램이 한 번도 컴파일된 적이 없었음** — `build-installer.ps1` 이 항상
    `Invalid section tag` 로 죽었습니다. 원인은 `.iss` 의 한 줄이 `[` 로 시작한 것입니다 —
    줄바꿈된 Pascal 배열 리터럴 `['vc_redist.x64.exe -> ' + ...]` 인데, **Inno Setup 은 앞에
    공백이 몇 칸이든 `[` 로 시작하는 줄을 섹션 태그로 읽습니다.** 여기서는 22칸이 들어가
    있었는데도 잡혔습니다.
    - 이 저장소의 어떤 자동 테스트도 `.iss` 를 **컴파일해 보지 않습니다.** BOM 검사
      (`ScriptEncodingTests`)는 인코딩만 봅니다. §4 의 패턴 그대로 — 테스트는 전부 통과하는데
      실제로 돌리면 깨지는 종류입니다.
    - 곁들여 알게 된 것: lzma2/max 로 압축한 설치 프로그램은 **1.53 GiB** 로, 포터블
      zip(**2.00 GiB**)과 달리 GitHub 릴리스 자산 상한 **2 GiB** 안에 들어옵니다. 릴리스를
      자동화한다면 설치 프로그램은 그대로 올라가고 zip 은 분할이 필요합니다.
15. **스크립트로 코드를 써 넣을 때 백슬래시가 반복해서 깨졌음** — PR #19 에서 테스트를
    파이썬 스크립트로 생성했는데 문자열의 `\v` 가 **수직 탭(0x0B)** 으로 해석돼
    `@"D:\videos\movie.srt"` 가 `@"D:<VT>ideos\movie.srt"` 가 됐습니다.
    - **테스트는 통과했습니다.** 후보 경로와 출력 경로가 똑같이 깨져 비교가 여전히 맞아
      떨어졌고, `Path.GetFullPath` 도 수직 탭을 파일명 문자로 받아들여 예외를 내지 않았습니다.
      통과하는 테스트가 엉뚱한 입력을 검증하고 있던 것입니다.
    - 같은 세션에서 `changes.txt` 의 UNC 예시(`\서버\공유`)도 같은 이유로 두 번 깨졌습니다.
    - **셸 → 파이썬 → 파일을 거쳐 경로 리터럴을 써 넣지 마세요.** 꼭 해야 하면 heredoc
      (`<<'PY'`) + raw 문자열을 쓰고, 쓴 뒤에 `repr()` 로 실제 바이트를 확인하세요.

**패턴**: 실패한 것들은 전부 *자동 테스트가 통과하는데 실기에서 깨지는* 종류였습니다.
플랫폼 차이(PS 5.1 vs pwsh 7), 외부 서비스의 실제 응답, GPU 유무. 새 기능을 넣을 때
"이게 CI에서 통과하는데 사용자 PC에서 깨질 수 있는 이유가 있나?"를 먼저 물어보세요.

---

## 7. 다음 작업 우선순위

1. **재생성된 자막을 사람이 읽고 품질을 판단하기.** 일어 잔류는 §6.10으로 해결됐지만
   (17.6% → 0.8%대), 남은 한국어가 **읽을 만한지는 아무도 안 봤습니다.** 수정 전/후 파일이
   `artifacts\quality-ab\{before,after}\`에 나란히 있습니다. 오역이 눈에 띄게 많으면 그때
   §7.4 LLM 엔진 A/B로 넘어갈 근거가 됩니다.
2. **§6.11 시작 전 모델 다운로드 확인.** 설정에서 `whisper-large-v3`(미설치)를 고르고 시작 →
   2.9GB 확인 창이 뜨는지, 받은 뒤 큐가 이어서 도는지. 이 김에 §5의 large-v3 다운로드
   미검증 항목도 같이 지워집니다.
3. `scripts\smoke-gpu.ps1`로 속도 측정(실시간 대비 배율).
4. **번역 품질 A/B.** 같은 파일을 NLLB 1.3B로 한 번, Qwen 7B로 한 번 돌려 나란히 비교.
   설정에서 엔진만 바꾸고 **재시도 → 시작**하면 됩니다 — §6.6 지문 무효화 덕에 번역만 다시
   하고 음성 인식은 재사용하므로 두 번째 측정은 번역 시간만 듭니다. 속도 차이(5~20배)를
   감수할 만한 품질 차이인지가 판단 기준입니다.
5. WPF 화면 나머지 확인 — 진행 중 상태 열이 단계에 따라 바뀌는지, 선택 항목 제거가 캐시까지
   지우는지. (상태 열 `취소됨` 표시, 버튼 비활성화, 알림 창은 확인됨)
6. 처리 방식 B/C 실측. 설치 프로그램은 **생성은 됩니다**(§6.14) — 남은 것은 실제로
   설치·업그레이드·제거를 돌려 보는 것입니다. 기존 설치가 `C:\Dev\KSubMaker`에 있으므로
   업그레이드 경로까지 한 번에 확인됩니다.
7. 남은 기능 제한: Fake AI 모드가 자막 원본 override를 무시하고 **설정 지문 무효화도 없음**
   (§6.6), 이미지 기반 내부 자막(PGS/VobSub) 선택은 되지만 처리 실패.
   `IModelManager.ResolveModelIdAsync`는 §6.11 이후로도 호출부가 없는 죽은 코드입니다 —
   "권장 모델이 없으면 설치된 것 중 가장 큰 걸로 조용히 대체"하는 그 동작이 §6.11의 요구와
   상충해서 일부러 쓰지 않았습니다. 지울지는 미정.

---

## 8. 작업 방식 메모

- 이 저장소는 **Windows에서만 실행**되지만 Linux에서 전체 빌드·테스트가 됩니다. 컨테이너에서
  개발하더라도 WPF 컴파일 오류는 잡힙니다.
- 사용자 PC에서 스크립트를 처음 돌릴 때 `PSSecurityException`이 나면 실행 정책 문제입니다
  (`Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned` + `Unblock-File`).
- 프로토콜을 바꾸면 `AGENTS.md §6` 절차를 따르세요: 버전 올리기 → C#/Python 양쪽 → 문서 →
  왕복 테스트. 지금까지 1.0 → 1.1(출력 충돌 정책, 자막 언어) → 1.2(CUDA 라이브러리 상태) →
  1.3(`extractAudio` 미리 추출 명령) → 1.4(`settings.initialPrompt`) →
  1.5(`sourceMode: "externalSubtitle"` + `process.subtitlePath`).
- 커밋 메시지는 한국어, 무엇을 왜 고쳤는지 서술형으로 씁니다. `git log`가 실제 사례입니다.
