#!/usr/bin/env bash
set -euo pipefail
mkdir -p audit-pipe-results
adb install -r android/build/outputs/apk/debug/app-debug.apk
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.StreamServerAndroidTest com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner 2>&1 | tee audit-pipe-results/result.txt
adb logcat -d > audit-pipe-results/logcat.txt
grep -q '^OK (14 tests)' audit-pipe-results/result.txt
! grep -Eq 'FAILURES|INSTRUMENTATION_FAILED|INSTRUMENTATION_ABORTED' audit-pipe-results/result.txt
