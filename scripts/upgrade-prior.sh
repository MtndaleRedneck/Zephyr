#!/usr/bin/env bash
# Installed-prior-release upgrade check, run on the CI emulator. Expects:
#   /tmp/up/old-app.apk /tmp/up/old-test.apk   (zephyr-android-v1.4.0 built as debug, plus the fixture test)
#   /tmp/up/new-app.apk /tmp/up/new-test.apk   (this build)
# Seeds a paired Zephyr state in the OLD install, installs the NEW build over it with `install -r` (no
# uninstall), then verifies the state and a cold open.
set -uo pipefail
PKG=com.zephyr.remote.debug
TESTPKG=$PKG.test
RUNNER=androidx.test.runner.AndroidJUnitRunner
mkdir -p instrument-results
run() { adb shell am instrument -w -e class "$1" $TESTPKG/$RUNNER 2>&1 | tee "instrument-results/$2.txt"; }
adb uninstall $PKG >/dev/null 2>&1; adb uninstall $TESTPKG >/dev/null 2>&1
adb install -r /tmp/up/old-app.apk || { echo "UPGRADE FAIL: old install"; exit 1; }
adb install -r /tmp/up/old-test.apk || { echo "UPGRADE FAIL: old test install"; exit 1; }
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
OLD_VC=$(adb shell dumpsys package $PKG | grep -m1 -o 'versionCode=[0-9]*' | cut -d= -f2)
run com.bingemote.ZephyrUpgradeFixtureTest#seed seed
grep -q "^OK (1 test)" instrument-results/seed.txt || { echo "UPGRADE FAIL: seed did not pass on the old install"; exit 1; }
# New build over the old one, NO uninstall (this is the user's update path).
adb install -r /tmp/up/new-app.apk || { echo "UPGRADE FAIL: new build does not install over the old one"; exit 1; }
adb install -r /tmp/up/new-test.apk || { echo "UPGRADE FAIL: new test install"; exit 1; }
NEW_VC=$(adb shell dumpsys package $PKG | grep -m1 -o 'versionCode=[0-9]*' | cut -d= -f2)
echo "versionCode old=$OLD_VC new=$NEW_VC"
[ "${NEW_VC:-0}" -gt "${OLD_VC:-0}" ] || { echo "UPGRADE FAIL: versionCode did not increase"; exit 1; }
run com.bingemote.ZephyrUpgradeFixtureTest#verify verify
grep -q "^OK (1 test)" instrument-results/verify.txt || { echo "UPGRADE FAIL: state or cold open after upgrade"; exit 1; }
echo "UPGRADE OK: $OLD_VC -> $NEW_VC, paired state, prefs and cold open survived"
