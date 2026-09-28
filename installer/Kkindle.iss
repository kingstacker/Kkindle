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
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; ChineseSimplified.isl is vendored because Inno Setup does not bundle this translation.
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
english.StopMcpBeforeUpgradeFailed=Setup could not stop the Kkindle MCP server from this installation. Close the AI client connected to Kkindle, then try again.
chinese.StopMcpBeforeUpgradeFailed=安装程序无法结束当前安装目录中的 Kkindle MCP 进程。请退出使用 Kkindle MCP 的 AI 客户端后重试。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*;backups\*;browser-data\*;app-root.json"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; The in-app updater runs the installer silently after the user confirms the
; pending update. Start the newly installed version when that installer exits.
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Flags: nowait skipifdoesntexist; Check: WizardSilent

[UninstallRun]
; The application deliberately excludes these paths from [Files] so an
; upgrade preserves the library. Clean them only during a real uninstall.
Filename: "{app}\{#MyAppExeName}"; Parameters: "/cleanup-uninstall"; Flags: runhidden waituntilterminated; Check: FileExists(ExpandConstant('{app}\{#MyAppExeName}'))

[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\backups"
Type: filesandordirs; Name: "{app}\browser-data"
Type: files; Name: "{app}\.kkindle-migration.kkindle"
Type: files; Name: "{app}\app-root.json"
Type: files; Name: "{app}\app-root.json.tmp"
Type: files; Name: "{app}\kkindle-crash.log"

[Code]
function StopInstalledMcpServer: String;
var
  PowerShellPath: String;
  ScriptPath: String;
  TargetPath: String;
  Script: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := '';
  TargetPath := ExpandConstant('{app}\mcp\Kkindle.McpServer.exe');
  if not FileExists(TargetPath) then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
  begin
    Result := CustomMessage('StopMcpBeforeUpgradeFailed');
    Exit;
  end;

  ScriptPath := ExpandConstant('{tmp}\Kkindle-StopMcp.ps1');
  Script :=
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$targetPath = [System.IO.Path]::GetFullPath($args[0])' + #13#10 +
    '$deadline = [DateTime]::UtcNow.AddSeconds(10)' + #13#10 +
    'do {' + #13#10 +
    '  $allProcesses = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = ''Kkindle.McpServer.exe''")' + #13#10 +
    '  $matchingProcesses = @()' + #13#10 +
    '  foreach ($process in $allProcesses) {' + #13#10 +
    '    if (-not $process.ExecutablePath) { Write-Error ''Cannot inspect a Kkindle MCP process path.''; exit 2 }' + #13#10 +
    '    if ([System.String]::Equals([System.IO.Path]::GetFullPath($process.ExecutablePath), $targetPath, [System.StringComparison]::OrdinalIgnoreCase)) {' + #13#10 +
    '      $matchingProcesses += $process' + #13#10 +
    '    }' + #13#10 +
    '  }' + #13#10 +
    '  if ($matchingProcesses.Count -eq 0) { exit 0 }' + #13#10 +
    '  foreach ($process in $matchingProcesses) {' + #13#10 +
    '    try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop } catch {}' + #13#10 +
    '  }' + #13#10 +
    '  Start-Sleep -Milliseconds 200' + #13#10 +
    '} while ([DateTime]::UtcNow -lt $deadline)' + #13#10 +
    'Write-Error ''Timed out while stopping the Kkindle MCP server.''' + #13#10 +
    'exit 1' + #13#10;

  if not SaveStringToFile(ScriptPath, Script, False) then
  begin
    Result := CustomMessage('StopMcpBeforeUpgradeFailed');
    Exit;
  end;

  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ScriptPath + '" "' + TargetPath + '"';
  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := CustomMessage('StopMcpBeforeUpgradeFailed')
  else if ResultCode <> 0 then
    Result := CustomMessage('StopMcpBeforeUpgradeFailed');

  DeleteFile(ScriptPath);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := StopInstalledMcpServer;
end;
