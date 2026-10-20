#define AppName "Nagster"
#define AppVersion "0.3.1"
#define AppExe "Nagster.exe"

[Setup]
AppId={{A654C8AD-6AB0-4200-A2B5-F36789499412}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DG Utilities
DefaultDirName={localappdata}\Programs\Nagster
DefaultGroupName={#AppName}
; Installs made before the rename live under "DG Nagging Alarms"; move them to the new folder and group.
UsePreviousAppDir=no
UsePreviousGroup=no
OutputDir=..\dist
OutputBaseFilename=Nagster-Setup-{#AppVersion}
SetupIconFile=..\Nagster\Assets\Alarm.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern

[Files]
; The publish is a single self-contained exe; naming it keeps stale files in publish\ out of the installer.
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\New alarm"; Filename: "{app}\{#AppExe}"; Parameters: "-new"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Quick reminder"; Filename: "{app}\{#AppExe}"; Parameters: "-quick"; IconFilename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "-tray"; Description: "Start Nagster"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Leftovers from DG Nagging Alarms, the app's name before it became Nagster.
Type: filesandordirs; Name: "{localappdata}\Programs\DG Nagging Alarms"
Type: filesandordirs; Name: "{userprograms}\DG Nagging Alarms"
Type: files; Name: "{userdesktop}\DG Nagging Alarms.lnk"

[UninstallDelete]
Type: files; Name: "{app}\*.tmp"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Nagster"; ValueData: """{app}\{#AppExe}"" -tray"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DGNaggingAlarms"; Flags: deletevalue

[Code]
// The pre-rename DGNaggingAlarms.exe isn't one of the files being replaced, so Setup won't offer to close it;
// stop it so its folder can be removed and it doesn't run alongside Nagster.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DGNaggingAlarms.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
