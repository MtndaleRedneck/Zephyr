<p align="center"><img src="docs/readme-icon.png" alt="Zephyr icon" width="96"></p>

# Zephyr

**Under development.** Not ready for public use yet. Use BingeMote for now.

A phone remote app and Zephyr Media Player for Windows, plus VLC phone remote support.

## Install

1. Download the Android app and the PC installer from [Releases](https://github.com/MtndaleRedneck/Zephyr/releases).
2. Open Zephyr Player on the PC, then pair from the phone app with the short code.
3. Keep the phone and PC on the same Wi-Fi.

Keep both devices on the same trusted home network and never forward their ports to the internet. The phone connects to Zephyr Player and Zephyr VLC Remote over HTTPS; VLC's own web remote on the PC is a separate plain-HTTP interface.

Already using BingeMote with VLC? That connection stays separate; set it up under VLC PC in the phone app's Settings. Zephyr supersedes BingeMote.

## License

Zephyr is free software under the [GNU GPL v3](LICENSE). The PC apps include open-source parts, such as libVLC, under their own licenses; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). VLC is a trademark of VideoLAN; Zephyr is not affiliated with VideoLAN.
