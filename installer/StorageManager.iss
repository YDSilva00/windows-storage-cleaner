; ─────────────────────────────────────────────────────────────────────────────
;  Storage Manager - Inno Setup script
;  Build with:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
;  (the script publishes the app first, then calls ISCC with the right defines)
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName        "Storage Manager"
#define AppPublisher   "YDSilva00"
#define AppURL         "https://github.com/YDSilva00/windows-storage-cleaner"
#define AppExeName     "StorageManager.exe"

[Setup]
; Never change AppId after the first public release - it identifies the app for upgrades/uninstall.
AppId={{6F3B9C2A-8E41-4D7A-9B5E-2C1F7A8D4E63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppComments=Find what is filling your drive and clean junk safely.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; Install for all users (admin) by default, but let the user pick "only for me" (no admin needed).
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit only (the app is published for win-x64).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\dist
OutputBaseFilename=StorageManager-Setup-{#AppVersion}-x64
SetupIconFile=..\src\StorageManager\Assets\app.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Close a running copy of the app before upgrading / uninstalling.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion

[InstallDelete]
; Remove files from an older version so stale DLLs never linger after an upgrade.
Type: filesandordirs; Name: "{app}\*.dll"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Find what is filling your drive"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
