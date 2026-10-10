#define MyAppName "Colitu VPN"
#define MyAppExeName "ColituVPN.exe"
#define MyAppPublisher "COLITU LIMITED"
#define MyAppURL "https://colitu.com"
; The kill-switch service: persistent firewall filters that survive a crash of the app (2.6.0).
#define KsServiceName "ColituKillSwitch"
#define KsServiceExe "ColituKillSwitchService.exe"

#if GetEnv("COLITU_APP_VERSION") != ""
  #define MyAppVersion GetEnv("COLITU_APP_VERSION")
#else
  #define MyAppVersion "2.6.0"
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
; runascurrentuser: postinstall entries run as the non-elevated original user by default, and the
; app's manifest requires administrator rights, so CreateProcess failed with code 740.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden waituntilterminated; RunOnceId: "KillColituVPN"
; Hands the system proxy back, removes the sign-in task and asks the kill-switch service to remove
; every Colitu firewall object; without it an uninstall while connected leaves the computer offline
; or every browser pointing at a local proxy that no longer exists.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--colitu-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "ColituCleanup"
; Then the service itself: stop it (waits), remove whatever firewall objects are still there (also
; when the service was already gone or broken), and delete it.
Filename: "{sys}\net.exe"; Parameters: "stop {#KsServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopKillSwitch"
Filename: "{app}\{#KsServiceExe}"; Parameters: "--remove-all"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveKillSwitchFilters"
Filename: "{sys}\sc.exe"; Parameters: "delete {#KsServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteKillSwitch"

[UninstallDelete]
; Created at runtime: sessions, cached settings, logs, generated core configs, downloaded updates.
Type: filesandordirs; Name: "{app}\guiConfigs"
Type: filesandordirs; Name: "{app}\guiLogs"
Type: filesandordirs; Name: "{app}\guiTemps"
Type: filesandordirs; Name: "{app}\binConfigs"
Type: filesandordirs; Name: "{app}\guiBackups"
Type: filesandordirs; Name: "{app}\guiUpdates"
Type: filesandordirs; Name: "{app}\bin"
; The kill-switch service's state and log (SYSTEM and administrators only).
Type: filesandordirs; Name: "{commonappdata}\Colitu VPN\KillSwitch"
Type: dirifempty; Name: "{commonappdata}\Colitu VPN"

[Code]
function IsDirEmpty(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Result := False;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function RunIcacls(const Params: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\icacls.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// The app and its cores run elevated (and at sign-in without a UAC prompt) and the kill-switch
// service runs as LocalSystem, so ordinary users must not be able to change them. Program Files
// already ensures that; a folder picked elsewhere (C:\Colitu VPN, D:\Apps) inherits "Authenticated
// Users: Modify", and a standard user may have created it beforehand and still own it (WRITE_DAC).
// So the folder is locked down before any file is copied into it, its owner and every child's
// ACL included, and the install stops if that fails.
// True when Dir is where an earlier Colitu VPN was installed, as the machine-wide uninstall entry
// says (only administrators can write it). A ColituVPN.exe in the folder proves nothing: anyone
// who created the folder could have put one there next to planted DLLs.
function IsPreviousInstallDir(const Dir: String): Boolean;
var
  Location: String;
begin
  Result := RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7B06C4E-1D5A-482F-A781-0F6C6C8A2B67}_is1', 'InstallLocation', Location)
    and (CompareText(RemoveBackslashUnlessRoot(Location), RemoveBackslashUnlessRoot(Dir)) = 0);
end;

function LockDownAppDir(): String;
var
  Dir: String;
begin
  Result := '';
  Dir := ExpandConstant('{app}');
  // Never take over a folder holding someone else's files (a drive root, a profile folder, or one
  // a standard user prepared).
  if DirExists(Dir) and not IsPreviousInstallDir(Dir) and not IsDirEmpty(Dir) then
  begin
    Result := 'The folder "' + Dir + '" already contains other files. Choose an empty folder for {#MyAppName}.';
    Exit;
  end;
  if not ForceDirectories(Dir) then
  begin
    Result := 'Could not create the folder "' + Dir + '".';
    Exit;
  end;
  if not RunIcacls('"' + Dir + '" /setowner *S-1-5-32-544 /T /C /Q')
    or not RunIcacls('"' + Dir + '" /reset /T /C /Q')
    or not RunIcacls('"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q') then
    Result := 'Could not restrict access to the folder "' + Dir + '" to administrators.';
end;

// Runs once the user clicked Install (not when the wizard opens): cancelling the wizard
// leaves a running connection alone.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Upgrade: stop the kill-switch service so its file can be replaced. Its firewall filters stay
  // in place while it is stopped (an armed switch keeps protecting during the update).
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#KsServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := LockDownAppDir();
end;

// Creates the service on a new install, points an existing one at this folder on an upgrade,
// and starts it. LocalSystem (WFP needs it), automatic start, restarted if it ever crashes.
procedure InstallKillSwitchService();
var
  ResultCode: Integer;
  BinPath: String;
begin
  // sc.exe must see one argument "C:\...\x.exe" with the quotes kept in the
  // stored path: binPath= "\"path\"". The old binPath= ""path"" split the path
  // at its first space, so the service was never created.
  BinPath := '"\"' + ExpandConstant('{app}\{#KsServiceExe}') + '\""';
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'create {#KsServiceName} binPath= ' + BinPath + ' start= auto obj= LocalSystem DisplayName= "Colitu VPN kill switch"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    // 1073 = the service exists (upgrade): update its path and start type instead.
    Exec(ExpandConstant('{sys}\sc.exe'), 'config {#KsServiceName} binPath= ' + BinPath + ' start= auto obj= LocalSystem', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  Exec(ExpandConstant('{sys}\sc.exe'), 'description {#KsServiceName} "Keeps traffic from leaving outside Colitu VPN while the kill switch is on, also if the app or the VPN core stops unexpectedly."', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure {#KsServiceName} reset= 86400 actions= restart/2000/restart/5000/restart/30000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#KsServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Again after the copy (files inherit the folder's ACL; this keeps the old behaviour as a backstop).
    RunIcacls('"' + ExpandConstant('{app}') + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX');
    // After the folder is locked down: the service runs as LocalSystem from here.
    InstallKillSwitchService();
  end;
end;
