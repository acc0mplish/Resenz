; Resenz 원클릭 설치 스크립트(Inno Setup 6)
; CI에서 호출: ISCC /DAppVersion=x.y.z /DSourceDir=... /DOutputDir=... installer.iss

#define AppName "Resenz"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef NameSuffix
  #define NameSuffix ""
#endif

[Setup]
AppId={{8F3A9C21-7B4E-4D5A-9C1F-2E6B8D4A7F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DSUK
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 관리자 권한 없는 per-user 설치를 %LocalAppData%에 허용
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=Resenz-{#AppVersion}-x64{#NameSuffix}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Resenz.exe

[Languages]
; 한국어 언어팩은 CI가 Languages 디렉터리로 받아 전달하며,
; 없으면 자동 생략, 영어만 사용.
#ifdef KoreanISL
Name: "korean"; MessagesFile: "{#KoreanISL}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "부팅 시 자동 시작"; GroupDescription: "시작 항목"

[Files]
; 릴리스 디렉터리 전체를 패키징
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Resenz.exe"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Resenz.exe"; Tasks: desktopicon

[Run]
; 메인 프로그램은 관리자 권한 필요: 자동 시작은 예약 작업으로(로그인 시 최고 권한으로 조용히 실행, UAC 팝업 없음).
Filename: "schtasks.exe"; Parameters: "/Create /TN ""Resenz"" /TR ""\""{app}\Resenz.exe\"" --minimized"" /SC ONLOGON /RL HIGHEST /F"; \
  Flags: runhidden; Tasks: startupicon; StatusMsg: "부팅 자동 시작 예약 작업 생성 중…"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""Resenz"" /F"; Flags: runhidden; Tasks: not startupicon
Filename: "{app}\Resenz.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""Resenz"" /F"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[UninstallDelete]
; 제거 때는 프로그램 디렉터리만 정리(사용자 데이터는 %LocalAppData%\Resenz. 구독과 설정을 실수로 지우지 않게 보존)
Type: filesandordirs; Name: "{app}"
