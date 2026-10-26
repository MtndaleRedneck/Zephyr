# Zephyr phone <-> PC protocol (draft, v0)

The wire contract the Android app and Windows apps share. A future TV app
implements the same contract. Versioned independently of app versions; additive
changes only within a major version.

INHERITANCE: BingeMote's shipped protocol is the base. These shapes are copied
from the actual 1.11.x source, not re-invented:

## Pairing (unchanged from BingeMote)
- 6-digit code shown on PC, entered on phone (SetupScreen filters digits, take(6)).
- On success the phone stores a per-source pairing token; all calls carry it.
- The pairing token NEVER appears in a stream URL (stream sessions get their own
  per-session token, see Streaming).

## Input (phone -> PC)
WebSocket preferred, per-message HTTP POST fallback (BingeMote frame() pattern):
- {"cmd":"move","dx":<double>,"dy":<double>}        -> /mouse/move   (WS timeout 2500ms)
- {"cmd":"button","button":<name>,"down":<bool>}    -> /mouse/button
- {"cmd":"scroll","dy":<double>}                    -> /mouse/scroll (2500ms)
- {"cmd":"zoom","delta":<double>}                   -> /mouse/zoom   (2500ms)
HTTP-only:
- POST /key        {"key":"enter|backspace|escape|space|tab|up|down|left|right"}
- POST /type       {"text":<string>}
- POST /volume/set {"volume":<0-100>}
- POST /volume/mute{"mute":<bool>}
- GET  /ping       (1500ms liveness)
- GET/POST /config (moviesFolder etc.)
- POST /movies/play
- POST /vlc/setup  (VLC-password adoption, one-shot per paired phone)

Phone-side input cadence: TCP_NODELAY socket + 8ms pace floor (1.11.1 fix,
NoDelaySocketFactory + MouseCadence policy) - carried into the new app verbatim.

## Source routing (NEW, replaces any PC-side arbitration)
The phone's Connected Sources selector picks the active PC target; input frames
go ONLY to the selected source. Zephyr Player and BingeMote helper run with
independent identities (separate port/mutex/install dir/firewall rules) - no
stand-down, no takeover, no implicit pausing. Conflicts surface to the user as
an explicit choice.

## Streaming (NEW, phone -> Zephyr Player)
- Zephyr Player opens playback with a URL the phone hands it:
  http://<phone-wifi-ip>:<port>/s/<sessionToken>/<id>
  - sessionToken: random per serving session, dies with it; never the pairing token.
  - HTTP GET with Range -> 206 + Content-Range (seek); no Range -> 200 full.
  - SAF content URIs served directly via positioned reads; non-seekable providers
    fall back to a one-time cache copy.
  - Sidecar subtitles (folder-tree permission only) at /s/<sessionToken>/<id>/sub/<n>;
    player attaches with Media.AddSlave(Subtitle).
- Serving runs in a foreground service with Stop action; Wi-Fi-only bind;
  WifiLock + partial wakelock. 2h screen-off soak test gates any smoothness claim.

## Pairing with the Zephyr Player (NEW)

Separate from BingeMote helper pairing: the Zephyr Player holds its OWN token store
(%AppData%/ZephyrPlayer/paired.json) and never shares the helper's.

- The player shows a 6-digit code while nothing is paired (one active code at a
  time, regenerated after every successful pair).
- Phone: POST /pair {"code":"123456"} (unauthenticated - the code is the proof)
  -> 200 {"token": "<32 hex>"} or 403 {"error":"pairing rejected"}.
- The minted token is persisted player-side and works for /play immediately.
- Every other player endpoint requires Authorization: Bearer <token>; a missing
  or unreadable store fails closed (401 for all).
- Protections (BingeMote helper pairing parity, xunit-covered): crypto-RNG code,
  10-minute lifetime, one-shot on success, constant-time compare, 5 wrong codes
  lock pairing until the player restarts (429), guesses while no code is showing
  never count toward the lock, 8 paired phones max (409), and every request
  (including /pair) is only served to loopback/RFC1918/link-local addresses.
- Still open (not blocking dev): an unpair/forget flow and a "pair another phone"
  surface player-side (the banner currently shows only while nothing is paired).

## Handoff (phone -> Zephyr Player)
- POST /play {"url":<stream-url>,"position":<sec>?,"subtitles":[<sub-url>...]}
  (exact shape TBD when the player's HTTP listener lands - this is the one NEW
  PC-side endpoint; everything above already exists on one side or the other.)

OPEN verification items (must pass before ship claims):
- libVLC native plugin trim + LGPL obligations for the bundled set
- SAF seekability across providers (fallback tested)
- Foreground service type (dataSync vs mediaPlayback) on current Android
- Wi-Fi lock necessity/behavior; screen-off soak
- Subtitle scope (sidecar-only v1; embedded already works via libVLC)

## Library (phone <-> Zephyr Player)

The paired phone picks and browses the PC's assigned movies folder. Same
contract as the BingeMote helper's `/movies/list` + `/browse` (jailed paths,
natural sort, video-extension filter, local drives only); the folder itself
lives in the player's own settings file, not the helper's.

- `GET /library/folder` (auth) -> `{"libraryFolder":"C:\\..."}`
- `POST /library/folder` (auth) `{"folder":"C:\\..."}` - validates the folder
  exists on a local drive (400 otherwise); empty string clears. Reply: the
  stored folder.
- `GET /library/list?path=<rel>` (auth) -> one level of the assigned folder:
  `{"path":"...","atRoot":bool,"entries":[{"name","dir"}]}`, folders first
  then video files. Paths are jailed to the assigned folder (`..` escapes 404).
  400 `no library folder` until one is picked.
- `GET /library/browse?path=<abs>` (auth) -> folder-picker listing: drives
  when path is empty, otherwise subfolders only (never files), local
  fixed/removable drives only - a token holder must not reach UNC shares or
  relative paths.

## Input receiver (phone -> Zephyr Player, HTTP fallback set)

Paired phone only (Bearer). Each route replays into Windows via SendInput.
Unknown names are rejected at the edge (400) rather than silently dropped.

- `POST /input/move` `{"dx":<float>,"dy":<float>}` - sub-pixel fractions carry
  across calls (helper InputSender parity).
- `POST /input/button` `{"button":"left|right","down":<bool>}`
- `POST /input/scroll` `{"dy":<float>}`
- `POST /input/zoom` `{"delta":<float>}` (Ctrl+wheel)
- `POST /input/key` `{"key":"enter|backspace|escape|space|tab|up|down|left|right"}`
- `POST /input/type` `{"text":<string>}` (2000-char cap, Enter for newlines)

WebSocket transport (preferred in the Input section) is not implemented yet;
these POSTs are the working set.

## Control (phone -> Zephyr Player)

Paired phone only (Bearer). Drives the player's libVLC; every route 404s until
the control seam is wired, `/control/status` answers state for the phone UI.

- `POST /control/play` `{"url","position"?,"subtitles"?}` (same payload as /play)
- `POST /control/pause` / `POST /control/resume`
- `POST /control/seek` `{"position":<sec>}`
- `POST /control/volume` `{"volume":0-100}` (400 outside)
- `POST /control/mute` `{"mute":<bool>}`
- `POST /control/captions` `{"track":<id>}` (-1 = off)
- `POST /control/fullscreen` `{"on":<bool>}`
- `GET /control/status` -> `{"state","time","length","volume","muted","spu","fullscreen"}`

Verification honesty: route parsing + dispatch are loopback-tested; media
open/decode/pause/seek/volume are integration-tested headless against real
libVLC with a generated audio file; video OUTPUT is not verified on CI.
