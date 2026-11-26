#!/usr/bin/env bash
# Installed-prior-release upgrade check, run on the CI emulator. Expects:
#   /tmp/up/old-app.apk /tmp/up/old-test.apk   (zephyr-android-v1.5.0 built as debug, or the real signed 1.5.0 asset in the signed-upgrade job, plus the fixture test)
#   /tmp/up/new-app.apk /tmp/up/new-test.apk   (this build)
# Seeds a paired Zephyr state in the OLD install, installs the NEW build over it with `install -r` (no
# uninstall), then verifies the state and a cold open.
set -uo pipefail
PKG=${UPGRADE_PKG:-com.zephyr.remote.debug}   # com.zephyr.remote for the signed release (signed-upgrade job)
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
# Interval bound: the log buffers are cleared at the start of each run() and read once at its end. An ANR that happens
# BETWEEN runs (install, wake, sleep) is in no run's log and neither gate below can see it.
# Every run keeps its FULL raw system log (never truncated) and a filtered view. A missing, failed or empty capture is
# an explicit failure marker, not "no ANR events".
ANR_ALLOW="com.google.android.apps.nexuslauncher com.android.systemui"
run() {
  adb logcat -b all -c 2>/dev/null || adb logcat -c
  adb shell rm -rf /sdcard/Android/data/$PKG/files/verify-fail-hierarchy.xml /sdcard/Android/data/$PKG/files/verify-fail-window.txt /sdcard/Android/data/$PKG/files/verify-fail.png 2>/dev/null || true
  adb shell am instrument -w -e class "$1" $TESTPKG/$RUNNER 2>&1 | tee "instrument-results/$2.txt"
  # Failure-time evidence written by the fixture (screenshot, accessibility tree, dumpsys window). Diagnostic only: never changes a verdict.
  mkdir -p "instrument-results/$2-device"
  for f in verify-fail.png verify-fail-hierarchy.xml verify-fail-window.txt; do
    adb pull "/sdcard/Android/data/$PKG/files/$f" "instrument-results/$2-device/$f" >/dev/null 2>&1 || true
  done
  local raw="instrument-results/$2-rawlog.txt" sys="instrument-results/$2-syslog.txt" rc
  rm -f "instrument-results/$2-logcapture-failed"
  if adb logcat -d -b main,system,events,crash > "$raw" 2>"instrument-results/$2-rawlog.err" && [ -s "$raw" ]; then
    # grep exit 1 means "no matching lines" (a clean log); exit 2+ or a failed write is a capture failure, never "no events".
    if : > "$sys"; then
      grep -E "ANR in |am_anr|FATAL EXCEPTION|am_crash|AndroidRuntime.*Process: " "$raw" > "$sys"; rc=$?
      if [ "$rc" -gt 1 ]; then echo "LOG FILTER FAILED for $2 (grep rc=$rc)" | tee "instrument-results/$2-logcapture-failed"; fi
    else
      echo "LOG FILTER FAILED for $2: cannot write $sys" | tee "instrument-results/$2-logcapture-failed"
    fi
  else
    echo "LOG CAPTURE FAILED for $2: no usable system log (see $2-rawlog.err); the run is treated as unverifiable" | tee "instrument-results/$2-logcapture-failed"
    : > "$sys" 2>/dev/null || true
  fi
}
# Exact package of an ANR line: "ANR in <pkg>" (ActivityManager) or the third field of an am_anr event.
anr_pkg() { # $1 = one log line
  case "$1" in
    *"ANR in "*) printf '%s\n' "$1" | sed -n 's/.*ANR in \([A-Za-z0-9_.]*\).*/\1/p' | head -1;;
    *am_anr*) printf '%s\n' "$1" | sed -n 's/.*am_anr[[:space:]]*:[[:space:]]*\[[^,]*,[^,]*,\([^,]*\),.*/\1/p' | head -1;;
  esac
}
# True (0) = this run is NOT eligible for any retry and a pass of it must not be trusted: unusable log capture, any
# crash (FATAL EXCEPTION / am_crash / AndroidRuntime Process:) from ANY process (an unclassified non-app crash forbids a retry
# conservatively until someone classifies it), any ANR whose exact package is not allowlisted or cannot be parsed.
app_failure_in_log() { # $1 = result name
  local f="instrument-results/$1-syslog.txt" line pkg ok p lines
  [ -e "instrument-results/$1-logcapture-failed" ] && return 0
  [ -s "instrument-results/$1-rawlog.txt" ] || return 0
  grep -qE "FATAL EXCEPTION|am_crash|AndroidRuntime.*Process: " "$f" 2>/dev/null && return 0
  lines=$(cat "$f") || return 0   # an unreadable filtered log is never "clean"
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in *"ANR in "*|*am_anr*)
      pkg=$(anr_pkg "$line"); ok=0
      [ -n "$pkg" ] || return 0
      for p in $ANR_ALLOW; do [ "$pkg" = "$p" ] && ok=1; done
      [ "$ok" = 1 ] || return 0;;
    esac
  done <<< "$lines"
  return 1
}
# Positive evidence that an allowlisted non-app ANR happened during this run (exact package).
foreign_anr_in_log() { # $1 = result name
  local f="instrument-results/$1-syslog.txt" line pkg p lines
  lines=$(cat "$f") || return 1   # unreadable = no positive evidence = no retry
  while IFS= read -r line || [ -n "$line" ]; do
    pkg=$(anr_pkg "$line")
    for p in $ANR_ALLOW; do [ -n "$pkg" ] && [ "$pkg" = "$p" ] && return 0; done
  done <<< "$lines"
  return 1
}
# Focus must be exactly ONE ANR dialog (any package), that dialog must be an exact allowlisted package, and no window of the
# app under test may be in focus next to it. Input: the full mCurrentFocus capture. Output (stdout): "<pkg> <window id>".
# Anything else (two dialogs, an unknown dialog, an app window alongside) returns 1: no choosing one of several.
single_allowed_dialog() { # $1 = focus capture
  local all n id pkg p ok=0
  case "$1" in *"$PKG"*) return 1;; esac
  all=$(printf '%s\n' "$1" | grep -o 'Window{[0-9a-f]* u0 Application Not Responding: [^} ]*}' | sort -u)
  [ -n "$all" ] || return 1
  n=$(printf '%s\n' "$all" | wc -l)
  [ "$n" = 1 ] || return 1
  id=$(printf '%s\n' "$all" | sed -n 's/^Window{\([0-9a-f]*\) u0 .*/\1/p')
  pkg=$(printf '%s\n' "$all" | sed -n 's/.*Application Not Responding: \([^} ]*\)}.*/\1/p')
  for p in $ANR_ALLOW; do [ "$pkg" = "$p" ] && ok=1; done
  [ "$ok" = 1 ] && [ -n "$id" ] || return 1
  printf '%s %s\n' "$pkg" "$id"
}
# Same-window evidence for the case where the ANR happened before the first run's log was cleared (emulator boot), so no run
# log holds an "ANR in" line while the dialog still owns focus. Requires ALL: the seed run's log is clean of app/unknown ANR
# and crash (fails closed), focus is exactly one allowlisted dialog with no app window next to it, AND the system's own
# input_focus event for that very window id and package is in THIS job's seed run log only (the seed run precedes verify).
focused_dialog_logged_in_job() {
  local focus pp pkg id seed="instrument-results/seed-rawlog.txt" hit
  [ -s "$seed" ] || return 1
  if app_failure_in_log seed; then echo "NOTE: seed log shows an app-owned or unknown ANR/crash or is unusable: no window-only retry"; return 1; fi
  focus=$(adb shell dumpsys window | grep "mCurrentFocus")
  pp=$(single_allowed_dialog "$focus") || return 1
  pkg=${pp% *}; id=${pp#* }
  ACCEPTED_DIALOG=""
  hit=$(grep -m1 -E "input_focus: \[Focus (request|entering) $id Application Not Responding: ${pkg//./\\.}[ ,]" "$seed") || return 1
  echo "NOTE: allowlisted ANR dialog package=$pkg window=$id seen earlier in seed-rawlog.txt: $hit"
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] && printf 'upgrade-prior: allowlisted ANR dialog package=%s window=%s was in the seed run log before verify: %s\n' "$pkg" "$id" "$hit" >> "$GITHUB_STEP_SUMMARY"
  ACCEPTED_DIALOG="$pp"   # the exact "<pkg> <id>" the gate checked: dismiss_foreign_anr must see this same pair, or it does nothing
  return 0
}
# Run-log branch: an allowlisted ANR is in this run's log, so the dialog in focus must still be exactly ONE allowlisted dialog;
# bind that pair now (ACCEPTED_DIALOG) so dismiss taps only this identity. Nothing to bind = no retry.
accept_focused_dialog() { # $1 = result name; the logged ANR package must be the SAME package as the focused dialog
  local pp pkg line lp found=0 f="instrument-results/$1-syslog.txt"
  pp=$(single_allowed_dialog "$(adb shell dumpsys window | grep "mCurrentFocus")") || { ACCEPTED_DIALOG=""; return 1; }
  pkg=${pp% *}
  while IFS= read -r line || [ -n "$line" ]; do
    lp=$(anr_pkg "$line"); if [ -n "$lp" ] && [ "$lp" = "$pkg" ]; then found=1; fi
  done < "$f"
  [ -r "$f" ] || { ACCEPTED_DIALOG=""; return 1; }
  [ "$found" = 1 ] || { echo "NOTE: no ANR line for the focused package $pkg in this run's log: no run-log acceptance"; ACCEPTED_DIALOG=""; return 1; }
  ACCEPTED_DIALOG="$pp"
  echo "NOTE: ANR for $pkg is in this run's log and that same package is the one dialog in focus: $ACCEPTED_DIALOG"
}
# A green instrument result is only trusted when its own log is clean: no app or unknown ANR/crash, usable capture.
green_must_be_clean() { # $1 = result name, $2 = label
  if grep -q "^OK (1 test)" "instrument-results/$1.txt" && app_failure_in_log "$1"; then
    echo "UPGRADE FAIL: $2 passed but its own log is unusable or records an app-owned/unknown ANR or crash (see $1-syslog.txt, $1-rawlog.txt)"; exit 1
  fi
}
# A non-app ANR dialog can sit in front of the app and fail ONLY the window assertion. Allowlist of the two emulator
# packages whose dialog may be dismissed; anything else (the app, an unknown package, no dialog) fails closed.
# ONE focus capture, read once and kept: dismiss only if it names an allowlisted package exactly. Evidence is saved first.
dismiss_foreign_anr() { # $1 = label for the saved evidence
  local focus pkg ok=0
  focus=$(adb shell dumpsys window | grep "mCurrentFocus")
  printf '%s\n' "$focus" > "instrument-results/$1-focus.txt"
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 && adb pull /sdcard/ui.xml "instrument-results/$1-ui.xml" >/dev/null 2>&1
  adb logcat -d | grep -E "ANR in|Application Not Responding" | tail -20 > "instrument-results/$1-anr-log.txt"
  pkg=$(single_allowed_dialog "$focus") && ok=1
  [ "$ok" = 1 ] || return 1
  if [ -z "${ACCEPTED_DIALOG:-}" ] || [ "$pkg" != "$ACCEPTED_DIALOG" ]; then echo "NOTE: the dialog in focus ($pkg) is not the pair the gate accepted (${ACCEPTED_DIALOG:-none}): no tap"; return 1; fi
  echo "NOTE: allowlisted non-app ANR dialog in focus: $focus"
  local B X1 Y1 X2 Y2
  B=$(grep -o 'text="Wait"[^>]*bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' "instrument-results/$1-ui.xml" | grep -o "\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]")
  # Exactly ONE Wait node, or no tap: with two, the first could belong to some other window.
  [ -n "$B" ] && [ "$(printf '%s\n' "$B" | wc -l)" = 1 ] || { echo "NOTE: need exactly one Wait button in the saved dump, found: $(printf '%s' "$B" | grep -c .)"; return 1; }
  X1=$(echo $B | cut -d'[' -f2 | cut -d',' -f1); Y1=$(echo $B | cut -d'[' -f2 | cut -d',' -f2 | cut -d']' -f1)
  X2=$(echo $B | cut -d'[' -f3 | cut -d',' -f1); Y2=$(echo $B | cut -d'[' -f3 | cut -d',' -f2 | cut -d']' -f1)
  adb shell input tap $(( (X1+X2)/2 )) $(( (Y1+Y2)/2 )); sleep 2
  echo "NOTE: tapped Wait on the allowlisted dialog"
}
# Retry ONLY the known window-assertion failure, and only when an allowlisted non-app ANR dialog was in focus.
# Any other failed assertion (state, port, token, prefs, process death) is never retried.
# Diagnostic only: prints what the run's own raw log says about ANR dialogs, the app launch and main-thread stalls when the
# window assertion failed. Never changes a verdict, an allowlist or a retry decision.
window_fail_diag() { # $1 result name
  local raw="instrument-results/$1-rawlog.txt"
  echo "== window-failure diagnostic ($1, diagnostic only)"
  echo "-- ANR / not responding / input dispatch lines in this run's log"; grep -nE "ANR in |Not Responding|am_anr|Input dispatching timed out" "$raw" | cut -c1-260 | head -30 || true
  echo "-- app launch lines"; grep -nE "am_proc_start.*$PKG|am_create_activity.*$PKG|am_on_resume.*$PKG|Displayed $PKG|START u0.*$PKG|Start proc.*$PKG" "$raw" | cut -c1-260 | head -20 || true
  echo "-- skipped frames / slow main thread / strict mode"; grep -nE "Skipped [0-9]+ frames|Davey|Slow (operation|Looper)|StrictMode" "$raw" | cut -c1-260 | head -20 || true
  echo "-- memory pressure and GC"; grep -nE "lowmemorykiller|Low on memory|am_low_memory|am_kill" "$raw" | cut -c1-200 | tail -10 || true
  echo "-- focus now"; adb shell dumpsys window | grep -m2 mCurrentFocus || true
  echo "== end window-failure diagnostic"
}
retry_window_only() { # $1 class#method, $2 result name
  ACCEPTED_DIALOG=""
  grep -q "^OK (1 test)" "instrument-results/$2.txt" && return 0
  grep -q "app window never appeared after cold open" "instrument-results/$2.txt" || return 0
  grep -qE "^[0-9]+\) " "instrument-results/$2.txt" && [ "$(grep -cE '^[0-9]+\) ' "instrument-results/$2.txt")" = 1 ] || return 0
  window_fail_diag "$2"
  # Gate on this run's own log first: any app-owned ANR/crash evidence, or an ANR line naming no allowlisted package, is final.
  if app_failure_in_log "$2"; then echo "NOTE: $2 saved log shows an app-owned or unknown ANR/crash: no retry (see $2-syslog.txt)"; return 0; fi
  { foreign_anr_in_log "$2" && accept_focused_dialog "$2"; } || focused_dialog_logged_in_job || { echo "NOTE: $2 saved log has no allowlisted ANR evidence: no retry"; return 0; }
  dismiss_foreign_anr "$2" || return 0
  echo "NOTE: $2 failed only the window assertion with an allowlisted non-app ANR dialog in focus; ONE rerun (first failure kept as $2-first.txt)"
  cp "instrument-results/$2.txt" "instrument-results/$2-first.txt"
  for part in rawlog.txt syslog.txt focus.txt ui.xml anr-log.txt; do cp "instrument-results/$2-$part" "instrument-results/$2-first-$part" 2>/dev/null || true; done
  run "$1" "$2"
}
# Public-log-safe prefs diagnostic: file names, sizes and times of the app's shared_prefs, plus ONLY these explicit fixture keys
# of the plain "settings" file (documentation address and demo values written by the fixture; no token, no secret file content).
prefs_diag() { # $1 = label
  echo "== prefs diagnostic: $1"
  adb shell run-as $PKG ls -l shared_prefs 2>&1 | sed 's/^/  /' | head -20
  adb shell run-as $PKG cat shared_prefs/settings.xml 2>&1 | grep -E 'name="(zephyrHost|zephyrPort|zephyrPcName|selectedSource|skipMainSec|skipSecondarySec|haptics)"' | sed 's/^/  /' | head -10 || true
  echo "  (end $1)"
}
# Environment settle (harness only, runs BEFORE any app install or run, identical for baseline and candidate). The emulator
# runner can hand over while the image is still processing boot-completed work, and a launcher ANR dialog (nexuslauncher
# "Input dispatching timed out ... no focused window") can already own focus before any app under test exists. That dialog
# then fails the window assertion of every later run. Here: wait for sys.boot_completed, then (bounded) require focus without
# an ANR dialog. A dialog may be tapped (Wait) ONLY when it is exactly one allowlisted-package dialog, no app window of $PKG is in
# focus, and the app under test is not installed yet. Anything else, or a dialog still there at the end, is an explicit
# failure with the evidence saved. Nothing here changes a gate, an allowlist, a retry rule or any app/state assertion.
snap() { # $1 = label: focus, processes and timing evidence (diagnostic only)
  local f="instrument-results/snap-$1.txt"
  { echo "== $1 $(date -u +%H:%M:%S) uptime: $(adb shell cat /proc/uptime | tr -d '\r')"
    echo "boot_completed=$(adb shell getprop sys.boot_completed | tr -d '\r') bootanim=$(adb shell getprop init.svc.bootanim | tr -d '\r')"
    adb shell dumpsys window | grep -E "mCurrentFocus|mFocusedApp|isKeyguardShowing" | head -6
    adb shell ps -A 2>/dev/null | grep -E "nexuslauncher|com.android.systemui|$PKG" | head -8
    # Passive resource and ANR evidence (diagnostic only; read-only commands, no gate or assertion reads these).
    echo "-- runner: nproc=$(nproc) loadavg=$(cut -d' ' -f1-3 /proc/loadavg) $(grep -E 'MemTotal|MemAvailable' /proc/meminfo | tr -s ' ' | tr '\n' ' ')"
    echo "-- avd config: $(grep -hE '^hw.ramSize|^hw.cpu.ncore|^vm.heapSize' "$HOME"/.android/avd/*.avd/config.ini 2>/dev/null | tr '\n' ' ')"
    echo "-- guest: loadavg=$(adb shell cat /proc/loadavg | tr -d '\r') $(adb shell grep -E 'MemTotal|MemAvailable' /proc/meminfo | tr -s ' ' | tr '\r\n' '  ')"
    echo "-- guest top:"; adb shell top -b -n 1 -m 8 2>&1 | head -14 || true
    echo "-- guest anr dir (may be unreadable without root):"; adb shell ls /data/anr 2>&1 | head -5 || true
    echo "-- guest logcat ANR/slow-launcher lines (cap 25):"
    adb logcat -b all -d -v threadtime 2>&1 | grep -E "ANR in|am_anr|Input dispatching timed out|Slow (operation|Looper)|nexuslauncher.*(Skipped [0-9]+ frames|Davey)" | tail -25 || true
  } > "$f" 2>&1
  cat "$f"
}
settle_environment() {
  mkdir -p instrument-results
  local i=0 focus pp pkg B X1 Y1 X2 Y2 clean=0 taps=0 t0=$SECONDS
  adb wait-for-device
  until [ "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ] || [ "$i" -ge 60 ]; do sleep 2; i=$((i+1)); done
  [ "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ] || { snap settle-fail; echo "SETTLE FAIL: sys.boot_completed never became 1 (harness environment, nothing installed)"; exit 1; }
  adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard
  snap preinstall-start
  while [ $((SECONDS - t0)) -lt 120 ]; do
    focus=$(adb shell dumpsys window | grep "mCurrentFocus")
    case "$focus" in
      *"Application Not Responding"*)
        clean=0
        pp=$(single_allowed_dialog "$focus") || { printf '%s\n' "$focus" > instrument-results/settle-focus-fail.txt; snap settle-fail; echo "SETTLE FAIL: focus holds an ANR dialog that is not exactly one allowlisted-package dialog: $focus"; exit 1; }
        adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 && adb pull /sdcard/ui.xml instrument-results/settle-ui-$taps.xml >/dev/null 2>&1
        B=$(grep -o 'text="Wait"[^>]*bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' instrument-results/settle-ui-$taps.xml | grep -o "\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]")
        [ -n "$B" ] && [ "$(printf '%s\n' "$B" | wc -l)" = 1 ] || { snap settle-fail; echo "SETTLE FAIL: need exactly one Wait button on the dialog, found $(printf '%s' "$B" | grep -c .)"; exit 1; }
        adb shell pm path "$PKG" >/dev/null 2>&1 && { snap settle-fail; echo "SETTLE FAIL: $PKG is installed; a dialog may only be tapped before the app under test exists"; exit 1; }
        [ "$taps" -lt 3 ] || { snap settle-fail; echo "SETTLE FAIL: an ANR dialog keeps coming back after $taps taps (pre-install)"; exit 1; }
        X1=$(echo $B | cut -d'[' -f2 | cut -d',' -f1); Y1=$(echo $B | cut -d'[' -f2 | cut -d',' -f2 | cut -d']' -f1)
        X2=$(echo $B | cut -d'[' -f3 | cut -d',' -f1); Y2=$(echo $B | cut -d'[' -f3 | cut -d',' -f2 | cut -d']' -f1)
        echo "SETTLE NOTE: pre-install allowlisted ANR dialog ($pp) in focus at +$((SECONDS - t0))s; tapping Wait (tap $((taps+1)))"
        adb shell input tap $(( (X1+X2)/2 )) $(( (Y1+Y2)/2 )); taps=$((taps+1)); sleep 3;;
      *)
        # Fail closed: an empty or failed focus read, a shown keyguard or a non-awake screen is NOT clean.
        if [ -n "$focus" ] && [ "$(adb shell dumpsys window | grep -o 'isKeyguardShowing=[a-z]*' | head -1)" = "isKeyguardShowing=false" ] \
           && [ "$(adb shell dumpsys power | grep -o 'mWakefulness=[A-Za-z]*' | head -1)" = "mWakefulness=Awake" ]; then
          clean=$((clean+1)); [ "$clean" -ge 4 ] && break
        else
          clean=0; adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard
        fi
        sleep 3;;
    esac
  done
  focus=$(adb shell dumpsys window | grep "mCurrentFocus")
  case "$focus" in *"Application Not Responding"*) snap settle-fail; echo "SETTLE FAIL: an ANR dialog is still in focus after the bounded settle: $focus"; exit 1;; esac
  [ "$clean" -ge 4 ] || { snap settle-fail; echo "SETTLE FAIL: focus, keyguard and wakefulness never read clean (non-empty focus, no ANR dialog, keyguard false, Awake) within 120s"; exit 1; }
  snap preinstall-settled
  echo "SETTLE OK: boot completed, no ANR dialog in focus (4 consecutive reads), $taps dialog taps, $((SECONDS - t0))s"
}
mkdir -p instrument-results
cp /tmp/up/new-app.sha256 instrument-results/tested-apk.sha256 || echo "NOTE: no /tmp/up/new-app.sha256 to record"
settle_environment
adb uninstall $PKG >/dev/null 2>&1; adb uninstall $TESTPKG >/dev/null 2>&1
adb install -r /tmp/up/old-app.apk || { echo "UPGRADE FAIL: old install"; exit 1; }
adb install -r /tmp/up/old-test.apk || { echo "UPGRADE FAIL: old test install"; exit 1; }
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS
OLD_VC=$(vc)
run com.bingemote.ZephyrUpgradeFixtureTest#seed seed
green_must_be_clean seed "pass 1 seed"
grep -q "^OK (1 test)" instrument-results/seed.txt || { echo "UPGRADE FAIL: seed did not pass on the old install"; exit 1; }
# Make sure the seeded prefs are on disk before the install replaces the app.
adb shell sync; sleep 3
prefs_diag "pass 1 after seed, before the new build is installed"
snap post-seed
# New build over the old one, NO uninstall (this is the user's update path).
adb install -r /tmp/up/new-app.apk || { echo "UPGRADE FAIL: new build does not install over the old one"; exit 1; }
adb install -r /tmp/up/new-test.apk || { echo "UPGRADE FAIL: new test install"; exit 1; }
NEW_VC=$(vc)
echo "versionCode old=$OLD_VC new=$NEW_VC"
need_upgrade "$OLD_VC" "$NEW_VC" "pass 1"
# A fresh headless emulator can still be on the lock screen; wake and dismiss it so a window check is about the app.
adb shell input keyevent KEYCODE_WAKEUP; adb shell wm dismiss-keyguard; sleep 2
prefs_diag "pass 1 after install -r, before verify"
snap pre-verify
run com.bingemote.ZephyrUpgradeFixtureTest#verify verify
retry_window_only com.bingemote.ZephyrUpgradeFixtureTest#verify verify
green_must_be_clean verify "pass 1 verify"
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
green_must_be_clean vlcseed "pass 2 seed"
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
green_must_be_clean vlcverify "pass 2 verify"
echo "UPGRADE OK (pass 2): VLC-source helper token, VLC password and live polls survived the upgrade"
