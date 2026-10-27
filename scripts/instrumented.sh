#!/usr/bin/env bash
# Instrumented suites, run the way the proven BingeMote rig runs them: ONE test
# class per fresh `am instrument` session. connectedDebugAndroidTest runs all 27
# tests in a single session, and on this emulator (pixel_6, API 34) the
# accessibility pipeline wedges after early tree work - every later findObject
# silently misses (documented in SeekGestureTest.kt, base runs 39955727138 /
# 39956293965). Fresh session per class = fresh accessibility tree. This is the
# same invocation shape scripts/screenshots.sh used in the base repo.
set -uo pipefail
PKG=com.zephyr.remote.debug
TESTPKG=com.zephyr.remote.debug.test
RUNNER=androidx.test.runner.AndroidJUnitRunner

adb install -r android/build/outputs/apk/debug/app-debug.apk
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
# Match the base rig environment (screenshots.sh): no error dialogs, tester geometry.
adb shell settings put global hide_error_dialogs 1
adb shell wm size 1080x2310
adb shell wm density 480

# Gesture rigs first (their accessibility tree must be fresh), then the SAF pair:
# the picker test ESTABLISHES the persisted grant the listing test asserts through.
WIRE_PORT=47999
PAIR_CODE=$(grep -oP 'PAIRCODE \K\d+' /tmp/wire-host.log 2>/dev/null || true)
# Host->emulator leg for the phone-stream handoff: the wire host fetches the
# phone-served URL through the runner loopback. (adb reverse is the wrong
# direction AND makes adbd hold device port 47888 -> EADDRINUSE in the test.)
adb forward tcp:47888 tcp:47888 || true

CLASSES="
com.bingemote.AutoSetupCheckTest
com.bingemote.StoreMigrationTest
com.bingemote.NoticeTokenResetTest
com.zephyr.remote.StreamServerAndroidTest
com.bingemote.SeekGestureTest
com.bingemote.CancelGestureTest
com.bingemote.MouseCadenceTest
com.bingemote.InputRoutingTest
com.zephyr.remote.PhonePickerGrantTest
com.zephyr.remote.PhoneMoviesAndroidTest
com.zephyr.remote.ZephyrWireTest
com.zephyr.remote.ZephyrDiscoveryTest
com.bingemote.StreamTargetTest
"

FAIL=0
mkdir -p instrument-results
for cls in $CLASSES; do
  echo "=== $cls ==="
  # Clean slate between classes: no leftover picker/system UI on top, fresh logcat.
  adb shell am force-stop com.android.documentsui 2>/dev/null || true
  adb shell input keyevent KEYCODE_HOME
  adb logcat -c
  adb shell am instrument -w -e class "$cls" -e wire_port "$WIRE_PORT" -e pair_code "$PAIR_CODE" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/$(basename $cls).txt"
  adb logcat -d -s SAFTest:I SAFTest:W >> "instrument-results/$(basename $cls).txt" 2>/dev/null || true
  sleep 2
  for f in $(adb shell ls /sdcard/zephyr-picker-*.png /sdcard/zephyr-picker-*.xml 2>/dev/null); do
    adb exec-out cat "$f" > "instrument-results/$(basename $f)" 2>/dev/null || true
    adb shell rm -f "$f"
  done
  if grep -q 'PICKER UNVERIFIED' "instrument-results/$(basename $cls).txt"; then
    echo "WARNING: $cls skipped as UNVERIFIED (assumption-skip reports as OK) - not a pass"
  fi
  if ! grep -q '^OK (' "instrument-results/$(basename $cls).txt"; then
    # Hierarchy evidence: the test writes dumps into its internal storage (a
    # shell-side dump shows the launcher by now, CI-observed - the picker is gone
    # once the session ends). Pull via run-as (test APK is debuggable).
    for f in picker-tree.xml picker-roots.xml; do
      adb exec-out run-as "$TESTPKG" cat "app_evidence/$f" > "instrument-results/$(basename $cls).$f" 2>/dev/null || true
      adb exec-out run-as "$TESTPKG" rm -f "app_evidence/$f" 2>/dev/null || true
    done
    echo "SUITE FAIL: $cls"
    FAIL=1
  fi
done
# BadToken integration (revoked pairing -> pair-again notice -> re-pair), two
# phases with a wire-host restart between: the restart drops every pairing
# server-side, which is the same 401 the PC's remove-phone produces. Each phase
# needs a FRESH host because the pairing code is one-shot (ZephyrWireTest already
# spent the original above).
echo "=== com.zephyr.remote.ZephyrBadTokenSeedTest ==="
pkill -f ZephyrPlayer.HeadlessHost || true
sleep 2
: > /tmp/wire-host.log
nohup dotnet run --project windows/ZephyrPlayer.HeadlessHost -c Release --no-build -- "$WIRE_PORT" /tmp/zephyr-wire-lib > /tmp/wire-host.log 2>&1 &
for i in $(seq 1 30); do grep -q PAIRCODE /tmp/wire-host.log && break; sleep 1; done
BT_CODE_A=$(grep -oP 'PAIRCODE \K\d+' /tmp/wire-host.log || true)
if [ -z "$BT_CODE_A" ]; then echo "wire host restart (phase A) failed"; FAIL=1; else
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.ZephyrBadTokenSeedTest -e wire_port "$WIRE_PORT" -e pair_code "$BT_CODE_A" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/ZephyrBadTokenSeedTest.txt"
if ! grep -q '^OK (' "instrument-results/ZephyrBadTokenSeedTest.txt"; then echo "SUITE FAIL: ZephyrBadTokenSeedTest"; FAIL=1; fi

echo "=== com.zephyr.remote.ZephyrBadTokenPathsTest ==="
pkill -f ZephyrPlayer.HeadlessHost || true
sleep 2
: > /tmp/wire-host.log
nohup dotnet run --project windows/ZephyrPlayer.HeadlessHost -c Release --no-build -- "$WIRE_PORT" /tmp/zephyr-wire-lib > /tmp/wire-host.log 2>&1 &
for i in $(seq 1 30); do grep -q PAIRCODE /tmp/wire-host.log && break; sleep 1; done
BT_CODE_B=$(grep -oP 'PAIRCODE \K\d+' /tmp/wire-host.log || true)
if [ -z "$BT_CODE_B" ]; then echo "wire host restart (phase B) failed"; FAIL=1; else
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.ZephyrBadTokenPathsTest -e wire_port "$WIRE_PORT" -e pair_code "$BT_CODE_B" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/ZephyrBadTokenPathsTest.txt"
if ! grep -q '^OK (' "instrument-results/ZephyrBadTokenPathsTest.txt"; then echo "SUITE FAIL: ZephyrBadTokenPathsTest"; FAIL=1; fi
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.ZephyrBadTokenAfterRemovalTest -e wire_port "$WIRE_PORT" -e pair_code "$BT_CODE_B" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/ZephyrBadTokenAfterRemovalTest.txt"
if ! grep -q '^OK (' "instrument-results/ZephyrBadTokenAfterRemovalTest.txt"; then echo "SUITE FAIL: ZephyrBadTokenAfterRemovalTest"; FAIL=1; fi
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.ZephyrBadTokenRepairedTest "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/ZephyrBadTokenRepairedTest.txt"
if ! grep -q '^OK (' "instrument-results/ZephyrBadTokenRepairedTest.txt"; then echo "SUITE FAIL: ZephyrBadTokenRepairedTest"; FAIL=1; fi
fi
fi

adb shell wm size reset; adb shell wm density reset
exit $FAIL
