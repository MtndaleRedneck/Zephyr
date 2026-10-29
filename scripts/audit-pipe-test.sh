#!/usr/bin/env bash
set -euo pipefail
mkdir -p audit-pipe-results
adb install -r android/build/outputs/apk/debug/app-debug.apk
adb install -r android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
cp android/build/outputs/apk/debug/app-debug.apk audit-pipe-results/
cp android/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk audit-pipe-results/
(cd audit-pipe-results && sha256sum *.apk | tee SHA256SUMS)
adb logcat -c
adb shell am instrument -w -e class com.zephyr.remote.StreamServerAndroidTest com.zephyr.remote.debug.test/androidx.test.runner.AndroidJUnitRunner 2>&1 | tee audit-pipe-results/result.txt
adb logcat -d > audit-pipe-results/logcat.txt
grep -q '^OK (14 tests)' audit-pipe-results/result.txt
! grep -Eq 'FAILURES|INSTRUMENTATION_FAILED|INSTRUMENTATION_ABORTED' audit-pipe-results/result.txt
python3 - <<'PYCODE'
from pathlib import Path
import re
s = Path('audit-pipe-results/logcat.txt').read_text()
pids = set(re.findall(r'ZephyrAudit: APP_PID=(\d+)', s))
assert len(pids) == 1, f'Expected one app PID marker, got {pids}'
pid = pids.pop()
warnings = [line for line in s.splitlines() if re.match(r'\S+\s+\S+\s+' + pid + r'\s', line) and 'A resource failed to call' in line]
report = f'APP_PID={pid} CLOSE_WARNING_COUNT={len(warnings)}\n'
Path('audit-pipe-results/resource-ownership.txt').write_text(report + '\n'.join(warnings))
print(report)
assert not warnings, 'App process emitted resource-close warnings'
assert 'FD_COUNT before=' in s, 'FD-count assertion marker missing'
PYCODE
