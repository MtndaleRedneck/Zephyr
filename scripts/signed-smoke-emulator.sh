#!/usr/bin/env bash
# Runs inside the emulator action. Install of the published old APK, then the exact signed APK over it.
set -euo pipefail
cd /tmp/zs
PKG=com.zephyr.remote
until [ "$(adb shell service check package 2>/dev/null | tr -d '\r')" = "Service package: found" ]; do sleep 5; done
adb shell settings put global verifier_verify_adb_installs 0
wake() { adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard; sleep 2; }
adb install -r old.apk
adb shell dumpsys package "$PKG" | grep -E "versionName=$OLD_NAME" | head -1
wake
adb shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null
sleep 20
adb shell pidof "$PKG" >/dev/null
adb shell am force-stop "$PKG"
adb logcat -c
adb install -r new.apk
adb shell dumpsys package "$PKG" | grep -E "versionName=$NEW_NAME" | head -1
adb shell dumpsys package "$PKG" | grep -E "versionCode=$NEW_CODE" | head -1
wake
adb shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null
sleep 30
adb shell pidof "$PKG" >/dev/null || { echo "ASSERT FAIL: process not alive after the upgrade launch"; exit 1; }
adb shell dumpsys activity activities | grep -E "ResumedActivity" | tee resumed.txt
grep -q "$PKG/" resumed.txt || { echo "ASSERT FAIL: the app is not the resumed activity"; exit 1; }
adb shell uiautomator dump /sdcard/ui.xml >/dev/null
adb shell cat /sdcard/ui.xml > ui.xml
grep -q "package=\"$PKG\"" ui.xml || { echo "ASSERT FAIL: no app window in the UI hierarchy"; exit 1; }
echo "app UI nodes: $(grep -o "package=\"$PKG\"" ui.xml | wc -l)"
adb logcat -d -b all > log.txt
if grep -Eq "Process: $PKG|ANR in $PKG|>>> $PKG <<<|Fatal signal [0-9]+ .*$PKG|am_crash.*$PKG|am_anr.*$PKG" log.txt; then
  echo "ASSERT FAIL: crash, ANR or native fault of $PKG in the log"; grep -E "Process: $PKG|ANR in $PKG|>>> $PKG <<<|Fatal signal|am_crash|am_anr" log.txt | head -20; exit 1
fi
echo "ok: signed build installed over old, resumed activity and app UI present, no crash/ANR/native fault in the log"
echo "note: the old install is not seeded, so this does not test state migration (upgrade-prior covers paired state)"
