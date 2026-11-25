#!/usr/bin/env bash
# Evidence-only tap-through of the DEBUG build: Settings scroll kept on Back from Feedback, fresh entry to Feedback at the top,
# Home -> Settings at the top. Taps come from the live uiautomator dump (text / content-desc), never fixed coordinates.
# Each step asserts and fails closed; screenshots are kept either way. Never touches app code.
set -uo pipefail
PKG=com.zephyr.remote.debug
OUT=nav-shots; mkdir -p "$OUT"; FAIL=0
adb install -r android/build/outputs/apk/debug/app-debug.apk || exit 1
adb shell settings put global hide_error_dialogs 1
adb shell wm size 1080x2310; adb shell wm density 480
# Fail closed: a stale, missing or unreadable dump, or one that is not this app's window, is an ERROR (rc 2), never "not visible".
dump() {
  rm -f /tmp/ui.xml; adb shell rm -f /sdcard/ui.xml >/dev/null 2>&1
  adb shell uiautomator dump /sdcard/ui.xml 2>&1 | grep -q "dumped to" || return 2
  adb pull /sdcard/ui.xml /tmp/ui.xml >/dev/null 2>&1 || return 2
  [ -s /tmp/ui.xml ] || return 2
}
# find_node <exact|contains> <text|desc> <value>: rc 0 and "cx cy" if an on-screen match exists in THIS app's window, rc 1 if
# none, rc 2 if the XML cannot be parsed or no node belongs to $PKG (launcher / ANR dialog / wrong app).
find_node() { PKG=$PKG python3 - "$1" "$2" "$3" <<'PY'
import sys, os, re, xml.etree.ElementTree as ET
mode, attr, val = sys.argv[1:4]
attr = 'text' if attr == 'text' else 'content-desc'
try:
    nodes = list(ET.parse('/tmp/ui.xml').iter('node'))
except Exception:
    sys.exit(2)
if not any(n.get('package') == os.environ['PKG'] for n in nodes):
    sys.exit(2)
for n in nodes:
    if n.get('package') != os.environ['PKG']:
        continue
    v = n.get(attr) or ''
    if (mode == 'exact' and v == val) or (mode == 'contains' and val in v):
        m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', n.get('bounds') or '')
        if m:
            x1, y1, x2, y2 = map(int, m.groups())
            if y2 > 0 and y1 < 2310 and x2 > 0:
                print((x1 + x2) // 2, (y1 + y2) // 2); sys.exit(0)
sys.exit(1)
PY
}
shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
expect() { # $1 label, $2 yes|no, then find args (mode attr value)
  local label=$1 want=$2 rc; shift 2
  if ! dump; then echo "NAV FAIL $label (ui dump failed or unreadable)"; FAIL=1; return; fi
  find_node "$@" >/dev/null; rc=$?
  case $rc in
    0) got=yes;; 1) got=no;;
    *) echo "NAV FAIL $label (dump unreadable or not the app window)"; FAIL=1; return;;
  esac
  if [ "$got" = "$want" ]; then echo "NAV OK   $label ($want)"; else echo "NAV FAIL $label (wanted $want, got $got)"; FAIL=1; fi
}
tap() {
  local p rc
  if ! dump; then echo "NAV FAIL cannot dump before tap $*"; FAIL=1; return 1; fi
  p=$(find_node "$@"); rc=$?
  if [ "$rc" != 0 ]; then echo "NAV FAIL cannot find $* (rc $rc)"; FAIL=1; return 1; fi
  adb shell input tap $p; sleep 2
}
scroll_up() { for i in $(seq 1 "$1"); do adb shell input swipe 540 1900 540 700 300; sleep 1; done; sleep 1; }   # content moves up
scroll_down() { for i in $(seq 1 "$1"); do adb shell input swipe 540 700 540 1900 300; sleep 1; done; sleep 1; }
adb shell am force-stop $PKG
adb shell am start -n $PKG/com.bingemote.MainActivity --es screen settings >/dev/null; sleep 8
shot nav-1-settings-fresh;        expect "1 Settings fresh: title visible (top)" yes exact text "Settings"
scroll_up 4
shot nav-2-settings-scrolled;     expect "2 Settings scrolled: title off screen" no exact text "Settings"
tap contains text "Send feedback, report a bug"
shot nav-3-feedback-first-entry;  expect "3 Feedback first entry: at top" yes exact text "Feedback"
expect "3b Feedback screen unique label (not a failed tap)" yes contains text "Tell us what is on your mind"
scroll_up 3
shot nav-4-feedback-scrolled;     expect "4 Feedback scrolled: title off screen" no exact text "Feedback"
# Back to Settings: the title is off screen, so use the system Back (Feedback has a BackHandler to the same onBack).
adb shell input keyevent KEYCODE_BACK; sleep 2
shot nav-5-settings-after-back;   expect "5 Settings after Back from Feedback: position kept (title still off screen)" no exact text "Settings"
expect "5b Settings after Back: Feedback card still in view" yes contains text "Send feedback, report a bug"
tap contains text "Send feedback, report a bug"
shot nav-6-feedback-reentry;      expect "6 Feedback re-entered after being scrolled: back at the top" yes exact text "Feedback"
expect "6b Feedback screen unique label" yes contains text "Tell us what is on your mind"
# Back from Feedback again: Settings is still scrolled (position kept). Settings has a BackHandler to Home (SettingsScreen.kt:147),
# so Home is reachable WITH Settings scrolled; the gear then must reopen Settings at the top (openSettings(resetScroll = true)).
adb shell input keyevent KEYCODE_BACK; sleep 2
shot nav-7-settings-scrolled-again; expect "7 Settings scrolled again after Back from Feedback (title off screen)" no exact text "Settings"
adb shell input keyevent KEYCODE_BACK; sleep 2
shot nav-8-home;                    expect "8 Home shows the Settings gear" yes exact desc "Settings"
tap exact desc "Settings"
shot nav-9-settings-from-home;      expect "9 Home -> Settings after it was scrolled: reset to the top (title visible)" yes exact text "Settings"
# Header Back arrow also works from the top of Settings (reaches Home), and the header arrow of Feedback returns to Settings.
tap exact desc "Back"
shot nav-10-home-via-header-back;   expect "10 header Back from Settings: Home" yes exact desc "Settings"
adb shell wm size reset; adb shell wm density reset
[ "$FAIL" = 0 ] && echo "NAV ALL OK" || { echo "NAV FAILED"; exit 1; }
