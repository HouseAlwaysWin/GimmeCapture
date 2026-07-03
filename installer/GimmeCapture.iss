; Inno Setup script for GimmeCapture — a Windows install wizard that lets the user choose the install
; drive/folder, creates Start-Menu (and optional desktop) shortcuts, and registers an uninstaller /
; Add-or-Remove-Programs entry. The zip artifact + in-app GitHub auto-updater are unchanged.
;
; Built by scripts/build-installer.ps1 (locally) and by .github/workflows/release.yml (CI) with:
;   ISCC.exe /DAppVersion=<x.y.z> /DSourceDir=<publish folder> installer\GimmeCapture.iss
; Requires Inno Setup 6.3+ (for the x64compatible architecture identifier).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  ; Default assumes a `publish` folder at the repo root next to this `installer` folder.
  #define SourceDir "..\publish"
#endif

#define AppName "GimmeCapture"
#define AppPublisher "HouseAlwaysWin"
#define AppExeName "GimmeCapture.exe"
#define AppUrl "https://github.com/HouseAlwaysWin/GimmeCapture"

[Setup]
; Stable app identity — used for upgrades/uninstall. Do NOT change once shipped.
AppId={{B24B0000-0DA3-4899-A1B6-6B027E07DC76}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

; Per-user by default (no UAC prompt); the user may switch to all-users (admin) on the first page. A
; per-user install keeps the app in a user-writable folder so the in-app updater can replace files without
; elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Show the "Select Destination Location" page so the user can pick the install drive/folder.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=output
OutputBaseFilename=GimmeCapture_Setup_{#AppVersion}
SetupIconFile=..\src\GimmeCapture\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running GimmeCapture before overwriting (e.g. reinstall / manual upgrade).
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish output (single-file GimmeCapture.exe + ffmpeg-lib\*.dll, which FFmpegRuntime loads from
; next to the exe). recursesubdirs/createallsubdirs keeps the ffmpeg-lib subfolder.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
