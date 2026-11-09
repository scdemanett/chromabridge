; Inno Setup 6 script for ChromaBridge.
; Build: run tools\build-installer.ps1 (add -Sign for a release). It compiles this script once
; per edition: "full" ships the third-party archives in bundles.json inside the install folder,
; "lite" leaves them for the helper to download on first use.

#define AppName "ChromaBridge"
; The version and edition are passed in by tools\build-installer.ps1
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Edition
  #define Edition "full"
#endif
#define TaskName "ChromaBridge Helper"

[Setup]
AppId={{6F0C1B7E-3C7E-4D0B-9B6C-5E7C2B5E8A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Steven Demanett
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=ChromaBridge-{#AppVersion}-{#Edition}-setup
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
; Corsair's SDK library is never shipped loose: the helper unpacks it from Corsair's archive
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "iCUESDK.x64_2019.dll"; Flags: recursesubdirs ignoreversion
#if Edition == "full"
Source: "..\artifacts\bundled\*"; DestDir: "{app}\bundled"; Flags: ignoreversion
#endif

[InstallDelete]
; Releases before 0.1.4 shipped Corsair's library loose in the install folder
Type: files; Name: "{app}\iCUESDK.x64_2019.dll"
; Bundled archives of an earlier version are not wanted next to the new ones
Type: filesandordirs; Name: "{app}\bundled"

[UninstallDelete]
Type: files; Name: "{app}\edition.txt"

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
; Removes the logon task and the tray app's Run entry for the logged-in user (the hive the add
; above wrote to, which is not the uninstaller's own when another account elevated it)
Filename: "{app}\ChromaBridge.Service.exe"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[Code]
// True while the helper or a host process is still running. tasklist is the one process
// listing available here; its output goes through a file.
function HelperRunning(): Boolean;
var
  ResultCode: Integer;
  ListFile: String;
  Output: AnsiString;
begin
  ListFile := ExpandConstant('{tmp}\tasks.txt');
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FO CSV /NH > "' + ListFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
  if LoadStringFromFile(ListFile, Output) then
    Result := (Pos('"ChromaBridge.Service.exe"', Output) > 0) or (Pos('"ChromaBridge.Host.exe"', Output) > 0);
end;

// Before files are replaced on an upgrade: close the tray app and ask the helper to stop,
// which hands lighting control back and releases the files. The helper waits for its hosts
// to release their devices, which takes seconds and varies, so wait for the processes to
// be gone rather than a fixed time; a silent install that finds a file in use just aborts.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, Waited: Integer;
  Helper: String;
begin
  Result := '';
  Helper := ExpandConstant('{app}\ChromaBridge.Service.exe');

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ChromaBridge.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if FileExists(Helper) then
  begin
    Exec(Helper, '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    Waited := 0;
    while HelperRunning() and (Waited < 45000) do
    begin
      Sleep(1000);
      Waited := Waited + 1000;
    end;

    // Still there after 45 s: a host stuck in a vendor SDK. End it rather than fail the install.
    if HelperRunning() then
    begin
      Log('The helper did not stop in 45 s; ending it');
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ChromaBridge.Host.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ChromaBridge.Service.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
    end
    else
      Log('The helper stopped after ' + IntToStr(Waited div 1000) + ' s');
  end;
end;

// After the program files are gone: the downloaded and unpacked third-party pieces can always
// be fetched again, so they go; settings and logs are the user's, so they are asked about.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\{#AppName}');
    DelTree(Data + '\openrgb', True, True, True);
    DelTree(Data + '\icue', True, True, True);
    DelTree(Data + '\downloads', True, True, True);
    DelTree(Data + '\updates', True, True, True);
    DeleteFile(Data + '\logon-task.xml');

    if MsgBox('Also remove your ChromaBridge settings and logs?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(Data, True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
    end;
  end;
end;

// Record the edition so the updater fetches the same one. On a switch from Full to Lite the
// bundled folder is no longer wanted.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\edition.txt'), '{#Edition}', False);
    #if Edition == "lite"
    DelTree(ExpandConstant('{app}\bundled'), True, True, True);
    #endif
  end;
end;
