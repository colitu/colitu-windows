#define MyAppName "Colitu VPN"
#define MyAppExeName "ColituVPN.exe"
#define MyAppPublisher "Colitu"
#define MyAppURL "https://colitu.com"

#if GetEnv("COLITU_APP_VERSION") != ""
  #define MyAppVersion GetEnv("COLITU_APP_VERSION")
#else
  #define MyAppVersion "2.4.0"
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
SetupIconFile=..\v2rayN\v2rayN\Resources\colitu.ico
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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillColituVPN"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
