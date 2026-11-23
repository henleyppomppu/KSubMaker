# KSubMaker

**폴더 하나를 지정하면, 그 안의 영상들에 한국어 자막(`*.ko.srt`)을 만들어 주는 Windows
데스크톱 프로그램입니다.**

음성 인식은 [faster-whisper](https://github.com/SYSTRAN/faster-whisper), 번역은
CTranslate2 위의 [NLLB-200](https://huggingface.co/facebook/nllb-200-distilled-600M)(기본) 또는
로컬 LLM이 담당합니다. **모든 처리는 로컬에서 이루어지며 모델 다운로드 외에는 인터넷 연결이
필요 없습니다.**

---

## 화면

> 스크린샷은 아직 준비 중입니다. 아래는 각 화면이 실제로 무엇을 담고 있는지에 대한 설명입니다.
> 이미지 파일은 `docs/images/`에 추가하고 여기에서 링크하면 됩니다.

| 화면 | 내용 |
| --- | --- |
| **메인 창** | 대상 폴더 선택, 하위 폴더/숨김 폴더 포함, "한국어 자막이 있으면 건너뛰기" 체크박스, 스캔·시작·일시정지·중지·재시도·취소·**선택 항목 제거**·완료 항목 제거 버튼. 선택과 관련된 버튼(취소·재시도·선택 항목 제거·자막 원본 선택)은 지금 선택한 행에 적용할 수 없으면 비활성화됩니다. 아래쪽 목록에 파일명·길이·상태·단계·전체 진행률·단계 진행률·남은 시간·속도·감지 언어·모델·**자막 원본**·출력 경로·오류가 열로 표시됩니다. 행에서 마우스 오른쪽 → **자막 원본 선택**으로 그 파일만 음성 대신 내장 자막 트랙에서 번역하도록 바꿀 수 있고, **선택 항목 제거**로 목록에서 지울 수 있습니다(그 작업의 캐시도 함께 삭제되며, 원본 영상과 이미 만들어진 자막 파일은 그대로 남습니다). 상단에 큐 상태와 GPU 요약, 대기/실행/완료/실패 개수. |
| **설정 창** | 5개 탭 — **음성 인식**(원본 언어, Whisper 모델, 연산 정밀도, 빔 크기, VAD, 단어 타임스탬프, 이전 문맥 사용), **번역**(엔진, 번역/LLM 모델, 문체, 배치 한도, 문맥 줄 수, **고유명사 사전**), **자막**(기존 자막 처리, 출력 충돌 처리, 접미사, 줄/글자 수 제한, 큐 길이·간격, 짧은 큐 병합), **실행**(처리 방식 A/B/C, CPU 병렬 수, 자동 재시도, Fake AI 모드, 스캔 기본값), **경로**(캐시/모델/로그 폴더, 로그 수준, 경로 가리기), **시스템**(감지된 하드웨어와 권장 설정). |
| **모델 관리 창** | 카탈로그의 9개 모델 목록(이름·종류·상태·크기·예상 VRAM·진행률·라이선스). 다운로드·일시정지·재개·삭제·검증·모델 폴더 열기. 권장 모델에 표시가 붙습니다. |
| **로그 창** | 현재 로그 파일 보기. |

---

## 요구 사항

| 항목 | 최소 | 권장 |
| --- | --- | --- |
| 운영체제 | Windows 10 (1809 이상) x64 | Windows 11 x64 |
| CPU | 4코어 x64 | 8코어 이상 |
| RAM | 8 GB | 16 GB 이상 |
| GPU | 없어도 동작 (CPU 모드) | NVIDIA GPU, VRAM 8 GB 이상 + 최신 드라이버 |
| 디스크 | 프로그램 약 3 GB (CUDA 라이브러리 약 1.8 GB 포함) + 모델 최소 약 3 GB | 여유 20 GB (모델 전부 + 작업 캐시) |
| .NET 런타임 | **불필요** (self-contained 배포) | |
| Python | **불필요** (임베디드 런타임 포함) | |

* **GPU가 없으면** CPU 모드로 동작합니다. 영상 길이의 **5~15배** 시간이 걸립니다. 프로그램이
  자동으로 작은 모델과 `int8` 정밀도, 처리 방식 B를 고릅니다.
* **CUDA 툴킷을 따로 설치할 필요는 없습니다.** NVIDIA **드라이버**만 최신이면 됩니다 —
  GPU 추론에 필요한 CUDA 12용 cuBLAS와 cuDNN 9는 배포본 안의 임베디드 파이썬에 **함께
  들어 있습니다**(디스크 약 1.8 GB). 드라이버는 이 라이브러리들을 제공하지 않으므로, 없으면 첫 모델
  로드가 `CUDA_LIBRARY_MISSING`으로 실패합니다
  ([`docs/TROUBLESHOOTING.md §2`](docs/TROUBLESHOOTING.md#2-cuda_library_missing--cublas64_12dll--cudnn을-불러오지-못한다)).
* 작업 캐시로 추출되는 WAV는 **영상 1시간당 약 110 MB**입니다.

---

## 설치

### 설치 프로그램 (권장)

1. `KSubMaker-<버전>-setup.exe`를 실행합니다.
2. 기본 설치 경로는 `C:\Program Files\KSubMaker`입니다.
3. 설치가 끝나면 시작 메뉴에 **KSubMaker**가 생깁니다. 바탕화면 바로 가기는 선택 사항입니다.
4. 첫 실행 시 **모델 관리** 화면에서 모델을 내려받으세요([아래](#모델-다운로드)).

NVIDIA GPU가 감지되지 않으면 설치 중에 경고가 표시되지만 **설치는 계속 진행됩니다.**
CPU만으로도 동작하기 때문입니다.

**제거해도 `%LOCALAPPDATA%\KSubMaker`(모델과 설정)는 지워지지 않습니다.** 제거 마지막에
"모델과 설정도 삭제할까요?"를 묻고, 기본값은 "아니오"입니다.

### 포터블 zip

1. `KSubMaker-portable-<버전>-win-x64.zip`을 **쓰기 권한이 있는 폴더**에 풉니다
   (`C:\Program Files` 아래는 피하세요).
2. `KSubMaker.App.exe`를 실행합니다.

포터블 배포도 설정·모델·로그는 `%LOCALAPPDATA%\KSubMaker`에 저장합니다. 압축을 푼 폴더 안에
전부 담기지는 않습니다.

---

## 소스에서 빌드하기

### 필요한 것

| 도구 | 버전 |
| --- | --- |
| .NET SDK | 10.0.100 이상 (`global.json`이 `latestFeature` 롤포워드로 고정) |
| Python | 3.11 이상 (워커 개발·테스트용) |
| PowerShell | 5.1 이상 (패키징 스크립트용) |
| Inno Setup | 6 이상 (설치 프로그램을 만들 때만) |

### 빌드와 테스트

```powershell
# 저장소 루트에서
dotnet restore
dotnet build KSubMaker.sln -c Release
dotnet test KSubMaker.sln -c Release
```

솔루션에는 테스트 프로젝트 두 개가 등록되어 있습니다.

| 프로젝트 | 내용 | 규모 |
| --- | --- | --- |
| `tests/KSubMaker.UnitTests` | 도메인 규칙, 프로토콜 직렬화, 애플리케이션 서비스, 하드웨어 병합, 앱 매니페스트, `ErrorCodes` ↔ `errors.py` 패리티 | 실행 1,459건 |
| `tests/KSubMaker.IntegrationTests` | 퍼시스턴스 왕복, 파이프라인, 체크포인트 재개, 워커 핸드셰이크, `process` 명령 구성 | 실행 140건 |

> **참고:** 통합 테스트 중 FFmpeg나 Python이 필요한 것은 그 도구가 없으면 **실패가 아니라
> 건너뜁니다.** `dotnet test` 결과의 skipped 개수를 함께 확인하세요.

Python 워커:

```powershell
python -m pip install -e "worker[dev]"
python -m pytest worker/tests
```

현재 상태에서 `python -m pytest worker/tests`는 **670개 테스트가 모두 통과**합니다. GPU도,
모델도, 네트워크도 필요 없습니다 — 무거운 라이브러리는 전부 함수 안에서 지연 import 되고
테스트는 가짜 객체를 씁니다.

`pip install -e "worker[dev]"`는 faster-whisper·ctranslate2·transformers까지 함께 설치하므로
용량이 큽니다. **테스트만 돌리려면** 설치 없이 이렇게 하면 됩니다.

```bash
PYTHONPATH=worker python3 -m pytest worker/tests -q
```

두 스위트를 한 번에 돌리려면:

```powershell
.\scripts\run-tests.ps1
```

### Linux/CI에서 빌드하기

`Directory.Build.props`의 `EnableWindowsTargeting=true` 덕분에 WPF 프로젝트를 포함한 솔루션
전체가 Linux에서 컴파일됩니다. **실행**은 Windows에서만 됩니다.

---

## 실행

1. **폴더 선택** — 영상이 들어 있는 폴더를 고릅니다. 하위 폴더는 기본으로 포함하지 않으며, 폴더 트리 위 "하위 폴더 포함" 체크박스(또는 보기 메뉴)로 켤 수 있습니다.
2. **스캔** — `.mp4 .mkv .avi .mov .wmv .webm .m4v .ts .mts .m2ts`를 찾습니다. 심볼릭 링크
   순환과 접근 거부된 폴더는 안전하게 건너뜁니다.
3. **시작** — 큐를 처리합니다. 진행 중에도 일시정지·중지·개별 취소가 가능합니다.
4. 결과는 **원본 영상 옆에** `{영상 이름}.ko.srt`로 저장됩니다.

처리 도중 프로그램을 닫아도 됩니다. 큐와 진행 상태는 데이터베이스에, 중간 산출물(인식 결과,
부분 번역)은 캐시에 남아 있어 다음 실행에서 **이어서** 진행합니다.

---

## 모델 다운로드

첫 실행 후 **모델 관리** 화면을 열고 필요한 모델을 내려받으세요. 권장 모델에는 표시가
붙습니다(감지된 VRAM 기준).

빠른 안내:

| 환경 | 받을 것 | 대략 용량 |
| --- | --- | --- |
| GPU 없음 / 시험용 | `whisper-small` + `nllb-200-distilled-600M` | 약 2.9 GiB |
| VRAM 8 GB | `whisper-large-v3-turbo` + `nllb-200-distilled-600M` | 약 4.0 GiB |
| VRAM 12 GB 이상 | `whisper-large-v3` + `nllb-200-distilled-1.3B` | 약 8.3 GiB |

* 다운로드는 **이어받기**가 됩니다. 중간에 끊겨도 다시 누르면 이어서 받습니다.
* 받은 뒤 **검증** 버튼으로 SHA-256 무결성을 확인할 수 있습니다. **검증은 인터넷 없이
  동작합니다.**
* 인터넷이 없는 PC에 수동으로 넣는 방법은
  [`docs/MODEL_MANAGEMENT.md`](docs/MODEL_MANAGEMENT.md#8-인터넷이-없는-pc에-수동-설치)에
  있습니다.

> ⚠️ **기본 번역 모델인 NLLB-200은 CC-BY-NC-4.0(비상업적 사용) 라이선스입니다.**
> 상업적 용도라면 번역 엔진을 로컬 LLM(Qwen2.5, Apache-2.0)으로 바꾸거나 다른 모델을 쓰세요.
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 참고.

**로컬 LLM 번역**을 쓰려면 `llama-server` 실행 파일이 추가로 필요합니다(기본 배포에 없음).
`scripts\fetch-llama.ps1` 또는
[`docs/MODEL_MANAGEMENT.md §7`](docs/MODEL_MANAGEMENT.md#7-llama-server-받기-로컬-llm-엔진용--기본-미포함)을
보세요.

---

## 패키징

`scripts/` 아래의 PowerShell 스크립트들입니다. 전부 PowerShell 5.1에서 동작하고,
`Get-Help <스크립트> -Full`로 도움말을 볼 수 있습니다.

> **처음 실행할 때.** Windows 기본 실행 정책(`Restricted`)은 `.ps1` 파일을 아예 로드하지
> 않습니다. `PSSecurityException`이 나면 스크립트 문제가 아니라 정책 문제입니다. 이 창에서만
> 풀려면 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`를 먼저 실행하세요.
> zip으로 받아 압축을 푼 경우 차단 표시까지 지워야 할 수 있습니다 —
> [`docs/TROUBLESHOOTING.md §21`](docs/TROUBLESHOOTING.md#21-scriptsps1을-실행할-수-없다-pssecurityexception).

| 스크립트 | 하는 일 |
| --- | --- |
| `fetch-ffmpeg.ps1` | **LGPL 공유 빌드** FFmpeg(Windows x64)를 HTTPS로 받아 SHA-256을 확인하고 `tools/ffmpeg/bin`에 `ffmpeg.exe`·`ffprobe.exe`와 필요한 DLL을 풉니다 |
| `fetch-llama.ps1` | llama.cpp Windows CUDA 릴리스를 `tools/llama/`에 받습니다. **선택 사항** — 로컬 LLM 엔진에만 필요합니다 |
| `build-worker.ps1` | python-build-standalone CPython 3.11을 `tools/python/`에 풀고 `worker/`를 설치합니다. 이어서 **CUDA 지원 라이브러리**(`nvidia-cublas-cu12` 12.x + `nvidia-cudnn-cu12` 9.x, 다운로드 약 1.2 GB / 디스크 약 1.8 GB)를 같은 런타임에 설치하고 늘어난 용량을 보고합니다 — `ctranslate2`가 이것들을 요구하지만 휠에도 드라이버에도 들어 있지 않기 때문입니다. `-SkipCudaLibraries`로 생략하면 CPU 전용 배포본이 됩니다. 마지막으로 `hello`+`shutdown`을 파이프로 넣어 stdout이 유효한 JSON인지 스모크 테스트합니다 |
| `build-portable.ps1` | `dotnet publish -r win-x64 --self-contained true` 후 `tools/`와 워커 페이로드를 넣고 `VERSION.txt`를 쓴 뒤 `artifacts/KSubMaker-portable-<버전>-win-x64.zip`으로 압축합니다 |
| `build-installer.ps1` | 포터블 빌드를 돌린 뒤 ISCC로 `installer/KSubMaker.iss`를 컴파일합니다. Inno Setup이 없으면 명확한 오류를 냅니다 |
| `run-tests.ps1` | `dotnet test` + `pytest` |
| `smoke-gpu.ps1` | **별도로 실행하는** 실제 GPU 스모크 테스트. 하드웨어를 감지하고 짧은 클립을 실제 모델로 인식한 뒤 소요 시간을 보고합니다. CUDA GPU가 없으면 실행을 거부합니다 |

전형적인 릴리스 순서:

```powershell
.\scripts\fetch-ffmpeg.ps1
.\scripts\build-worker.ps1
.\scripts\run-tests.ps1
.\scripts\build-installer.ps1
```

---

## 설정 개요

전체 설정은 `%LOCALAPPDATA%\KSubMaker\database\ksubmaker.db`의 `AppSettings` 테이블에
key/value로 저장됩니다.

### 음성 인식

| 설정 | 기본값 | 설명 |
| --- | --- | --- |
| 원본 언어 | `auto` | ISO-639-1 코드 또는 자동 감지 |
| Whisper 모델 | `auto` | 하드웨어 권장값을 따름 |
| 연산 정밀도 | 자동 | `float32`/`float16`/`bfloat16`/`int8_float16`/`int8` |
| 빔 크기 | `5` | |
| VAD 필터 | 켜짐 | 무음 구간 제거 |
| 단어 타임스탬프 | 켜짐 | **번역 전 세그먼트 분할에 필요합니다** |
| 이전 문맥 사용 | **꺼짐** | 켜면 긴 영상에서 문장 반복 폭주가 잘 생깁니다 |

### 번역

| 설정 | 기본값 | 설명 |
| --- | --- | --- |
| 번역 엔진 | 로컬 번역 모델 | NLLB. 빠르고 결정적이며 VRAM이 적게 듭니다 |
| 번역 모델 / LLM 모델 | `auto` | |
| 문체 | 자연스럽게 | 자연/직역/존댓말/반말/원문 유지. **NLLB는 이 지시를 부분적으로만 따릅니다** |
| 배치 한도 | 30항목 / 2500자 / 180초 | 먼저 걸리는 조건에서 배치가 닫힙니다 |
| 문맥 줄 수 | `3` | 앞 배치의 마지막 N줄을 읽기 전용 문맥으로 전달 |
| 용어집 | 비어 있음 | `원문 용어 → 고정 한국어` |

### 자막 / 출력

| 설정 | 기본값 | 설명 |
| --- | --- | --- |
| 기존 자막 처리 | 항상 음성 인식 | 외부 자막 있으면 건너뛰기 / 내장 트랙 사용 / 한국어 자막 있으면 완료 처리 |
| 출력 충돌 처리 | **건너뛰기** | 덮어쓰기 / 번호 붙여 저장 |
| 출력 접미사 | `ko` | `{영상}.ko.srt` |
| 큐당 최대 줄 / 줄당 최대 글자 | 2 / 22 | |
| 큐 최소·최대 길이 | 1.0초 / 7.0초 | |
| 최소 간격 | 50 ms | |
| 짧은 큐 병합 | 켜짐 | |

### 실행

| 설정 | 기본값 | 설명 |
| --- | --- | --- |
| 처리 방식 | 자동 | A(파일 단위 순차) / B(전체 인식 후 전체 번역) / C(파이프라인 병렬) |
| CPU 병렬 수 | 자동 | GPU 단계는 이 값과 무관하게 직렬화됩니다 |
| 자동 재시도 | 켜짐 | 복구 가능한 오류에서 한 번 더 |
| Fake AI 모드 | 꺼짐 | 모델·GPU 없이 전체 경로를 시험 |

---

## `%LOCALAPPDATA%\KSubMaker` 폴더 구조

```
%LOCALAPPDATA%\KSubMaker\
├── database\
│   ├── ksubmaker.db            SQLite (작업, 설정, 모델 설치 기록). WAL 모드
│   ├── ksubmaker.db-wal
│   └── ksubmaker.db-shm
├── cache\
│   └── {jobId}\
│       ├── audio.wav                   16kHz 모노 PCM (시간당 약 110MB)
│       ├── job.json                    마지막으로 끝난 단계 + 원본 파일 지문
│       ├── transcription.json          음성 인식 결과
│       ├── translation.partial.json    {세그먼트 id: 한국어}
│       └── finalization.json           무엇을 어디에 썼는지
├── models\
│   └── {modelId}\
│       ├── ...모델 파일...
│       └── .ksubmaker-manifest.json    파일별 크기 + SHA-256 (오프라인 검증용)
└── logs\
    └── ksubmaker-YYYYMMDD.log          하루 단위 롤링, 20MB/파일, 14개 보관
```

`cache`, `models`, `logs`는 설정 → 경로에서 다른 드라이브로 옮길 수 있습니다.
`ffmpeg`/`python`/`llama` 실행 파일은 **설치 폴더의 `tools\`** 아래에 있으며 옮길 수 없습니다.

---

## 개인정보 처리

**모든 처리는 로컬에서 이루어지며 모델 다운로드 외에는 인터넷 연결이 필요 없습니다.**

* 영상, 오디오, 인식 결과, 번역 결과가 **기기 밖으로 나가지 않습니다.** 클라우드 번역 API는
  구현되어 있지 않습니다.
* 네트워크를 쓰는 곳은 **모델 다운로드 하나**뿐이며, `https://huggingface.co`로만 갑니다.
  HTTPS가 아닌 URL은 코드에서 거부합니다.
* 로컬 LLM 엔진은 `127.0.0.1`의 임시 포트에만 바인딩합니다. 외부에서 접근할 수 없습니다.
* 텔레메트리, 사용 통계, 크래시 리포트 전송이 **없습니다.**
* 로그는 로컬 파일에만 쓰이며, 설정에서 경로 가리기를 켜면 디렉터리 성분이 `***`로 바뀝니다.

---

## 라이선스

KSubMaker의 소스 코드는 **MIT 라이선스**입니다. [`LICENSE`](LICENSE)를 보세요.

함께 배포되거나 사용자가 내려받는 구성 요소는 각자의 라이선스를 따릅니다. 전체 목록과 의무
사항은 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)에 있습니다. 특히 중요한 두 가지:

* **NLLB-200 (기본 번역 모델) — CC-BY-NC-4.0. 비상업적 사용만 허용됩니다.**
* **FFmpeg — LGPL 공유 빌드를 별도 프로세스로 실행합니다.** GPL 빌드로 바꿔치기하면
  KSubMaker 전체의 배포 조건이 달라집니다.

---

## 현재 제한사항

정직하게 적습니다. 아래는 전부 코드를 읽어 확인한 사실입니다.

### 테스트

* **"전부 통과"가 "전부 실행"은 아닙니다.** FFmpeg나 Python이 필요한 통합 테스트는 그 도구가
  없으면 실패가 아니라 **건너뜁니다.** `dotnet test` 결과의 skipped 개수를 함께 보세요.
* **실제 GPU 경로는 어떤 자동 테스트로도 검증되지 않습니다.** 단위·통합 테스트는 GPU 없이
  도는 것이 목표이기 때문입니다. CUDA 경로 검증은 `scripts\smoke-gpu.ps1`을 **직접 실행**해야
  합니다. 여기에는 CUDA 지원 라이브러리(cuBLAS/cuDNN) 로드도 포함됩니다 — 자동 테스트가
  검증하는 것은 DLL 폴더 탐색·등록 호출·오류 문자열 분류까지이고, `os.add_dll_directory()`가
  실제로 Windows 로더를 만족시키는지는 GPU 기계에서만 확인됩니다.
* Python 워커 테스트 670개는 GPU·모델·네트워크 없이 모두 통과합니다.

### 프로토콜 / 통합

* **호스트가 실제로 보내는 워커 명령은 `hello`, `detectHardware`, `process`, `cancel`,
  `shutdown` 다섯입니다.** `probe`, `listModels`, `downloadModel`, `verifyModel`,
  `deleteModel`, `cancelDownload`는 워커에 완전히 구현되어 있지만 호스트는 C# 쪽
  구현(ffprobe, HTTP 다운로더)을 직접 씁니다.
* **CUDA 판정은 워커가 떠 있을 때 확정됩니다.** 시작 직후에 보이는 CUDA 표시는 `nvcuda.dll`
  로드 여부만 본 추정치이며, **드라이버가 있다는 뜻일 뿐입니다.** 첫 작업이 워커를 띄우는
  순간(또는 설정 화면에서 **새로 고침**을 누르는 순간) 워커가 CUDA 디바이스를 열어 보고
  `cublas64_12.dll`·`cudnn64_9.dll`까지 실제로 로드해 본 값으로 교체되고, 권장 설정도 다시
  계산됩니다. 워커를 시작 시점에 미리 띄우지는 않습니다 — 아직 아무것도 하지 않은 사용자를 몇 초
  기다리게 할 만한 정보가 아닙니다.
* **지원 라이브러리 로드 검사는 Windows에서만 돕니다.** 그 검사가 막는 고장(pip가 DLL을 Windows
  DLL 검색 경로 밖에 두는 것)이 Windows 고유이기 때문입니다. 다른 플랫폼에서는
  `cudaLibrariesAvailable`이 항상 `true`로 보고됩니다.

### 기능

* **이미지 기반 자막(PGS/VobSub)은 지원하지 않습니다.** 텍스트 자막 트랙만 읽습니다. 자막
  원본 목록에는 나오지만, 고르면 그 작업은 실패합니다.
* **`AskPerFile`(파일마다 묻기)은 스캔 직후에만 묻습니다.** 이미 큐에 있는 파일은 그 행에서
  **자막 원본 선택**을 쓰세요.
* **자막 원본 선택은 워커 경로에서만 의미가 있습니다.** Fake AI(인프로세스) 모드는 언제나
  오디오를 씁니다.
* **NLLB 엔진은 문체 지시를 부분적으로만 따릅니다.** 존댓말/반말은 번역 후 어미 정규화로
  근사할 뿐입니다. 문체 제어가 중요하면 로컬 LLM 엔진을 쓰세요(대신 결정적이지 않습니다).
* **출력 형식은 SRT 하나입니다.** ASS/VTT는 지원하지 않습니다.
* **번역 대상 언어는 한국어 고정입니다.**

### 운영

* **긴 경로 지원은 완전하지 않습니다.** 앱 매니페스트가 `longPathAware`를 선언하지만
  Windows의 `LongPathsEnabled` 정책이 켜져 있어야 하고, ffmpeg·Python 워커 같은 외부 실행
  파일은 각자의 매니페스트를 따릅니다. 대처법은
  [`docs/TROUBLESHOOTING.md §18`](docs/TROUBLESHOOTING.md#18-긴-경로와-한글-경로).
* **워커가 걸리면(15분 무응답) 진행 중이던 작업은 실패합니다.** 다음 작업은 새 워커에서
  시작하지만, 실패한 작업은 자동으로 재개되지 않습니다.
* **코드 서명을 하지 않습니다.** 설치 시 SmartScreen 경고가 뜹니다.
* **`llama-server`는 기본 배포에 포함되지 않습니다.** 로컬 LLM 엔진을 쓰려면 따로 받아야
  합니다.

---

## 문서

| 문서 | 내용 |
| --- | --- |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 계층 구조, 프로세스 경계, 데이터 흐름, 기술 선택 근거, 처리 방식 A/B/C, CUDA OOM 사다리, 체크포인트 |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | 결정 기록(ADR) 29건 |
| [`docs/WORKER_PROTOCOL.md`](docs/WORKER_PROTOCOL.md) | 명령·이벤트 전체 필드표, 예시 JSON, 시퀀스 다이어그램, 버전 규칙 |
| [`docs/MODEL_MANAGEMENT.md`](docs/MODEL_MANAGEMENT.md) | 모델 카탈로그, 다운로드·이어받기·검증·삭제, `auto` 해석, 오프라인 설치, `llama-server` 받기 |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | 오류 코드 22개 전부에 대한 증상 → 원인 → 해결 |
| [`AGENTS.md`](AGENTS.md) | 기여자·에이전트를 위한 저장소 규칙 |
| [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) | 제3자 구성 요소 라이선스와 의무 |
| [`worker/README.md`](worker/README.md) | Python 워커 자체 문서 |
