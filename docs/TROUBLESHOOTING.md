# 문제 해결

증상 → 원인 → 해결 순서로 정리했습니다. 오류 코드는
`src/KSubMaker.Domain/Errors/ErrorCodes.cs`(및 거울인 `worker/ksubmaker_worker/errors.py`)의
실제 값이며, 25개가 전부입니다.

**먼저 볼 곳:** 메인 화면의 **로그 보기** 버튼 →
`%LOCALAPPDATA%\KSubMaker\logs\ksubmaker-YYYYMMDD.log`

---

## 0. 오류 코드 한눈에 보기

| 코드 | UI에 나오는 한국어 | 자동 재시도 | 이 문서의 항목 |
| --- | --- | --- | --- |
| `VIDEO_NOT_FOUND` | 영상 파일을 찾을 수 없습니다… | 아니요 | [§10](#10-작업이-video_not_found로-실패한다) |
| `VIDEO_UNREADABLE` | 영상 파일을 읽을 수 없습니다… | 아니요 | [§10](#10-작업이-video_not_found로-실패한다) |
| `AUDIO_TRACK_NOT_FOUND` | 영상에 오디오 트랙이 없어 자막을 만들 수 없습니다. | 아니요 | [§6](#6-audio_track_not_found--오디오가-없는-영상) |
| `SUBTITLE_SOURCE_NOT_FOUND` | 원본 자막 파일을 찾을 수 없습니다… | 아니요 | [§6.1](#61-subtitle_source_notfound--원본-자막-파일-문제) |
| `SUBTITLE_SOURCE_UNREADABLE` | 원본 자막 파일을 읽지 못했습니다… | 아니요 | [§6.1](#61-subtitle_source_notfound--원본-자막-파일-문제) |
| `FFMPEG_NOT_FOUND` | FFmpeg 실행 파일을 찾을 수 없습니다… | 아니요 | [§5](#5-ffmpeg_not_found--ffmpeg가-없다) |
| `FFMPEG_FAILED` | 음성 추출에 실패했습니다… | **예** | [§5](#5-ffmpeg_not_found--ffmpeg가-없다) |
| `CUDA_NOT_AVAILABLE` | CUDA를 사용할 수 없어 CPU 모드로 동작합니다… | 아니요 | [§1](#1-nvidia-gpu가-없거나-cuda를-쓸-수-없다) |
| `CUDA_LIBRARY_MISSING` | CUDA 지원 라이브러리(cuBLAS 12 / cuDNN 9)를 불러오지 못했습니다… | 아니요 | [§2](#2-cuda_library_missing--cublas64_12dll--cudnn을-불러오지-못한다) |
| `CUDA_OUT_OF_MEMORY` | GPU 메모리가 부족합니다… | **예** | [§3](#3-cuda_out_of_memory--gpu-메모리-부족) |
| `WHISPER_MODEL_NOT_FOUND` | 음성 인식 모델이 설치되어 있지 않습니다… | 아니요 | [§4](#4-모델을-찾을-수-없다) |
| `WHISPER_MODEL_LOAD_FAILED` | 음성 인식 모델을 불러오지 못했습니다… | 아니요 | [§4](#4-모델을-찾을-수-없다) |
| `TRANSCRIPTION_FAILED` | 음성 인식에 실패했습니다. | 아니요 | [§11](#11-transcription_failed--음성-인식-결과가-비어-있다) |
| `NO_SPEECH` | 음성이 없습니다. 자막으로 만들 내용이 없습니다. | 아니요 | [§11](#11-transcription_failed--음성-인식-결과가-비어-있다) — 실패가 아니라 상태 **음성 없음**(건너뜀)으로 표시됩니다 |
| `TRANSLATION_MODEL_NOT_FOUND` | 번역 모델이 설치되어 있지 않습니다… | 아니요 | [§4](#4-모델을-찾을-수-없다) |
| `TRANSLATION_FAILED` | 번역에 실패했습니다. | 아니요 | [§12](#12-translation_failed--로컬-llm-엔진이-시작되지-않는다) |
| `INVALID_TRANSLATION_RESPONSE` | 번역 결과 형식이 올바르지 않아 해당 구간을 다시 시도했습니다. | **예** | [§13](#13-invalid_translation_response--번역-결과-형식-오류) |
| `OUTPUT_WRITE_FAILED` | 자막 파일을 저장하지 못했습니다… | 아니요 | [§7](#7-output_write_failed--자막을-저장하지-못한다) |
| `DISK_SPACE_LOW` | 디스크 여유 공간이 부족합니다. | 아니요 | [§8](#8-disk_space_low--디스크-여유-공간-부족) |
| `WORKER_CRASHED` | AI 작업 프로세스가 예기치 않게 종료되었습니다. | **예** | [§9](#9-worker_crashed--ai-작업-프로세스가-죽는다) |
| `OPERATION_CANCELLED` | 작업이 취소되었습니다. | — | 정상 동작 |
| `MODEL_DOWNLOAD_FAILED` | 모델 다운로드에 실패했습니다… | 아니요 | [§15](#15-모델-다운로드가-실패하거나-멈춘다) |
| `MODEL_VERIFICATION_FAILED` | 모델 파일 검증에 실패했습니다… | 아니요 | [§15](#15-모델-다운로드가-실패하거나-멈춘다) |
| `PROTOCOL_ERROR` | 작업 프로세스와의 통신에 문제가 발생했습니다. | 아니요 | [§14](#14-protocol_error) |
| `UNKNOWN` | 알 수 없는 오류가 발생했습니다. | 아니요 | 로그를 확인하세요 |

"자동 재시도"는 `ErrorCodes.IsAutoRetryable`의 값이며, **설정 → 실행 → "복구 가능한 오류 시
자동 재시도"** 가 켜져 있을 때(기본값) 한 번 더 시도합니다.

---

## 1. NVIDIA GPU가 없거나 CUDA를 쓸 수 없다

**증상.** 메인 화면 상단의 GPU 표시가 "없음"이거나, 설정 → 시스템에 "NVIDIA GPU를 찾지
못했습니다" / "CUDA 런타임을 찾지 못했습니다"라는 경고가 뜹니다. 처리가 매우 느립니다.

**원인.** `WindowsHardwareDetector`가 판정합니다.
1. `nvidia-smi`를 PATH → `%ProgramFiles%\NVIDIA Corporation\NVSMI` → `%SystemRoot%\System32`
   순서로 찾습니다. 못 찾으면 GPU 없음.
2. 찾았더라도 `nvcuda.dll`을 로드할 수 없으면 `CudaAvailable = false`.

**해결.**

| 상황 | 조치 |
| --- | --- |
| NVIDIA GPU가 정말 없음 | 정상입니다. CPU 모드로 동작하며, 카탈로그 권장값이 `whisper-small`(RAM 16GB 이상이면 `medium`), 정밀도 `int8`, 빔 1, **처리 방식 B**로 자동 조정됩니다. 영상 길이의 5~15배 시간이 걸립니다. |
| GPU는 있는데 감지 안 됨 | 명령 프롬프트에서 `nvidia-smi`를 직접 실행해 보세요. 실행되지 않으면 NVIDIA 드라이버를 다시 설치하세요. |
| `nvidia-smi`는 되는데 CUDA 불가 | 드라이버가 오래됐을 가능성이 있습니다. **NVIDIA 드라이버를 최신으로 업데이트**하세요. 설정 → 시스템의 경고에 `cublas64_12.dll`이나 `cudnn64_9.dll`이 적혀 있다면 드라이버 문제가 아니라 **지원 라이브러리 누락**입니다 — [§2](#2-cuda_library_missing--cublas64_12dll--cudnn을-불러오지-못한다)로 가세요. |
| 노트북의 하이브리드 그래픽 | Windows **설정 → 시스템 → 디스플레이 → 그래픽**에서 `KSubMaker.App.exe`를 "고성능"(외장 GPU)으로 지정하세요. |
| 원격 데스크톱 세션 | 일부 RDP 구성에서는 GPU가 보이지 않습니다. 로컬 콘솔에서 실행하세요. |

**두 단계의 판정이 있습니다.**

1. **로컬(호스트) 감지** — `WindowsHardwareDetector`가 `nvcuda.dll` 로드 가능성으로 봅니다.
   이것은 **드라이버가 있다**는 뜻일 뿐입니다. 프로그램을 켠 직후 상태 표시줄에 보이는 값이
   이것이며, **추정치**입니다.
2. **워커 확인** — 파이썬 워커가 CUDA 디바이스를 열고, 그 다음 `cublas64_12.dll`과
   `cudnn64_9.dll`을 **실제로 로드해 봅니다**. 둘 다 성공해야 "CUDA 사용 가능"입니다.
   설정 → 시스템의 **새로 고침**을 누르거나 첫 작업이 시작되면 이 값으로 바뀝니다.

예전에는 1단계만 보고 "CUDA 사용 가능"이라고 표시했고, 2단계에서 실패하는 기계에서는 GPU
모델을 권장한 뒤 작업 중간에 죽었습니다. 그 실패의 대처법이 다음 항목입니다.

---

## 2. `CUDA_LIBRARY_MISSING` — `cublas64_12.dll` / cuDNN을 불러오지 못한다

**증상.** GPU도 드라이버도 멀쩡한데 작업이 시작하자마자 실패합니다. 로그에는 이렇게 남습니다.

```
[INF] WorkerHardwareProbe: worker 하드웨어 확인: CUDA=true (13.1), GPU 1개
[ERR] WorkerJobProcessor: worker 오류: TRANSCRIPTION_FAILED 음성 인식을 시작하지 못했습니다.
      RuntimeError('Library cublas64_12.dll is not found or cannot be loaded')
```

(수정 이후에는 `TRANSCRIPTION_FAILED`가 아니라 `CUDA_LIBRARY_MISSING`으로 보고되고, 하드웨어
확인도 `CUDA=false`로 정정됩니다.)

**원인 — 드라이버로는 부족합니다.** `ctranslate2 >= 4.5`는 두 라이브러리에 링크되어 있습니다.

| 파일 | 무엇 | 어디서 오는가 |
| --- | --- | --- |
| `cublas64_12.dll`, `cublasLt64_12.dll` | cuBLAS (CUDA **12**) | `nvidia-cublas-cu12` 휠 |
| `cudnn64_9.dll` (+ `cudnn_*64_9.dll`) | cuDNN **9** | `nvidia-cudnn-cu12` 휠 |

이 둘은 **CUDA 툴킷** 구성 요소입니다. NVIDIA **드라이버**는 이것들을 설치하지 않고,
`ctranslate2` 휠도 담고 있지 않습니다. 그래서 드라이버가 CUDA 13.1을 보고하는 최신 기계에서도
`cublas64_12.dll`이 없을 수 있습니다 — 드라이버 버전과 툴킷 라이브러리는 별개입니다.

파일 이름에 주 버전이 박혀 있다는 점도 중요합니다. cuDNN **10**을 설치해도
`cudnn64_9.dll`을 찾는 코드에는 아무 도움이 되지 않습니다.

**설치만으로는 부족합니다.** pip는 DLL을
`...\tools\python\Lib\site-packages\nvidia\<구성요소>\bin`에 넣는데, 그 폴더는 Windows의 DLL
검색 경로가 아닙니다. CPython 3.8부터 인터프리터가
`SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)`를 호출하므로 `PATH`에 추가해도
소용이 없습니다. 워커는 시작하자마자
`worker/ksubmaker_worker/cuda_setup.py`에서 `os.add_dll_directory()`로 그 폴더들을
등록합니다 — 그리고 `ctranslate2`를 import하기 **전에** 해야 합니다.

**해결 1 (권장). 워커를 다시 설치합니다.**

```powershell
.\scripts\build-worker.ps1
```

`nvidia-cublas-cu12`와 `nvidia-cudnn-cu12`를 임베디드 파이썬에 설치하고, 두 DLL이 제자리에
있는지 확인한 뒤, 늘어난 용량(약 1.8 GB)을 알려 줍니다.
CPU 전용으로 만들고 싶다면 `-SkipCudaLibraries`를 붙이세요.

**해결 2. 손으로 넣기 (설치 프로그램으로 받은 배포본에서 스크립트를 쓸 수 없을 때).**

```powershell
# <앱>은 설치 폴더. 기본값 C:\Program Files\KSubMaker
$py = "<앱>\tools\python\python.exe"
& $py -m pip install "nvidia-cublas-cu12>=12.9,<13" "nvidia-cudnn-cu12>=9.24,<10"
```

버전 상한을 그대로 두세요. `<13` / `<10`이 없으면 언젠가 CUDA 13 / cuDNN 10 휠이 들어오고,
설치는 깨끗하게 끝난 뒤 로드에서만 실패합니다.

pip를 쓸 수 없다면(오프라인 등) 다른 기계에서 두 휠을 받아 `.whl`을 zip으로 풀고,
`nvidia\cublas\bin\*.dll`과 `nvidia\cudnn\bin\*.dll`을 `<앱>\tools\python\` 아래
같은 경로로 복사하거나, 더 단순하게 **`<앱>\tools\python\bin\`** 에 전부 넣으세요 —
`cuda_setup.py`가 그 폴더도 검색합니다.

**확인.**

```powershell
# 1) 파일이 있는지
Get-ChildItem "<앱>\tools\python\Lib\site-packages\nvidia" -Recurse -Filter "cublas64_12.dll"

# 2) 워커가 실제로 로드할 수 있는지 — 이것이 진짜 확인입니다
'{"command":"detectHardware","requestId":"r1","protocolVersion":"1.2"}',
'{"command":"shutdown","requestId":"r2"}' |
    & "<앱>\tools\python\python.exe" -m ksubmaker_worker
```

`hardware` 이벤트에서 이렇게 나와야 합니다.

```json
{"cudaAvailable":true,"cudaDeviceDetected":true,"cudaLibrariesAvailable":true,"missingCudaLibraries":[]}
```

`cudaLibrariesAvailable`이 `false`이면 `missingCudaLibraries`에 어떤 파일이 문제인지 적혀
있습니다. stderr의 `cuda_setup:` 로 시작하는 줄에는 어떤 폴더를 등록했고 어떤 DLL을 찾았는지가
그대로 나옵니다.

**그래도 안 되면.** 설정 → 음성 인식에서 정밀도를 `int8`로 두고 GPU 없이 계속 쓸 수 있습니다.
느리지만 결과는 같습니다.

---

## 3. `CUDA_OUT_OF_MEMORY` — GPU 메모리 부족

**증상.** 처리 도중 "GPU 메모리가 부족합니다"가 뜨고, 로그에 "GPU 메모리가 부족하여 … 설정을
낮추고 다시 시도합니다"가 먼저 보입니다.

**자동으로 무슨 일이 일어나는가.** 워커가 실패시키기 전에 사다리를 한 번 내려갑니다
(`commands._with_oom_recovery`):

```
① 모델 언로드 + gc.collect() + torch.cuda.empty_cache()
② 배치를 절반으로 분할        ← 버리는 게 아니라 두 조각으로 나눠 둘 다 번역
③ 정밀도 강등  float32/bfloat16 → float16 → int8_float16 → int8
④ "더 작은 모델을 고르세요" 로그
⑤ 딱 한 번 재시도
```

두 번째도 OOM이면 `recoverable: true`와 함께 실패합니다. 세 번째 동일 시도가 성공할 리 없기
때문입니다.

**직접 할 수 있는 것 (효과 큰 순서).**

1. **다른 GPU 사용 프로그램을 끄세요.** 브라우저 하드웨어 가속, 게임, 다른 AI 도구가 VRAM을
   먼저 잡고 있는 경우가 가장 흔합니다.
2. **더 작은 Whisper 모델을 고르세요.** 설정 → 음성 인식 → Whisper 모델.
   `large-v3` → `large-v3-turbo` → `medium` → `small` → `base` 순서로 내려갑니다.
   (`HardwareRecommendationPolicy.DowngradeWhisper`가 쓰는 순서와 같습니다.)
3. **정밀도를 낮추세요.** 설정 → 음성 인식 → 연산 정밀도를 `int8_float16` 또는 `int8`로.
4. **번역 모델을 600M으로.** `nllb-200-distilled-1.3B` → `nllb-200-distilled-600M`.
5. **처리 방식을 B로 고정하세요.** 설정 → 실행 → 처리 방식 → "전체 인식 후 전체 번역".
   두 모델이 동시에 상주하지 않으므로 VRAM 요구가 크게 줄어듭니다.
6. **번역 배치를 줄이세요.** 설정 → 번역 → 배치 최대 항목/문자 수.

VRAM별 권장 조합은 [`MODEL_MANAGEMENT.md §7.1`](MODEL_MANAGEMENT.md#61-하드웨어--권장-모델-hardwarerecommendationpolicyrecommend)에 있습니다.

---

## 4. 모델을 찾을 수 없다

**증상.** `WHISPER_MODEL_NOT_FOUND` / `TRANSLATION_MODEL_NOT_FOUND` — "모델이 설치되어 있지
않습니다. 모델 관리 화면에서 다운로드하세요."

**원인과 해결.**

| 원인 | 확인 방법 | 해결 |
| --- | --- | --- |
| 모델을 아직 안 받음 | 모델 화면에서 상태가 "설치되지 않음" | 다운로드 |
| 매니페스트가 없음 (수동 복사) | 파일은 있는데 상태가 "설치되지 않음" | [`MODEL_MANAGEMENT.md §9.3`](MODEL_MANAGEMENT.md#83-매니페스트-만들기)대로 매니페스트를 만들거나, 다운로드 버튼을 눌러 트리 API만 호출시키세요 |
| 폴더 이름이 잘못됨 | `%LOCALAPPDATA%\KSubMaker\models` 안의 폴더 이름 확인 | 폴더 이름은 **모델 id**여야 합니다(저장소 이름이 아님). 예: `whisper-large-v3-turbo` |
| 모델 폴더를 옮겼는데 워커가 못 찾음 | 로그에서 `worker 환경: KSUBMAKER_MODELS_DIR=...`이 새 경로인지 확인 | 호스트가 워커에 `KSUBMAKER_MODELS_DIR`을 넘깁니다. 값이 옛 경로라면 KSubMaker를 다시 시작하세요 — 환경 변수는 워커 프로세스가 뜰 때 한 번 정해집니다 |
| 취소된 다운로드가 빈 폴더를 남김 | 폴더에 `.part`만 있음 | 모델 화면에서 삭제 후 다시 다운로드 |

`WHISPER_MODEL_LOAD_FAILED`는 다릅니다 — 파일은 있는데 로드가 실패한 것입니다.
모델 화면의 **검증**을 눌러 보세요. 실패하면 삭제 후 재다운로드입니다.
검증은 통과하는데도 실패한다면 CUDA/cuDNN 문제일 가능성이 크므로 로그의 `detail`을 보세요.

---

## 5. `FFMPEG_NOT_FOUND` — FFmpeg가 없다

**증상.** "FFmpeg 실행 파일을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다."

**원인.** `ffmpeg.exe`/`ffprobe.exe`를 아래 순서로 찾는데 전부 실패했습니다.

```
① <설치 폴더>\tools\ffmpeg\bin\ffmpeg.exe
② <설치 폴더>\tools\ffmpeg.exe
③ <설치 폴더>\ffmpeg.exe
④ PATH   ← 최후의 수단. 여기서 찾으면 로그에 경고가 남습니다
```

**해결.**

* 설치 프로그램으로 깔았다면 **다시 설치**하세요. 백신이 격리했을 수 있습니다.
* 포터블 zip이라면 `tools\ffmpeg\bin\`에 `ffmpeg.exe`와 `ffprobe.exe`, 그리고 함께 들어 있던
  DLL이 모두 있는지 확인하세요.
* 소스에서 빌드했다면 `scripts\fetch-ffmpeg.ps1`을 실행하세요.
* 로그에 **"PATH의 …을(를) 사용합니다"** 경고가 보이면, 번들이 깨져서 검증되지 않은 임의의
  FFmpeg 빌드로 돌고 있다는 뜻입니다. 재설치를 권합니다.

**`FFMPEG_FAILED`는 다릅니다.** FFmpeg는 찾았는데 실행이 실패한 것입니다.
* 파일이 손상됐거나 코덱이 특이한 경우 → 해당 파일을 다른 플레이어로 열어 보세요.
* 다른 프로그램이 파일을 잠근 경우 → 그 프로그램을 닫고 재시도.
* 자동 재시도 대상이므로 일시적 문제라면 알아서 회복됩니다.

---

## 6. `AUDIO_TRACK_NOT_FOUND` — 오디오가 없는 영상

**증상.** "영상에 오디오 트랙이 없어 자막을 만들 수 없습니다."

**원인.** `ffprobe` 결과에 오디오 스트림이 하나도 없습니다. 무음 영상, 오디오를 제거한 인코딩,
또는 컨테이너가 손상된 경우입니다.

**해결.** 이 파일에 대해서는 할 수 있는 것이 없습니다. 큐에서 제거하세요.

---

## 6.1 `SUBTITLE_SOURCE_*` — 원본 자막 파일 문제

"옆에 있는 자막 파일을 번역해서 사용" 정책을 켰을 때만 나옵니다.

**`SUBTITLE_SOURCE_NOT_FOUND`.** 고른 자막 파일이 사라졌습니다. 어느 파일을 쓸지는 큐가 그
작업을 **시작하는 순간** 정해지므로, 폴더를 검색한 뒤 파일을 옮기거나 이름을 바꾸면 이 오류가
납니다. 파일을 되돌리거나, 그 작업의 소스를 음성 인식으로 바꿔 다시 시도하세요.

**`SUBTITLE_SOURCE_UNREADABLE`.** 파일은 있는데 자막을 하나도 읽지 못했습니다. 흔한 원인:

* **이미지 기반 자막.** `.sub`/`.idx`(VobSub)는 그림이라 OCR이 필요하며 지원하지 않습니다.
  애초에 후보에서 빠지지만, 확장자만 `.srt`로 바꾼 파일이라면 여기까지 옵니다.
* **깨진 파일.** 중간에서 잘렸거나 내용이 자막 형식이 아닙니다.
* **인코딩.** UTF-8 → CP932(일본어) → CP949 → CP1252 순으로 해독을 시도합니다. 이 넷 중
  어느 것도 아니면 글자가 깨진 채로 통과할 수 있는데, 그 경우 오류 대신 이상한 번역이 나옵니다.
  자막 파일을 UTF-8로 저장해 다시 시도하세요.

어느 파일이 선택됐는지는 로그에 남습니다: `원본 자막으로 … 을(를) 씁니다.`
정말 오디오가 있는데 안 잡힌다면 명령 프롬프트에서 직접 확인해 보세요.

```
tools\ffmpeg\bin\ffprobe.exe -hide_banner -show_streams -select_streams a "D:\Videos\문제파일.mkv"
```

내장 자막 트랙이 있다면 다른 길이 있습니다. **설정 → 자막 → 기존 자막 처리**를
"내장 자막 트랙 사용"으로 바꾸면 ASR 대신 내장 자막을 번역합니다.
(주의: 현재 이 경로는 자막 트랙의 언어 태그를 워커에 전달하지 않아 **영어로 가정**합니다.)

---

## 7. `OUTPUT_WRITE_FAILED` — 자막을 저장하지 못한다

**증상.** "자막 파일을 저장하지 못했습니다. 폴더 쓰기 권한과 디스크 여유 공간을 확인하세요."

**원인과 해결.**

| 원인 | 해결 |
| --- | --- |
| 폴더에 쓰기 권한이 없음 | 영상이 `C:\Program Files` 같은 보호된 위치에 있으면 관리자 권한이 필요합니다. 영상을 사용자 폴더로 옮기는 편이 낫습니다 |
| 읽기 전용 미디어 | NAS/DVD/네트워크 공유가 읽기 전용인지 확인 |
| 네트워크 드라이브 연결 끊김 | 다시 연결 후 재시도 |
| 다른 프로그램이 대상 파일을 잠금 | 플레이어나 자막 편집기를 닫으세요 |
| 경로가 너무 긺 | [§18](#18-긴-경로와-한글-경로) |
| 여유 공간 부족 | [§8](#8-disk_space_low--디스크-여유-공간-부족) |

**중요:** 실패해도 기존 자막 파일은 절대 손상되지 않습니다. `AtomicSubtitleWriter`는 정책 확인 →
여유 공간 확인 → **같은 폴더**의 임시 파일에 쓰기 → 이동 순서로 동작합니다.

### 자막이 안 만들어졌는데 작업은 "완료"로 표시된다

버그가 아니라 **출력 충돌 정책**입니다. 기본값이 `Skip`이므로 `{영상}.ko.srt`가 이미 있으면
쓰지 않고 완료로 표시합니다. 로그에 "이미 자막 파일이 있어 건너뜁니다"가 남습니다.

덮어쓰려면 설정 → 자막 → 출력 충돌 처리를 "덮어쓰기" 또는 "번호를 붙여 새 파일"로 바꾸세요.

이 설정은 워커에도 그대로 전달됩니다(프로토콜 1.1의 `settings.outputConflictPolicy`). 워커가
쓰지 않기로 하면 `completed` 이벤트의 `skipped`가 `true`로 오고, 작업은 성공으로 끝나되 파일은
건드리지 않습니다.

`번호를 붙여 새 파일`을 고르면 `{영상}.ko (2).srt`, `{영상}.ko (3).srt` … 순으로 저장하며
기존 파일은 그대로 둡니다.

---

## 8. `DISK_SPACE_LOW` — 디스크 여유 공간 부족

**증상.** "디스크 여유 공간이 부족합니다. 현재 xxMB, 최소 50MB가 필요합니다."

**원인.** 자막을 쓰기 **전에** 대상 볼륨의 여유 공간을 확인하고 50MB 미만이면 거부합니다.
SRT 자체는 작지만, 그렇게 꽉 찬 볼륨은 임시 파일 쓰기도 중간에 실패하기 때문에 명확한 메시지를
먼저 내는 쪽을 택했습니다.

**공간을 먹는 곳.**

| 위치 | 내용 | 정리 방법 |
| --- | --- | --- |
| `%LOCALAPPDATA%\KSubMaker\cache\{jobId}\audio.wav` | 추출된 16kHz 모노 PCM. **시간당 약 110MB** | [§17](#17-체크포인트-캐시-비우기) |
| `%LOCALAPPDATA%\KSubMaker\models\` | 모델. 전부 받으면 약 21GiB | 모델 화면에서 안 쓰는 모델 삭제 |
| `%LOCALAPPDATA%\KSubMaker\logs\` | 최대 20MB × 14개 | 오래된 파일 삭제(프로그램을 끈 상태에서) |
| 영상이 있는 볼륨 | 결과 SRT | — |

캐시와 모델 폴더는 설정 → 경로에서 여유 있는 드라이브로 옮길 수 있습니다.

---

## 9. `WORKER_CRASHED` — AI 작업 프로세스가 죽는다

**증상.** "AI 작업 프로세스가 예기치 않게 종료되었습니다." 자동 재시도 대상입니다.

**확인.** 로그에서 아래 줄을 찾으세요.

```
작업 도중 worker가 종료되었습니다. (작업 …, 종료 코드 …) <stderr 마지막 줄들>
```

호스트는 워커 stderr의 마지막 50줄을 링 버퍼에 보관했다가 여기에 붙입니다. 진짜 원인은 거의
항상 그 안에 있습니다.

**흔한 원인.**

| stderr에 보이는 것 | 원인 | 해결 |
| --- | --- | --- |
| `ImportError` / `ModuleNotFoundError` | Python 페이로드가 불완전 | 재설치. 소스 빌드라면 `scripts\build-worker.ps1` |
| `CUDA error` / `cudnn` | 드라이버 문제 | NVIDIA 드라이버 업데이트 |
| `MemoryError` / 시스템 메모리 부족 | RAM 부족 | 더 작은 모델, 다른 프로그램 종료 |
| 아무것도 없이 종료 코드 -1073741819 (0xC0000005) | 네이티브 액세스 위반 | 대개 드라이버/CUDA 조합 문제. 드라이버 업데이트 후에도 계속되면 `int8` 정밀도로 낮춰 보세요 |
| 백신 로그에 격리 기록 | 백신이 `python.exe`를 종료 | 설치 폴더를 예외로 등록 |

**15분 무응답 종료.** `WorkerOptions.IdleTimeout`(기본 15분) 동안 stdout에 아무 이벤트도 오지
않으면 워커가 걸린 것으로 보고 죽입니다. 진행 중이던 작업은 실패하고, 다음 작업은 새 워커에서
시작합니다. 정상적인 처리라면 CPU에서 도는 large-v3조차 그보다 훨씬 자주 진행률을 보냅니다.

**워커를 직접 실행해 보기.** (개발/진단용)

```powershell
$env:PYTHONPATH = "worker"
'{"command":"hello","requestId":"r1","protocolVersion":"1.2"}',
'{"command":"shutdown","requestId":"r2"}' | python -m ksubmaker_worker
```

`ready` → `ack` → `ack` → `goodbye` 네 줄이 JSON으로 나와야 합니다.

---

## 10. 작업이 `VIDEO_NOT_FOUND`로 실패한다

**원인.** 스캔 이후 파일이 이동·삭제·이름 변경됐습니다. 큐는 데이터베이스에 남아 있으므로
프로그램을 껐다 켜도 예전 경로를 기억합니다.

**해결.** 큐에서 해당 작업을 제거하고 폴더를 다시 스캔하세요. 같은 경로의 파일은 기존
작업 레코드를 재사용하므로 중복 생기지 않습니다.

`VIDEO_UNREADABLE`은 파일이 있는데 읽히지 않는 경우입니다. 손상됐거나, 다른 프로그램이
배타적으로 잠갔거나, 네트워크 드라이브 연결이 끊겼습니다.

---

## 11. `TRANSCRIPTION_FAILED` — 음성 인식 결과가 비어 있다

**증상.** "음성 인식에 실패했습니다." 또는 로그의 `detail`에
`transcription contains no segments`.

**원인.**

| 원인 | 해결 |
| --- | --- |
| 영상 전체가 무음 또는 배경음만 | 이제 `TRANSCRIPTION_FAILED`가 아니라 **`NO_SPEECH`** 로 보고되고, 실패가 아닌 **음성 없음**(건너뜀)으로 표시되며 자동 재시도하지 않습니다. 소리가 정말 있는데 이렇게 나오면 VAD가 전부 걸러낸 것이니 설정 → 음성 인식에서 **VAD 필터를 끄고** 다시 시도하세요. 확인: `ffmpeg -i 영상 -af volumedetect -f null -` 의 `max_volume`이 -90 dB 근처면 무음입니다 |
| 언어를 잘못 지정 | 설정 → 음성 인식 → 원본 언어를 `auto`로 |
| `phase=translate`인데 인식 체크포인트가 없음 | 처리 방식을 A(파일 단위 순차)로 바꾸고 다시 시도. 또는 캐시를 비우고 처음부터 |
| 내장 자막 트랙이 이미지 기반(PGS/VobSub) | `parse_srt`가 읽지 못합니다. 텍스트 자막 트랙만 지원합니다. 소스 모드를 오디오로 되돌리세요 |

---

## 12. `TRANSLATION_FAILED` — 로컬 LLM 엔진이 시작되지 않는다

이 코드는 대부분 로컬 LLM 경로에서 나옵니다.

| 로그의 `detail` | 원인 | 해결 |
| --- | --- | --- |
| `llama-server not found in tools/llama, app directory or PATH` | 실행 파일이 없음 | `scripts\fetch-llama.ps1` 실행. 또는 [`MODEL_MANAGEMENT.md §8`](MODEL_MANAGEMENT.md#7-llama-server-받기-로컬-llm-엔진용--기본-미포함)대로 수동 설치 |
| `missing gguf at …` | GGUF 모델 파일이 없음 | 모델 화면에서 Qwen2.5 GGUF 다운로드 |
| `llama-server exited with … : <stderr>` | 서버가 기동 중 죽음 | GGUF 파일이 손상됐을 수 있습니다. 모델 화면에서 검증 → 재다운로드 |
| `llama-server did not become healthy within 180s` | 180초 안에 준비되지 않음 | 느린 디스크에서 큰 모델을 처음 로드하면 발생할 수 있습니다. 더 작은 모델(3B)로 시도하거나, VRAM이 부족해 CPU로 폴백된 것은 아닌지 확인 |
| `번역 서버가 시작되지 않았습니다.` | 내부 순서 오류 | 로그 전체를 확인하고 재시도 |

**가장 간단한 우회:** 설정 → 번역 → 번역 엔진을 **"로컬 번역 모델"**(기본값, NLLB)로
되돌리세요. 별도 실행 파일이 필요 없습니다.

---

## 13. `INVALID_TRANSLATION_RESPONSE` — 번역 결과 형식 오류

**증상.** "번역 결과 형식이 올바르지 않아 해당 구간을 다시 시도했습니다."

**무슨 일이 일어나는가.** 번역 엔진이 돌려준 결과를 `batching.validate`가 검사합니다.
누락 id, 중복 id, 요청하지 않은 id, 빈 번역이 있으면 **빠진 id만** 다시 요청합니다(최대 3회).
이미 맞게 온 줄은 다시 요청하지 않습니다 — 재요청이 멀쩡한 줄을 망가뜨릴 수 있기 때문입니다.

### 13.1 번역할 것이 없는 자막은 엔진에 보내지 않습니다

일본어 자막에는 `♪`(노래), `～`(장음), `…`, `。`, `！？`, `＊`, 빈 괄호처럼 **글자가 하나도 없는**
큐가 흔합니다. NLLB는 이런 입력에 대해 매번 빈 문자열을 돌려주고, 예전에는 그것을 "손상된 응답"으로
취급해서 **이런 큐 하나 때문에 작업 전체가 실패**했습니다.

지금은 어떤 문자 체계로든 글자나 십진 숫자가 하나도 없는 큐는 아예 모델을 거치지 않고 원문
그대로 통과합니다. id와 시간이 유지되므로 자막 파일에는 `♪`가 그대로 남습니다. 판정은 유니코드
카테고리로 하므로 일본어·한국어·키릴·그리스·아랍 문자는 당연히 "번역할 내용 있음"입니다.

### 13.2 끝내 번역되지 않은 줄은 원문으로 남고, 작업은 완료됩니다

재시도를 다 쓰고도 비어 있는 줄이 남으면 **작업을 실패시키지 않습니다.** 그 줄만 원문을 그대로
쓰고 나머지는 정상적으로 번역된 채로 자막이 만들어집니다. 로그 창에

```
번역되지 않은 자막 N개는 원문을 그대로 사용했습니다.
```

가 남으니, 자막 안에 원문이 몇 줄 섞여 있다면 이 메시지를 찾아보세요. 몇 분치 GPU 작업을 통째로
버리는 것보다 몇 줄이 원문으로 남는 편이 낫다는 판단입니다.

또한 **한 번 더 물어도 답이 같으면 거기서 멈춥니다.** 여기 쓰는 엔진은 결정적이라, 똑같은 입력을
세 번 보내 봐야 똑같은 답이 세 번 올 뿐입니다. 직전 시도와 빠진 id 집합이 정확히 같으면 남은
시도를 버리지 않고 바로 마무리합니다.

### 13.3 그래도 이 오류가 뜨는 경우

다음 네 가지는 "까다로운 줄"이 아니라 **엔진이 고장 난 것**이라 그대로 실패시킵니다. 자동 재시도
대상입니다.

| 조건 | 왜 실패시키는가 |
| --- | --- |
| 요청하지 않은 id가 섞여 옴 | 응답이 요청과 대응되지 않습니다. 잘못된 시간대에 잘못된 번역이 붙습니다 |
| 같은 id가 두 번 옴 | 위와 같음. 다시 물어도 고쳐지지 않는 종류의 오류입니다 |
| id를 숫자로 읽을 수 없음 | 응답 자체가 형식을 벗어났습니다(주로 로컬 LLM) |
| 배치의 **절반 이상**이 비어서 옴(단, 최소 4줄 이상일 때) | 번역할 내용이 있는 줄만 남긴 뒤에도 절반이 비었다면 원문 언어 코드가 틀렸거나 모델이 제대로 로드되지 않은 것입니다. 파일의 절반이 원문으로 남은 자막을 받는 것보다 오류를 보는 편이 낫습니다 |

"최소 4줄" 조건이 있는 이유: GPU 메모리가 부족하면 배치를 반씩 쪼개다가 한 줄짜리 배치까지
내려갑니다. 한 줄 중 한 줄이 비면 비율로는 100%지만, 그것이야말로 위에서 설명한 평범한 경우입니다.

**원인과 해결.**

| 원인 | 해결 |
| --- | --- |
| 로컬 LLM이 지시를 안 따름(설명을 덧붙이거나 항목을 합침) | 배치를 줄이세요: 설정 → 번역 → 배치 최대 항목을 30 → 15로 |
| 컨텍스트 창 초과 | 배치 최대 문자 수를 2500 → 1500으로 |
| 용어집이 너무 큼 | 프롬프트에는 최대 40개만 들어갑니다. 정말 필요한 것만 남기세요 |
| 작은 LLM 모델의 한계 | 3B → 7B로 올리거나, **NLLB 엔진으로 바꾸세요.** NLLB는 문장 단위 모델이라 구조적으로 id가 흔들리지 않습니다 |

---

## 14. `PROTOCOL_ERROR`

호스트와 워커의 통신 형식 문제입니다. 정상 사용에서는 거의 나오지 않습니다.

| 로그의 `detail` | 의미 | 해결 |
| --- | --- | --- |
| `unknown command '…'` | 워커가 모르는 명령 | 호스트와 워커 버전 불일치. 재설치 |
| `missing 'command' field` | 잘못된 입력 줄 | 재설치 |
| `job '…' is still running` | 이미 작업이 도는 중에 또 요청 | 정상 보호 동작. 큐를 멈췄다 다시 시작 |
| `unknown phase '…'` | 알 수 없는 처리 단계 | 버전 불일치 |
| `프로토콜 버전이 호환되지 않습니다` | **주 버전 불일치** | 호스트와 워커가 다른 릴리스입니다. 반드시 재설치하세요 |

부 버전만 다르면 경고 로그만 남기고 계속 동작합니다.

---

## 15. 모델 다운로드가 실패하거나 멈춘다

| 증상 | 원인 | 해결 |
| --- | --- | --- |
| `MODEL_DOWNLOAD_FAILED` | 네트워크 오류, 프록시, 방화벽 | 브라우저에서 `https://huggingface.co` 접속 확인. 사내망이면 프록시 예외 필요 |
| 진행률이 멈춤 | 60초 동안 한 바이트도 안 오면 자동으로 끊습니다 | 다시 다운로드를 누르면 **이어받습니다**(`.part` 파일 덕분) |
| `MODEL_VERIFICATION_FAILED` | SHA-256 불일치 | 다운로드가 손상됐습니다. 모델 화면에서 삭제 후 재다운로드 |
| 모델 삭제가 안 됨 | KSubMaker 자신이 모델을 로드한 상태 | 큐를 멈추고, 필요하면 프로그램을 껐다 켜고 삭제 |
| 다운로드는 됐는데 "설치되지 않음" | 매니페스트가 없음 | [`MODEL_MANAGEMENT.md §9.3`](MODEL_MANAGEMENT.md#83-매니페스트-만들기) |

**부분 파일 직접 정리.** `.part` 파일은 일부러 남습니다. 확실히 처음부터 받고 싶다면 모델
폴더에서 `*.part`를 지우세요.

```powershell
Get-ChildItem "$env:LOCALAPPDATA\KSubMaker\models" -Recurse -Filter *.part | Remove-Item
```

---

## 16. 프로그램이 시작되지 않는다

### "이미 실행 중입니다" 안내가 뜬다

**원인.** 단일 인스턴스 뮤텍스(`Global\KSubMaker`)입니다. 인스턴스가 둘이면 같은 SQLite 파일을
놓고 싸우기 때문에 막습니다.

**해결.**
1. 작업 표시줄에서 이미 떠 있는 창을 찾으세요.
2. 창이 없다면 작업 관리자에서 `KSubMaker.App.exe`를 종료하세요.
3. 그래도 안 되면 **로그아웃/재부팅**. 뮤텍스는 프로세스가 사라지면 함께 사라집니다.

**참고.** `Global\` 네임스페이스 접근이 거부되는 잠긴 정책 환경에서는 가드가 "열림"으로
실패합니다 — 앱이 안 뜨는 것보다 인스턴스가 둘 뜨는 편이 낫다는 판단입니다.

### 시작 직후 오류 대화상자

로그가 아직 없을 수 있습니다. 대화상자의 메시지를 보세요.

| 메시지에 보이는 것 | 해결 |
| --- | --- |
| 데이터베이스 관련 | [§19](#19-데이터베이스-초기화) |
| 경로/권한 관련 | `%LOCALAPPDATA%\KSubMaker`에 쓰기 권한이 있는지 확인 |
| .NET 관련 | self-contained 배포라면 발생하지 않아야 합니다. 재설치 |

---

## 17. 체크포인트 캐시 비우기

**언제 필요한가.** 디스크가 부족할 때, 처리가 계속 이상한 지점에서 이어질 때, 원본을
재인코딩했는데도 예전 결과가 나올 때.

**정상 동작.** 작업을 큐에서 제거하면 `JobQueueService`가 해당 작업의 캐시 디렉터리를 지웁니다.
원본 파일의 크기나 수정 시각이 바뀌면 체크포인트는 자동으로 무효화됩니다.

**자동 정리.** KSubMaker는 시작할 때(작업 목록을 불러온 직후) 한 번,
`JobQueueService.CleanupOrphanedCacheAsync`가 **어느 작업에도 속하지 않는 캐시 폴더**와
강제 종료가 남긴 `*.tmp` 파일을 지웁니다. 회수한 용량은 로그에 남습니다:

```
남아 있던 캐시를 정리했습니다. (128.4MB, 작업 12건 유지)
```

UI 스레드 밖에서 돌고 실패해도 시작을 막지 않습니다. 아직 큐에 있는 작업의 폴더는 건드리지
않으므로 이어하기 정보는 그대로 남습니다. 그래도 손으로 비우고 싶다면:

**손으로 비우기.** **반드시 KSubMaker를 종료한 상태에서** 하세요.

```powershell
# 캐시 전체 삭제
Remove-Item "$env:LOCALAPPDATA\KSubMaker\cache\*" -Recurse -Force

# 또는 추출된 WAV만 (체크포인트는 유지 → 인식 결과 재사용)
Get-ChildItem "$env:LOCALAPPDATA\KSubMaker\cache" -Recurse -Filter *.wav | Remove-Item -Force
```

캐시를 지우면 이어하기 정보가 사라져 다음 실행은 처음부터 합니다. 자막 결과 파일에는 영향이
없습니다.

한 작업만 처음부터 다시 돌리고 싶다면 그 작업의 폴더만 지우세요. 작업 id는 로그에서 확인할 수
있습니다.

---

## 18. 긴 경로와 한글 경로

### 한글 경로

**정상 동작합니다.** 호스트와 워커 사이의 stdin/stdout은 **BOM 없는 UTF-8**로 고정되어 있고
(`PYTHONIOENCODING=utf-8`), 워커의 JSON 출력은 `ensure_ascii=False`, 호스트의 직렬화는
`UnsafeRelaxedJsonEscaping`을 씁니다. 한글이 깨지면 그것은 버그이므로 로그와 함께 신고해 주세요.

결과 SRT 자체도 **UTF-8 BOM + CRLF**라 국내 플레이어에서 인코딩을 바꿀 필요가 없습니다.

### 260자를 넘는 경로

**앱 쪽은 준비되어 있습니다.** `src/KSubMaker.App/app.manifest`가
`<ws2:longPathAware>true</ws2:longPathAware>`를 선언하므로 KSubMaker 자신의 파일 접근은
260자 제한을 받지 않습니다. **다만 그것만으로는 부족합니다** — Windows의 머신 정책이 켜져
있어야 하고, KSubMaker가 실행하는 외부 프로그램(ffmpeg, ffprobe, Python 워커)은 각자의
매니페스트를 따릅니다. 경로 관련 예외는 잡혀서 "해당 항목 건너뜀"이 되므로, 증상은 오류가
아니라 **"파일이 스캔에 안 잡힘"** 또는 `OUTPUT_WRITE_FAILED`로 나타납니다.

**해결.**

1. **Windows에서 긴 경로를 켜세요.** (Windows 10 1607 이상, 관리자 권한) 매니페스트와 이
   정책은 **둘 다** 있어야 합니다.

   ```powershell
   New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' `
       -Name 'LongPathsEnabled' -Value 1 -PropertyType DWORD -Force
   ```

   재부팅이 필요합니다. 이것만으로 모든 경우가 해결되지는 않습니다(FFmpeg 등 외부 실행 파일도
   따라야 합니다).

2. **더 확실한 방법: 경로를 짧게.** 깊은 폴더 구조를 얕게 옮기거나, `subst`로 드라이브 문자를
   할당하세요.

   ```
   subst X: "D:\아주\깊고\긴\폴더\구조\어딘가"
   ```

   그리고 `X:\`를 스캔하세요.

---

## 19. 데이터베이스 초기화

**언제 필요한가.** 큐가 이상한 상태로 굳었을 때, 시작 시 데이터베이스 오류가 날 때,
설정을 전부 기본값으로 되돌리고 싶을 때.

**무엇을 잃는가.** 작업 큐, 모든 설정(용어집 포함), 모델 설치 기록.
**모델 파일 자체와 이미 만들어진 자막 파일은 사라지지 않습니다.** 모델은 다시 스캔되어
"설치됨"으로 인식됩니다(매니페스트가 있으므로).

**절차.**

1. KSubMaker를 **완전히 종료**하세요. 작업 관리자에서 `KSubMaker.App.exe`와 `python.exe`가
   없는지 확인합니다.
2. 데이터베이스 파일 세 개를 지우거나 이름을 바꿉니다.

   ```powershell
   $db = "$env:LOCALAPPDATA\KSubMaker\database"
   Rename-Item "$db\ksubmaker.db"     "ksubmaker.db.bak"     -ErrorAction SilentlyContinue
   Remove-Item "$db\ksubmaker.db-wal" -ErrorAction SilentlyContinue
   Remove-Item "$db\ksubmaker.db-shm" -ErrorAction SilentlyContinue
   ```

   `-wal`과 `-shm`을 함께 지우는 것이 중요합니다. WAL 모드라 이 둘에 커밋되지 않은 데이터가
   남아 있을 수 있습니다.

3. KSubMaker를 실행합니다. EF Core 마이그레이션이 새 데이터베이스를 만듭니다
   (`DatabaseInitializer`가 `MigrateAsync`를 호출).

**중단된 작업 되살리기.** 크래시로 "실행 중" 상태에 갇힌 작업은 데이터베이스를 지울 필요가
없습니다. 시작 시 `ResetOrphanedActiveJobsAsync`가 자동으로 대기 상태로 되돌립니다.

---

## 20. 로그

| 항목 | 값 |
| --- | --- |
| 위치 | `%LOCALAPPDATA%\KSubMaker\logs\` (설정 → 경로에서 변경 가능) |
| 파일명 | `ksubmaker-YYYYMMDD.log`, 크기 초과 시 `ksubmaker-YYYYMMDD_001.log` |
| 롤링 | 하루 단위 + 파일당 20MB |
| 보관 | 14개 |
| 디스크 플러시 | 2초마다 (크래시 직전 줄이 가장 중요하므로) |
| 열기 | 메인 화면의 **로그 보기** 버튼 |

**수준 바꾸기.** 설정 → 경로/로깅 → 로그 수준. `Verbose`/`Debug`/`Information`(기본)/`Warning`/
`Error`/`Fatal`. **재시작 없이 즉시 적용됩니다**(`LoggingLevelSwitch` 하나만 돌리므로).
문제 재현 시에는 `Debug`를 권합니다.

**로그를 공유할 때.** 설정 → 로깅 → "로그에서 경로 가리기"를 켜면 디렉터리 성분이 `***`로
바뀝니다. 파일명은 남습니다.

**로그에서 볼 만한 것.**

| 로그 줄 | 의미 |
| --- | --- |
| `Worker 실행 방식: …` | 워커를 어떤 방식으로 띄웠는지. 프로덕션에서 "PATH의 …"가 보이면 배포가 깨진 것 |
| `하드웨어 감지 결과: GPU=…, VRAM=…` | 감지 결과 |
| `권장 설정: …` | 하드웨어 권장 정책의 한국어 설명 |
| `작업 처리 방식: …` | A/B/C 중 무엇이 선택됐는지 |
| `ffmpeg을(를) 번들에서 찾지 못해 PATH의 …` | 번들 손상 경고 |
| `데이터베이스 마이그레이션 …건을 적용합니다` | 업그레이드 시 정상 |

---

## 21. `scripts\*.ps1`을 실행할 수 없다 (PSSecurityException)

**증상.**

```
.\smoke-gpu.ps1 : 이 시스템에서 스크립트를 실행할 수 없으므로 ... 파일을 로드할 수 없습니다.
    + FullyQualifiedErrorId : UnauthorizedAccess
```

**원인.** KSubMaker의 버그가 아니라 Windows PowerShell의 기본 실행 정책입니다. 클라이언트
Windows의 기본값은 `Restricted`라서 `.ps1` 파일이 아예 로드되지 않습니다. 여기에 더해, 저장소를
zip으로 받아 압축을 풀었다면 각 파일에 **차단 표시(Mark-of-the-Web)** 가 붙어 있을 수 있는데,
이 경우 정책을 `RemoteSigned`로 바꿔도 서명이 없는 스크립트는 계속 막힙니다.

**해결 — 상황에 맞게 하나만 고르세요.**

| 상황 | 명령 |
| --- | --- |
| 한 번만 실행 (권장, 시스템 설정을 바꾸지 않음) | `powershell -ExecutionPolicy Bypass -File .\smoke-gpu.ps1` |
| 이 창에서 여러 스크립트를 연달아 실행 | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` 실행 후 평소대로 `.\build-portable.ps1` |
| 계속 쓰겠다 (관리자 권한 불필요) | `Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned` |

`Process` 범위는 그 PowerShell 창을 닫으면 원래대로 돌아가므로 가장 안전합니다.

**차단 표시 해제.** 위 방법으로도 막히거나 `RemoteSigned`를 선택했다면 한 번만 실행하세요.

```powershell
Get-ChildItem D:\Workspace\KSubMaker -Recurse -Include *.ps1 | Unblock-File
```

**확인.** 현재 정책은 `Get-ExecutionPolicy -List`로 범위별로 볼 수 있습니다.

**참고 — `smoke-gpu.ps1`은 GPU가 없으면 일부러 거부합니다.** 정책 문제를 푼 뒤
"CUDA GPU를 찾을 수 없습니다" 메시지와 함께 종료 코드 1로 끝난다면 정상 동작입니다. GPU 없이
전체 경로만 확인하려면 `-AllowCpu`를 붙이세요. 다만 CPU 모드는 영상 길이의 5~15배가 걸리므로
짧은 클립으로만 쓰세요.

---

## 22. 어디에도 해당하지 않을 때

1. 로그 수준을 `Debug`로 올리고 문제를 재현하세요.
2. 메인 화면 **로그 보기**로 로그를 열어 오류 코드와 `detail`을 확인하세요.
3. **Fake AI 모드**(설정 → 실행)로 같은 폴더를 돌려 보세요. 여기서도 실패한다면 AI가 아니라
   스캔/큐/파일 쓰기 쪽 문제입니다. 성공한다면 모델·GPU 쪽입니다.
4. 워커를 직접 실행해 보세요([§9](#9-worker_crashed--ai-작업-프로세스가-죽는다)).
5. 신고할 때 아래를 함께 주세요.
   - 로그 파일 (경로 가리기를 켠 상태여도 됩니다)
   - 설정 → 시스템 화면의 하드웨어 정보
   - 문제가 된 파일의 컨테이너·코덱 (`ffprobe` 출력)
   - 재현 절차
