#!/usr/bin/env bash
# Real signed package update and real cold-launch navigation, no debug screen overrides.
set -euo pipefail
mkdir -p screenshots
curl --fail --location --retry 2 -o /tmp/shipped-bingemote.apk https://github.com/MtndaleRedneck/BingeMote/releases/download/android-v1.11.1/BingeMote-Android-1.11.1.apk
adb install /tmp/shipped-bingemote.apk
adb install -r signed-apk/Zephyr-Android-1.0.0.apk
adb shell dumpsys package com.bingemote | grep -E 'versionCode=21|versionName=1.0.0'
adb shell pm clear com.bingemote
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 5
read_ui() {
  adb shell uiautomator dump /sdcard/release-ui.xml >/dev/null
  adb exec-out cat /sdcard/release-ui.xml > /tmp/release-ui.xml
}
tap_text() {
  read_ui
  python3 - "$1" <<'PY'
import sys,xml.etree.ElementTree as E,re,subprocess
nodes=[n for n in E.parse('/tmp/release-ui.xml').iter('node') if n.attrib.get('text')==sys.argv[1]]
assert nodes, 'Missing visible text: '+sys.argv[1]
x1,y1,x2,y2=map(int,re.findall(r'\d+',nodes[0].attrib['bounds']))
subprocess.run(['adb','shell','input','tap',str((x1+x2)//2),str((y1+y2)//2)],check=True)
PY
  sleep 2
}
read_ui
grep -q 'Set up Zephyr Player' /tmp/release-ui.xml
adb exec-out screencap -p > screenshots/release-cold-zephyr.png
tap_text Cancel
read_ui
grep -q 'Connected Sources' /tmp/release-ui.xml
adb exec-out screencap -p > screenshots/release-cancel-settings.png
tap_text 'VLC PC'
read_ui
grep -q 'Step 1' /tmp/release-ui.xml
grep -q 'BingeMote' /tmp/release-ui.xml
adb exec-out screencap -p > screenshots/release-vlc-setup.png
cp /tmp/release-ui.xml screenshots/release-vlc-setup.xml
echo 'SIGNED_UPDATE_OLD20_TO_NEW21_AND_COLD_CANCEL_VLC_ROUTE_PASS'
