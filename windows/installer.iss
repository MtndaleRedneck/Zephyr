; Zephyr Player installer (Inno Setup, free). PER-USER install (round-6 N-8):
; PrivilegesRequired=lowest, so the app, its data and its settings ALWAYS land in the
; installing user's profile - even when someone else's admin credentials would have
; answered an install-time prompt under the old admin install. The firewall rules and
; the HTTP listen permission are NOT set up here: the app explains and asks Windows
; once on first run (the explained elevation in FirewallSetup), because inbound TCP to
; the HttpListener server is attributed to the System binary (http.sys), so the rule
; shape and the per-user URL ACL must be owned by the app, not guessed at install time.
; Independent identity from BingeMote: own install dir, Run value, firewall rule names
; and port (47823 vs the helper's 8488/8489). The BingeMote helper is never touched by
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
    // completes.
    if not ShellExec('runas', ExpandConstant('{app}\ZephyrPlayer.exe'), '--remove-firewall', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('firewall cleanup was declined or failed to start; rules left in place');
  end;
end;
