#!/usr/bin/env bash
# Real signed package update and real cold-launch navigation, no debug screen overrides.
set -euo pipefail
mkdir -p screenshots
curl --fail --location --retry 2 -o /tmp/shipped-bingemote.apk https://github.com/MtndaleRedneck/BingeMote/releases/download/android-v1.11.1/BingeMote-Android-1.11.1.apk
adb install /tmp/shipped-bingemote.apk
# Rootable CI emulator only: seed ordinary old-install preferences before upgrade.
# This is not a production-app debug hook and never touches the user's device.
adb root
adb wait-for-device
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 3
adb shell am force-stop com.bingemote
cat > /tmp/retained-settings.xml <<'XML'
<?xml version='1.0' encoding='utf-8' standalone='yes' ?>
<map>
<boolean name="setupDone" value="true" />
<string name="selectedSource">vlc</string>
<string name="pcName">Upgrade continuity fixture</string>
<string name="host">192.0.2.27</string>
</map>
XML
adb push /tmp/retained-settings.xml /data/local/tmp/retained-settings.xml
APP_UID=$(adb shell stat -c %u /data/user/0/com.bingemote | tr -d '\r')
adb shell mkdir -p /data/user/0/com.bingemote/shared_prefs
adb shell cp /data/local/tmp/retained-settings.xml /data/user/0/com.bingemote/shared_prefs/settings.xml
adb shell chown "$APP_UID:$APP_UID" /data/user/0/com.bingemote/shared_prefs/settings.xml
adb shell chmod 660 /data/user/0/com.bingemote/shared_prefs/settings.xml
adb shell restorecon -R /data/user/0/com.bingemote/shared_prefs
adb exec-out cat /data/user/0/com.bingemote/shared_prefs/settings.xml > /tmp/retained-before.xml
adb shell sha256sum /data/user/0/com.bingemote/shared_prefs/secrets.xml > /tmp/secrets-before.sha256
adb install -r signed-apk/Zephyr-Android-1.0.0.apk
adb shell dumpsys package com.bingemote | grep -E 'versionCode=21|versionName=1.0.0'
adb exec-out cat /data/user/0/com.bingemote/shared_prefs/settings.xml > /tmp/retained-after.xml
cmp /tmp/retained-before.xml /tmp/retained-after.xml
adb shell sha256sum /data/user/0/com.bingemote/shared_prefs/secrets.xml > /tmp/secrets-after.sha256
cmp /tmp/secrets-before.sha256 /tmp/secrets-after.sha256
adb shell am start -n com.bingemote/com.bingemote.MainActivity
sleep 4
adb shell uiautomator dump /sdcard/retained-ui.xml >/dev/null
adb exec-out cat /sdcard/retained-ui.xml > /tmp/retained-ui.xml
if grep -q 'Set up Zephyr Player' /tmp/retained-ui.xml; then echo 'configured install lost setup state'; exit 1; fi
adb exec-out cat /data/user/0/com.bingemote/shared_prefs/settings.xml > /tmp/retained-launched.xml
python3 - <<'PYXML'
import xml.etree.ElementTree as E
root=E.parse('/tmp/retained-launched.xml').getroot()
entries={n.attrib['name']: n for n in root}
assert entries['setupDone'].attrib['value']=='true'
assert entries['selectedSource'].text=='vlc'
assert entries['pcName'].text=='Upgrade continuity fixture'
PYXML
adb exec-out screencap -p > screenshots/release-retained-vlc.png
echo 'SIGNED_UPGRADE_PREFERENCES_AND_VLC_SELECTION_RETAINED'
# Separate fresh-install fixture AFTER checking retained configured-install data.
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
