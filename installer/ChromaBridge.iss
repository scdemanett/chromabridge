; Inno Setup 6 script for ChromaBridge.
; Build: run tools\publish.ps1, then compile this file with ISCC.exe.

#define AppName "ChromaBridge"
#define AppVersion "0.1.0"
#define TaskName "ChromaBridge Helper"

[Setup]
AppId={{6F0C1B7E-3C7E-4D0B-9B6C-5E7C2B5E8A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ChromaBridge contributors
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=ChromaBridge-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
; The helper's logon task and the Program Files folder both need an administrator
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\ChromaBridge.exe
SetupIconFile=..\src\ChromaBridge.ico
; "dynamic" follows the Windows light or dark app theme (Inno Setup 6.6 or later)
WizardStyle=modern dynamic

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ChromaBridge.exe"

[Run]
; Create the logon task for the helper, start it through the task so it runs elevated in the
; user's session, and have the tray app start at login for the person installing.
Filename: "{app}\ChromaBridge.Service.exe"; Parameters: "--register-startup"; Flags: runhidden waituntilterminated; StatusMsg: "Setting ChromaBridge to start at login..."
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#TaskName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting the ChromaBridge helper..."
Filename: "{sys}\reg.exe"; Parameters: "add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v {#AppName} /t REG_SZ /d ""\""{app}\ChromaBridge.exe\"" --tray"" /f"; Flags: runhidden runasoriginaluser
Filename: "{app}\ChromaBridge.exe"; Description: "Open {#AppName}"; Flags: postinstall nowait runasoriginaluser skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ChromaBridge.exe /F"; Flags: runhidden; RunOnceId: "StopTray"
Filename: "{app}\ChromaBridge.Service.exe"; Parameters: "--stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopHelper"
Filename: "{app}\ChromaBridge.Service.exe"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v {#AppName} /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"

[Code]
// Before files are replaced on an upgrade: close the tray app and ask the helper to stop,
// which hands lighting control back and releases the files.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Helper: String;
begin
  Result := '';
  Helper := ExpandConstant('{app}\ChromaBridge.Service.exe');

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ChromaBridge.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if FileExists(Helper) then
  begin
    Exec(Helper, '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // The helper waits for its provider hosts to release their devices
    Sleep(6000);
  end;
end;
