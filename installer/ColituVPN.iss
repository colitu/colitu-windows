#define MyAppName "Colitu VPN"
#define MyAppExeName "ColituVPN.exe"
#define MyAppPublisher "Colitu"
#define MyAppURL "https://colitu.com"

#if GetEnv("COLITU_APP_VERSION") != ""
  #define MyAppVersion GetEnv("COLITU_APP_VERSION")
#else
  #define MyAppVersion "2.5.4"
#endif

#if GetEnv("COLITU_PUBLISH_DIR") != ""
  #define PublishDir GetEnv("COLITU_PUBLISH_DIR")
#else
  #define PublishDir "..\artifacts\publish\ColituVPN\win-x64"
#endif

#if GetEnv("COLITU_OUTPUT_DIR") != ""
  #define OutputDir GetEnv("COLITU_OUTPUT_DIR")
#else
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{B7B06C4E-1D5A-482F-A781-0F6C6C8A2B67}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Colitu VPN
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=ColituVPN-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\ColituVPN\Resources\colitu.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter={#MyAppExeName}
MinVersion=10.0
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Runtime folders (sessions, logs, generated core configs) never ship: a stray copy from a test run would
; overwrite the user's own data on every update.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\guiConfigs\*,\guiLogs\*,\guiTemps\*,\binConfigs\*,\guiBackups\*,\guiUpdates\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Hidden-window launcher scripts shipped up to 2.4.0; unused, and antivirus heuristics flag them.
Type: files; Name: "{app}\bin\xray\xray_no_window.ps1"
Type: files; Name: "{app}\bin\xray\xray_no_window.vbs"
Type: files; Name: "{app}\xray-dosyalari\xray_no_window.ps1"
Type: files; Name: "{app}\xray-dosyalari\xray_no_window.vbs"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden waituntilterminated; RunOnceId: "KillColituVPN"
; Hands the system proxy back and removes the sign-in task; without it an uninstall while
; connected leaves every browser pointing at a local proxy that no longer exists.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--colitu-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "ColituCleanup"

[UninstallDelete]
; Created at runtime: sessions, cached settings, logs, generated core configs, downloaded updates.
Type: filesandordirs; Name: "{app}\guiConfigs"
Type: filesandordirs; Name: "{app}\guiLogs"
Type: filesandordirs; Name: "{app}\guiTemps"
Type: filesandordirs; Name: "{app}\binConfigs"
Type: filesandordirs; Name: "{app}\guiBackups"
Type: filesandordirs; Name: "{app}\guiUpdates"
Type: filesandordirs; Name: "{app}\bin"

[Code]
// Runs once the user clicked Install (not when the wizard opens): cancelling the wizard
// leaves a running connection alone.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// The app and its cores run elevated (and at sign-in without a UAC prompt), so ordinary
// users must not be able to change them. Program Files already ensures that; a folder
// picked elsewhere (C:\Colitu VPN, D:\Apps) would inherit "Authenticated Users: Modify".
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Exec(ExpandConstant('{sys}\icacls.exe'),
      '"' + ExpandConstant('{app}') + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
