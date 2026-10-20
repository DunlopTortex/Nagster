#define AppName "DG Nagging Alarms"
#define AppVersion "0.1.0"
#define AppExe "DGNaggingAlarms.exe"

[Setup]
AppId={{A654C8AD-6AB0-4200-A2B5-F36789499412}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DG Utilities
DefaultDirName={localappdata}\Programs\DG Nagging Alarms
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=DGNaggingAlarms-Setup-{#AppVersion}
SetupIconFile=..\DGNaggingAlarms\Assets\Alarm.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\New alarm"; Filename: "{app}\{#AppExe}"; Parameters: "-new"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Quick reminder"; Filename: "{app}\{#AppExe}"; Parameters: "-quick"; IconFilename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "-tray"; Description: "Start DG Nagging Alarms"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.tmp"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DGNaggingAlarms"; ValueData: """{app}\{#AppExe}"" -tray"; Flags: uninsdeletevalue
