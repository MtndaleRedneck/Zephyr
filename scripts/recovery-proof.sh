#!/usr/bin/env bash
set -euo pipefail
mkdir -p screenshots instrument-results
adb root; adb wait-for-device
adb install /tmp/original.apk
adb install /tmp/binge/app/build/outputs/apk/androidTest/release/app-release-androidTest.apk
RUNNER=com.bingemote.test/androidx.test.runner.AndroidJUnitRunner
proof() {
 adb shell am instrument -w -e class com.bingemote.RecoveryContinuityTest -e phase "$1" "$RUNNER" | tee "instrument-results/recovery-$1.txt"
 grep -q 'OK (1 test)' "instrument-results/recovery-$1.txt"
}
proof seed
adb shell cat /data/user/0/com.bingemote/shared_prefs/settings.xml | tee instrument-results/seed-disk-settings.xml
grep -q 'DESKTOP-TEST' instrument-results/seed-disk-settings.xml
adb install -r /tmp/faulty.apk
proof faulty
adb install -r /tmp/binge/app/build/outputs/apk/release/app-release.apk
proof restored
adb install android/build/outputs/apk/release/app-release.apk
adb shell dumpsys package com.bingemote | grep -E 'versionCode=22|versionName=1.12.0'
adb shell dumpsys package com.zephyr.remote | grep -E 'versionCode=22|versionName=1.1.0'
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 4
adb exec-out screencap -p > screenshots/restored-bingemote.png
adb shell am start -n com.zephyr.remote/com.bingemote.MainActivity
sleep 4
adb exec-out screencap -p > screenshots/separate-zephyr-cold.png
adb shell input keyevent KEYCODE_HOME
sleep 3
adb shell input swipe 540 1900 540 700 400
sleep 4
adb exec-out screencap -p > screenshots/side-by-side-launcher.png
# Independent debug fixture only, never clearing the recovery target package.
adb install -r android/build/outputs/apk/debug/app-debug.apk
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell am instrument -w -e class com.bingemote.ReleaseIdentityTest com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner | tee instrument-results/identity-defaults.txt
grep -q 'OK (1 test)' instrument-results/identity-defaults.txt
adb shell am instrument -w -e class com.bingemote.PlaybackRoutingTest com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner | tee instrument-results/playback-routing.txt
grep -q 'OK (1 test)' instrument-results/playback-routing.txt
bash scripts/screenshots.sh
