set -euo pipefail
# CI screenshot rig (ported from BingeMote, trimmed to the new UI evidence):
# installs the DEBUG build (com.bingemote.debug - never the working install) and
# captures the Movies screen in both origins at the reference profile.
PKG=com.bingemote.debug
mkdir -p screenshots
adb install -r android/build/outputs/apk/debug/app-debug.apk
dismiss_anr() {
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || return 0
  adb shell cat /sdcard/ui.xml | grep -q "isn't responding" || return 0
  B=$(adb shell cat /sdcard/ui.xml | grep -o 'text="Wait"[^>]*bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' | grep -o "\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]" | head -1)
  [ -z "$B" ] && return 0
  X1=$(echo $B | cut -d'[' -f2 | cut -d',' -f1); Y1=$(echo $B | cut -d'[' -f2 | cut -d',' -f2 | cut -d']' -f1)
  X2=$(echo $B | cut -d'[' -f3 | cut -d',' -f1); Y2=$(echo $B | cut -d'[' -f3 | cut -d',' -f2 | cut -d']' -f1)
  adb shell input tap $(( (X1+X2)/2 )) $(( (Y1+Y2)/2 ))
  sleep 2
  return 0
}
black() { [ "$(stat -c%s "$1")" -lt 30000 ]; }
shoot() { # $1 screen
  for try in 1 2 3; do
    adb shell am force-stop $PKG
    adb shell am start -n $PKG/com.bingemote.MainActivity --es screen "$1"
    sleep 8
    adb shell pidof $PKG
    dismiss_anr
    adb exec-out screencap -p > "screenshots/$1.png"
    if ! black "screenshots/$1.png"; then break; fi
    echo "black frame on $1 (try $try), retrying"
    sleep 4
  done
}
# Reference phone profile (1080x2310 @ 480dpi), restored at the end.
adb shell settings put global hide_error_dialogs 1
adb shell wm size 1080x2310
adb shell wm density 480
# Warm-up launch so a cold density change can't hand us a black frame.
adb shell am start -n $PKG/com.bingemote.MainActivity --es screen main >/dev/null
sleep 6
shoot main
shoot settings
shoot settings-pair
shoot settings-pair-2
shoot settings-pair-paired
shoot movies-phone
shoot movies
adb shell wm size reset
adb shell wm density reset

# Launcher icon under the PLATFORM's own mask (Pixel launcher applies the real
# adaptive-icon mask; validates resource sizing/crop, no baked border).
adb shell input keyevent KEYCODE_HOME
sleep 2
# The drawer must be SETTLED before capture: an earlier run caught the open
# transition (one enlarged icon + touch indicator) - that validates nothing.
# Verify a known launcher-grid app label is on screen; retry the swipe if not.
DRAWER_OK=0
for try in 1 2 3; do
  adb shell input swipe 540 1900 540 700 400
  sleep 3
  adb shell uiautomator dump /sdcard/zephyr-drawer.xml >/dev/null 2>&1 || true
  adb exec-out cat /sdcard/zephyr-drawer.xml > /tmp/zephyr-drawer.xml 2>/dev/null || true
  if grep -qE 'text="(Chrome|Gmail|Maps|Camera|YouTube|Photos|Play Store)"' /tmp/zephyr-drawer.xml; then
    DRAWER_OK=1
    break
  fi
  echo "drawer not settled (try $try), retrying swipe"
  sleep 2
done
echo "drawer settled: $DRAWER_OK"
adb exec-out screencap -p > screenshots/launcher-drawer.png
adb shell input keyevent KEYCODE_HOME
if [ "$DRAWER_OK" != "1" ]; then
  echo "LAUNCHER CAPTURE UNVERIFIED: drawer never reached settled grid state"
fi
