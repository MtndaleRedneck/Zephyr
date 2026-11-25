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
# Version gate, fail closed: both numbers must be present, numeric and positive, and the new one strictly greater.
vc() { adb shell dumpsys package $PKG | grep -m1 -o 'versionCode=[0-9]*' | cut -d= -f2; }
need_upgrade() { # old new label
  case "$1" in ''|*[!0-9]*) echo "UPGRADE FAIL: $3 old versionCode unreadable ('$1')"; exit 1;; esac
  case "$2" in ''|*[!0-9]*) echo "UPGRADE FAIL: $3 new versionCode unreadable ('$2')"; exit 1;; esac
  case "$1$2" in ''|*[!0-9]*) echo "UPGRADE FAIL: $3 versionCode unreadable (old='$1' new='$2')"; exit 1;; esac
  [ "$1" -gt 0 ] && [ "$2" -gt "$1" ] || { echo "UPGRADE FAIL: $3 versionCode did not increase (old=$1 new=$2)"; exit 1; }
}
run() { adb shell am instrument -w -e class "$1" $TESTPKG/$RUNNER 2>&1 | tee "instrument-results/$2.txt"; }
# A non-app ANR dialog can sit in front of the app and fail ONLY the window assertion. Allowlist of the two emulator
# packages whose dialog may be dismissed; anything else (the app, an unknown package, no dialog) fails closed.
ANR_ALLOW="com.google.android.apps.nexuslauncher com.android.systemui"
# ONE focus capture, read once and kept: dismiss only if it names an allowlisted package exactly. Evidence is saved first.
dismiss_foreign_anr() { # $1 = label for the saved evidence
  local focus pkg ok=0
  focus=$(adb shell dumpsys window | grep -m1 "mCurrentFocus")
  printf '%s\n' "$focus" > "instrument-results/$1-focus.txt"
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 && adb pull /sdcard/ui.xml "instrument-results/$1-ui.xml" >/dev/null 2>&1
  adb logcat -d | grep -E "ANR in|Application Not Responding" | tail -20 > "instrument-results/$1-anr-log.txt"
  for pkg in $ANR_ALLOW; do
    case "$focus" in *"Application Not Responding: $pkg}"*) ok=1;; esac
  done
  [ "$ok" = 1 ] || return 1
  echo "NOTE: allowlisted non-app ANR dialog in focus: $focus"
  local B X1 Y1 X2 Y2
  B=$(grep -o 'text="Wait"[^>]*bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' "instrument-results/$1-ui.xml" | grep -o "\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]" | head -1)
  [ -n "$B" ] || { echo "NOTE: no Wait button found in the saved dump"; return 1; }
  X1=$(echo $B | cut -d'[' -f2 | cut -d',' -f1); Y1=$(echo $B | cut -d'[' -f2 | cut -d',' -f2 | cut -d']' -f1)
  X2=$(echo $B | cut -d'[' -f3 | cut -d',' -f1); Y2=$(echo $B | cut -d'[' -f3 | cut -d',' -f2 | cut -d']' -f1)
  adb shell input tap $(( (X1+X2)/2 )) $(( (Y1+Y2)/2 )); sleep 2
  echo "NOTE: tapped Wait on the allowlisted dialog"
}
# Retry ONLY the known window-assertion failure, and only when an allowlisted non-app ANR dialog was in focus.
# Any other failed assertion (state, port, token, prefs, process death) is never retried.
retry_window_only() { # $1 class#method, $2 result name
  grep -q "^OK (1 test)" "instrument-results/$2.txt" && return 0
  grep -q "app window never appeared after cold open" "instrument-results/$2.txt" || return 0
  grep -qE "^[0-9]+\) " "instrument-results/$2.txt" && [ "$(grep -cE '^[0-9]+\) ' "instrument-results/$2.txt")" = 1 ] || return 0
  dismiss_foreign_anr "$2" || return 0
  echo "NOTE: $2 failed only the window assertion with an allowlisted non-app ANR dialog in focus; ONE rerun (first failure kept as $2-first.txt)"
  cp "instrument-results/$2.txt" "instrument-results/$2-first.txt"
  run "$1" "$2"
}
adb uninstall $PKG >/dev/null 2>&1; adb uninstall $TESTPKG >/dev/null 2>&1
adb install -r /tmp/up/old-app.apk || { echo "UPGRADE FAIL: old install"; exit 1; }
adb install -r /tmp/up/old-test.apk || { echo "UPGRADE FAIL: old test install"; exit 1; }
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
OLD_VC=$(vc)
run com.bingemote.ZephyrUpgradeFixtureTest#seed seed
grep -q "^OK (1 test)" instrument-results/seed.txt || { echo "UPGRADE FAIL: seed did not pass on the old install"; exit 1; }
# Make sure the seeded prefs are on disk before the install replaces the app.
adb shell sync; sleep 3
# New build over the old one, NO uninstall (this is the user's update path).
adb install -r /tmp/up/new-app.apk || { echo "UPGRADE FAIL: new build does not install over the old one"; exit 1; }
adb install -r /tmp/up/new-test.apk || { echo "UPGRADE FAIL: new test install"; exit 1; }
NEW_VC=$(vc)
echo "versionCode old=$OLD_VC new=$NEW_VC"
need_upgrade "$OLD_VC" "$NEW_VC" "pass 1"
# A fresh headless emulator can still be on the lock screen; wake and dismiss it so a window check is about the app.
adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard; sleep 2
run com.bingemote.ZephyrUpgradeFixtureTest#verify verify
retry_window_only com.bingemote.ZephyrUpgradeFixtureTest#verify verify
if ! grep -q "^OK (1 test)" instrument-results/verify.txt; then
  echo "== diagnostics: window and focus state"
  adb shell dumpsys window | grep -E "mCurrentFocus|mFocusedApp|isKeyguardShowing|mDreamingLockscreen" | head -8
  echo "== diagnostics: activity and crash log lines"
  adb logcat -d | grep -E "ActivityTaskManager|AndroidRuntime|FATAL|ANR in|$PKG" | tail -40
  echo "UPGRADE FAIL: state or cold open after upgrade"; exit 1
fi
echo "UPGRADE OK: $OLD_VC -> $NEW_VC, paired state, prefs and cold open survived"

# Second pass: the VLC-source half (helper token, VLC password, setup flags, live helper+VLC polls) through the same
# install-over path. A clean install first, because the Zephyr pass above leaves Zephyr as the selected source.
echo "== pass 2: VLC-source state through the same upgrade =="
adb uninstall $PKG >/dev/null 2>&1; adb uninstall $TESTPKG >/dev/null 2>&1
adb install -r /tmp/up/old-app.apk || { echo "UPGRADE FAIL: pass 2 old install"; exit 1; }
adb install -r /tmp/up/old-test.apk || { echo "UPGRADE FAIL: pass 2 old test install"; exit 1; }
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
OLD_VC2=$(vc)
run com.bingemote.UpgradeConnectionTest#seedOldRelease vlcseed
grep -q "^OK (1 test)" instrument-results/vlcseed.txt || { echo "UPGRADE FAIL: VLC-source seed did not pass on the old install"; exit 1; }
adb shell sync; sleep 3
adb install -r /tmp/up/new-app.apk || { echo "UPGRADE FAIL: pass 2 new build does not install over the old one"; exit 1; }
adb install -r /tmp/up/new-test.apk || { echo "UPGRADE FAIL: pass 2 new test install"; exit 1; }
NEW_VC2=$(vc)
echo "pass 2 versionCode old=$OLD_VC2 new=$NEW_VC2"
need_upgrade "$OLD_VC2" "$NEW_VC2" "pass 2"
adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard; sleep 2
run com.bingemote.UpgradeConnectionTest#verifyNewReleaseAfterUpdate vlcverify
if ! grep -q "^OK (1 test)" instrument-results/vlcverify.txt; then
  echo "== diagnostics: activity and crash log lines"
  adb logcat -d | grep -E "ActivityTaskManager|AndroidRuntime|FATAL|ANR in|$PKG" | tail -40
  echo "UPGRADE FAIL: VLC-source state, credentials or polls after upgrade"; exit 1
fi
echo "UPGRADE OK (pass 2): VLC-source helper token, VLC password and live polls survived the upgrade"
