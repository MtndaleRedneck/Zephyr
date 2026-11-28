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
# The notification-bar suites post real notifications; API 34 blocks them without the runtime grant (the BingeMote rig grants it too).
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
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

# New classes that seed prefs / press Home (MoviesStaleLoadTest, StreamServiceBackgroundBindAndroidTest) run LAST: in run 45768614879 SeekGestureTest and CancelGestureTest failed right after MoviesStaleLoadTest ("seeded demo seek block did not appear"); cause unproven, so they no longer precede the gesture rigs.
# UpgradeConnectionTest and RecoveryBridgeTest are NOT in this list on purpose: they need a previously
# installed release (seed, replace the package, verify) and BingeMote-only package ids, so a plain
# one-APK session cannot run them. The Zephyr upgrade check is a separate job (1.5.0 source -> this build).
CLASSES="
com.bingemote.ZephyrAutoPairTest
com.bingemote.TopBarParityTest
com.bingemote.HeaderGeometryTest
com.bingemote.AutoSetupCheckTest
com.bingemote.StoreMigrationTest
com.bingemote.LegacyRepairAfterMigrationTest
com.bingemote.NoticeTokenResetTest
com.zephyr.remote.StreamServerAndroidTest
com.zephyr.remote.StreamServiceRefusalAndroidTest
com.bingemote.SeekGestureTest
com.bingemote.CancelGestureTest
com.bingemote.MouseCadenceTest
com.bingemote.InputRoutingTest
com.zephyr.remote.PhonePickerStorageTest
com.zephyr.remote.PhonePickerGrantTest
com.zephyr.remote.PhoneMoviesAndroidTest
com.zephyr.remote.StreamServiceLifecycleTest
com.bingemote.UnpairedCardRoutingTest
com.zephyr.remote.StreamServiceNoWifiTest
com.zephyr.remote.ZephyrWireTest
com.zephyr.remote.ZephyrDiscoveryTest
com.bingemote.StreamTargetTest
com.bingemote.SubtitleClientTest
com.bingemote.SourceSwitchSubtitleTest
com.zephyr.remote.StreamServiceBackgroundBindAndroidTest
com.bingemote.MoviesStaleLoadTest
com.bingemote.SubtitleJournalTest
com.bingemote.ZephyrCaptionDeliveryTest
com.bingemote.SubtitleSheetTest
com.bingemote.HapticTickTest
com.bingemote.ImmediateFlipTest
com.bingemote.NotificationTapMatrixTest
com.bingemote.PlaybackRoutingTest
com.bingemote.DisconnectedPlaybackTest
com.bingemote.BarActionsTest
com.bingemote.BarSpacingTest
com.bingemote.AutomaticOnlySetupTest
com.bingemote.ModernizationUiTest
com.bingemote.RefinementsUiTest
com.bingemote.ReleaseIdentityTest
"

# Clear only our own FailureDumpRule/test-failure outputs so anything pulled later belongs to THIS run.
EVDIR=/sdcard/Android/data/$PKG/files
if ! adb shell "rm -f $EVDIR/bm-dump* $EVDIR/bm-updates-* $EVDIR/bm-andialog* $EVDIR/bm-dismissed-dialogs.txt $EVDIR/bm-setup-selected-pc-fail.* $EVDIR/bm-setup-confirmed-address-fail.*"; then
  echo "CLEAR DIAG: stale-evidence cleanup failed - pulls may include files from an earlier run on this emulator"
fi
# Verified pull: temp name, per-type signature check, keep-or-log. A failed or error-text pull is deleted
# and logged, never kept as a fake capture.
pull_device_file() { # $1=device file name under the app files dir, $2=local prefix
  local name="$1" prefix="$2" out sz sig
  out="instrument-results/$prefix.$name"
  if ! adb exec-out cat "$EVDIR/$name" > "$out.tmp" 2>/dev/null; then
    rm -f "$out.tmp"; echo "PULL DIAG: $name transport failed - not kept"; return 0
  fi
  sz=$(stat -c%s "$out.tmp" 2>/dev/null || echo 0)
  sig=$(file -b "$out.tmp" 2>/dev/null | cut -c1-60)
  is_adb_error_stub() { [ "$1" -lt 200 ] && grep -qm1 'No such file\|Permission denied' "$2"; }
  case "$name" in
    *.png) echo "$sig" | grep -q 'PNG image' || { rm -f "$out.tmp"; echo "PULL DIAG: $name not a PNG ($sz bytes, $sig) - not kept"; return 0; };;
    *.uix|*.xml) { echo "$sig" | grep -Eq 'XML|text' && ! is_adb_error_stub "$sz" "$out.tmp"; } || { rm -f "$out.tmp"; echo "PULL DIAG: $name not XML ($sz bytes, $sig) - not kept"; return 0; };;
    *) { [ "$sz" -gt 0 ] && ! is_adb_error_stub "$sz" "$out.tmp"; } || { rm -f "$out.tmp"; echo "PULL DIAG: $name empty/error-stub ($sz bytes) - not kept"; return 0; };;
  esac
  mv "$out.tmp" "$out"
  echo "PULLED: $out ($sz bytes, $sig)"
}

FAIL=0
mkdir -p instrument-results
for cls in $CLASSES; do
  echo "=== $cls ==="
  # Clean slate between classes: no leftover picker/system UI on top, fresh logcat.
  adb shell am force-stop com.android.documentsui 2>/dev/null || true
  adb shell input keyevent KEYCODE_HOME
  adb logcat -c
  RAWFLAG=""; case "$cls" in *PhonePicker*|*PhoneMovies*) RAWFLAG="-r";; esac  # raw status stream (status codes by test name) for the SAF classes only
  adb shell am instrument -w $RAWFLAG -e class "$cls" -e wire_port "$WIRE_PORT" -e pair_code "$PAIR_CODE" "$TESTPKG/$RUNNER" 2>&1 | tee "instrument-results/$(basename $cls).txt"
  adb logcat -d -s SAFTest:I SAFTest:W HeaderGeom:I FgBindBackground:I AndroidRuntime:E >> "instrument-results/$(basename $cls).txt" 2>/dev/null || true
  sleep 2
  for f in $(adb shell ls /sdcard/zephyr-picker-*.png /sdcard/zephyr-picker-*.xml /sdcard/zephyr-autopair-*.png /sdcard/parity-saf-grant-*.png /sdcard/parity-storage-picker-*.png /sdcard/hg-*.png 2>/dev/null); do
    adb exec-out cat "$f" > "instrument-results/$(basename $f)" 2>/dev/null || true
    adb shell rm -f "$f"
  done
  # Evidence the tests write on purpose (update-success expiry: the appeared and the expired frames), pulled on pass AND fail.
  for f in $(adb shell "ls $EVDIR/bm-updates-*.png" 2>/dev/null | tr -d '\r'); do
    adb exec-out cat "$f" > "instrument-results/$(basename $cls).$(basename $f)" 2>/dev/null || true
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
    # Triage screenshots (emulator-only fixtures; a failed suite's own file names) live in the
    # app's external files dir. Pull them with a suite prefix so names stay distinct.
    for f in $(adb shell 'ls /sdcard/Android/data/*/files/triage-*.png' 2>/dev/null | tr -d '\r'); do
      adb exec-out cat "$f" > "instrument-results/$(basename $cls).$(basename $f)" 2>/dev/null || true
      adb shell rm -f "$f" 2>/dev/null || true
    done
    adb logcat -d -s ZephyrTriage:E | cut -c1-9000 | tail -20
    # FailureDumpRule evidence (screenshot, hierarchy, logcat, paired recurrence capture) plus the setup tests' own *-fail captures.
    if dump_listing=$(adb shell "ls $EVDIR/ 2>/dev/null"); then :; else
      echo "PULL DIAG: evidence dir listing failed (rc=$?) - cannot confirm which failure evidence exists this run"; dump_listing=""
    fi
    dumps=$(printf '%s\n' "$dump_listing" | tr -d '\r' | grep -E '^bm-(dump|andialog|dismissed-dialogs|setup-selected-pc-fail|setup-confirmed-address-fail)' || true)
    if [ -z "$dumps" ]; then echo "PULL DIAG: no FailureDumpRule/test-failure evidence files listed for $cls"; fi
    for dump in $dumps; do pull_device_file "$dump" "$(basename $cls)"; done
    adb shell "rm -f $EVDIR/bm-dump* $EVDIR/bm-updates-* $EVDIR/bm-andialog* $EVDIR/bm-dismissed-dialogs.txt $EVDIR/bm-setup-selected-pc-fail.* $EVDIR/bm-setup-confirmed-address-fail.*" 2>/dev/null || true
    # First-failure capture, taken right after the session (diagnostic only; never changes the verdict, never reruns):
    # screen, focus, hierarchy, the class's raw log (logcat was cleared before this class) and the log lines that name an ANR,
    # crash, focus change, launch or slow main thread. Kept under the class name so the FIRST failure is never overwritten.
    base="instrument-results/$(basename $cls).FAIL"
    echo "FAIL CAPTURE NOTE: screen/ui dump below are POST-SESSION state (after the instrumentation process exited), not the failing assert moment; artifacts may hold pairing code/token and stay private in CI"
    adb exec-out screencap -p > "$base-screen.png" 2>/dev/null || echo "FAIL CAPTURE: screencap failed for $cls"
    adb shell dumpsys window 2>/dev/null | grep -E "mCurrentFocus|mFocusedApp|isKeyguardShowing" > "$base-focus.txt" || true
    adb shell uiautomator dump /sdcard/failcap.xml >/dev/null 2>&1 && adb pull /sdcard/failcap.xml "$base-ui.xml" >/dev/null 2>&1 || echo "FAIL CAPTURE: uiautomator dump unavailable for $cls"
    if adb logcat -d -b all > "$base-rawlog.txt" 2>"$base-rawlog.err" && [ -s "$base-rawlog.txt" ]; then
      grep -E "ANR in |Not Responding|am_anr|am_crash|FATAL EXCEPTION|input_focus|Displayed |Davey|Skipped [0-9]+ frames|am_proc_start|am_kill|Input dispatching" "$base-rawlog.txt" | cut -c1-260 | tail -80 > "$base-keylines.txt" || true
      echo "FAIL CAPTURE: $cls raw log $(wc -l < "$base-rawlog.txt") lines, key lines $(wc -l < "$base-keylines.txt")"; tail -40 "$base-keylines.txt"
    else
      echo "FAIL CAPTURE: no usable log for $cls (see $base-rawlog.err)"
    fi
    cp "instrument-results/$(basename $cls).txt" "$base-result.txt" 2>/dev/null || true
    echo "SUITE FAIL: $cls"
    FAIL=1
  fi
done
# Named results for the real-picker SAF tests: a JUnit Assume skip is status -4 and prints OK, so require final status 0 by name.
# No skip is accepted for these classes and a dropped class fails.
awk '/INSTRUMENTATION_STATUS: class=/{c=$0; sub(/.*class=/,"",c)} /INSTRUMENTATION_STATUS: test=/{t=$0; sub(/.*test=/,"",t)} /INSTRUMENTATION_STATUS_CODE: /{k=$0; sub(/.*CODE: /,"",k); print c "#" t " " k}' instrument-results/*.txt > instrument-results/test-results.txt || true
for req in com.zephyr.remote.PhonePickerGrantTest#pickerGrantListsMoviesAsApp com.zephyr.remote.PhoneMoviesAndroidTest#listsFoldersThenMoviesWithSidecars com.zephyr.remote.PhoneMoviesAndroidTest#subfolderIsNotRoot com.zephyr.remote.PhonePickerStorageTest#realPickerGrantsSharedStorageFolderAndListsIt; do
  if grep -qx "$req 0" instrument-results/test-results.txt; then echo "REQUIRED PASSED: $req"; else echo "REQUIRED NOT PASSED: $req"; FAIL=1; fi
done
if grep -E "^com\.zephyr\.remote\.(PhonePicker|PhoneMovies)[A-Za-z]*#[A-Za-z]+ -4$" instrument-results/test-results.txt; then echo "SAF test skipped (Assume): not accepted"; FAIL=1; fi
adb shell wm size reset; adb shell wm density reset
exit $FAIL
