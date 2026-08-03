#define MyAppName "Hercules Wave Bridge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Sam-D03"
#define MyAppURL "https://github.com/Sam-D03/hercules-wave-link-bridge"
#define MyAppExeName "HerculesWaveBridge.exe"

[Setup]
AppId={{6D9998EF-D93E-4DC7-92B7-1A3D19E8EB94}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=Hercules-Wave-Bridge-Setup
SetupIconFile=..\assets\hercules-wave-bridge.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
LicenseFile=..\LICENSE
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
AppMutex=Local\HerculesWaveBridge
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Install {#MyAppName}
VersionInfoProductName={#MyAppName}

[Files]
Source: "..\artifacts\app\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Hercules Wave Bridge"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  HerculesDownloadUrl = 'https://www.hercules.com/en/stream-control/';

function HerculesRuntimeInstalled: Boolean;
var
  SdkRoot: String;
begin
  SdkRoot := ExpandConstant('{pf64}\Hercules\HSM Series\sdk');
  Result := FileExists(SdkRoot + '\hsm_api_core_x64.dll') and
            FileExists(SdkRoot + '\tlusbapi_x64.dll');
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HerculesRuntimeInstalled;
  if Result then
    Exit;

  if MsgBox(
       'Hercules Wave Bridge needs the driver and display runtime installed by the official Hercules Stream Control software.' + #13#10 + #13#10 +
       'Open the Hercules download page now?',
       mbError,
       MB_YESNO) = IDYES then
  begin
    ShellExec('open', HerculesDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
