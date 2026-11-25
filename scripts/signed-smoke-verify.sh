#!/usr/bin/env bash
# Verifies the rebuilt signed APK and the published 1.5.0 APK. Fails on any mismatch.
set -euo pipefail
cd /tmp/zs
BT=$(ls -d "$ANDROID_HOME"/build-tools/* | sort -V | tail -1)
sig() { # file expected_signer -> exactly one signer, exactly one cert, equal to expected, v3 or v2 verified
  local out; out=$("$BT/apksigner" verify -v --print-certs "$1")
  echo "$out" | sed 's/^/  /'
  [ "$(echo "$out" | grep -c '^Number of signers: 1$')" = 1 ] || { echo "ASSERT FAIL: $1 is not signed by exactly one signer"; exit 1; }
  [ "$(echo "$out" | grep -c 'certificate SHA-256 digest:')" = 1 ] || { echo "ASSERT FAIL: $1 shows more than one certificate"; exit 1; }
  echo "$out" | grep -q "certificate SHA-256 digest: $2\$" || { echo "ASSERT FAIL: $1 signer is not $2"; exit 1; }
  echo "$out" | grep -Eq 'Verified using v(2|3) scheme \(APK Signature Scheme v[23]\): true' || { echo "ASSERT FAIL: $1 has no verified v2/v3 signature"; exit 1; }
}
badge() { # file name code
  local full b; full=$("$BT/aapt2" dump badging "$1")   # whole output captured: a producer failure aborts here (set -e)
  b=${full%%$'\n'*}                                    # first line, taken in the shell, no pipe
  echo "  $b"
  echo "$b" | grep -q "package: name='com.zephyr.remote' versionCode='$3' versionName='$2'" || { echo "ASSERT FAIL: $1 is not com.zephyr.remote $2 ($3)"; exit 1; }
}
sig new.apk "$WANT_SIGNER"
sig old.apk "$WANT_SIGNER"
badge new.apk "$NEW_NAME" "$NEW_CODE"
badge old.apk "$OLD_NAME" "$OLD_CODE"
echo "ok: signer, version name and code verified for both APKs"
