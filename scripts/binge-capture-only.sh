#!/usr/bin/env bash
set -euo pipefail
mkdir -p screenshots instrument-results
adb root; adb wait-for-device
adb install /tmp/binge/app/build/outputs/apk/release/app-release.apk
adb install /tmp/binge/app/build/outputs/apk/androidTest/release/app-release-androidTest.apk
RUNNER=com.bingemote.test/androidx.test.runner.AndroidJUnitRunner
adb shell am instrument -w -e class com.bingemote.RecoveryContinuityTest -e phase seed "$RUNNER" | tee instrument-results/fixture.txt
grep -q 'OK (1 test)' instrument-results/fixture.txt
adb shell wm size 1080x2310
adb shell wm density 400
adb shell am instrument -w -e class com.bingemote.BingeSettingsVisualTest "$RUNNER" | tee instrument-results/labels.txt
adb shell uiautomator dump /sdcard/final-ui.xml || true
adb pull /sdcard/final-ui.xml instrument-results/final-ui.xml || true
adb exec-out screencap -p > screenshots/post-test.png
grep -q 'OK (1 test)' instrument-results/labels.txt
adb pull /sdcard/Android/data/com.bingemote/files/binge-settings-labels.png screenshots/bingemote-release-settings-labels.png
