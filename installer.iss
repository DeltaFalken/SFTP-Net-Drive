#define AppName "SFTP Net Drive"
#define AppPublisher "DeltaFalken"

#ifndef SourceExe
  #define SourceExe "dist\win-x64\SftpNetDrive.exe"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "SFTP-Net-Drive-Setup-win-x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.1.0"
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

[Languages]
Name: english; MessagesFile: "compiler:Default.isl"
Name: german; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: autostart;   Description: "SFTP Net Drive automatisch mit Windows starten"; GroupDescription: "Startoptionen:"

[Registry]
; App Paths — makes the exe findable by name from the Run dialog / shell
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SftpNetDrive.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\SftpNetDrive.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SftpNetDrive.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "SftpNetDrive.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\SftpNetDrive.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\SftpNetDrive.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SftpNetDrive.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('autostart') then
    begin
      ExePath := ExpandConstant('{app}\SftpNetDrive.exe');
      // Use \" around the exe path so schtasks handles spaces in Program Files correctly.
      // Resulting /TR value seen by Task Scheduler: "C:\...\SftpNetDrive.exe" --autostart
      Exec(ExpandConstant('{sys}\schtasks.exe'),
        '/Create /TN "SftpNetDrive" /TR "\"' + ExePath + '\" --autostart" /SC ONLOGON /RL HIGHEST /F',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Terminate any running instance so all file handles are released before deletion.
    // Dokan unmounts drives automatically when its client process exits.
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SftpNetDrive.exe /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);

    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "SftpNetDrive" /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKEY_CURRENT_USER,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'SftpNetDrive');
  end;
end;
