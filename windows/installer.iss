; Zephyr Player installer (Inno Setup, free). PER-USER install (round-6 N-8):
; PrivilegesRequired=lowest, so the app, its data and its settings ALWAYS land in the
; installing user's profile - even when someone else's admin credentials would have
; answered an install-time prompt under the old admin install. The firewall rules and
; the HTTP listen permission are NOT set up here: the app explains and asks Windows
; once on first run (the explained elevation in FirewallSetup), because inbound TCP to
; the HttpListener server is attributed to the System binary (http.sys), so the rule
; shape and the per-user URL ACL must be owned by the app, not guessed at install time.
; Independent identity from BingeMote: own install dir, Run value, firewall rule names
; and port (47823 vs the helper's 8491/8492). The BingeMote helper is never touched by
; this installer.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppName=Zephyr Player
AppVersion={#MyAppVersion}
AppVerName=Zephyr Player {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Zephyr Player
DefaultGroupName=Zephyr Player
PrivilegesRequired=lowest
OutputBaseFilename=Zephyr-PC-{#MyAppVersion}
UninstallDisplayIcon={app}\ZephyrPlayer.exe
SetupIconFile=ZephyrPlayer\assets\icon.ico
; 2.0 (P0-6): ask Explorer to refresh its icon cache at the end of setup
; (SHChangeNotify SHCNE_ASSOCCHANGED) so an upgrade over 1.x shows the new
; Zephyr icon on the shortcuts and taskbar without a sign-out.
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Files]
; The whole publish dir: the exe alone crashes seconds after launch - the
; LibVLC native dlls and plugins tree sit next to it and must install too.
Source: "out\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Zephyr Player"; Filename: "{app}\ZephyrPlayer.exe"
Name: "{userdesktop}\Zephyr Player"; Filename: "{app}\ZephyrPlayer.exe"

[Run]
Filename: "{app}\ZephyrPlayer.exe"; Description: "Open Zephyr Player"; Flags: nowait postinstall skipifsilent
; After a silent update, reopen as the signed-in user (not elevated).
Filename: "{app}\ZephyrPlayer.exe"; Flags: nowait skipifnotsilent runasoriginaluser

[UninstallRun]
; Round-8 N-10: the published 1.1.0 installer recorded blind [UninstallRun] steps with
; exactly these RunOnceIds (delrule/deldiscrule/delacl). NEVER reuse these three ids for a real
; command in any future script: the newest entry with a shared id is the ONLY one that runs,
; so reusing an id would silently re-arm the inherited blind delete. "delacl" is a bare
; 'netsh http delete urlacl url=http://+:47823/' with no ownership check, and the
; same-AppId upgrade inherits those entries in the merged uninstall log. Per Inno's
; documented RunOnceId rule, when the log holds two entries with the same id only the
; LATEST one executes (comparison is case-sensitive), so recording these harmless
; no-ops at 1.2.0 install time permanently suppresses the inherited blind steps.
; Real cleanup is the ownership-checked, elevated --remove-firewall pass in
; CurUninstallStepChanged - a foreign 47823 reservation is never deleted now, not
; deleted-then-repaired.
Filename: "{cmd}"; Parameters: "/c rem zephyr-delrule-superseded"; Flags: runhidden waituntilterminated; RunOnceId: "delrule"
Filename: "{cmd}"; Parameters: "/c rem zephyr-deldiscrule-superseded"; Flags: runhidden waituntilterminated; RunOnceId: "deldiscrule"
Filename: "{cmd}"; Parameters: "/c rem zephyr-delacl-superseded"; Flags: runhidden waituntilterminated; RunOnceId: "delacl"

[Code]
function InitializeSetup(): Boolean;
var
  I: Integer;
begin
  // The self-updater launches us a moment before its old copy has fully exited:
  // give the single-instance mutex up to ~10 s to release instead of failing the update.
  for I := 1 to 20 do
  begin
    if not CheckForMutexes('ZephyrPlayer') then
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
    // The TCP rule names program=System (http.sys owns the listener), so unlike an
    // exe-scoped rule it is NOT inert once the exe is gone - it must not be left
    // behind. This needs one admin prompt; on a decline the leftover is a LocalSubnet,
    // Private-only rule on an unused port plus the user's own URL ACL. Decline-safe:
    // a cancelled prompt (or any failure) skips cleanup and the uninstall still
    // completes. The uninstalling user's SID is passed so the ACL is removed only when
    // it names that user (or is the published 1.1.0 Everyone grant) - another user's
    // or another app's reservation is never touched.
    if not ShellExec('runas', ExpandConstant('{app}\ZephyrPlayer.exe'),
      '--remove-firewall "' + GetUninstallUserSid + '"', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('firewall cleanup was declined or failed to start; rules left in place');
  end;
end;
