; Zephyr VLC Remote for Windows installer (Inno Setup, free). Asks for admin once, during install,
; so it can add the firewall rule itself and the app never needs a surprise admin prompt.
; Builds the ZephyrVLCRemote-PC-<version>.exe installer download from the published single-file exe.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{87B0604B-EF8E-463E-BFA2-597DD3249EC4}
AppName=Zephyr VLC Remote
AppVersion={#MyAppVersion}
AppVerName=Zephyr VLC Remote {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Zephyr VLC Remote
DefaultGroupName=Zephyr VLC Remote
PrivilegesRequired=admin
OutputBaseFilename=ZephyrVLCRemote-PC-{#MyAppVersion}
SetupIconFile=Assets\zephyr-vlc-remote.ico
UninstallDisplayIcon={app}\ZephyrVlcRemote.exe
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Files]
Source: "out\ZephyrVlcRemote.exe"; DestDir: "{app}"; Flags: ignoreversion

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
; The installer is already elevated, so it owns the firewall rule and the listen
; permission: no surprise admin prompt on first run. Old 1.x rule name removed too.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr VLC Remote Helper"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr VLC Remote"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Zephyr VLC Remote"" dir=in action=allow protocol=TCP localport=8488 profile=private"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Zephyr VLC Remote"" dir=in action=allow protocol=UDP localport=8489 profile=private"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:8488/"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "http add urlacl url=http://+:8488/ sddl=D:(A;;GX;;;WD)"; Flags: runhidden waituntilterminated
Filename: "{app}\ZephyrVlcRemote.exe"; Description: "Open Zephyr VLC Remote"; Flags: nowait postinstall skipifsilent
; After a silent update, reopen the app for him as the signed-in user (not elevated).
Filename: "{app}\ZephyrVlcRemote.exe"; Flags: nowait skipifnotsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Zephyr VLC Remote Watchdog"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "delwatchdog"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Zephyr VLC Remote"""; Flags: runhidden waituntilterminated; RunOnceId: "delrule"
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:8488/"; Flags: runhidden waituntilterminated; RunOnceId: "delacl"
