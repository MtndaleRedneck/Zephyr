; Zephyr VLC Remote for Windows installer (Inno Setup, free).
; Per-user install (PrivilegesRequired=lowest): the app lands in the INSTALLING user's
; profile even when someone else's admin credentials would otherwise be used (round-5
; installer finding). Firewall rules and the listen permission need admin, so they are
; owned by the app's explained first-run elevation (--install-firewall), not by a silent
; install step. Builds the ZephyrVLCRemote-PC-<version>.exe installer download.
; MySourceDir selects which published exe is packaged: "out" is the CI test build
; (PAIR_CODE_HOOK compiled in, never shipped); "release-out" is the clean shipped build.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
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
; Firewall note: the app's rules are program-scoped to {app}\ZephyrVlcRemote.exe, which
; this uninstaller deletes - a rule for a missing program matches nothing, so the
; leftover is inert. Deleting rules needs admin, which a per-user uninstall must not
; demand as a surprise. The vlc.exe rule (also program-scoped, Private-only) is shared
; with any co-installed BingeMote install and is deliberately left; the app's ownership
; marker next to its data folder lets a future elevated step remove what it created.
