; Mochi setup. Build with:  installer\build.ps1
#define AppName "Mochi"
#define AppVersion "1.1.4"

[Setup]
AppId={{6F1C2B7A-4D0E-4B7E-9C5A-2A7E0C4D9B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Ayush Kumar Patel
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; per-user install by default: no administrator prompt; the user can still pick "all users"
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Mochi-Setup-{#AppVersion}
SetupIconFile=..\Mochi\Assets\Icons\mochi.ico
UninstallDisplayIcon={app}\Mochi.exe
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "contextmenu"; Description: "Add ""Convert with Mochi"" to the Explorer right-click menu"; GroupDescription: "Explorer:"
Name: "autostart"; Description: "Start Mochi in the background with Windows (Shift-drag in Explorer opens the donut)"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\app\Mochi.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Classes\*\shell\Mochi"; ValueType: string; ValueName: ""; ValueData: "Convert with Mochi"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Mochi"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Mochi.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Mochi\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mochi.exe"" --donut ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Mochi"; ValueType: string; ValueName: ""; ValueData: "Convert with Mochi"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Mochi"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Mochi.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Mochi\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mochi.exe"" --donut ""%1"""; Tasks: contextmenu

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Mochi.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Mochi.exe"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\Mochi.exe"; Parameters: "--background"; Tasks: autostart

[Run]
Filename: "{app}\Mochi.exe"; Parameters: "--background"; Description: "Start Mochi in the background now"; Flags: nowait postinstall skipifsilent
