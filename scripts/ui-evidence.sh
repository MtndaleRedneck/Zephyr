#!/usr/bin/env bash
# One emulator job for the Zephyr UI candidate: demo-data screenshots (headers, Movies, Feedback, Your PC, palette),
# then the three real-tap suites that prove header equality and scroll behavior. Demo seed only, no real PC or user data.
set -uo pipefail
PKG=com.zephyr.remote.debug
TESTPKG=com.zephyr.remote.debug.test
RUNNER=androidx.test.runner.AndroidJUnitRunner
mkdir -p instrument-results screenshots
bash scripts/screenshots.sh || echo "SCREENSHOT STEP FAILED (continuing to the tests)"
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
adb shell settings put global hide_error_dialogs 1
adb shell wm size 1080x2310
adb shell wm density 480
FAIL=0
for cls in com.bingemote.HeaderGeometryTest com.bingemote.TopBarParityTest com.bingemote.ScrollNavigationTest; do
  echo "=== $cls ==="
  adb shell input keyevent KEYCODE_HOME
  adb logcat -c
  adb shell am instrument -w -e class "$cls" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/$(basename $cls).txt"
  adb logcat -d -s HeaderGeom:I AndroidRuntime:E >> "instrument-results/$(basename $cls).txt" 2>/dev/null || true
  grep -q '^OK (' "instrument-results/$(basename $cls).txt" || { echo "SUITE FAIL: $cls"; FAIL=1; adb exec-out screencap -p > "instrument-results/$(basename $cls).FAIL-screen.png" 2>/dev/null || true; }
done
for f in $(adb shell ls /sdcard/hg-*.png /sdcard/topbar-parity.png 2>/dev/null | tr -d '\r'); do
  adb exec-out cat "$f" > "instrument-results/$(basename $f)" 2>/dev/null || true
done
adb shell wm size reset; adb shell wm density reset
exit $FAIL
