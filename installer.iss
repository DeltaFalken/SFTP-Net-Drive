#define AppName "SFTP Net Drive"
#define AppPublisher "DeltaFalken"

#ifndef SourceExe
  #define SourceExe "dist\win-x64\SftpNetDrive.exe"
#endif
#ifndef SourceNPDll
  #define SourceNPDll "dist\win-x64\SftpNetDriveNP.dll"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "SFTP-Net-Drive-Setup-win-x64"
#endif
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef Arch
  #define Arch "x64os"
#endif

[Setup]
AppId={{SFTP-Net-Drive-12345678-1234-1234-1234-123456789ABC}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/DeltaFalken/SFTP-Net-Drive
AppSupportURL=https://github.com/DeltaFalken/SFTP-Net-Drive/issues
AppUpdatesURL=https://github.com/DeltaFalken/SFTP-Net-Drive/releases
DefaultDirName={autopf}\SFTP Net Drive
DefaultGroupName=SFTP Net Drive
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
UninstallDisplayIcon={app}\SftpNetDrive.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=SftpNetDrive.exe
RestartApplications=no
; A reboot is required for the Network Provider to be picked up by the MPR.
AlwaysRestart=yes

[Languages]
Name: english; MessagesFile: "compiler:Default.isl"
Name: german; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: autostart;   Description: "SFTP Net Drive automatisch mit Windows starten"; GroupDescription: "Startoptionen:"

[Registry]
; App Paths — makes the EXE findable by name from Run dialog / shell.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SftpNetDrive.exe"; ValueType: string; ValueName: "";     ValueData: "{app}\SftpNetDrive.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SftpNetDrive.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

; Windows Network Provider registration — tells the MPR that
; SftpNetDriveNP.dll handles \\SftpNetDrive\... UNC paths.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\SftpNetDriveNP\NetworkProvider"; ValueType: string;  ValueName: "Name";         ValueData: "SFTP Net Drive";               Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\SftpNetDriveNP\NetworkProvider"; ValueType: string;  ValueName: "ProviderPath"; ValueData: "{app}\SftpNetDriveNP.dll"

[Files]
Source: "{#SourceExe}";    DestDir: "{app}"; DestName: "SftpNetDrive.exe";    Flags: ignoreversion
Source: "{#SourceNPDll}";  DestDir: "{app}"; DestName: "SftpNetDriveNP.dll";  Flags: ignoreversion restartreplace uninsrestartdelete

[Icons]
Name: "{group}\{#AppName}";                    Filename: "{app}\SftpNetDrive.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}";            Filename: "{app}\SftpNetDrive.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SftpNetDrive.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]

// ── Helpers ────────────────────────────────────────────────────────────────────

// Ensure SftpNetDriveNP is FIRST in ProviderOrder so Dokan/WinFsp cannot
// intercept our \\SftpNetDrive\... paths before us.
procedure AddToProviderOrder();
var
  Key, Current: String;
begin
  Key := 'SYSTEM\CurrentControlSet\Control\NetworkProvider\Order';
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, Key, 'ProviderOrder', Current) then
    Current := '';
  // Remove any existing entry (may be at wrong position from a previous install)
  StringChangeEx(Current, ',SftpNetDriveNP', '', True);
  StringChangeEx(Current, 'SftpNetDriveNP,', '', True);
  StringChangeEx(Current, 'SftpNetDriveNP',  '', True);
  // Strip stray commas
  while (Length(Current) > 0) and (Current[1] = ',') do Delete(Current, 1, 1);
  while (Length(Current) > 0) and (Current[Length(Current)] = ',') do Delete(Current, Length(Current), 1);
  // Insert at front
  if Current <> '' then
    Current := 'SftpNetDriveNP,' + Current
  else
    Current := 'SftpNetDriveNP';
  RegWriteStringValue(HKEY_LOCAL_MACHINE, Key, 'ProviderOrder', Current);
end;

// Remove SftpNetDriveNP from the ProviderOrder list.
procedure RemoveFromProviderOrder();
var
  Key, Current: String;
begin
  Key := 'SYSTEM\CurrentControlSet\Control\NetworkProvider\Order';
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, Key, 'ProviderOrder', Current) then
    Exit;
  // Remove all three possible positions: middle, end, start
  StringChangeEx(Current, ',SftpNetDriveNP', '', True);
  StringChangeEx(Current, 'SftpNetDriveNP,', '', True);
  StringChangeEx(Current, 'SftpNetDriveNP',  '', True);
  RegWriteStringValue(HKEY_LOCAL_MACHINE, Key, 'ProviderOrder', Current);
end;

// ── Install steps ──────────────────────────────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Delete the legacy task (no UserId restriction) from older installs.
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "SftpNetDrive" /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Register the Network Provider in ProviderOrder.
    AddToProviderOrder();

    // Register per-user startup task if the user selected that option.
    if WizardIsTaskSelected('autostart') then
    begin
      ExePath := ExpandConstant('{app}\SftpNetDrive.exe');
      Exec(ExePath, '--register-startup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

// ── Uninstall steps ────────────────────────────────────────────────────────────

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExePath: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Terminate running instance so file handles are released.
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SftpNetDrive.exe /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);

    // Remove the per-user startup task (correct SID lookup via the app itself).
    ExePath := ExpandConstant('{app}\SftpNetDrive.exe');
    if FileExists(ExePath) then
      Exec(ExePath, '--unregister-startup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Remove legacy generic task name.
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "SftpNetDrive" /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);

    RegDeleteValue(HKEY_CURRENT_USER,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'SftpNetDrive');

    // Remove the Network Provider from ProviderOrder.
    RemoveFromProviderOrder();
  end;
end;
