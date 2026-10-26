# Verification map (what "tested" means here, per feature)

Rule: green counts only on the exact commit, on the CI run that built it.
No green is inherited across changed source. First full-surface windows green:
pending (all runs before 46e6246 died at compile or one failing test).

## Windows player (CI: windows-latest)

| Feature | Coverage | Honest limit |
| --- | --- | --- |
| Build | dotnet build + test-project build on every run | - |
| Pairing gate | unit tests (code lifetime, one-shot spend, lockout, cap) | - |
| /play + auth | PlaybackServer loopback tests (real HttpListener, real HTTP) | - |
| /library/{folder,list,browse} | 9 loopback tests against the real server | - |
| /input/* | 6 route tests to the IInputSink seam | SendInput injection itself UNVERIFIABLE headless |
| /control/* | 5 route tests to the IPlaybackControl seam | - |
| libVLC decode | headless test: Playing, length, clock, pause, seek, resume on a real WAV | VIDEO OUTPUT unverified (no display); volume/mute apply UNVERIFIED (dummy audio path never applies it, reproducible - test skipped with reason) |
| Updater | 7 tests: feed parse, version compare, sha256 verify (incl. bad-sha delete), host allowlist | Feed points at the future public repo; private phase reports check failure honestly (not "up to date"). Silent install/update flow UNVERIFIABLE in CI |
| Installer | Inno Setup script built in CI, artifact ZephyrPlayer-*.exe | Install on a real PC unverified (no user homework by policy) |
| Pair banner | render test asserts gold/white pixel signature | A single opaque-pixel assert once passed on a blank frame; only the signature assert counts |
| Find my PC (discovery responder) | DiscoveryTests: real-socket answer with the Zephyr identity; coexistence test: helper-style responder + player responder bound to 8489 simultaneously, one broadcast, BOTH identities received | Broadcast loopback semantics are the CI runner's; real home-router broadcast verified at install time |
| Real VLC network playback | windows job: choco-installed real VLC plays a phone-style http URL via in_play and attaches a :sub-file option (Subtitle stream asserted in status.json) | Uses a local range server, not the emulator's StreamServer - that half is ZephyrWireTest's fetch-back |
| App icon | .ico frames decoded and inspected (9 sizes); installer SetupIconFile wired | Title-bar/taskbar/Alt-Tab appearance on a real desktop unverified |

## Android app (CI: ubuntu emulator, one class per instrument session)

| Feature | Coverage | Honest limit |
| --- | --- | --- |
| Unit suite | 36 JVM tests (ZephyrClient contract vs MockWebServer, updater, gestures) | Contract tests: the client's expectation of the server, not the server |
| Source routing | InputRoutingTest: Zephyr-selected input reaches ONLY the player (helper gets ZERO requests, asserted), unpaired Zephyr never falls through, VLC-selected never touches the player | - |
| Streaming server | StreamServerAndroidTest (range requests, SAF reads) | 2h screen-off soak NOT done - no smoothness claim |
| Phone movies | PhoneMoviesAndroidTest | - |
| SAF picker grant flow | UNVERIFIED: DocumentsUI roots drawer not automatable on the emulator image (routes exhausted, test skipped with visible reason) | A human must do the one-time folder pick; the app-side handling after a grant IS covered |
| Data continuity | StoreMigrationTest: 1.9 toggle cleanup + paired-install continuity (helper pairing, identity, VLC default, zephyrToken moved to encrypted prefs) | Release signing continuity is release-time evidence, not CI |
| Settings screens | emulator screenshots (Connected Sources, pairing form incl. Find my PC) visually reviewed | Pixel-level review by a human, not assertions |
| Find my PC (client) | ZephyrDiscoveryTest: loopback fake player -> parsed through the helper's own broadcast path; helper replies ignored | Real broadcast to a real PC verified at install time |
| Phone-origin target routing | StreamTargetTest: VLC selected -> in_play on the VLC mock with the phone URL + :sub-file option, player gets ZERO; Zephyr selected -> /play on the player mock, VLC gets ZERO | VLC-side playback of the URL proven by the windows-job real-VLC step |
| Launcher icon | screenshots job captures the app drawer: platform mask applied to the real installed resources | Pixel launcher, not Samsung One UI's squircle - device variance stated |

## Cross-side (Android <-> Windows)

ZephyrWireTest (instrumented job): the emulator's real ZephyrClient talks to the
player's REAL server code (PlaybackServer + PairingGate + Library, hosted by
ZephyrPlayer.HeadlessHost on the runner): pair with the host's generated code,
set/list the library on real disk, play a phone-served stream that the host
fetches BACK through adb forward (asserts 200 + 200000 bytes + range 206), then
an input frame. The seam that remains: the play stub fetches but does not
decode/render (that is the headless libVLC suite's job), and the host is Linux,
so Windows-only seams (SendInput, video output, real installer) stay on their
own rows above.
