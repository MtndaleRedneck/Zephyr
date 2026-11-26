## Zephyr 1.8.1

The Accent color card now has eight colors. Burnt orange and teal are gone, and if you had picked one, the app goes back to orange.

**Fixed**
- The Home play and pause buttons are back to your accent color and the original triangle shape.

## Zephyr 1.8.0

Pick your accent color. Settings has a new Accent color card with orange as the default and nine more to choose from.

**Improved**
- Pairing follows the same steps as the VLC remote, and says plainly when a pairing can't be confirmed instead of showing a check mark.
- Settings now lines up with the Home top bar.
- The Home play button is a clean white triangle.

## Zephyr 1.7.0

Pairing now shows a clear message if the connection can't be saved. The top bar now matches on Home, Movies and Settings, and switching from Phone to PC no longer leaves the phone's list on screen.

## Zephyr 1.5.0

Zephyr now has the same updated foundations as BingeMote 1.14: the same screens, controls and playback, with Zephyr's own icon, name and PC connection.

**Improved**
- Rebuilt on current Android foundations; unused code is stripped, so the app is smaller.
- Movies: when the PC's movies folder changes, the phone follows the PC's setting instead of keeping a stale copy.
- Movies and the reconnect banner refill by themselves once the PC answers again.
- Settings and subtitle sheets keep their place and state when revisited.
- Mouse movement is smoother and subtitles load reliably with Zephyr Player and VLC.
- Switching between Zephyr and VLC keeps each one's own connection and subtitles.

**Fixed**
- Pairing saves all at once and rolls back if anything fails, so you can't end up half-paired. Existing Zephyr pairings and settings are kept.
- Connection and pairing handling on unstable Wi-Fi; play and pause no longer miss after reconnecting.

Zephyr no longer imports pairings from BingeMote. A fresh install pairs on its own: open Setup and pair once.

This update can't be installed from inside Zephyr 1.4.0. Install it once manually; later updates work in the app.

## Zephyr 1.2.0

- The PC helper is now Zephyr VLC Remote: your pairings carry over on the PC, and the phone retargets itself once. If mouse, keyboard or PC volume stop answering after the update, open Setup and pair once more.
- Find my PC is faster and safer: both player generations are found at once, and the phone never sends your pairing to a PC it re-finds by name alone - a PC at a new address asks for a quick re-pair instead.
- Movies keep playing in the background and stop on their own 5 minutes after the movie really ends, with a Stop button on the notification. Subtitle picks, lock-screen skip labels and the folder browser work with Zephyr Player too.
- Fixes: pair-again messages clear after re-pairing, volume taps while muted can't stay muted, errors read like plain sentences, and the brand color matches the player.
- Update checks are kinder: a leftover downloaded update is cleaned up, and an offline check no longer mutes the next one for 6 hours.

## Zephyr 1.1.0

- Zephyr is now a separate Android app named Zephyr, with its green chair/TV icon. It installs beside BingeMote rather than replacing it. Pair Zephyr with your PC separately.
- Switch between VLC PC and Zephyr PC from the quiet source selector on the main screen. Playback, volume, subtitles, fullscreen and lock-screen controls follow the selected player.
- Settings keeps orange source buttons on the left, with plain connection details on the right and gray Not paired text. The phone movie browser shows the selected folder path.
- Settings labels Primary skip buttons and Secondary skip buttons. Each set uses the same interval forward and back. Defaults are 10 seconds primary and 30 seconds secondary; valid saved choices stay unchanged.
- Primary choices are 5, 10, 15, and 30 seconds. Secondary choices are 15 and 30 seconds, and 1, 2, and 3 minutes.
- Zephyr uses its own update channel so BingeMote recovery updates cannot replace it.

If you installed the earlier Zephyr update that replaced BingeMote, install the BingeMote 1.12 recovery first. Do not uninstall it or clear its data. Then install this separate Zephyr app.
## Zephyr 1.0

- Control Zephyr Player or VLC from the same phone remote, with separate PC connections.
- Browse movies on your PC or stream movies and subtitles from your phone.
- Keep the familiar playback, mouse and keyboard controls, with a new integrated player on the PC.
