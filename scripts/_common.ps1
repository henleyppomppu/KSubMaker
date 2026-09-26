<#
.SYNOPSIS
    KSubMaker 패키징 스크립트가 공유하는 도우미 함수 모음입니다.

.DESCRIPTION
    scripts/*.ps1 이 dot-source 하는 내부 파일입니다. 직접 실행할 일은 없습니다.

        . (Join-Path $PSScriptRoot '_common.ps1')

    제공하는 것:
      Get-KsmRepoRoot        저장소 루트 경로
      Get-KsmToolsDirectory  <저장소 루트>\tools  (IAppPaths.ToolsDirectory 와 같은 이름)
      Get-KsmVersion         Directory.Build.props 의 <Version>
      Initialize-KsmTls      PowerShell 5.1 에서 TLS 1.2 활성화
      Invoke-KsmDownload     HTTPS 다운로드 (+ 선택적 SHA-256 검증)
      Assert-KsmFileHash     SHA-256 대조
      Get-KsmGitHubAsset     GitHub 릴리스에서 이름 패턴에 맞는 자산 찾기
      Expand-KsmArchive      .zip / .tar.gz / .tar 압축 해제
      Get-KsmPythonExe       ToolLocator 와 같은 순서로 파이썬 실행 파일 찾기
      Remove-KsmDirectory    디렉터리 삭제 (-WhatIf 지원)
      Write-KsmStep/Note/Warn/Ok  일관된 콘솔 출력

.NOTES
    PowerShell 5.1 호환. 삼항 연산자, null 병합 연산자, -Parallel 을 쓰지 않습니다.
#>

#Requires -Version 5.1

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# 콘솔 출력
# ---------------------------------------------------------------------------

function Write-KsmStep {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-KsmNote {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Write-KsmOk {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-KsmWarn {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Message)
    Write-Warning $Message
}

# ---------------------------------------------------------------------------
# 경로
# ---------------------------------------------------------------------------

function Get-KsmRepoRoot {
    <#
    .SYNOPSIS
        저장소 루트의 절대 경로를 돌려줍니다.
    .DESCRIPTION
        scripts/ 의 부모 디렉터리이며, KSubMaker.sln 이 실제로 있는지 확인합니다.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $root = Split-Path -Parent $PSScriptRoot

    if (-not (Test-Path -LiteralPath (Join-Path $root 'KSubMaker.sln'))) {
        throw "저장소 루트를 찾지 못했습니다. KSubMaker.sln 이 '$root' 에 없습니다."
    }

    return $root
}

function Get-KsmToolsDirectory {
    <#
    .SYNOPSIS
        <저장소 루트>\tools 경로를 돌려줍니다. 없으면 만듭니다.
    .DESCRIPTION
        이 디렉터리는 배포 시 앱 실행 파일 옆으로 그대로 복사되며,
        IAppPaths.ToolsDirectory (= <앱 폴더>\tools) 와 같은 구조여야 합니다.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    [OutputType([string])]
    param(
        [switch] $Create
    )

    $tools = Join-Path (Get-KsmRepoRoot) 'tools'

    if ($Create -and -not (Test-Path -LiteralPath $tools)) {
        if ($PSCmdlet.ShouldProcess($tools, '디렉터리 생성')) {
            New-Item -ItemType Directory -Path $tools -Force | Out-Null
        }
    }

    return $tools
}

function Get-KsmVersion {
    <#
    .SYNOPSIS
        Directory.Build.props 의 <Version> 값을 돌려줍니다.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $propsPath = Join-Path (Get-KsmRepoRoot) 'Directory.Build.props'

    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Directory.Build.props 를 찾지 못했습니다: $propsPath"
    }

    [xml] $props = Get-Content -LiteralPath $propsPath -Raw

    foreach ($group in $props.Project.PropertyGroup) {
        if ($null -ne $group.Version -and -not [string]::IsNullOrWhiteSpace([string] $group.Version)) {
            return ([string] $group.Version).Trim()
        }
    }

    throw "Directory.Build.props 에서 <Version> 을 읽지 못했습니다."
}

function Remove-KsmDirectory {
    <#
    .SYNOPSIS
        디렉터리를 재귀 삭제합니다. -WhatIf 를 지원합니다.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    if ($PSCmdlet.ShouldProcess($Path, '디렉터리 삭제')) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# 네트워크
# ---------------------------------------------------------------------------

function Initialize-KsmTls {
    <#
    .SYNOPSIS
        TLS 1.2 (와 가능하면 1.3) 를 활성화합니다.
    .DESCRIPTION
        Windows PowerShell 5.1 은 기본적으로 SSL3/TLS1.0 만 켜져 있는 경우가 있어
        요즘의 HTTPS 엔드포인트에 연결하지 못합니다.
    #>
    [CmdletBinding()]
    param()

    try {
        $protocols = [Net.ServicePointManager]::SecurityProtocol
        $protocols = $protocols -bor [Net.SecurityProtocolType]::Tls12

        # Tls13 은 .NET Framework 4.8 이상에서만 존재합니다.
        $tls13 = @([enum]::GetNames([Net.SecurityProtocolType]) | Where-Object { $_ -eq 'Tls13' })
        if ($tls13) {
            $protocols = $protocols -bor [Net.SecurityProtocolType]::Tls13
        }

        [Net.ServicePointManager]::SecurityProtocol = $protocols
    }
    catch {
        Write-KsmWarn "TLS 설정을 변경하지 못했습니다: $($_.Exception.Message)"
    }
}

function Assert-KsmFileHash {
    <#
    .SYNOPSIS
        파일의 SHA-256 이 기대값과 같은지 확인하고, 다르면 예외를 던집니다.
    .PARAMETER Path
        검사할 파일.
    .PARAMETER Expected
        기대하는 SHA-256 (대소문자 무관, 64자리 16진수). 비어 있으면 검사를 건너뜁니다.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $Expected
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()

    if ([string]::IsNullOrWhiteSpace($Expected)) {
        Write-KsmNote "SHA-256: $actual  (고정된 해시가 없어 검증을 건너뜁니다)"
        return $actual
    }

    $normalized = $Expected.Trim().ToLowerInvariant() -replace '^sha256:', ''

    if ($actual -ne $normalized) {
        throw "SHA-256 이 일치하지 않습니다.`n  파일   : $Path`n  기대값 : $normalized`n  실제값 : $actual"
    }

    Write-KsmOk "SHA-256 검증 통과: $actual"
    return $actual
}

function Invoke-KsmDownload {
    <#
    .SYNOPSIS
        HTTPS URL 을 파일로 내려받고, 선택적으로 SHA-256 을 검증합니다.
    .PARAMETER Uri
        HTTPS URL. http:// 는 거부합니다.
    .PARAMETER OutFile
        저장할 경로.
    .PARAMETER Sha256
        기대하는 SHA-256. 생략하면 계산해서 출력만 합니다.
    .PARAMETER Headers
        추가 HTTP 헤더 (예: GitHub 토큰).
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $OutFile,
        [string] $Sha256,
        [hashtable] $Headers
    )

    if ($Uri -notmatch '^https://') {
        throw "HTTPS 가 아닌 URL 은 사용할 수 없습니다: $Uri"
    }

    if (-not $PSCmdlet.ShouldProcess($Uri, "다운로드 -> $OutFile")) {
        return $OutFile
    }

    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Initialize-KsmTls

    Write-KsmNote "다운로드: $Uri"

    # Invoke-WebRequest 의 진행 막대는 대용량 파일에서 전송 자체보다 느립니다.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        $arguments = @{
            Uri             = $Uri
            OutFile         = $OutFile
            UseBasicParsing = $true
            TimeoutSec      = 1800
        }
        if ($Headers) { $arguments['Headers'] = $Headers }

        Invoke-WebRequest @arguments
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    if (-not (Test-Path -LiteralPath $OutFile)) {
        throw "다운로드가 끝났는데 파일이 없습니다: $OutFile"
    }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $OutFile).Length / 1MB, 1)
    Write-KsmNote "받은 크기: $sizeMb MB"

    Assert-KsmFileHash -Path $OutFile -Expected $Sha256 | Out-Null

    return $OutFile
}

function Get-KsmGitHubAsset {
    <#
    .SYNOPSIS
        GitHub 릴리스에서 이름 패턴에 맞는 자산을 찾아 URL 과 이름을 돌려줍니다.
    .DESCRIPTION
        URL 을 문서에 박아 두는 대신 릴리스 API 로 "찾습니다". 상류 릴리스의 파일 이름이
        바뀌어도 패턴만 고치면 되고, 존재하지 않는 URL 을 지어내지 않습니다.

        일치하는 자산이 없으면 사용할 수 있는 자산 이름을 전부 출력하고 예외를 던집니다.
    .PARAMETER Repository
        'owner/name' 형식.
    .PARAMETER Tag
        릴리스 태그. 'latest' 면 최신 릴리스를 씁니다.
    .PARAMETER Pattern
        자산 이름에 대한 -like 와일드카드 패턴.
    .PARAMETER GitHubToken
        선택. API 요청 한도를 늘립니다.
    .OUTPUTS
        PSCustomObject: Name, Url, SizeBytes, Tag
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Repository,
        [string] $Tag = 'latest',
        [Parameter(Mandatory)][string] $Pattern,
        [string] $GitHubToken
    )

    Initialize-KsmTls

    # 'latest' 는 저장소가 그렇게 표시한 릴리스일 뿐, **자산이 있는** 릴리스라는 뜻이 아닙니다.
    # llama.cpp 가 2026-09 에 자산이 nightly-tag.txt 하나뿐인 v0.5.0 을 latest 로 올리면서
    # fetch-llama.ps1 이 통째로 실패했습니다. 그때는 목록을 받아 뒤로 훑습니다.
    $useReleaseList = $Tag -eq 'latest'

    if ($useReleaseList) {
        $apiUrl = "https://api.github.com/repos/$Repository/releases?per_page=30"
    }
    else {
        $apiUrl = "https://api.github.com/repos/$Repository/releases/tags/$Tag"
    }

    $headers = @{
        'Accept'     = 'application/vnd.github+json'
        'User-Agent' = 'KSubMaker-build-script'
    }
    if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        $headers['Authorization'] = "Bearer $GitHubToken"
    }

    Write-KsmNote "릴리스 조회: $apiUrl"

    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -UseBasicParsing -TimeoutSec 120
    }
    catch {
        throw ("GitHub 릴리스 정보를 가져오지 못했습니다: $apiUrl`n" +
               "  $($_.Exception.Message)`n" +
               "  API 요청 한도에 걸렸다면 -GitHubToken 을 지정하거나, -Url 로 자산 주소를 직접 지정하세요.")
    }

    # 목록 응답이면 최신순으로 훑어 패턴에 맞는 자산을 **가진** 첫 릴리스를 고릅니다.
    # @() 로 전체를 감싸는 이유는 §4.2 — 속성에만 걸면 뒤따르는 파이프라인이 다시 벗겨냅니다.
    $candidates = if ($useReleaseList) { @($release) } else { @(, $release) }

    $matched = @()
    $searched = @()

    foreach ($candidate in $candidates) {
        $assets = @($candidate.assets)
        if ($assets.Count -eq 0) { continue }

        $searched += $candidate.tag_name
        $hit = @($assets | Where-Object { $_.name -like $Pattern })

        if ($hit.Count -gt 0) {
            $release = $candidate
            $matched = $hit
            break
        }
    }

    if ($matched.Count -eq 0) {
        $lastAssets = if ($searched.Count -gt 0) { @($candidates[0].assets) } else { @() }
        $available = (@($lastAssets | ForEach-Object { "      - $($_.name)" })) -join "`n"
        throw ("패턴 '$Pattern' 에 맞는 자산을 찾지 못했습니다. ($Repository / 확인한 릴리스: " +
               "$(@($searched) -join ', '))`n" +
               "    가장 최근 릴리스의 자산:`n$available`n" +
               "    -AssetPattern 을 조정하거나 -Url 로 직접 지정하세요.")
    }

    if ($matched.Count -gt 1) {
        Write-KsmNote "패턴에 맞는 자산이 여러 개입니다. 가장 작은 것을 고릅니다:"
        foreach ($asset in $matched) {
            Write-KsmNote "  - $($asset.name)"
        }
        $matched = @($matched | Sort-Object -Property size)
    }

    $chosen = $matched[0]

    return [pscustomobject]@{
        Name      = $chosen.name
        Url       = $chosen.browser_download_url
        SizeBytes = $chosen.size
        Tag       = $release.tag_name
    }
}

# ---------------------------------------------------------------------------
# 압축 해제
# ---------------------------------------------------------------------------

function Expand-KsmArchive {
    <#
    .SYNOPSIS
        zip / tar.gz / tgz / tar 압축을 지정한 디렉터리에 풉니다.
    .DESCRIPTION
        zip 은 .NET 압축 API 로, tar 계열은 Windows 10 1803 이상에 기본 포함된 tar.exe(bsdtar)로
        씁니다. tar.exe 가 없으면 명확한 오류를 냅니다.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $Destination
    )

    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "압축 파일을 찾지 못했습니다: $ArchivePath"
    }

    if (-not $PSCmdlet.ShouldProcess($Destination, "'$([System.IO.Path]::GetFileName($ArchivePath))' 압축 해제")) {
        return
    }

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $lower = $ArchivePath.ToLowerInvariant()

    if ($lower.EndsWith('.zip')) {
        Write-KsmNote "압축 해제(zip): $Destination"
        # PowerShell 5.1 의 Expand-Archive 는 느리므로 .NET API 를 직접 씁니다.
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        [System.IO.Compression.ZipFile]::ExtractToDirectory(
            (Resolve-Path -LiteralPath $ArchivePath).ProviderPath,
            (Resolve-Path -LiteralPath $Destination).ProviderPath)
        return
    }

    if ($lower.EndsWith('.tar.gz') -or $lower.EndsWith('.tgz') -or $lower.EndsWith('.tar')) {
        $tar = Get-Command -Name 'tar.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $tar) {
            $tar = Get-Command -Name 'tar' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if (-not $tar) {
            throw ("tar 계열 압축을 풀려면 tar.exe 가 필요합니다. Windows 10 1803 이상에는 기본 포함되어 있습니다.`n" +
                   "  대안: 7-Zip 등으로 '$ArchivePath' 를 '$Destination' 에 직접 푼 뒤 스크립트를 다시 실행하세요.")
        }

        Write-KsmNote "압축 해제(tar): $Destination"
        & $tar.Source -x -f (Resolve-Path -LiteralPath $ArchivePath).ProviderPath -C (Resolve-Path -LiteralPath $Destination).ProviderPath
        if ($LASTEXITCODE -ne 0) {
            throw "tar 압축 해제에 실패했습니다. 종료 코드 $LASTEXITCODE"
        }
        return
    }

    throw "지원하지 않는 압축 형식입니다: $ArchivePath"
}

# ---------------------------------------------------------------------------
# 파이썬 찾기
# ---------------------------------------------------------------------------

function Test-KsmDotnetSdk {
    <#
    .SYNOPSIS
        빌드에 쓸 수 있는 .NET SDK 가 있는지 확인합니다.
    .DESCRIPTION
        'dotnet' 이 PATH 에 있다는 것만으로는 부족합니다. .NET **런타임**만 설치해도
        dotnet.exe 는 존재하며, 그 상태에서 dotnet publish 를 부르면
        "The application 'publish' does not exist" 라는 엉뚱한 오류가 납니다.

        버전 만족 여부는 직접 계산하지 않고 dotnet 에게 물어봅니다. 저장소 루트에서
        'dotnet --version' 을 실행하면 global.json 의 version/rollForward 규칙이 그대로
        적용되므로, 성공하면 그 SDK 로 빌드할 수 있다는 뜻입니다. SDK 해석 규칙을
        스크립트에서 재구현하면 틀리기만 합니다.
    .OUTPUTS
        PSCustomObject: HasCommand, Path, Sdks, Required, Satisfied, Resolved, Reason
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $result = [pscustomobject]@{
        HasCommand = $false
        Path       = $null
        Sdks       = @()
        Required   = $null
        Satisfied  = $false
        Resolved   = $null
        Reason     = $null
    }

    $globalJson = Join-Path (Get-KsmRepoRoot) 'global.json'
    if (Test-Path -LiteralPath $globalJson) {
        try {
            $parsed = Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json
            if ($parsed.PSObject.Properties['sdk'] -and $parsed.sdk.PSObject.Properties['version']) {
                $result.Required = [string] $parsed.sdk.version
            }
        }
        catch {
            Write-Verbose "global.json 을 읽지 못했습니다: $($_.Exception.Message)"
        }
    }

    $dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet) {
        $result.Reason = "'dotnet' 명령을 찾을 수 없습니다."
        return $result
    }

    $result.HasCommand = $true
    $result.Path = $dotnet.Source

    try {
        $listed = Invoke-KsmProcess -FilePath $dotnet.Source -ArgumentList @('--list-sdks') -TimeoutSeconds 60
    }
    catch {
        $result.Reason = "dotnet --list-sdks 실행에 실패했습니다: $($_.Exception.Message)"
        return $result
    }

    if ($listed.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($listed.StandardOutput)) {
        $result.Sdks = @(
            $listed.StandardOutput -split "`r?`n" |
                Where-Object { $_ -match '^\s*(\d[^\s]*)\s+\[' } |
                ForEach-Object { $Matches[1] }
        )
    }

    if ($result.Sdks.Count -eq 0) {
        $result.Reason = '.NET 런타임만 설치되어 있고 SDK 가 없습니다.'
        return $result
    }

    # 최종 판정은 dotnet 에게 맡깁니다 — global.json 이 적용된 결과가 곧 정답입니다.
    try {
        $resolved = Invoke-KsmProcess -FilePath $dotnet.Source -ArgumentList @('--version') `
            -WorkingDirectory (Get-KsmRepoRoot) -TimeoutSeconds 60
    }
    catch {
        $result.Reason = "dotnet --version 실행에 실패했습니다: $($_.Exception.Message)"
        return $result
    }

    if ($resolved.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolved.StandardOutput)) {
        $result.Satisfied = $true
        $result.Resolved = $resolved.StandardOutput.Trim()
        return $result
    }

    $result.Reason = 'global.json 이 요구하는 SDK 버전을 만족하는 SDK 가 없습니다.'
    return $result
}

function Get-KsmDotnetSdkHelp {
    <#
    .SYNOPSIS
        .NET SDK 문제에 대한 사람이 읽을 수 있는 안내문을 만듭니다.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] $Check
    )

    $lines = New-Object System.Collections.Generic.List[string]

    if (-not $Check.HasCommand) {
        [void] $lines.Add('.NET SDK 가 설치되어 있지 않습니다.')
    }
    elseif ($Check.Sdks.Count -eq 0) {
        [void] $lines.Add('.NET **런타임**만 설치되어 있습니다. 빌드하려면 **SDK** 가 필요합니다.')
        [void] $lines.Add("  dotnet 경로 : $($Check.Path)")
    }
    else {
        [void] $lines.Add('설치된 .NET SDK 가 global.json 의 요구 버전을 만족하지 않습니다.')
        [void] $lines.Add("  설치됨 : $($Check.Sdks -join ', ')")
    }

    if ($Check.Required) {
        [void] $lines.Add("  필요함 : $($Check.Required) 이상 (global.json, rollForward=latestFeature)")
    }

    [void] $lines.Add('')
    [void] $lines.Add('  https://dotnet.microsoft.com/download/dotnet/10.0 에서')
    [void] $lines.Add('  ".NET SDK x64" 를 설치하세요. Runtime / Desktop Runtime 이 아니라 SDK 입니다.')
    [void] $lines.Add('  설치 후 PowerShell 창을 새로 열고 dotnet --list-sdks 로 확인하세요.')

    return $lines.ToArray()
}

function Assert-KsmDotnetSdk {
    <#
    .SYNOPSIS
        빌드 가능한 .NET SDK 가 없으면 실행 가능한 안내와 함께 예외를 던집니다.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $check = Test-KsmDotnetSdk
    if ($check.Satisfied) {
        return $check
    }

    throw ((Get-KsmDotnetSdkHelp -Check $check) -join [Environment]::NewLine)
}

function Test-KsmStorePythonAlias {
    <#
    .SYNOPSIS
        Windows "앱 실행 별칭" 스텁인지 판별합니다.
    .DESCRIPTION
        Windows 10/11 은 %LOCALAPPDATA%\Microsoft\WindowsApps 에 python.exe 라는 0 바이트
        재분석 지점을 기본으로 깔아 둡니다. 스토어에서 파이썬을 설치하지 않았다면 이 스텁은
        Microsoft Store 를 열고 아무 출력 없이 바로 끝납니다.

        Get-Command 는 이것을 멀쩡한 python.exe 로 보고하기 때문에, 경로만 보고 고르면
        "워커가 아무 응답도 하지 않는다"는 진단 불가능한 실패가 납니다. 실제로 그렇게 한 번
        깨졌습니다.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    if ($Path -like '*\Microsoft\WindowsApps\*') {
        return $true
    }

    # 스토어 별칭이 아니더라도 0 바이트 재분석 지점은 실행해 봐야 소용이 없습니다.
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['Length'] -and $item.Length -eq 0) {
        return $true
    }

    return $false
}

function Test-KsmPython {
    <#
    .SYNOPSIS
        후보 파이썬이 실제로 실행되는지, 워커 모듈을 가져올 수 있는지 확인합니다.
    .OUTPUTS
        PSCustomObject: Path, Runs, Version, HasWorkerModule, Reason
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [switch] $RequireWorkerModule
    )

    $result = [pscustomobject]@{
        Path            = $Path
        Runs            = $false
        Version         = $null
        HasWorkerModule = $false
        Reason          = $null
    }

    if (Test-KsmStorePythonAlias -Path $Path) {
        $result.Reason = 'Microsoft Store 앱 실행 별칭 스텁입니다(실제 파이썬이 아닙니다).'
        return $result
    }

    try {
        $probe = Invoke-KsmProcess -FilePath $Path `
            -ArgumentList @('-c', 'import sys; sys.stdout.write("%d.%d" % sys.version_info[:2])') `
            -TimeoutSeconds 30
    }
    catch {
        $result.Reason = "실행할 수 없습니다: $($_.Exception.Message)"
        return $result
    }

    if ($probe.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($probe.StandardOutput)) {
        $result.Reason = "버전을 확인할 수 없습니다(종료 코드 $($probe.ExitCode))."
        return $result
    }

    $result.Runs = $true
    $result.Version = $probe.StandardOutput.Trim()

    if (-not $RequireWorkerModule) {
        return $result
    }

    try {
        $module = Invoke-KsmProcess -FilePath $Path `
            -ArgumentList @('-c', 'import ksubmaker_worker') `
            -TimeoutSeconds 60
        $result.HasWorkerModule = ($module.ExitCode -eq 0)
        if (-not $result.HasWorkerModule) {
            $result.Reason = 'ksubmaker_worker 모듈을 가져올 수 없습니다.'
        }
    }
    catch {
        $result.Reason = "모듈 확인에 실패했습니다: $($_.Exception.Message)"
    }

    return $result
}

function Get-KsmPythonExe {
    <#
    .SYNOPSIS
        워커를 실행할 파이썬을 찾습니다.
    .DESCRIPTION
        KSubMaker.Worker.Tools.ToolLocator 와 같은 우선순위입니다.

          1. <저장소 루트>\tools\python\python.exe   (임베디드 런타임 — build-worker.ps1 이 만듭니다)
          2. $env:KSUBMAKER_WORKER_PYTHON
          3. py -3 런처가 가리키는 파이썬
          4. PATH 의 python / python3

        후보는 **실제로 실행해 보고** 채택합니다. Microsoft Store 앱 실행 별칭 스텁
        (%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe) 은 항상 건너뜁니다 —
        Get-Command 는 이것을 정상 python.exe 로 보고하지만 실행하면 스토어만 열고 끝납니다.

        찾지 못하면 $null 을 돌려줍니다. 이유를 보려면 -Verbose 를 붙이세요.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [switch] $BundledOnly,

        # 워커 모듈까지 가져올 수 있는 파이썬만 채택합니다.
        [switch] $RequireWorkerModule
    )

    $candidates = New-Object System.Collections.Generic.List[object]

    $bundled = Join-Path (Get-KsmToolsDirectory) 'python\python.exe'
    if (Test-Path -LiteralPath $bundled) {
        $candidates.Add([pscustomobject]@{
            Path   = (Resolve-Path -LiteralPath $bundled).ProviderPath
            Source = '번들 런타임(tools\python)'
        })
    }

    if (-not $BundledOnly) {
        if (-not [string]::IsNullOrWhiteSpace($env:KSUBMAKER_WORKER_PYTHON)) {
            if (Test-Path -LiteralPath $env:KSUBMAKER_WORKER_PYTHON) {
                $candidates.Add([pscustomobject]@{
                    Path   = (Resolve-Path -LiteralPath $env:KSUBMAKER_WORKER_PYTHON).ProviderPath
                    Source = 'KSUBMAKER_WORKER_PYTHON'
                })
            }
            else {
                $resolved = Get-Command -Name $env:KSUBMAKER_WORKER_PYTHON -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($resolved) {
                    $candidates.Add([pscustomobject]@{ Path = $resolved.Source; Source = 'KSUBMAKER_WORKER_PYTHON' })
                }
            }
        }

        # py 런처는 스토어 별칭에 가려지지 않으므로 PATH 검색보다 신뢰할 수 있습니다.
        $pyLauncher = Get-Command -Name 'py.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pyLauncher) {
            try {
                $viaLauncher = Invoke-KsmProcess -FilePath $pyLauncher.Source `
                    -ArgumentList @('-3', '-c', 'import sys; sys.stdout.write(sys.executable)') `
                    -TimeoutSeconds 30
                if ($viaLauncher.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($viaLauncher.StandardOutput)) {
                    $candidates.Add([pscustomobject]@{
                        Path   = $viaLauncher.StandardOutput.Trim()
                        Source = 'py -3 런처'
                    })
                }
            }
            catch {
                Write-Verbose "py 런처 확인 실패: $($_.Exception.Message)"
            }
        }

        foreach ($name in @('python.exe', 'python3.exe', 'python', 'python3')) {
            foreach ($found in @(Get-Command -Name $name -CommandType Application -ErrorAction SilentlyContinue)) {
                $candidates.Add([pscustomobject]@{ Path = $found.Source; Source = "PATH($name)" })
            }
        }
    }

    $seen = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    $fallback = $null

    foreach ($candidate in $candidates) {
        if (-not $seen.Add($candidate.Path)) {
            continue
        }

        $check = Test-KsmPython -Path $candidate.Path -RequireWorkerModule:$RequireWorkerModule

        if (-not $check.Runs) {
            Write-Verbose "건너뜀 [$($candidate.Source)] $($candidate.Path) — $($check.Reason)"
            continue
        }

        if ($RequireWorkerModule -and -not $check.HasWorkerModule) {
            Write-Verbose "건너뜀 [$($candidate.Source)] $($candidate.Path) — $($check.Reason)"
            if (-not $fallback) { $fallback = $candidate.Path }
            continue
        }

        Write-Verbose "채택 [$($candidate.Source)] $($candidate.Path) (Python $($check.Version))"
        return $candidate.Path
    }

    if ($RequireWorkerModule -and $fallback) {
        Write-Verbose "워커 모듈을 가진 파이썬이 없습니다. 실행 가능한 후보만 반환: $fallback"
        return $fallback
    }

    return $null
}

# ---------------------------------------------------------------------------
# 프로세스 실행
# ---------------------------------------------------------------------------

function ConvertTo-KsmArgumentString {
    <#
    .SYNOPSIS
        인자 배열을 Windows 명령줄 문자열 하나로 안전하게 인용합니다.
    .DESCRIPTION
        CommandLineToArgvW 의 규칙을 그대로 구현합니다. 공백이 들어간 경로를 문자열
        연결로 이어 붙이면 깨지므로, PowerShell 5.1 처럼 ProcessStartInfo.ArgumentList 를
        쓸 수 없는 환경에서 이 함수를 씁니다.

        규칙:
          * 공백/탭/줄바꿈/따옴표가 없으면 그대로 둡니다.
          * 그렇지 않으면 큰따옴표로 감싸고,
            - 따옴표 앞의 역슬래시는 두 배로 늘린 뒤 따옴표를 \" 로 이스케이프
            - 닫는 따옴표 앞의 역슬래시도 두 배로 늘립니다.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string[]] $ArgumentList = @()
    )

    $parts = New-Object System.Collections.Generic.List[string]

    foreach ($argument in $ArgumentList) {
        $value = [string] $argument

        if ($value.Length -eq 0) {
            $parts.Add('""')
            continue
        }

        if ($value -notmatch '[\s"]') {
            $parts.Add($value)
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void] $builder.Append('"')

        $backslashes = 0
        foreach ($char in $value.ToCharArray()) {
            if ($char -eq '\') {
                $backslashes++
                continue
            }

            if ($char -eq '"') {
                [void] $builder.Append('\', ($backslashes * 2) + 1)
                [void] $builder.Append('"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                [void] $builder.Append('\', $backslashes)
                $backslashes = 0
            }
            [void] $builder.Append($char)
        }

        if ($backslashes -gt 0) {
            [void] $builder.Append('\', $backslashes * 2)
        }
        [void] $builder.Append('"')

        $parts.Add($builder.ToString())
    }

    return ($parts -join ' ')
}

function Set-KsmProcessArguments {
    <#
    .SYNOPSIS
        ProcessStartInfo 에 인자를 설정합니다. ArgumentList 가 있으면 그것을, 없으면
        직접 인용한 Arguments 문자열을 씁니다.
    .DESCRIPTION
        ProcessStartInfo.ArgumentList 는 .NET Core 2.1 부터 있습니다. Windows PowerShell
        5.1 은 .NET Framework 위에서 돌기 때문에 그 속성이 없습니다.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo] $StartInfo,
        [string[]] $ArgumentList = @()
    )

    $hasArgumentList = $null -ne ($StartInfo | Get-Member -Name 'ArgumentList' -MemberType Property -ErrorAction SilentlyContinue)

    if ($hasArgumentList) {
        foreach ($argument in $ArgumentList) {
            $StartInfo.ArgumentList.Add([string] $argument)
        }
        return
    }

    $StartInfo.Arguments = ConvertTo-KsmArgumentString -ArgumentList $ArgumentList
}

function Set-KsmStandardInputEncoding {
    <#
    .SYNOPSIS
        가능하면 stdin 인코딩을 지정합니다.
    .DESCRIPTION
        ProcessStartInfo.StandardInputEncoding 은 .NET Core 2.1 부터 있습니다.
        닷넷 프레임워크(Windows PowerShell 5.1)에는 없으므로 조용히 건너뜁니다.
        그 경우 stdin 은 콘솔 기본 인코딩을 따르지만, 이 저장소가 stdin 으로 보내는
        것은 ASCII 범위의 JSON 명령뿐이라 문제가 되지 않습니다.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo] $StartInfo,
        [Parameter(Mandatory)][System.Text.Encoding] $Encoding
    )

    if ($null -ne ($StartInfo | Get-Member -Name 'StandardInputEncoding' -MemberType Property -ErrorAction SilentlyContinue)) {
        $StartInfo.StandardInputEncoding = $Encoding
    }
}

function Invoke-KsmProcess {
    <#
    .SYNOPSIS
        실행 파일을 인자 배열로 실행하고 stdout/stderr/종료 코드를 돌려줍니다.
    .DESCRIPTION
        인자를 하나의 문자열로 이어 붙이지 않습니다. 경로에 공백이 있어도 안전합니다.
    .OUTPUTS
        PSCustomObject: ExitCode, StandardOutput, StandardError
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [string[]] $ArgumentList = @(),
        [string] $StandardInput,
        [string] $WorkingDirectory,
        [hashtable] $Environment,
        [int] $TimeoutSeconds = 0
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    if ($PSBoundParameters.ContainsKey('StandardInput')) {
        $startInfo.RedirectStandardInput = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    # ArgumentList(.NET Core) 가 있으면 그것을, 없으면(.NET Framework / PowerShell 5.1)
    # 직접 인용한 명령줄 문자열을 씁니다. 문자열 연결은 절대 하지 않습니다.
    Set-KsmProcessArguments -StartInfo $startInfo -ArgumentList $ArgumentList

    if ($Environment) {
        foreach ($key in $Environment.Keys) {
            $startInfo.Environment[[string] $key] = [string] $Environment[$key]
        }
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    try {
        [void] $process.Start()

        # 두 스트림을 동시에 비동기로 읽습니다. 하나만 동기로 읽으면 다른 파이프가
        # 가득 차서 자식 프로세스가 멈춥니다.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if ($PSBoundParameters.ContainsKey('StandardInput')) {
            if (-not [string]::IsNullOrEmpty($StandardInput)) {
                $process.StandardInput.Write($StandardInput)
            }
            $process.StandardInput.Flush()
            $process.StandardInput.Close()
        }

        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try { $process.Kill() } catch { }
                throw "프로세스가 ${TimeoutSeconds}초 안에 끝나지 않아 강제 종료했습니다: $FilePath"
            }
        }
        else {
            $process.WaitForExit()
        }

        return [pscustomobject]@{
            ExitCode       = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError  = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}
