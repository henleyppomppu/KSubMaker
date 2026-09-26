; ============================================================================
;  KSubMaker — Inno Setup 6 설치 스크립트
;
;  scripts\build-installer.ps1 이 아래 심볼을 정의해서 ISCC 를 호출합니다.
;
;      /DAppVersion=1.2.0
;      /DPublishDir=<저장소>\publish\win-x64-Release
;      /DOutputDir=<저장소>\artifacts
;      /DRepoRoot=<저장소>
;      /DSetupBaseName=KSubMaker-1.2.0-setup
;
;  직접 컴파일할 때는 아래 기본값이 쓰입니다.
;
;  설계 메모 (docs/DECISIONS.md ADR-012):
;    * WiX / MSIX 가 아니라 Inno Setup 을 쓰는 이유는 한국어 UI 내장, 수천 개 파일을
;      담은 게시 디렉터리를 하베스팅 없이 처리, [Code] 로 조건부 경고를 쉽게 넣을 수
;      있기 때문입니다.
;    * **제거 시 %LOCALAPPDATA%\KSubMaker 를 지우지 않습니다.** 사용자가 내려받은
;      모델이 수 GB 이기 때문입니다. 대신 제거가 끝난 뒤 지울지 물어보고, 기본값은
;      "아니오" 입니다.
;    * NVIDIA GPU 가 없어도 설치를 막지 않습니다. CPU 모드로 동작하기 때문입니다.
;
;  Inno Setup 6.3 이상이 필요합니다 (ArchitecturesAllowed 의 x64compatible 식별자).
;  6.0~6.2 를 쓴다면 x64compatible 을 x64 로 바꾸세요.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

#ifndef RepoRoot
  #define RepoRoot ".."
#endif

#ifndef PublishDir
  #define PublishDir RepoRoot + "\publish\win-x64-Release"
#endif

#ifndef OutputDir
  #define OutputDir RepoRoot + "\artifacts"
#endif

#ifndef SetupBaseName
  #define SetupBaseName "KSubMaker-" + AppVersion + "-setup"
#endif

#define AppName        "KSubMaker"
#define AppPublisher   "KSubMaker"
#define AppExeName     "KSubMaker.App.exe"
#define AppMutexName   "Global\KSubMaker"

; 게시 결과가 없으면 컴파일 단계에서 바로 알려 줍니다.
#if !FileExists(PublishDir + "\" + AppExeName)
  #error 게시 결과를 찾을 수 없습니다. 먼저 scripts\build-portable.ps1 을 실행하세요.
#endif

[Setup]
; AppId 는 절대 바꾸지 마세요. 업그레이드 감지와 제거 항목이 이 값에 묶여 있습니다.
AppId={{8C3A7F1E-2B4D-4A6C-9E15-5D0B7A3C9F42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
; 설치 마법사 자체의 아이콘. 앱 exe 에 박힌 것과 같은 파일입니다 (scripts/make-icon.py 생성).
SetupIconFile={#RepoRoot}\src\KSubMaker.App\Assets\app.ico

OutputDir={#OutputDir}
OutputBaseFilename={#SetupBaseName}

; 64비트 Windows 전용입니다. x64 와 ARM64 의 x64 에뮬레이션을 모두 허용합니다.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0

; {autopf} 는 관리자 권한이면 Program Files, 아니면 사용자별 위치로 해석됩니다.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 앱이 실행 중이면 닫아 달라고 요청합니다. 값은 App.xaml.cs 의 단일 인스턴스 뮤텍스와 같습니다.
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no

Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
ShowLanguageDialog=no
AllowNoIcons=yes

LicenseFile={#RepoRoot}\LICENSE

[Languages]
; 한국어 전용입니다. UI 문자열, 오류 메시지, 설정 화면이 전부 한국어이므로
; 다른 언어를 노출하면 설치 프로그램만 번역된 것처럼 보입니다.
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
korean.NoGpuTitle=NVIDIA GPU를 찾지 못했습니다
korean.NoGpuMessage=이 컴퓨터에서 NVIDIA GPU를 찾지 못했습니다.%n%nKSubMaker는 GPU 없이도 동작하지만, CPU만 사용하면 처리 시간이 영상 길이의 5~15배까지 늘어납니다. 프로그램이 자동으로 더 작은 모델과 낮은 정밀도를 선택합니다.%n%nNVIDIA 그래픽 카드가 있는데도 이 메시지가 보인다면 그래픽 드라이버를 최신 버전으로 설치한 뒤 다시 시도해 주세요.%n%n설치를 계속하시겠습니까?
korean.CreateDesktopIcon=바탕 화면에 바로 가기 만들기
korean.LaunchApp={#AppName} 실행
korean.ViewReadme=사용 설명서 보기
korean.KeepDataTitle=사용자 데이터 삭제
korean.KeepDataMessage=KSubMaker를 제거했습니다.%n%n내려받은 AI 모델, 설정, 작업 기록, 로그는 다음 폴더에 그대로 남아 있습니다.%n%n%1%n%n이 폴더에는 수 GB의 모델 파일이 들어 있을 수 있습니다. 나중에 다시 설치할 계획이라면 그대로 두는 것이 좋습니다.%n%n이 폴더도 지금 삭제하시겠습니까?
korean.KeepDataDeleted=사용자 데이터 폴더를 삭제했습니다.
korean.KeepDataFailed=사용자 데이터 폴더를 완전히 삭제하지 못했습니다. 다음 폴더를 직접 확인해 주세요.%n%n%1
korean.VcRedistTitle=Visual C++ 재배포 가능 패키지
korean.VcRedistMessage=KSubMaker의 로컬 LLM 번역 엔진에는 Microsoft Visual C++ 재배포 가능 패키지(x64)가 필요합니다. 이 컴퓨터에는 설치되어 있지 않습니다.%n%n지금 함께 설치할까요? (약 25MB를 내려받습니다)%n%n설치하지 않아도 KSubMaker는 동작합니다. 기본 번역 엔진(NLLB)과 음성 인식에는 필요하지 않습니다. 다만 로컬 LLM 엔진을 선택하면 번역이 시작되지 않습니다.
korean.VcRedistDownloading=Visual C++ 재배포 가능 패키지를 내려받는 중...
korean.VcRedistFailed=Visual C++ 재배포 가능 패키지를 설치하지 못했습니다.%n%n%1%n%nKSubMaker 설치는 계속됩니다. 로컬 LLM 엔진을 쓰려면 나중에 다음 주소에서 직접 설치하세요.%n%nhttps://aka.ms/vs/17/release/vc_redist.x64.exe
korean.NonCommercialTitle=기본 번역 모델 라이선스 안내
korean.NonCommercialMessage=기본 번역 모델인 NLLB-200은 CC-BY-NC-4.0 라이선스로 배포되며 비상업적 용도로만 사용할 수 있습니다.%n%n상업적으로 사용하려면 설정에서 번역 엔진을 바꾸거나 다른 모델을 사용해야 합니다. 자세한 내용은 설치 폴더의 THIRD_PARTY_NOTICES.md 를 참고하세요.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ---------------------------------------------------------------------------
; 게시 결과 전체.
;   build-portable.ps1 이 이 폴더에 이미 다음을 넣어 두었습니다.
;     KSubMaker.App.exe 와 self-contained .NET 런타임
;     tools\ffmpeg\bin\{ffmpeg,ffprobe}.exe  (+ LGPL 공유 DLL)
;     tools\python\python.exe                (+ 설치된 ksubmaker_worker)
;     tools\llama\                           (있을 때만 — 선택 구성 요소)
;     worker\ksubmaker_worker\               (ToolLocator 의 PYTHONPATH 폴백)
;     docs\, README.md, VERSION.txt
;
;   Excludes 의 '\' 접두사는 게시 폴더 최상위만 가리킵니다. 그래야 최상위 LICENSE 는
;   빼면서도 tools\ffmpeg\LICENSE (LGPL 의무) 같은 하위 라이선스 파일은 그대로 담깁니다.
;   역슬래시가 없는 패턴(__pycache__ 등)은 어느 깊이에서든 이름으로 일치합니다.
; ---------------------------------------------------------------------------
Source: "{#PublishDir}\*"; DestDir: "{app}"; \
    Excludes: "*.pdb,*.pyc,*.pyo,__pycache__,.pytest_cache,\LICENSE,\THIRD_PARTY_NOTICES.md"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; 라이선스 문서는 최상위에 명시적으로 넣습니다. LICENSE 는 확장자를 붙여야
; 사용자가 더블클릭했을 때 메모장으로 열립니다.
Source: "{#RepoRoot}\LICENSE";                 DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD_PARTY_NOTICES.md";  DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "영상 폴더에서 한국어 자막(.ko.srt)을 만듭니다"
Name: "{group}\사용 설명서";        Filename: "{app}\README.md"
Name: "{group}\문제 해결";          Filename: "{app}\docs\TROUBLESHOOTING.md"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\README.md";     Description: "{cm:ViewReadme}"; Flags: shellexec nowait postinstall skipifsilent unchecked

[UninstallDelete]
; 설치 폴더 안에서 실행 중에 생기는 것들만 지웁니다.
; %LOCALAPPDATA%\KSubMaker 는 여기에 **절대** 넣지 않습니다 — [Code] 에서 사용자에게 물어봅니다.
Type: filesandordirs; Name: "{app}\tools\python\Lib\site-packages\__pycache__"
Type: filesandordirs; Name: "{app}\worker\ksubmaker_worker\__pycache__"
Type: dirifempty;     Name: "{app}\worker"
Type: dirifempty;     Name: "{app}\tools"
Type: dirifempty;     Name: "{app}"

[Code]

{ ------------------------------------------------------------------------- }
{  NVIDIA GPU 감지                                                           }
{                                                                            }
{  KSubMaker.Infrastructure 의 WindowsHardwareDetector 와 같은 곳을 봅니다:  }
{  System32 -> Program Files\NVIDIA Corporation\NVSMI -> PATH.               }
{  설치를 막지 않습니다. GPU 가 없어도 CPU 모드로 동작합니다.                }
{ ------------------------------------------------------------------------- }
function HasNvidiaGpu(): Boolean;
var
  ExitCode: Integer;
begin
  Result := False;

  { 드라이버 설치는 nvidia-smi.exe 를 System32 에 둡니다. }
  if FileExists(ExpandConstant('{sys}\nvidia-smi.exe')) then
  begin
    Result := True;
    Exit;
  end;

  { 오래된 드라이버의 위치. }
  if FileExists(ExpandConstant('{commonpf64}\NVIDIA Corporation\NVSMI\nvidia-smi.exe')) then
  begin
    Result := True;
    Exit;
  end;

  { CUDA 드라이버 라이브러리. 카드가 있으면 반드시 있습니다. }
  if FileExists(ExpandConstant('{sys}\nvcuda.dll')) then
  begin
    Result := True;
    Exit;
  end;

  { 마지막으로 PATH 에서 실제 실행을 시도합니다. }
  if Exec('nvidia-smi.exe', '-L', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := (ExitCode = 0);
end;

{ ------------------------------------------------------------------------- }
{  Visual C++ 재배포 가능 패키지                                             }
{                                                                            }
{  번들 llama.cpp 의 ggml-base.dll / ggml-cuda.dll 이 MSVCP140.dll 을         }
{  임포트하는데, 배포본에는 그 파일이 없습니다. tools\python 밑의 사본은      }
{  llama-server.exe 에게 보이지 않습니다 — 별도 프로세스라 자기 폴더를 봅니다.}
{                                                                            }
{  없으면 조용히 망가집니다. 프로세스가 아예 안 뜨거나(STATUS_DLL_NOT_FOUND,  }
{  stderr 도 없음), 뜨더라도 ggml 이 CUDA 백엔드 로드에 실패해 아무 말 없이   }
{  CPU 로 떨어집니다. 후자는 "로컬 LLM 이 원래 느린 것" 과 구분되지 않습니다. }
{                                                                            }
{  설치를 막지는 않습니다. 기본 엔진(NLLB)과 음성 인식에는 필요 없습니다.     }
{ ------------------------------------------------------------------------- }
function HasVcRedist(): Boolean;
var
  Installed: Cardinal;
begin
  { 재배포 패키지가 쓰는 표준 위치. 14.x 는 2015~2022 가 공유합니다. }
  Result := RegQueryDWordValue(HKEY_LOCAL_MACHINE,
              'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed)
            and (Installed = 1);

  if Result then
    Exit;

  { 레지스트리가 지워졌거나 다른 경로로 배포된 경우를 위한 보루. 로더가 실제로 }
  { 찾는 곳은 System32 이므로 파일 존재가 레지스트리보다 진실에 가깝습니다.    }
  Result := FileExists(ExpandConstant('{sys}\msvcp140.dll'))
            and FileExists(ExpandConstant('{sys}\vcruntime140.dll'));
end;

function InstallVcRedist(): Boolean;
var
  TempFile: string;
  ExitCode: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\vc_redist.x64.exe');

  try
    DownloadTemporaryFile('https://aka.ms/vs/17/release/vc_redist.x64.exe',
                          'vc_redist.x64.exe', '', nil);
  except
    MsgBox(FmtMessage(ExpandConstant('{cm:VcRedistFailed}'), [GetExceptionMessage]),
           mbError, MB_OK);
    Exit;
  end;

  { /norestart 가 중요합니다. 재부팅 요구가 설치 도중 끼어들면 안 됩니다. }
  if not Exec(TempFile, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then
  begin
    MsgBox(FmtMessage(ExpandConstant('{cm:VcRedistFailed}'), ['Exec failed']), mbError, MB_OK);
    Exit;
  end;

  { 0 = 성공, 1638 = 같거나 더 새 버전이 이미 있음, 3010 = 성공하고 재부팅 필요. }
  Result := (ExitCode = 0) or (ExitCode = 1638) or (ExitCode = 3010);

  { 이 줄을 접지 마세요. Inno Setup 은 '[' 로 시작하는 줄을 — 앞에 공백이 몇 칸이든 —
    섹션 태그로 읽습니다. Pascal 배열 리터럴을 다음 줄로 내리면 컴파일이
    "Invalid section tag" 로 죽습니다. }
  if not Result then
    MsgBox(FmtMessage(ExpandConstant('{cm:VcRedistFailed}'), ['vc_redist.x64.exe -> ' + IntToStr(ExitCode)]),
      mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  if not HasNvidiaGpu() then
  begin
    { 정보 제공 후 사용자가 결정합니다. 기본 선택은 "예"(계속) 입니다. }
    if MsgBox(ExpandConstant('{cm:NoGpuMessage}'),
              mbInformation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
      Result := False;
  end;

end;

{ 선행 조건 설치는 Inno 가 이 자리를 위해 둔 훅입니다. 사용자가 설치를 확정한 뒤, 파일을     }
{ 복사하기 전에 불리고, 그동안 마법사가 "설치 준비 중" 페이지를 보여 줍니다.                }
{ 빈 문자열을 돌려주면 설치가 계속됩니다 — 여기서는 무엇이 실패하든 계속합니다.              }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  if HasVcRedist() then
    Exit;

  if MsgBox(ExpandConstant('{cm:VcRedistMessage}'),
            mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    InstallVcRedist();

  { 거절했거나 실패했어도 KSubMaker 설치는 계속합니다. 기본 번역 엔진(NLLB)과 음성 인식은 }
  { 이것 없이도 동작하며, 막을 이유가 없습니다. 로컬 LLM 을 고르면 워커가 같은 내용을     }
  { 안내합니다(llm_translator.missing_msvc_runtime). }
end;

{ ------------------------------------------------------------------------- }
{  설치 완료 안내                                                            }
{ ------------------------------------------------------------------------- }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { 기본 번역 모델의 비상업 라이선스는 설치 시점에 한 번 분명히 알립니다. }
    MsgBox(ExpandConstant('{cm:NonCommercialMessage}'), mbInformation, MB_OK);
  end;
end;

{ ------------------------------------------------------------------------- }
{  제거: 사용자 데이터는 지우지 않고 물어봅니다                              }
{                                                                            }
{  %LOCALAPPDATA%\KSubMaker 에는 사용자가 직접 내려받은 모델(수 GB)과 설정,  }
{  작업 기록, 로그가 들어 있습니다. 말없이 지우면 재설치할 때마다 몇 GB 를   }
{  다시 받아야 합니다. 기본값은 "아니오" 입니다.                             }
{ ------------------------------------------------------------------------- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\KSubMaker');

    if not DirExists(DataDir) then
      Exit;

    if MsgBox(FmtMessage(ExpandConstant('{cm:KeepDataMessage}'), [DataDir]),
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      if DelTree(DataDir, True, True, True) then
        MsgBox(ExpandConstant('{cm:KeepDataDeleted}'), mbInformation, MB_OK)
      else
        MsgBox(FmtMessage(ExpandConstant('{cm:KeepDataFailed}'), [DataDir]),
               mbError, MB_OK);
    end;
  end;
end;
