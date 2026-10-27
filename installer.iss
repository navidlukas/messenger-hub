; Inno Setup script for Messenger Hub.
; Produces MessengerHub-Setup.exe, which installs the app into a user-writable
; folder (no admin) and makes shortcuts. Files it writes do NOT carry the
; "downloaded from the internet" mark, so MessengerHub.exe runs without a
; SmartScreen warning after install. The version is passed in from CI:
;   ISCC.exe /DMyAppVersion=0.9.0 installer.iss

#define MyAppName "Messenger Hub"
#define MyAppExeName "MessengerHub.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{8F3A1C2E-7B94-4D6A-9E15-2C0A4B8D6F71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=navidlukas
AppPublisherURL=https://github.com/navidlukas/messenger-hub

; Install per-user, no administrator rights needed. Program Files is avoided on
; purpose: the app must be able to write its data\ folder next to the exe.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\MessengerHub
DisableProgramGroupPage=yes
DefaultGroupName=Messenger Hub
UninstallDisplayIcon={app}\{#MyAppExeName}

; If the app (or its tray) is running during an upgrade, close it first.
CloseApplications=yes
RestartApplications=no

OutputDir=dist
OutputBaseFilename=MessengerHub-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; Everything the build produced. data\ is created at runtime and is never shipped,
; so a reinstall/upgrade keeps the user's logins and settings untouched.
Source: "dist\MessengerHub\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Messenger Hub"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Messenger Hub"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Messenger Hub"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Messenger Hub"; Flags: nowait postinstall skipifsilent
