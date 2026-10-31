- Feedback button now opens the Zephyr feedback form (it previously pointed at the old BingeMote form) and links to the Zephyr GitHub issues page
- Ships the license (GPLv3) and third-party notices, including the .NET runtime notices, in the install folder
- Installs over 1.0.0 directly: pairing and settings are kept
- Uninstall removes the app's files, shortcuts, autostart, watchdog task, firewall rules and URL reservation; your settings folder in AppData is kept by design

Tested on a Windows CI machine (install over 1.0.0, feedback, uninstall), not on a home PC with a real phone.

Installer sha256: 83f3babe5bc38dbb258bcdd60eef53bb8f3638998325b91b989d6c9af2973790 (47,089,269 bytes)
