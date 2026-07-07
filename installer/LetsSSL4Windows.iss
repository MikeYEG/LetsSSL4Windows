; Inno Setup script for LetsSSL4Windows (single multi-mode executable).
; Build with: ISCC.exe /DAppVersion=1.2.3 installer\LetsSSL4Windows.iss
; (or run build\build-installer.ps1, which publishes then invokes ISCC).
; Requires the file to be published first into build\publish (see build\publish.ps1).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "LetsSSL4Windows"
#define AppPublisher "MikeYEG"
#define AppUrl "https://github.com/MikeYEG/LetsSSL4Windows"
#define AppExe "LetsSSL4Windows.exe"

[Setup]
AppId={{B3F1B0C2-7E4A-4C9D-9A2E-5B6D2F8A1C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\build\installer-output
OutputBaseFilename=LetsSSL4Windows-Setup-{#AppVersion}
; Stamp the version onto the generated Setup.exe (Explorer > Details) and use
; the app icon for the installer.
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
SetupIconFile=..\src\LetsSSL.App\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installing files to Program Files and registering the service needs elevation.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "installservice"; Description: "Install and start the renewal service (recommended, always-on)"; GroupDescription: "Automatic renewal:"
Name: "trayatlogin"; Description: "Start the system-tray companion at login"; GroupDescription: "System tray:"; Flags: unchecked

[Files]
; publish.ps1 names the file with the version; install it under the canonical
; name so shortcuts, the service, and self-relaunch keep working.
Source: "..\build\publish\LetsSSL4Windows-{#AppVersion}.exe"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} (System Tray)"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Launch the tray for the current user at login (optional task).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "LetsSSL4WindowsTray"; ValueData: """{app}\{#AppExe}"" --tray"; \
  Flags: uninsdeletevalue; Tasks: trayatlogin

[Run]
; Register + start the renewal Windows Service (if selected).
Filename: "{app}\{#AppExe}"; Parameters: "--install-service"; \
  StatusMsg: "Installing the renewal service..."; \
  Flags: runhidden waituntilterminated; Tasks: installservice
; Offer to launch the app at the end.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Description: "Start the system-tray companion now"; \
  Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; Stop and remove the service before files are deleted (no-op if not installed).
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-service"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveRenewalService"
