; The version will be set by the build script
#ifndef MyAppVersion
  #define MyAppVersion "v1.0.0"
#endif

; Extract numeric version for VersionInfoVersion (remove 'v' and any suffix after a dash)
#define MyAppVersionWithoutV StringChange(MyAppVersion, "v", "")
#define DashPos Pos("-", MyAppVersionWithoutV)
#define MyAppNumericVersion (DashPos > 0) ? Copy(MyAppVersionWithoutV, 1, DashPos - 1) : MyAppVersionWithoutV

#define MyAppPublisher "w4po"
#define MyAppName "ExplorerTabUtility"
#define MyAppExeName MyAppName + ".exe"
#define MyAppRelativePath MyAppName + "\" + MyAppExeName
#define MyAppURL "https://github.com/w4po/ExplorerTabUtility"
#define DesktopRuntimeVersion "10.0"

; Source directory for build artifacts
#ifndef SourceDir
  #define SourceDir "..\artifacts"
#endif

[Setup]
AppId={{E1F2A3C4-F5D4-3B2E-1D5A-8F7B2F8D3E7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={userpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
OutputDir={#SourceDir}
OutputBaseFilename={#MyAppName}_{#MyAppVersion}_Setup
SetupIconFile=..\{#MyAppName}\Icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64os arm64
ArchitecturesInstallIn64BitMode=x64os arm64
UninstallDisplayIcon={app}\{#MyAppRelativePath}
UninstallDisplayName={#MyAppName}
; Older installers recorded a wildcard removal of the entire installation directory.
UninstallLogMode=overwrite
CloseApplications=yes
RestartApplications=no

; Add version information to the installer
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoTextVersion={#MyAppVersion}
VersionInfoCopyright= {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppNumericVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "Windows Integration"; Flags: unchecked

[Files]
Source: "..\LICENSE"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion
Source: "{#SourceDir}\{#MyAppName}_{#MyAppVersion}_x64_Net{#DesktopRuntimeVersion}_FrameworkDependent\{#MyAppName}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UsePayload('x64', True)
Source: "{#SourceDir}\{#MyAppName}_{#MyAppVersion}_arm64_Net{#DesktopRuntimeVersion}_FrameworkDependent\{#MyAppName}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UsePayload('arm64', True)
Source: "{#SourceDir}\{#MyAppName}_{#MyAppVersion}_x64_NetFW4.8.1\{#MyAppName}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UsePayload('x64', False)
Source: "{#SourceDir}\{#MyAppName}_{#MyAppVersion}_arm64_NetFW4.8.1\{#MyAppName}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UsePayload('arm64', False)

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppRelativePath}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppRelativePath}"; Tasks: desktopicon

[Registry]
; Add to startup if the startup task is selected
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "{app}\{#MyAppRelativePath}"; Flags: uninsdeletevalue; Tasks: startupicon
; Set StartupApproved registry entry (enabled = 02 00 00 00 00 00 00 00 00 00 00 00)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "{#MyAppName}"; ValueData: 02 00 00 00 00 00 00 00 00 00 00 00; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; Launch the application after installation if selected
Filename: "{app}\{#MyAppRelativePath}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PayloadArchitecture: String;
  DesktopRuntimeInstalled: Boolean;

function HasRuntime(const Root, Framework, Manifest: String): Boolean;
var
  Entry: TFindRec;
  Shared: String;
begin
  Result := False;
  Shared := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Shared + '{#DesktopRuntimeVersion}.*', Entry) then
  try
    repeat
      if (Pos('-', Entry.Name) = 0) and FileExists(Shared + Entry.Name + '\' + Manifest) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

procedure InitializeWizard;
var
  DotNetRoot: String;
begin
  if ProcessorArchitecture = paARM64 then
    PayloadArchitecture := 'arm64'
  else
    PayloadArchitecture := 'x64';

  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\' + PayloadArchitecture,
    'InstallLocation', DotNetRoot) then
    DotNetRoot := ExpandConstant('{commonpf}\dotnet');

  DesktopRuntimeInstalled := FileExists(AddBackslash(DotNetRoot) + 'dotnet.exe') and
    HasRuntime(DotNetRoot, 'Microsoft.NETCore.App', 'Microsoft.NETCore.App.deps.json') and
    HasRuntime(DotNetRoot, 'Microsoft.WindowsDesktop.App', 'Microsoft.WindowsDesktop.App.deps.json');
end;

function UsePayload(const Architecture: String; ModernRuntime: Boolean): Boolean;
begin
  Result := (Architecture = PayloadArchitecture) and (ModernRuntime = DesktopRuntimeInstalled);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', '{#MyAppName}');
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
