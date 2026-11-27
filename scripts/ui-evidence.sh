#!/usr/bin/env bash
# One emulator job for the Zephyr UI candidate: demo-data screenshots (headers, Movies, Feedback, Your PC, palette),
# then the three real-tap suites that prove header equality and scroll behavior. Demo seed only, no real PC or user data.
set -uo pipefail
PKG=com.zephyr.remote.debug
TESTPKG=com.zephyr.remote.debug.test
RUNNER=androidx.test.runner.AndroidJUnitRunner
mkdir -p instrument-results screenshots
FAIL=0
if ! bash scripts/screenshots.sh; then echo "SCREENSHOT STEP FAILED"; FAIL=1; fi
# Required evidence PNGs: each must exist and not be a black/blank frame (screenshots.sh treats under 30000 bytes as black).
for n in main settings movies movies-phone feedback yourpc settings-accent3 main-connected-accent3; do
  f="screenshots/$n.png"
  if [ ! -f "$f" ] || [ "$(stat -c%s "$f")" -lt 30000 ]; then echo "UI PROOF INCOMPLETE: required screenshot missing or blank: $n"; FAIL=1; fi
done
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
adb shell settings put global hide_error_dialogs 1
adb shell wm size 1080x2310
adb shell wm density 480
for cls in com.bingemote.HeaderGeometryTest com.bingemote.TopBarParityTest com.bingemote.ScrollNavigationTest; do
  echo "=== $cls ==="
  adb shell input keyevent KEYCODE_HOME
  adb logcat -c
  adb shell am instrument -w -e class "$cls" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/$(basename $cls).txt"
  adb logcat -d -s HeaderGeom:I AndroidRuntime:E >> "instrument-results/$(basename $cls).txt" 2>/dev/null || true
  # The expected test count is read from the class source (number of @Test), so "OK (0 tests)" or a dropped test fails.
  src=$(find android/src/androidTest -name "$(basename ${cls##*.}).kt" | head -1)
  want=$(grep -c '@Test' "$src")
  if [ -z "$src" ] || [ "$want" -lt 1 ] || ! grep -q "^OK ($want tests\?)" "instrument-results/$(basename $cls).txt"; then
    echo "SUITE FAIL: $cls (expected exactly $want passing tests, none skipped)"; FAIL=1
    adb exec-out screencap -p > "instrument-results/$(basename $cls).FAIL-screen.png" 2>/dev/null || true
  fi
done
for f in $(adb shell ls /sdcard/hg-*.png /sdcard/topbar-parity.png 2>/dev/null | tr -d '\r'); do
  adb exec-out cat "$f" > "instrument-results/$(basename $f)" 2>/dev/null || true
done
adb shell wm size reset; adb shell wm density reset
exit $FAIL
