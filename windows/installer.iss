; Zephyr Player installer (Inno Setup, free). Asks for admin once during install so
; it can own the firewall rule and HTTP listen permission - no surprise admin prompt
; on first run. Independent identity from BingeMote: own install dir, Run value,
; firewall rule names and port (47823 vs the helper's 8488/8489). The BingeMote
; helper is never touched by this installer.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppName=Zephyr Player
AppVersion={#MyAppVersion}
AppVerName=Zephyr Player {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Zephyr Player
DefaultGroupName=Zephyr Player
PrivilegesRequired=admin
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
; The installer is already elevated, so it owns the firewall rule and the listen
; permission (own names; the helper's rules are untouched).
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr Player"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Zephyr Player"" dir=in action=allow protocol=TCP localport=47823 profile=private"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr Player Discovery"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Zephyr Player Discovery"" dir=in action=allow protocol=UDP localport=8489 profile=private"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:47823/"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "http add urlacl url=http://+:47823/ sddl=D:(A;;GX;;;WD)"; Flags: runhidden waituntilterminated
Filename: "{app}\ZephyrPlayer.exe"; Description: "Open Zephyr Player"; Flags: nowait postinstall skipifsilent
; After a silent update, reopen as the signed-in user (not elevated).
Filename: "{app}\ZephyrPlayer.exe"; Flags: nowait skipifnotsilent runasoriginaluser

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr Player"""; Flags: runhidden waituntilterminated; RunOnceId: "delrule"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr Player Discovery"""; Flags: runhidden waituntilterminated; RunOnceId: "deldiscrule"
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:47823/"; Flags: runhidden waituntilterminated; RunOnceId: "delacl"
