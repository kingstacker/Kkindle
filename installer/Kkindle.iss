#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef MyNumericVersion
  #define MyNumericVersion "0.1.0.0"
#endif

#ifndef SourceDir
  #error SourceDir must point to the dotnet publish directory
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#define MyAppName "Kkindle"
#define MyAppPublisher "kingstacker"
#define MyAppUrl "https://github.com/kingstacker/Kkindle"
#define MyAppExeName "Kkindle.exe"

[Setup]
AppId={{83D8903A-0C75-49D4-A4B7-FA64E2E94B99}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile={#SourceDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Kkindle-{#MyAppVersion}-win-x64-setup
SetupIconFile={#SourceDir}\Assets\Kkindle.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.19041
VersionInfoVersion={#MyNumericVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Kkindle Windows installer

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*;backups\*;app-root.json"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Flags: nowait skipifdoesntexist; Check: WizardSilent
