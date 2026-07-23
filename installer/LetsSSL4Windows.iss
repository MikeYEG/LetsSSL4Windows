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

; The installer ships the framework-dependent build (a few MB instead of ~150 MB)
; and installs the .NET 8 Desktop Runtime on demand if it's missing (see [Code]).
; Microsoft channel link — always resolves to the latest 8.0.x desktop runtime:
#define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

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
; name so shortcuts, the service, and self-relaunch keep working. This is the
; framework-dependent build (published into build\publish-fd by the release
; workflow); the runtime it needs is ensured by the [Code] section below.
Source: "..\build\publish-fd\LetsSSL4Windows-{#AppVersion}.exe"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion

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

[Code]
{ The app is framework-dependent, so it needs the .NET 8 Desktop Runtime (x64).
  If it isn't already present, download Microsoft's runtime installer and run it
  silently before the app's files are laid down. }

var
  DownloadPage: TDownloadWizardPage;

{ True if any Microsoft.WindowsDesktop.App 8.x runtime is installed under the
  64-bit Program Files\dotnet share (where the x64 desktop runtime lives). }
function IsDotNetDesktop8Installed(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  RuntimeInstaller: String;
begin
  Result := True;
  { Ensure the runtime once the user commits on the Ready page, before install. }
  if (CurPageID = wpReady) and (not IsDotNetDesktop8Installed()) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#DotNetRuntimeUrl}', 'windowsdesktop-runtime-8-win-x64.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('.NET Desktop Runtime download aborted by the user.')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;

      RuntimeInstaller := ExpandConstant('{tmp}\windowsdesktop-runtime-8-win-x64.exe');
      if Exec(RuntimeInstaller, '/install /quiet /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      begin
        { 0 = success, 3010 = success but a reboot is required. }
        if (ResultCode <> 0) and (ResultCode <> 3010) then
        begin
          MsgBox(Format('The .NET Desktop Runtime installer exited with code %d, so setup cannot continue.', [ResultCode]), mbCriticalError, MB_OK);
          Result := False;
        end;
      end
      else
      begin
        MsgBox('The .NET Desktop Runtime installer could not be started, so setup cannot continue.', mbCriticalError, MB_OK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
