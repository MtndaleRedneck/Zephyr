; Zephyr VLC Remote for Windows installer (Inno Setup, free).
; Per-user install (PrivilegesRequired=lowest): the app lands in the INSTALLING user's
; profile even when someone else's admin credentials would otherwise be used (round-5
; installer finding). Firewall rules and the listen permission need admin, so they are
; owned by the app's explained first-run elevation (--install-firewall), not by a silent
; install step. Builds the ZephyrVLCRemote-PC-<version>.exe installer download.
; MySourceDir selects which published exe is packaged: "out" is the CI test build
; (PAIR_CODE_HOOK compiled in, never shipped); "release-out" is the clean shipped build.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.1"
#endif
#ifndef MySourceDir
  #define MySourceDir "release-out"
#endif

[Setup]
AppId={{87B0604B-EF8E-463E-BFA2-597DD3249EC4}
AppName=Zephyr VLC Remote
AppVersion={#MyAppVersion}
AppVerName=Zephyr VLC Remote {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Zephyr VLC Remote
DefaultGroupName=Zephyr VLC Remote
PrivilegesRequired=lowest
OutputBaseFilename=ZephyrVLCRemote-PC-{#MyAppVersion}
SetupIconFile=Assets\zephyr-vlc-remote.ico
UninstallDisplayIcon={app}\ZephyrVlcRemote.exe
; The uninstaller must offer to close a running helper, not uninstall under it.
AppMutex=ZephyrVlcRemote.SingleInstance
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Files]
Source: "{#MySourceDir}\ZephyrVlcRemote.exe"; DestDir: "{app}"; Flags: ignoreversion
; License notices for the bundled open-source parts.
Source: "..\..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; The installer owns the autostart entry so uninstall removes it; the app rewrites the same value.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Zephyr VLC Remote"; ValueData: """{app}\ZephyrVlcRemote.exe"" --autostart"; Flags: uninsdeletevalue
; Drop the 1.x autostart entry name and the 1.x exe and shortcuts on upgrade.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ZephyrVlcRemote"; Flags: deletevalue

[Icons]
Name: "{userprograms}\Zephyr VLC Remote"; Filename: "{app}\ZephyrVlcRemote.exe"; IconFilename: "{app}\ZephyrVlcRemote.exe"
Name: "{userdesktop}\Zephyr VLC Remote"; Filename: "{app}\ZephyrVlcRemote.exe"; IconFilename: "{app}\ZephyrVlcRemote.exe"

[InstallDelete]
Type: files; Name: "{app}\ZephyrVlcRemote.exe"
Type: files; Name: "{userprograms}\Zephyr VLC Remote Helper.lnk"
Type: files; Name: "{userdesktop}\Zephyr VLC Remote Helper.lnk"

[Run]
Filename: "{app}\ZephyrVlcRemote.exe"; Description: "Open Zephyr VLC Remote"; Flags: nowait postinstall skipifsilent
; After a silent update, reopen the app for him as the signed-in user.
Filename: "{app}\ZephyrVlcRemote.exe"; Flags: nowait skipifnotsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Zephyr VLC Remote Watchdog"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "delwatchdog"

; Firewall note: the UDP discovery rule is program-scoped to {app}\ZephyrVlcRemote.exe
; (inert once the exe is deleted, but removed anyway by the cleanup below). The vlc.exe
; rule (program-scoped, Private-only, LocalSubnet) may be shared with a co-installed
; BingeMote install and is deliberately left; the app's ownership marker next to its
; data folder lets a future elevated step remove what it created.
[Code]
function InitializeSetup(): Boolean;
var
  I: Integer;
begin
  // The self-updater launches us a moment before its old helper copy has fully exited:
  // give the single-instance mutex up to ~10 s to release instead of failing the update.
  for I := 1 to 20 do
  begin
    if not CheckForMutexes('ZephyrVlcRemote.SingleInstance') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  // Still held after the grace period: let Inno's own AppMutex handling take over.
  Result := True;
end;


function GetUninstallUserSid: String;
var
  TmpFile, S: String;
  Raw: AnsiString;
  ResultCode, P, I: Integer;
begin
  Result := '';
  TmpFile := ExpandConstant('{tmp}\zeph-uninst-sid.txt');
  Exec('cmd.exe', '/c whoami /user /fo csv /nh > "' + TmpFile + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  // LoadStringFromFile takes AnsiString in Inno 6; whoami CSV output is ASCII.
  if LoadStringFromFile(TmpFile, Raw) then
  begin
    S := String(Raw);
    P := Pos('S-1-5-', S);
    if P > 0 then
    begin
      Result := Copy(S, P, Length(S));
      for I := 1 to Length(Result) do
        if (Result[I] = '"') or (Result[I] = ',') or (Result[I] = ' ') or (Result[I] = #13) or (Result[I] = #10) then
        begin
          SetLength(Result, I - 1);
          Break;
        end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove our firewall rules and the listen permission while the exe still exists.
    // The TCP rule names program=System (http.sys owns the listener), so unlike the old
    // exe-scoped rule it is NOT inert after the exe is gone - it must not be left
    // behind. This needs one admin prompt; on a decline the leftover is a LocalSubnet,
    // Private-only rule on an unused port plus the user's own URL ACL.
    // The uninstalling user's SID is passed so the ACL is removed only when it names
    // that user (or is a broad 1.x Everyone grant) - another user's or another app's
    // reservation is never touched.
    // Decline-safe: a cancelled prompt (or any failure) skips cleanup and the uninstall
    // still completes; the leftover is a Private+LocalSubnet rule on an unused port plus
    // the user's own URL ACL.
    if not ShellExec('runas', ExpandConstant('{app}\ZephyrVlcRemote.exe'),
      '--remove-firewall "' + GetUninstallUserSid + '"', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('firewall cleanup was declined or failed to start; rules left in place');
  end;
end;
