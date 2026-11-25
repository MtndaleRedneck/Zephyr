#!/usr/bin/env bash
# Runs inside the emulator action. Install of the published old APK, then the exact signed APK over it.
set -euo pipefail
cd /tmp/zs
PKG=com.zephyr.remote
ready=0
for _ in $(seq 1 60); do
  if [ "$(adb shell service check package 2>/dev/null | tr -d '\r')" = "Service package: found" ]; then ready=1; break; fi
  sleep 5
done
[ "$ready" = 1 ] || { echo "ASSERT FAIL: the package service did not come up within 300 s"; exit 1; }
adb shell settings put global verifier_verify_adb_installs 0
# Capture the whole dumpsys first (a failing adb aborts via set -e), then match on a here-string: no producer|grep pipe, no SIGPIPE.
pkgdump() { local o; o=$(adb shell dumpsys package "$PKG"); grep -m1 -E "$1" <<<"$o"; }
wake() { adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard; sleep 2; }
adb install -r old.apk
pkgdump "versionName=$OLD_NAME"
wake
adb shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null
sleep 20
adb shell pidof "$PKG" >/dev/null
adb shell am force-stop "$PKG"
adb logcat -c
adb install -r new.apk
pkgdump "versionName=$NEW_NAME"
pkgdump "versionCode=$NEW_CODE"
# Decisive evidence is always printed, pass or fail: a screenshot (base64 PNG between markers), window focus, and the log lines about the app.
evidence() {
  set +e
  echo "== evidence: screenshot (base64 PNG) =="
  adb exec-out screencap -p > shot.png 2>/dev/null
  echo "screenshot bytes: $(wc -c < shot.png) sha256: $(sha256sum shot.png | cut -d' ' -f1)"
  echo "-----BEGIN-SCREENSHOT-----"; base64 -w 200 shot.png; echo "-----END-SCREENSHOT-----"
  echo "== evidence: window focus =="; adb shell dumpsys window | grep -E "mCurrentFocus|mFocusedApp" | head -3
  echo "== evidence: log lines about the app (tail) =="; adb logcat -d -b all 2>/dev/null | grep -E "$PKG" | tail -40
  set -e
}
trap evidence EXIT
wake
adb shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null
sleep 30
adb shell pidof "$PKG" >/dev/null || { echo "ASSERT FAIL: process not alive after the upgrade launch"; exit 1; }
adb shell dumpsys activity activities | grep -E "ResumedActivity" | tee resumed.txt
grep -q "$PKG/" resumed.txt || { echo "ASSERT FAIL: the app is not the resumed activity"; exit 1; }
# uiautomator can fail to dump while the app still animates ("could not get idle state"), so retry and print what it said.
adb shell dumpsys window | grep -E "mCurrentFocus|mFocusedApp" | head -3 || true
UI_OK=0
for try in 1 2 3 4 5 6; do
  rm -f ui.xml; adb shell rm -f /sdcard/ui.xml
  adb shell uiautomator dump /sdcard/ui.xml 2>&1 | head -3 || true
  adb shell cat /sdcard/ui.xml > ui.xml 2>/dev/null || true
  if grep -q "package=\"$PKG\"" ui.xml; then UI_OK=1; break; fi
  echo "ui dump try $try had no app nodes (size $(wc -c < ui.xml 2>/dev/null || echo 0)); packages seen: $(grep -o 'package="[^"]*"' ui.xml 2>/dev/null | sort -u | tr '\n' ' ')"
  sleep 5
done
[ "$UI_OK" = 1 ] || { echo "ASSERT FAIL: no app window in the UI hierarchy after 6 tries"; exit 1; }
echo "app UI nodes: $(grep -o "package=\"$PKG\"" ui.xml | wc -l)"
adb logcat -d -b all > log.txt
if grep -Eq "Process: $PKG|ANR in $PKG|>>> $PKG <<<|Fatal signal [0-9]+ .*$PKG|am_crash.*$PKG|am_anr.*$PKG" log.txt; then
  echo "ASSERT FAIL: crash, ANR or native fault of $PKG in the log"; grep -E "Process: $PKG|ANR in $PKG|>>> $PKG <<<|Fatal signal|am_crash|am_anr" log.txt | head -20; exit 1
fi
echo "ok: signed build installed over old, resumed activity and app UI present, no crash/ANR/native fault in the log"
echo "note: the old install is not seeded, so this does not test state migration (upgrade-prior covers paired state)"
