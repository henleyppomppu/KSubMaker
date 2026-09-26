<#
.SYNOPSIS
    llama.cpp 의 llama-server (Windows x64, CUDA) 를 tools/llama 에 설치합니다. **선택 구성 요소**입니다.

.DESCRIPTION
    llama-server 는 번역 엔진을 "로컬 LLM"(설정 -> 번역 -> 번역 엔진)으로 골랐을 때만
    필요합니다. **기본 배포에는 포함되지 않으며, 기본 번역 엔진(CTranslate2 + NLLB)에는
    전혀 필요하지 않습니다.**

    설치 위치:

        tools\llama\llama-server.exe   (+ ggml / CUDA DLL)

    worker/ksubmaker_worker/llm_translator.py 의 find_llama_server() 가 다음 순서로 찾습니다.

        %KSUBMAKER_TOOLS_DIR%\llama    ->  패키지 상위의 tools\llama
        ->  <python 실행 파일 폴더>\tools\llama  ->  현재 폴더\tools\llama  ->  PATH

    각 후보에서 llama\llama-server.exe, llama\bin\llama-server.exe, llama-server.exe 를 봅니다.
    이 스크립트는 첫 번째 형태로 배치합니다.

    워커는 이 실행 파일을 127.0.0.1 의 임시 포트에 바인딩해 자식 프로세스로 띄우고,
    /health 를 기다린 뒤 OpenAI 호환 /v1/chat/completions 로 통신합니다. Ollama 같은
    외부 설치나 상주 서비스는 필요하지 않습니다 (docs/DECISIONS.md ADR-016).

    라이선스: llama.cpp 는 MIT 입니다. CUDA 빌드에는 NVIDIA CUDA 재배포 라이브러리가
    포함될 수 있으며 그것들은 NVIDIA EULA 를 따릅니다. 재배포한다면 llama.cpp 의 LICENSE 를
    tools\llama 에 함께 두세요. THIRD_PARTY_NOTICES.md 참고.

    다운로드 주소는 문서에 박아 두지 않고 GitHub 릴리스 API 로 찾습니다. 완전히 재현
    가능한 빌드를 원하면 -Url 과 -Sha256 을 고정하세요.

.PARAMETER Url
    받을 압축 파일의 HTTPS 주소를 직접 지정합니다. 지정하면 릴리스 조회를 건너뜁니다.

.PARAMETER Repository
    자산을 찾을 GitHub 저장소. 기본값 'ggml-org/llama.cpp'.

.PARAMETER Tag
    릴리스 태그. 기본값 'latest'. 재현성을 위해 'b4321' 처럼 고정하는 것을 권장합니다.

.PARAMETER AssetPattern
    자산 이름 와일드카드. 기본값은 Windows x64 CUDA 빌드를 고릅니다.
    llama.cpp 의 자산 이름은 릴리스마다 자주 바뀝니다. 일치하는 자산이 없으면 스크립트가
    사용 가능한 자산 이름을 모두 출력하므로, 그 목록을 보고 패턴을 조정하세요.

.PARAMETER Sha256
    기대하는 SHA-256. 생략하면 계산해서 출력만 합니다.

.PARAMETER GitHubToken
    선택. GitHub API 요청 한도를 늘립니다.

.PARAMETER Cpu
    CUDA 대신 CPU 전용 빌드를 받습니다 (자산 패턴을 '*win*cpu*x64*.zip' 으로 바꿉니다).
    GPU 가 없는 기계에서 로컬 LLM 을 시험할 때만 쓰세요. 매우 느립니다.

.PARAMETER Force
    tools\llama 가 이미 있어도 지우고 다시 설치합니다.

.PARAMETER KeepArchive
    받은 압축 파일을 tools\_downloads 에 남깁니다.

.PARAMETER SkipCudaRuntime
    CUDA 재배포 DLL(cudart-*.zip, 약 390MB) 내려받기를 건너뜁니다. **그러면 GPU 가속이
    동작하지 않습니다** — llama.cpp 는 ggml-cuda.dll 로드에 실패해도 오류 없이 CPU 로 떨어지고,
    --n-gpu-layers 99 는 조용히 무시됩니다.

.EXAMPLE
    .\scripts\fetch-llama.ps1

    최신 릴리스의 Windows x64 CUDA 빌드를 tools\llama 에 설치합니다.

.EXAMPLE
    .\scripts\fetch-llama.ps1 -Tag b4321 -Sha256 '…' -Force

    특정 릴리스를 해시까지 고정해 설치합니다.

.EXAMPLE
    .\scripts\fetch-llama.ps1 -AssetPattern '*win-cuda-12*x64*.zip'

    기본 패턴이 맞지 않을 때 직접 지정합니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
    설치 후 모델 화면에서 Qwen2.5 GGUF 모델을 내려받아야 실제로 번역이 됩니다.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Url,
    [string] $Repository = 'ggml-org/llama.cpp',
    [string] $Tag = 'latest',
    [string] $AssetPattern,
    [string] $Sha256,
    [string] $GitHubToken,
    [switch] $Cpu,
    [switch] $Force,
    [switch] $KeepArchive,
    [switch] $SkipCudaRuntime
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

try {
    if ([string]::IsNullOrWhiteSpace($AssetPattern)) {
        if ($Cpu) {
            $AssetPattern = '*win*cpu*x64*.zip'
        }
        else {
            # CUDA **12** 를 고른다. llama.cpp 는 win-cuda-12.4 와 win-cuda-13.x 를 모두 내는데,
            # CUDA 13.0 이 Maxwell/Pascal/Volta 지원을 제거했다(최소 Turing 7.5). ggml 의 아키텍처
            # 기본값도 `CUDAToolkit_VERSION VERSION_LESS "13"` 일 때만 50/61/70-virtual 을 넣는다.
            #
            # 실기 신고: GTX 1070 에서 llama-server 가 ggml-cuda.cu 의 CUDA error 로 즉사했다.
            # 같은 카드에서 음성 인식은 멀쩡했는데, 그쪽은 §4.3 에 따라 CUDA 12 로 고정돼 있다.
            #
            # 교환 조건: CUDA 12.4 런타임은 Blackwell(RTX 50, 12.0) 보다 먼저 나왔으므로 그쪽에서
            # 동작하지 않을 수 있다. 그때는 llm_translator 의 CPU 재시도가 받아낸다 — 느려지지만
            # 실패하지는 않는다. GTX 10 시리즈 사용자가 훨씬 많다고 보고 이쪽을 골랐다.
            $AssetPattern = '*win-cuda-12*x64*.zip'
        }
    }

    $toolsDir  = Get-KsmToolsDirectory -Create
    $targetDir = Join-Path $toolsDir 'llama'

    Write-KsmStep 'llama.cpp llama-server 설치 (선택 구성 요소)'
    Write-KsmNote "설치 위치 : $targetDir"
    Write-KsmNote '기본 번역 엔진(NLLB)에는 필요하지 않습니다. 로컬 LLM 엔진에만 필요합니다.'

    if ((Test-Path -LiteralPath $targetDir) -and -not $Force) {
        $existing = Join-Path $targetDir 'llama-server.exe'
        if (Test-Path -LiteralPath $existing) {
            Write-KsmOk "이미 설치되어 있습니다: $existing"
            Write-KsmNote '다시 설치하려면 -Force 를 지정하세요.'
            exit 0
        }
        Write-KsmWarn 'tools\llama 가 있지만 llama-server.exe 가 없습니다. 다시 설치합니다.'
    }

    # -- 받을 자산 결정 -----------------------------------------------------
    $downloadUrl  = $Url
    $downloadName = $null

    # 재배포 DLL 을 **같은 릴리스에서** 받으려면 태그가 필요합니다. -Url 로 직접 지정한 경우
    # $asset 이 없으므로(StrictMode 에서는 참조만 해도 예외) 여기서 따로 붙듭니다.
    $resolvedTag = $Tag

    if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
        $asset = Get-KsmGitHubAsset -Repository $Repository -Tag $Tag -Pattern $AssetPattern -GitHubToken $GitHubToken
        $downloadUrl  = $asset.Url
        $downloadName = $asset.Name
        $resolvedTag  = $asset.Tag
        Write-KsmNote "릴리스 : $($asset.Tag)"
        Write-KsmNote "자산   : $($asset.Name)  ($([math]::Round($asset.SizeBytes / 1MB, 1)) MB)"
    }
    else {
        $downloadName = [System.IO.Path]::GetFileName(([uri] $downloadUrl).AbsolutePath)
        if ([string]::IsNullOrWhiteSpace($downloadName)) {
            $downloadName = 'llama.zip'
        }
    }

    # -- 다운로드 -----------------------------------------------------------
    $downloadDir = Join-Path $toolsDir '_downloads'
    $archivePath = Join-Path $downloadDir $downloadName

    if (-not (Test-Path -LiteralPath $downloadDir)) {
        if ($PSCmdlet.ShouldProcess($downloadDir, '디렉터리 생성')) {
            New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
        }
    }

    Invoke-KsmDownload -Uri $downloadUrl -OutFile $archivePath -Sha256 $Sha256 | Out-Null

    if ($WhatIfPreference) {
        Write-KsmNote '-WhatIf 이므로 압축 해제와 배치는 건너뜁니다.'
        exit 0
    }

    # -- 압축 해제 ----------------------------------------------------------
    $stagingDir = Join-Path $downloadDir 'llama-staging'
    Remove-KsmDirectory -Path $stagingDir
    Expand-KsmArchive -ArchivePath $archivePath -Destination $stagingDir

    $serverExe = Get-ChildItem -LiteralPath $stagingDir -Filter 'llama-server.exe' -Recurse -File -ErrorAction SilentlyContinue |
                 Select-Object -First 1

    if (-not $serverExe) {
        $found = (Get-ChildItem -LiteralPath $stagingDir -Filter '*.exe' -Recurse -File -ErrorAction SilentlyContinue |
                  ForEach-Object { "      - $($_.Name)" }) -join "`n"
        throw ("압축을 푼 결과에서 llama-server.exe 를 찾지 못했습니다.`n" +
               "    포함된 실행 파일:`n$found`n" +
               "    다른 자산을 받았을 수 있습니다. -AssetPattern 을 확인하세요.")
    }

    $sourceDir = $serverExe.DirectoryName

    # -- 배치 ---------------------------------------------------------------
    # llama-server.exe 와 같은 폴더의 모든 DLL 을 함께 옮깁니다. CUDA 빌드는
    # ggml-cuda / cudart / cublas 등 여러 DLL 을 필요로 하며, 하나라도 빠지면
    # 프로세스가 조용히 시작에 실패합니다.
    Remove-KsmDirectory -Path $targetDir
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    $copied = 0
    Get-ChildItem -LiteralPath $sourceDir -File | ForEach-Object {
        if ($_.Extension -notin @('.exe', '.dll')) { return }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetDir $_.Name) -Force
        $copied++
    }

    foreach ($candidate in @('LICENSE', 'LICENSE.txt', 'README.md')) {
        $path = Join-Path (Split-Path -Parent $sourceDir) $candidate
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $targetDir $candidate) -Force
        }
        $path = Join-Path $sourceDir $candidate
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $targetDir $candidate) -Force
        }
    }

    Remove-KsmDirectory -Path $stagingDir
    if (-not $KeepArchive) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    }

    $installed = Join-Path $targetDir 'llama-server.exe'
    if (-not (Test-Path -LiteralPath $installed)) {
        throw "설치 후에도 llama-server.exe 가 없습니다: $installed"
    }

    Write-KsmOk "$copied 개 파일을 배치했습니다: $targetDir"

    # -- CUDA 재배포 DLL ----------------------------------------------------
    # 위 압축 파일에는 ggml-cuda.dll 이 들어 있지만 **그것이 링크하는 cuBLAS 는 없습니다.**
    # llama.cpp 는 그것을 cudart-*.zip 이라는 별도 자산으로 냅니다.
    #
    # 빠뜨리면 조용히 망가집니다. ggml 은 백엔드를 런타임에 동적 등록하므로 ggml-cuda.dll 로드가
    # 실패하면 오류도 로그도 없이 CPU 백엔드로 넘어가고, --n-gpu-layers 99 는 무시됩니다.
    # 실기에서 이 상태로 동작하고 있었고(VRAM 1.4GB, llama-server RAM 6.2GB, CPU 5,145초),
    # "로컬 LLM 이 느리다"는 것을 모델 탓으로 오해하게 만들었습니다.
    #
    # 버전은 **받은 자산 이름에서 뽑습니다.** cuda-13.3 빌드에 cuda-12.4 런타임을 붙이면
    # cublas64_13.dll 이 없어 같은 증상이 됩니다 — 이름에 메이저 버전이 박혀 있어 다른 세대끼리는
    # 절대 서로를 만족시키지 못합니다.
    $isCudaBuild = $downloadName -match 'cuda'

    if ($isCudaBuild -and -not $SkipCudaRuntime) {
        if ($downloadName -match 'cuda-(?<ver>[\d.]+)-') {
            $cudaVersion   = $Matches['ver']
            $cudartPattern = "cudart-*cuda-$cudaVersion-*.zip"

            Write-KsmStep "CUDA 재배포 DLL 설치 (cuda $cudaVersion)"
            Write-KsmNote '건너뛰려면 -SkipCudaRuntime 을 지정하세요 (약 390MB 절약, GPU 가속 불가).'

            $cudartAsset   = Get-KsmGitHubAsset -Repository $Repository -Tag $resolvedTag -Pattern $cudartPattern -GitHubToken $GitHubToken
            $cudartArchive = Join-Path $downloadDir $cudartAsset.Name

            Write-KsmNote "자산 : $($cudartAsset.Name)  ($([math]::Round($cudartAsset.SizeBytes / 1MB, 1)) MB)"
            Invoke-KsmDownload -Uri $cudartAsset.Url -OutFile $cudartArchive | Out-Null

            $cudartStaging = Join-Path $downloadDir 'cudart-staging'
            Remove-KsmDirectory -Path $cudartStaging
            Expand-KsmArchive -ArchivePath $cudartArchive -Destination $cudartStaging

            # 실행 파일과 같은 폴더에 둡니다. 프로세스의 실행 파일 디렉터리는 언제나 DLL 검색
            # 경로에 들어가므로, PATH 를 손대거나 자식 프로세스에 환경을 물려줄 필요가 없습니다.
            $cudartCopied = 0
            Get-ChildItem -LiteralPath $cudartStaging -Filter '*.dll' -Recurse -File | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetDir $_.Name) -Force
                $cudartCopied++
            }

            Remove-KsmDirectory -Path $cudartStaging
            if (-not $KeepArchive) {
                Remove-Item -LiteralPath $cudartArchive -Force -ErrorAction SilentlyContinue
            }

            Write-KsmOk "$cudartCopied 개 CUDA DLL 을 배치했습니다."
        }
        else {
            Write-KsmWarn ("자산 이름에서 CUDA 버전을 읽지 못해 재배포 DLL 을 건너뜁니다: $downloadName`n" +
                           "    GPU 가속이 동작하지 않습니다. cudart-llama-bin-win-cuda-<버전>-x64.zip 을 " +
                           "직접 받아 압축을 풀고 DLL 을 $targetDir 에 넣으세요.")
        }
    }

    # CUDA 빌드인데 cuBLAS 가 없으면 GPU 는 절대 쓰이지 않습니다. --help 가 성공해도 마찬가지라
    # (아래 실행 확인은 이것을 잡지 못합니다) 파일 존재를 직접 봅니다.
    if ($isCudaBuild) {
        $cublas = @(Get-ChildItem -LiteralPath $targetDir -Filter 'cublas64_*.dll' -File -ErrorAction SilentlyContinue)
        if ($cublas.Count -eq 0) {
            Write-KsmWarn ('cuBLAS DLL 이 없습니다. llama-server 는 정상적으로 뜨지만 GPU 를 쓰지 않고 ' +
                           'CPU 로 동작합니다(오류 없이).')
        }
        else {
            Write-KsmOk "GPU 가속 준비 완료: $($cublas[0].Name)"
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $targetDir 'LICENSE'))) {
        Write-KsmWarn ('llama.cpp 의 LICENSE 파일을 찾지 못했습니다. 재배포한다면 MIT 라이선스 전문을 ' +
                       'tools\llama 에 직접 넣으세요. (THIRD_PARTY_NOTICES.md 참고)')
    }

    # 실행 확인. 모델 없이도 동작하므로 실행 파일과 기본 DLL 이 온전한지는 봅니다.
    # **CUDA 로딩은 검증하지 못합니다** — 예전 주석은 그렇다고 적혀 있었고, 그 잘못된 전제 때문에
    # cuBLAS 가 통째로 빠진 배포본이 "실행 확인 완료"를 찍고 나갔습니다. ggml 은 CUDA 백엔드 로드에
    # 실패해도 CPU 로 조용히 넘어가므로 --help 는 언제나 0 으로 끝납니다. 그 확인은 위의 cublas
    # 파일 검사가 합니다.
    $help = Invoke-KsmProcess -FilePath $installed -ArgumentList @('--help') -TimeoutSeconds 120
    if ($help.ExitCode -ne 0) {
        Write-KsmWarn ("llama-server --help 가 종료 코드 $($help.ExitCode) 로 끝났습니다. " +
                       "DLL 이 빠졌거나 CUDA 런타임이 맞지 않을 수 있습니다.`n" +
                       $help.StandardError)
    }
    else {
        Write-KsmOk 'llama-server 실행 확인 완료.'
    }

    Write-KsmStep '다음 단계'
    Write-KsmNote '1. KSubMaker 의 모델 화면에서 Qwen2.5 GGUF 모델을 내려받으세요.'
    Write-KsmNote '2. 설정 -> 번역 -> 번역 엔진을 "로컬 LLM" 으로 바꾸세요.'
    Write-KsmNote '   자세한 내용: docs/MODEL_MANAGEMENT.md'
    exit 0
}
catch {
    # 여러 줄짜리 안내 메시지는 Write-Error 의 상자 서식에서 한 줄로 뭉개지므로
    # 본문을 그대로 찍고 종료 코드만 0 이 아닌 값으로 둡니다.
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Verbose $_.ScriptStackTrace
    exit 1
}
