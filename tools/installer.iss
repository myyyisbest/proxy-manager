; Build with tools/release.ps1. UTF-8 source, Windows x64.
#define MyAppName "应用代理管理器"
#ifndef MyAppVersion
  #define MyAppVersion "1.4.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\payload"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\release"
#endif

[Setup]
AppId={code:InstallationId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=ProxyManager
DefaultDirName={autopf}\ProxyManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=ProxyManager-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\ProxyManager\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\ProxyManager.exe
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes
ChangesAssociations=no
UsePreviousAppDir=yes
UsePreviousLanguage=no

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\ProxyManager.exe"; Check: not VerificationMode
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ProxyManager.exe"; Tasks: desktopicon; Check: not VerificationMode

[Run]
Filename: "{app}\ProxyManager.exe"; Description: "启动{#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked; Check: not VerificationMode

[UninstallRun]
Filename: "{app}\ProxyManager.exe"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopOwnedApplication"
Filename: "{app}\ProxyManager.exe"; Parameters: "--disable-autostart"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedAutostart"

[Code]
function VerificationMode(): Boolean;
begin
  Result := ExpandConstant('{param:VERIFY|0}') = '1';
end;

function InstallationId(Param: String): String;
begin
  Result := '{7A1C52E4-9B3D-4F8E-A6C1-2D5E8B4F9A13}';
  if VerificationMode() then
    Result := Result + '-Verification';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExePath: String;
  VersionMS, VersionLS: Cardinal;
  ResultCode: Integer;
begin
  Result := '';
  ExePath := ExpandConstant('{app}\ProxyManager.exe');
  { Restart Manager tracks files only in this install directory. }
  if FileExists(ExePath) and GetVersionNumbers(ExePath, VersionMS, VersionLS) then
  begin
    if VersionMS >= $00010004 then
      Exec(ExePath, '--shutdown', ExpandConstant('{app}'), SW_HIDE,
        ewWaitUntilTerminated, ResultCode);
  end;
end;
