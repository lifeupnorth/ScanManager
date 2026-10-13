; Inno Setup script for ScanManager. Build it with installer\build.ps1, which publishes the
; app and passes in the version, the target architecture and the published folder.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error SourceDir must point at the published app (use installer\build.ps1)
#endif

#define AppName "ScanManager"
#define AppExe "ScanManager.exe"
#define AppUrl "https://github.com/lifeupnorth/ScanManager"

[Setup]
; Never change AppId: Windows uses it to recognize upgrades and the uninstall entry.
AppId={{98D74232-B007-4F2A-BCE0-7B1A35871FF8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user install by default (no admin prompt); the first page lets the user switch to all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Windows 10 1809 or later, like .NET 10.
MinVersion=10.0.17763

; Close a running ScanManager before replacing its files.
CloseApplications=yes
RestartApplications=no

WizardStyle=modern
; The license page: the MIT License text, which the user must accept to install.
LicenseFile=..\LICENSE
SetupIconFile=..\src\ScanManager.App\Assets\ScanManager.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=ScanManager-{#AppVersion}-{#Arch}-setup
#ifdef Sign
; build.ps1 defines Sign and passes the "signtool" command when code signing is configured.
SignTool=signtool
SignedUninstaller=yes
#endif
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
