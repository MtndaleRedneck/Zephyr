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
adb shell settings put global hide_error_dialogs 1
adb shell am force-stop com.google.android.apps.nexuslauncher || true
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 6
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
grep -q 'OK (2 tests)' instrument-results/identity-defaults.txt
adb shell am instrument -w -e class com.bingemote.PlaybackRoutingTest com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner | tee instrument-results/playback-routing.txt
grep -q 'OK (1 test)' instrument-results/playback-routing.txt
bash scripts/screenshots.sh
# Fixture lives only in debug package. Keep release BingeMote data untouched.
adb shell am force-stop com.zephyr.remote.debug
cat > /tmp/visual-settings.xml <<'XML'
<?xml version='1.0' encoding='utf-8' standalone='yes' ?>
<map>
<string name="moviesPhoneTreeUri">content://com.android.externalstorage.documents/tree/primary%3AMovies</string>
<string name="pcName">DESKTOP-TEST</string>
<int name="skipMainSec" value="10" />
<int name="skipSecondarySec" value="30" />
</map>
XML
adb push /tmp/visual-settings.xml /data/local/tmp/visual-settings.xml
UID_DEBUG=$(adb shell stat -c %u /data/user/0/com.zephyr.remote.debug | tr -d '\r')
adb shell cp /data/local/tmp/visual-settings.xml /data/user/0/com.zephyr.remote.debug/shared_prefs/settings.xml
adb shell chown "$UID_DEBUG:$UID_DEBUG" /data/user/0/com.zephyr.remote.debug/shared_prefs/settings.xml
adb shell restorecon -R /data/user/0/com.zephyr.remote.debug/shared_prefs
# Clear only debug Zephyr pairing, via existing test fixture setup/teardown.
adb shell am instrument -w -e class com.bingemote.ReleaseIdentityTest#clearVisualPairingSynchronously com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner | tee instrument-results/visual-reset.txt
grep -q 'OK (1 test)' instrument-results/visual-reset.txt
adb shell am force-stop com.zephyr.remote.debug
adb shell am start -n com.zephyr.remote.debug/com.bingemote.MainActivity --es screen settings
sleep 4
adb exec-out screencap -p > screenshots/settings-unpaired-path.png
adb shell input swipe 350 1200 350 500 450
sleep 2
adb exec-out screencap -p > screenshots/settings-skip-labels.png
adb shell am force-stop com.zephyr.remote.debug
adb shell am start -n com.zephyr.remote.debug/com.bingemote.MainActivity --es screen movies-phone
sleep 4
adb exec-out screencap -p > screenshots/movies-selected-phone-path.png
# Remove only test fixtures, never the retained release apps.
adb uninstall com.zephyr.remote.debug.test
adb uninstall com.zephyr.remote.debug
adb shell wm size 1080x2310
adb shell wm density 400
adb shell input keyevent KEYCODE_HOME
sleep 4
adb shell input swipe 540 2100 540 400 500
sleep 4
adb exec-out screencap -p > screenshots/two-release-apps-launcher.png
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 5
adb exec-out screencap -p > screenshots/restored-bingemote-clean.png

