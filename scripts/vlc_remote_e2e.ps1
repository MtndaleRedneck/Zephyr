# Zephyr VLC Remote helper runtime tests on a real Windows machine (CI runner).
# Exercises: the elevated firewall/URL-ACL setup, the BingeMote pairing migration,
# the pairing flow, token auth, every input endpoint, the volume endpoints, and UDP
# discovery identity. Ports are Zephyr's own (8491/8492); BingeMote's are never touched.
$ErrorActionPreference = 'Stop'
$exe = "windows/ZephyrVlcRemote/out/ZephyrVlcRemote.exe"
if (-not (Test-Path $exe)) { throw "helper exe missing at $exe" }

# Assertions are counted: a run that exercised nothing must FAIL, never pass.
$script:assertCount = 0
function Invoke-Helper($path, $body, $token) {
    $headers = @{}
    if ($token) { $headers['X-Zephyr-VLC-Remote-Token'] = $token }
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:8492$path" -Method Post -Body $body -Headers $headers -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
    return @{ code = [int]$r.StatusCode; body = $r.Content }
}
function Assert($cond, $msg) { $script:assertCount++; if (-not $cond) { throw "ASSERT FAILED: $msg" } ; Write-Host "ok ($script:assertCount): $msg" }

# CI-ONLY cleanup (never product behavior): kill every helper copy by NAME, then VERIFY
# none remains. A stale-PID kill would leave the old instance holding the single-instance
# mutex and answering on 8492 while the "restarted" copy silently exits.
function Stop-HelperVerified {
    Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($i in 1..20) {
        if (-not (Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    Get-Process | Where-Object { $_.ProcessName -like '*Zephyr*' } | ForEach-Object { Write-Host "leftover: $($_.ProcessName) pid=$($_.Id)" }
    throw "a helper process survived the kill"
}

# Wait until 8492 answers AND prove the serving process is the just-started copy:
# exactly one helper process exists and it is the expected PID. (http.sys listens in the
# kernel, so the TCP connection's OwningProcess is System - identity is proven by
# exclusivity + the started PID, not by the socket table.)
function Wait-HelperListening([int]$pidToServe, [string]$what) {
    foreach ($i in 1..40) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close()
            $procs = @(Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue)
            if ($procs.Count -eq 1 -and ($pidToServe -le 0 -or $procs[0].Id -eq $pidToServe)) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    Get-Process | Where-Object { $_.ProcessName -like '*Zephyr*' } | ForEach-Object { Write-Host "process: $($_.ProcessName) pid=$($_.Id) path=$($_.Path)" }
    # Forensics: the shape gate compares the RUNNING copy's image path against the
    # rule's stored program string. Dump both so a mismatch is visible, not guessed.
    Write-Host "== firewall rule dump (Zephyr VLC Remote) =="
    netsh advfirewall firewall show rule name="Zephyr VLC Remote" verbose | Select-String -Pattern 'Rule Name|Enabled|Direction|Profiles|Action|Protocol|LocalPort|RemoteIP|Program'
    $logf = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote\helper.log'
    if (Test-Path $logf) { Write-Host "== helper.log tail =="; Get-Content $logf -Tail 60 }
    throw $what
}

# Round-6 rule shape: TCP is program=System (http.sys owns the listener) + Private +
# LocalSubnet; UDP discovery is program-scoped to the exe + Private + LocalSubnet; the
# listen permission names the installing user's SID, not Everyone.
function Assert-HelperFirewallRules([string]$exePath) {
    # The rule stores the exe's FULL path; a relative argument can never match.
    $exePath = (Resolve-Path $exePath).Path
    $ruleV = netsh advfirewall firewall show rule name="Zephyr VLC Remote" verbose | Out-String
    $blocks = $ruleV -split "\r?\n\r?\n"
    $tcp = ($blocks | Where-Object { $_ -match 'Protocol:\s*TCP' } | Select-Object -First 1)
    $udp = ($blocks | Where-Object { $_ -match 'Protocol:\s*UDP' } | Select-Object -First 1)
    Assert ($null -ne $tcp) "a TCP firewall rule exists"
    Assert ($tcp -match 'Program:\s*System') "the TCP rule names program System (http.sys owns the listener)"
    Assert ($tcp -match 'LocalPort:\s*8492') "the TCP rule is scoped to port 8492"
    Assert ($tcp -match 'Profiles:\s*Private') "the TCP rule is Private-only"
    Assert ($tcp -match 'LocalSubnet') "the TCP rule is LocalSubnet-scoped"
    Assert ($null -ne $udp) "a UDP firewall rule exists"
    Assert ($udp -match 'Program:\s*' + [regex]::Escape($exePath)) "the UDP discovery rule is program-scoped to the exe"
    Assert ($udp -match 'Profiles:\s*Private') "the UDP rule is Private-only"
    Assert ($udp -match 'LocalSubnet') "the UDP rule is LocalSubnet-scoped"
    $acl = netsh http show urlacl | Out-String
    Assert ($acl -match ':8492/') "the listen permission exists"
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # netsh stores well-known accounts as SDDL aliases (a RID-500 local admin comes
    # back as "LA", Everyone as "WD"), so compare PARSED security descriptors, not
    # the raw SID string.
    $block = ($acl -split "\r?\n\r?\n" | Where-Object { $_ -match ':8492/' } | Select-Object -First 1)
    $sddlMatch = [regex]::Match($block, 'SDDL:\s*(\S+)')
    $actual = if ($sddlMatch.Success) { (New-Object System.Security.AccessControl.RawSecurityDescriptor($sddlMatch.Groups[1].Value)).GetSddlForm('All') } else { '' }
    $expected = (New-Object System.Security.AccessControl.RawSecurityDescriptor("D:(A;;GX;;;$sid)")).GetSddlForm('All')
    if ($actual -ne $expected) {
        Write-Host "8492 reservation block:"; Write-Host $block
        Write-Host "expected SDDL: $expected (user SID $sid)"
    }
    Assert ($actual -eq $expected) "the listen permission names the installing user's SID, not Everyone"
    Assert ($acl -notmatch 'S-1-1-0') "the listen permission is not Everyone"
}
function Invoke-HelperGet($path, $token) {
    $headers = @{}
    if ($token) { $headers['X-Zephyr-VLC-Remote-Token'] = $token }
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:8492$path" -Method Get -Headers $headers -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
    return @{ code = [int]$r.StatusCode; body = $r.Content }
}


# --- Part 0: per-user install of the CLEAN build; firewall is the app's explained
# first-run elevation; the shipped exe must ignore the CI pairing hook. ---
Write-Host "== per-user install + clean-build hook proof =="
$setup = Get-ChildItem "windows/ZephyrVlcRemote/Output/ZephyrVLCRemote-PC-*.exe" | Select-Object -First 1
if (-not $setup) { throw "installer exe missing in helper/Output" }
& $setup.FullName /VERYSILENT /SUPPRESSMSGBOXES /NORESTART | Out-Null
if ($LASTEXITCODE -ne 0) { throw "silent install failed with exit $LASTEXITCODE" }
$installed = Join-Path $env:LOCALAPPDATA 'Programs\Zephyr VLC Remote\ZephyrVlcRemote.exe'
Assert (Test-Path $installed) "per-user silent install lands in the installing user's profile"
# The installer reopens the app after a silent install (for updates). On headless CI
# that copy sits on the explained first-run elevation prompt holding the single-instance
# mutex, so nothing else can bind 8492. Close it; the --install-firewall run below is
# exactly what a real user reaches by clicking through that prompt.
Start-Sleep -Seconds 2
Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue | Stop-Process -Force
# The installer no longer touches the firewall (it runs unelevated by design): the
# app's explained first-run elevation owns the rules.
$rule0 = netsh advfirewall firewall show rule name="Zephyr VLC Remote" 2>$null | Out-String
Assert ($rule0 -notmatch 'Zephyr VLC Remote') "unelevated installer adds no firewall rule (the app's explained elevation owns it)"
# The elevated setup, run from the installed exe (CI runners are already admin).
# Pass the user's SID + data dir exactly as the unelevated parent does in production,
# so the listen permission is scoped to the user even if the prompt's identity differs.
# NOTE: array ArgumentList does NOT quote elements containing spaces - build one
# pre-quoted string or "Zephyr VLC Remote" arrives as three arguments.
$sid0 = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$data0 = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
$fw0 = Start-Process $installed -ArgumentList "--install-firewall `"$sid0`" `"$data0`"" -Wait -PassThru
if ($fw0.ExitCode -ne 0) {
    Write-Host "install-firewall exit: $($fw0.ExitCode)"
    $log0 = Join-Path $data0 'helper.log'
    if (Test-Path $log0) { Write-Host "== helper.log tail =="; Get-Content $log0 -Tail 20 }
    $acl0 = netsh http show urlacl | Out-String
    $block0 = ($acl0 -split "\r?\n\r?\n" | Where-Object { $_ -match ':8492/' } | Select-Object -First 1)
    Write-Host "8492 reservation after setup:"; Write-Host $block0
}
Assert ($fw0.ExitCode -eq 0) "installed exe's elevated firewall setup succeeds"
Assert-HelperFirewallRules $installed
# Negative hook proof on the SHIPPED build: it was published without PAIR_CODE_HOOK,
# so ZEPHYR_VLC_REMOTE_PAIR_CODE must NOT produce a working pairing.
$env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
Remove-Item (Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote') -Recurse -Force -ErrorAction SilentlyContinue
Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue | Stop-Process -Force
$pClean = Start-Process $installed -PassThru
Wait-HelperListening $pClean.Id "installed clean helper did not start listening"
$rClean = Invoke-Helper '/pair' '{"code":"424242"}' $null
Assert ($rClean.code -eq 403) "the shipped build ignores the CI pairing hook (got $($rClean.code))"
Stop-HelperVerified
# Silent uninstall: app files go away, and the uninstaller's elevated cleanup removes
# our rules + the listen permission (CI is already elevated, so no prompt appears).
$unins = Join-Path $env:LOCALAPPDATA 'Programs\Zephyr VLC Remote\unins000.exe'
& $unins /VERYSILENT /NORESTART | Out-Null
Start-Sleep -Seconds 5
Assert (-not (Test-Path $installed)) "uninstaller removes the installed exe"
$ruleAfter = netsh advfirewall firewall show rule name="Zephyr VLC Remote" 2>$null | Out-String
Assert ($ruleAfter -notmatch 'Zephyr VLC Remote') "uninstall removes the helper firewall rules"
$aclAfter = netsh http show urlacl | Out-String
Assert ($aclAfter -notmatch ':8492/') "uninstall removes the listen permission"
# Belt and braces for later phases (the vlc.exe rule is intentionally left by uninstall).
netsh advfirewall firewall delete rule name="Zephyr VLC Remote" | Out-Null
netsh advfirewall firewall delete rule name="Zephyr VLC Remote VLC" | Out-Null
netsh http delete urlacl url=http://+:8492/ | Out-Null
Write-Host "ok: per-user install, clean-build hook proof, uninstall"

# A silent install now reopens the app on purpose (an update must come back up for him),
# and the silent uninstall can miss that brand-new process. Nothing from the
# install/uninstall phase may still be running once the runtime tests start their own exe.
Stop-HelperVerified
Start-Sleep -Seconds 1

# --- Part 1: the one-time elevated setup, run as-is (CI runners are already admin) ---
Write-Host "== install-firewall =="
# The helper is a WinExe, so `&` returns immediately and $LASTEXITCODE stays empty.
$fw = Start-Process $exe -ArgumentList "--install-firewall `"$sid0`" `"$data0`"" -Wait -PassThru
$fwExit = $fw.ExitCode
Write-Host "install-firewall exit code: $fwExit"
netsh advfirewall firewall show rule name="Zephyr VLC Remote"
$aclOut = netsh http show urlacl
$aclOut | Select-String '8492'
if ($fwExit -ne 0) { throw "install-firewall failed with exit $fwExit" }
if (-not ($aclOut | Select-String ':8492/')) { throw "URL ACL for port 8492 missing after setup" }
Assert-HelperFirewallRules $exe

# --- Part 1b: an old install's rules + ACL + marker are MIGRATED, not trusted ---------
# Round-7 blocker 1: a name-only check (or a marker an older build wrote) kept the
# round-6 exe-scoped TCP rule and a broad Everyone ACL forever. Plant exactly that old
# state, rerun the elevated setup (what the explained first-run elevation launches),
# and require the current shape plus the 1.x helper rule gone. The stale-marker half of
# the gate (a bare-timestamp firewall.ok must not count) is reviewed in source: the
# app side is FirewallMarkerCurrent + the shape check, and the dialog cannot be driven
# headless here.
Write-Host "== firewall migration from the old shape =="
netsh advfirewall firewall delete rule name="Zephyr VLC Remote" | Out-Null
netsh http delete urlacl url=http://+:8492/ | Out-Null
# The round-6 shape: exe-scoped TCP, no LocalSubnet scoping.
netsh advfirewall firewall add rule name="Zephyr VLC Remote" dir=in action=allow protocol=TCP localport=8492 program="$exe" profile=private | Out-Null
# The 1.x helper rule and the 1.x broad (Everyone) listen grant.
netsh advfirewall firewall add rule name="Zephyr VLC Remote Helper" dir=in action=allow protocol=TCP localport=8492 profile=private | Out-Null
netsh http add urlacl url=http://+:8492/ sddl="D:(A;;GX;;;WD)" | Out-Null
New-Item -ItemType Directory -Force $data0 | Out-Null
Set-Content (Join-Path $data0 'firewall.ok') (Get-Date).ToUniversalTime().ToString("o")
# The UNELEVATED detection itself (round-8 N-4): the hooked exe's --firewall-shape-ok
# runs the same live shape read the app gates on - it must REJECT the planted old
# shape, no dialog needed.
$shapeBefore = Start-Process $exe -ArgumentList "--firewall-shape-ok" -Wait -PassThru
Assert ($shapeBefore.ExitCode -eq 1) "the unelevated shape check rejects the planted old shape (exit $($shapeBefore.ExitCode))"
$fwm = Start-Process $exe -ArgumentList "--install-firewall `"$sid0`" `"$data0`"" -Wait -PassThru
Assert ($fwm.ExitCode -eq 0) "elevated setup migrates the old shape (exit $($fwm.ExitCode))"
$shapeAfter = Start-Process $exe -ArgumentList "--firewall-shape-ok" -Wait -PassThru
Assert ($shapeAfter.ExitCode -eq 0) "the unelevated shape check accepts the migrated shape (exit $($shapeAfter.ExitCode))"
Assert-HelperFirewallRules $exe
$legacy = netsh advfirewall firewall show rule name="Zephyr VLC Remote Helper" 2>$null | Out-String
Assert ($legacy -notmatch 'Zephyr VLC Remote Helper') "migration removes the 1.x helper rule"

# --- Part 1c: a FOREIGN port reservation is a conflict, never an overwrite -------------
# Another app or another Windows user's reservation on 8492 is not ours: setup must stop
# with the conflict exit and leave it in place (round-7 N4 + multi-user rule).
Write-Host "== foreign URL ACL conflict =="
netsh http delete urlacl url=http://+:8492/ | Out-Null
netsh http add urlacl url=http://+:8492/ sddl="D:(A;;GX;;;SY)" | Out-Null
$fwf = Start-Process $exe -ArgumentList "--install-firewall `"$sid0`" `"$data0`"" -Wait -PassThru
Assert ($fwf.ExitCode -eq 4) "a foreign reservation stops setup with the conflict exit (got $($fwf.ExitCode))"
$foreignAcl = netsh http show urlacl | Out-String
Assert ($foreignAcl -match 'S-1-5-18|\(A;;GX;;;SY\)') "the foreign reservation is left untouched"
netsh http delete urlacl url=http://+:8492/ | Out-Null
# Restore the good state for the rest of the suite.
$fw2 = Start-Process $exe -ArgumentList "--install-firewall `"$sid0`" `"$data0`"" -Wait -PassThru
Assert ($fw2.ExitCode -eq 0) "setup succeeds again once the foreign reservation is gone (exit $($fw2.ExitCode))"
Assert-HelperFirewallRules $exe

# --- Part 2: run the helper and drive its HTTP API over loopback ---
Write-Host "== helper runtime =="
$env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
$p = Start-Process $exe -PassThru
try {
    Wait-HelperListening $p.Id "helper did not start listening on 8492"
    Write-Host "helper is listening"

    # --- Part 2a: BingeMote migration on a real old config (settled: no forced re-pair) ---
    Write-Host "== BingeMote pairing migration =="
    Stop-HelperVerified
    $legacyDir = Join-Path $env:LOCALAPPDATA 'BingeMote'
    $ownDir = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $legacyDir | Out-Null
    $legacyCfg = Join-Path $legacyDir 'config.json'
    $legacyText = '{"phones":[{"token":"' + ('a1' * 32) + '","name":"CI migration phone","adoptSpent":true}],"moviesFolder":null,"declinedNetworks":["CI-CafeWiFi"],"homeNetworks":["CI-HomeLAN"]}'
    Set-Content $legacyCfg $legacyText -NoNewline
    Remove-Item $ownDir -Recurse -Force -ErrorAction SilentlyContinue
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not start on the migration run"
    $r = Invoke-Helper '/ping' '{}' ('a1' * 32)
    Assert ($r.code -eq 200) "an imported BingeMote pairing serves immediately (no re-pair)"
    Assert ((Get-Content $legacyCfg -Raw) -eq $legacyText) "the legacy config is byte-identical after the import"
    $ownCfgText = Get-Content (Join-Path $ownDir 'config.json') -Raw
    Assert ($ownCfgText -match 'CI migration phone') "the phone name carried over"
    Assert ($ownCfgText -match 'CI-HomeLAN') "the home Wi-Fi choice carried over"
    # A second run with an existing own config must NOT re-import or overwrite.
    Stop-HelperVerified
    Set-Content $legacyCfg ('{"phones":[{"token":"' + ('b2' * 32) + '","name":"should never import"}]}') -NoNewline
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the no-reimport check"
    $r = Invoke-Helper '/ping' '{}' ('a1' * 32)
    Assert ($r.code -eq 200) "the migrated phone still works on the next launch"
    $r = Invoke-Helper '/ping' '{}' ('b2' * 32)
    Assert ($r.code -eq 401) "a later legacy change never re-imports"
    Remove-Item $legacyDir -Recurse -Force -ErrorAction SilentlyContinue


    $r = Invoke-Helper '/ping' '{}' $null
    Assert ($r.code -eq 401) "ping without token is 401"

    $r = Invoke-Helper '/pair' '{"code":"000000"}' $null
    Assert ($r.code -eq 403) "pair with wrong code is 403"
    Assert ($r.body -match 'detail') "wrong code answers with a written-for-him detail"

    # CSRF / DNS-rebinding guards (round-5): browsers send Origin; rebound names fail Host.
    $rOrigin = Invoke-WebRequest -Uri 'http://127.0.0.1:8492/pair' -Method Post -Body '{"code":"424242"}' -Headers @{ Origin = 'http://evil.example' } -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
    Assert ([int]$rOrigin.StatusCode -eq 403) "a browser-style Origin header is refused"
    $rHost = Invoke-WebRequest -Uri 'http://127.0.0.1:8492/ping' -Headers @{ Host = 'evil.example.com'; 'X-Zephyr-VLC-Remote-Token' = ('a1' * 32) } -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
    Assert ([int]$rHost.StatusCode -eq 403) "a rebound DNS name in Host is refused"

    $r = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Write-Host "right-code pair -> $($r.code): $($r.body)"
    if ($r.code -ne 200) {
        Get-Process | Where-Object { $_.ProcessName -like '*ZephyrVlcRemote*' } | ForEach-Object { Write-Host "process: $($_.ProcessName) pid=$($_.Id) path=$($_.Path)" }
        $logf = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote\helper.log'
        if (Test-Path $logf) { Write-Host "== helper.log =="; Get-Content $logf }
        $cfgf = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote\config.json'
        if (Test-Path $cfgf) { Write-Host "== config.json =="; Get-Content $cfgf -Raw }
    }
    Assert ($r.code -eq 200) "pair with right code is 200"
    $paired = $r.body | ConvertFrom-Json
    $token = $paired.token
    Assert ($token.Length -eq 64) "token is 64 hex chars"
    Assert ((Get-Content (Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote\config.json') -Raw) -match 'dpapi:') "the persisted token is DPAPI-wrapped on disk"

    # Automatic-first VLC setup: the code-gated pair response hands over the PC's
    # generated VLC password, and the one-shot adopt fetch behaves exactly once.
    Assert ($paired.vlcPassword.Length -ge 16) "pair response carries the generated VLC password"
    Assert ($paired.vlcPassword -notmatch '\s') "generated password is a clean token"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-1"}' $token
    Assert ($r.code -eq 200) "adopt serves the password once"
    $adopted = ($r.body | ConvertFrom-Json).password
    Assert ($adopted -eq $paired.vlcPassword) "adopted password matches the pair password"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-1"}' $token
    Assert ($r.code -eq 200) "adopt replays for the same request id (lost response)"
    Assert ((($r.body | ConvertFrom-Json).password) -eq $adopted) "replay returns the same password"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-2"}' $token
    Assert ($r.code -eq 409) "a different request id in the retry window conflicts"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-1","ack":true}' $token
    Assert ($r.code -eq 200) "the consumed-ack spends the grant"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-3"}' $token
    Assert ($r.code -eq 410) "after the ack the grant is spent (410)"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-1","ack":true}' $token
    Assert ($r.code -eq 200) "a retried ack is an idempotent no-op"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":""}' $token
    Assert ($r.code -eq 400) "an empty request id is rejected"
    Write-Host "paired with machine: $($paired.name)"
    # Paired phones get stored with a name; a code made outside the window pairs as "Phone N".
    # The migrated BingeMote phone above is already phone #1, so THIS pairing is #2.
    $cfg = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote/config.json'
    $cfgText = Get-Content $cfg -Raw
    Assert ($cfgText -match '"name":"Phone 2"') "the first NEW pairing after a migration is named Phone 2: $cfgText"

    $r = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($r.code -eq 403) "pairing code is single-use"

    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "ping with token is 200"

    $r = Invoke-Helper '/ping' '{}' 'deadbeef'
    Assert ($r.code -eq 401) "ping with wrong token is 401"

    $r = Invoke-Helper '/mouse/move' '{"dx":10,"dy":-5}' $token
    Assert ($r.code -eq 200) "mouse/move"
    $r = Invoke-Helper '/mouse/button' '{"button":"left","down":true}' $token
    Assert ($r.code -eq 200) "mouse/button down"
    $r = Invoke-Helper '/mouse/button' '{"button":"left","down":false}' $token
    Assert ($r.code -eq 200) "mouse/button up"
    $r = Invoke-Helper '/mouse/scroll' '{"dy":-120}' $token
    Assert ($r.code -eq 200) "mouse/scroll"
    $r = Invoke-Helper '/mouse/zoom' '{"delta":0.2}' $token
    Assert ($r.code -eq 200) "mouse/zoom"
    $r = Invoke-Helper '/key' '{"key":"enter"}' $token
    Assert ($r.code -eq 200) "key"
    $r = Invoke-Helper '/type' '{"text":"hi"}' $token
    Assert ($r.code -eq 200) "type"
    $r = Invoke-Helper '/nope' '{}' $token
    Assert ($r.code -eq 404) "unknown path is 404"

    $r = Invoke-Helper '/volume/get' '{}' $token
    if ($r.code -eq 200) {
        $v = $r.body | ConvertFrom-Json
        Write-Host "volume/get: volume=$($v.volume) mute=$($v.mute)"
        $r = Invoke-Helper '/volume/set' '{"volume":50}' $token
        Assert ($r.code -eq 200) "volume/set"
        $r = Invoke-Helper '/volume/mute' '{"mute":true}' $token
        Assert ($r.code -eq 200) "volume/mute on"
        $r = Invoke-Helper '/volume/mute' '{"mute":false}' $token
        Assert ($r.code -eq 200) "volume/mute off"
    } else {
        # Headless runners have no audio device; the helper must say so clearly, not crash.
        Assert ($r.code -eq 503) "volume/get without an audio device is 503, got $($r.code)"
        Assert ($r.body -match 'no audio device') "503 body names the reason: $($r.body)"
        Write-Host "no audio device on this machine; 503 path verified"
    }

    # A second copy must exit quietly instead of failing to bind the port.
    Write-Host "== single instance =="
    $second = Start-Process $exe -Wait -PassThru
    Assert ($second.ExitCode -eq 0) "second instance exits quietly (code $($second.ExitCode))"
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "first instance still serving"

    # After pairing, a fresh launch shows no live code: re-pairing must fail.
    Write-Host "== no silent re-pair =="
    Stop-HelperVerified
    Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart"
    $r = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($r.code -eq 403) "no live pair code after pairing (got $($r.code))"
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "saved token still works after restart"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-after-restart"}' $token
    Assert ($r.code -eq 410) "the spent adopt grant stays spent across a helper restart"
    $pObj = Get-Process -Id $p.Id
    Assert ($pObj.MainWindowHandle -eq 0) "the listener stays bound with no visible window (tray-hidden/headless), handle=$($pObj.MainWindowHandle)"

    # A second phone pairs with a fresh code and both phones stay connected.
    Write-Host "== multi-phone pairing =="
    Stop-HelperVerified
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '123456'
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for second pairing"
    $r = Invoke-Helper '/pair' '{"code":"123456"}' $null
    Assert ($r.code -eq 200) "second phone pairs with a fresh code"
    $token2 = ($r.body | ConvertFrom-Json).token
    Assert ($token2 -ne $token) "second phone gets its own token"
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "first phone still works after second pairs"
    $r = Invoke-Helper '/ping' '{}' $token2
    Assert ($r.code -eq 200) "second phone works"
    $r = Invoke-Helper '/ping' '{}' ('0' * 64)
    Assert ($r.code -eq 401) "unknown token is still 401 with two phones paired"

    # An unacked adopt grant must survive a helper restart from its PERSISTED marker:
    # same request id replays, a different one conflicts, and the ack still spends it.
    Write-Host "== pre-ack adopt survives a restart =="
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-pre-restart"}' $token2
    Assert ($r.code -eq 200) "second phone fetches its VLC password"
    $preRestart = ($r.body | ConvertFrom-Json).password
    $cfgText = Get-Content $cfg -Raw
    Assert ($cfgText -match '"adoptRequestId":"req-pre-restart"') "the served marker is persisted at serve time"
    Assert (-not ($cfgText -match $preRestart)) "the password value is never written to config.json"
    Stop-HelperVerified
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart with an unacked adopt"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-pre-restart"}' $token2
    Assert ($r.code -eq 200) "same request id replays after the restart (no re-arm)"
    Assert ((($r.body | ConvertFrom-Json).password) -eq $preRestart) "replay returns the same password after restart"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-different"}' $token2
    Assert ($r.code -eq 409) "a different request id still conflicts after the restart"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-pre-restart","ack":true}' $token2
    Assert ($r.code -eq 200) "the ack still spends the persisted grant"
    $r = Invoke-Helper '/vlc/adopt' '{"requestId":"req-after-ack"}' $token2
    Assert ($r.code -eq 410) "the spent grant stays spent"

    # A 1.0/1.1 config stored one token; that phone must stay paired after upgrading.
    Write-Host "== old config migration =="
    Stop-HelperVerified
    Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
    $cfg = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote/config.json'
    Set-Content $cfg ('{"token":"' + $token + '","moviesFolder":null}')
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart on old config"
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "old single-token config keeps the phone paired"
    $r = Invoke-Helper '/ping' '{}' $token2
    Assert ($r.code -eq 401) "old config no longer carries the second phone (fresh format rewrites on next save)"

    # A vlcrc write deferred because VLC was open must survive a helper restart:
    # the pending value is persisted, and a restarted helper completes it once VLC is gone.
    Write-Host "== pending vlcrc write survives a restart =="
    # Clear vlcrc so the completed write is proof, not a leftover of the first pair's write.
    Remove-Item (Join-Path $env:APPDATA 'vlc\vlcrc') -Force -ErrorAction SilentlyContinue
    Copy-Item "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" (Join-Path $env:TEMP 'vlc.exe') -Force
    $fakeVlc = Start-Process (Join-Path $env:TEMP 'vlc.exe') -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 600' -PassThru
    Start-Sleep -Seconds 2
    Assert (-not $fakeVlc.HasExited) "the simulated VLC process is running"
    try {
        Stop-HelperVerified
        $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '555555'
        $p = Start-Process $exe -PassThru
        Wait-HelperListening $p.Id "helper did not restart for the pending-write test"
        $r = Invoke-HelperGet '/vlc/setup-status' $token
        $ss = $r.body | ConvertFrom-Json
        Assert ($ss.configured -eq $false -and $ss.pending -eq $false) "with vlcrc deleted, setup-status attests neither configured nor pending"
        $r = Invoke-Helper '/pair' '{"code":"555555"}' $null
        Assert ($r.code -eq 200) "third phone pairs while VLC is running"
        $paired3 = $r.body | ConvertFrom-Json
        Assert ($paired3.vlcPending -eq $true) "pair reports the vlcrc write is pending while VLC runs"
        $pendingPw = $paired3.vlcPassword
        Assert ($pendingPw.Length -ge 16) "pair hands over the pending password"
        $cfgText = Get-Content $cfg -Raw
        Assert ($cfgText -match '"pendingVlcPassword"') "the pending write is persisted in config.json"
        Stop-HelperVerified
        Stop-Process -Id $fakeVlc.Id -Force
        Start-Sleep -Seconds 1
        Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
        $p = Start-Process $exe -PassThru
        Wait-HelperListening $p.Id "helper did not restart after VLC closed"
        $vlcrc = Join-Path $env:APPDATA 'vlc\vlcrc'
        Assert (Test-Path $vlcrc) "the restarted helper completed the deferred vlcrc write"
        $vlcrcText = Get-Content $vlcrc -Raw
        Assert ($vlcrcText -match "http-password=$pendingPw") "vlcrc has the pending password"
        Assert ($vlcrcText -match 'extraintf=http') "vlcrc enables the web interface"
        Assert ($vlcrcText -match 'http-host=0.0.0.0') "vlcrc listens on the LAN"
        Start-Sleep -Seconds 1
        $cfgText = Get-Content $cfg -Raw
        Assert (-not ($cfgText -match '"pendingVlcPassword":"')) "the completed pending write is cleared from config.json"
        $r = Invoke-HelperGet '/vlc/setup-status' $token
        Assert (($r.body | ConvertFrom-Json).configured -eq $true) "setup-status attests configured after the completed write"

        # Re-pairing while VLC runs with those exact settings must NOT defer: the running
        # VLC read this vlcrc at startup, so control works now - a pending write here would
        # falsely tell the owner a reopen is required.
        $fakeVlc2 = Start-Process (Join-Path $env:TEMP 'vlc.exe') -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 600' -PassThru
        Start-Sleep -Seconds 2
        Assert (-not $fakeVlc2.HasExited) "the simulated VLC is running for the re-pair test"
        Stop-HelperVerified
        $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '777777'
        $p = Start-Process $exe -PassThru
        Wait-HelperListening $p.Id "helper did not restart for the re-pair test"
        $r = Invoke-Helper '/pair' '{"code":"777777"}' $null
        Assert ($r.code -eq 200) "fourth phone pairs while VLC runs with the settings already written"
        Assert ((($r.body | ConvertFrom-Json).vlcPending) -ne $true) "no false pending when vlcrc already has the settings"
        $r = Invoke-HelperGet '/vlc/setup-status' $token
        $ss = $r.body | ConvertFrom-Json
        Assert ($ss.configured -eq $true -and $ss.pending -eq $false) "setup-status attests configured, not pending, while VLC runs with the settings"
        Stop-Process -Id $fakeVlc2.Id -Force -ErrorAction SilentlyContinue
    } finally {
        Get-Process vlc -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    # Movies: the folder is jailed, listed, and a real launch starts VLC.
    Write-Host "== movies =="
    Assert ((Test-Path 'C:\Program Files\VideoLAN\VLC\vlc.exe') -or (Get-Command vlc.exe -ErrorAction SilentlyContinue)) "VLC installed by the workflow step"
    $movies = Join-Path $env:TEMP 'zvr-e2e-movies'
    New-Item -ItemType Directory -Force $movies | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $movies 'Sub') | Out-Null
    Set-Content (Join-Path $movies 'clip.mkv') 'fake'
    Set-Content (Join-Path $movies 'Sub/inner.mkv') 'fake'
    Set-Content (Join-Path $movies 'notes.txt') 'not a video'
    $folderJson = '{"moviesFolder":"' + ($movies -replace '\\','/') + '"}'
    $r = Invoke-Helper '/config' $folderJson $token
    Assert ($r.code -eq 200) "movies folder set from the phone"
    $r = Invoke-HelperGet '/movies/list?path=' $token
    Assert ($r.code -eq 200) "list the movies folder root"
    $list = $r.body | ConvertFrom-Json
    Assert ($list.atRoot -eq $true) "root reports atRoot"
    Assert ($list.entries[0].name -eq 'Sub' -and $list.entries[0].dir -eq $true) "folders list first"
    Assert (($list.entries | Where-Object { $_.name -eq 'clip.mkv' }).Count -eq 1) "video file listed"
    Assert (($list.entries | Where-Object { $_.name -eq 'notes.txt' }).Count -eq 0) "non-video files hidden"
    $r = Invoke-HelperGet '/movies/list?path=Sub' $token
    Assert (($r.body | ConvertFrom-Json).entries[0].name -eq 'inner.mkv') "subfolder lists"
    $r = Invoke-HelperGet '/movies/list?path=..' $token
    Assert ($r.code -eq 404) "list outside the folder is refused (got $($r.code))"
    $r = Invoke-Helper '/movies/play' '{"path":"../Windows/win.ini"}' $token
    Assert ($r.code -eq 400) "play outside the folder is refused (got $($r.code))"
    $r = Invoke-Helper '/movies/play' '{"path":"clip.mkv"}' $token
    Assert ($r.code -eq 200) "play starts VLC: $($r.body)"
    $vlcUp = $false
    foreach ($i in 1..20) {
        if (Get-Process vlc -ErrorAction SilentlyContinue) { $vlcUp = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert $vlcUp "VLC is running after play"
    Get-Process vlc -ErrorAction SilentlyContinue | Stop-Process -Force

    # B4: a saved place must SURVIVE a helper restart - the /movies/play answer asks
    # Resume / Start over instead of wiping older places.
    $clipFull = [IO.Path]::GetFullPath((Join-Path $movies 'clip.mkv'))
    $resumeDir = Join-Path $env:APPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $resumeDir | Out-Null
    # Seed AFTER killing the old instance: its poller saves the in-memory places
    # whenever VLC is down, which would clobber a seed written before the kill.
    Stop-HelperVerified
    $resumeJson = '{"' + ($clipFull -replace '\\','\\') + '":{"Position":120.0,"Length":600.0}}'
    Set-Content (Join-Path $resumeDir 'resume-places.json') $resumeJson -NoNewline
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the resume check"
    $rResume = Invoke-Helper '/movies/play' '{"path":"clip.mkv"}' $token
    Write-Host "resume play answer: $($rResume.code) $($rResume.body)"
    Assert ($rResume.body -match '"ask"') "a saved place survives a restart - play asks Resume / Start over"
    Assert ($rResume.body -match '"position":120') "the saved place's position comes back"

    # B4 end-to-end: NOT a seeded fixture - the helper itself records the place from
    # VLC's live status and persists it, then a restart still asks Resume / Start
    # over. This covers the write path; the seeded case above covers the load path.
    & ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=duration=400:size=320x240:rate=5 -c:v mpeg4 -y (Join-Path $movies 'long.mkv')
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg could not make the long clip" }
    Stop-HelperVerified
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the end-to-end resume check"
    $rLong = Invoke-Helper '/movies/play' '{"path":"long.mkv","resume":false}' $token
    Assert ($rLong.code -eq 200) "end-to-end resume: the long clip starts (start-over arm)"
    $vlcUp = $false
    foreach ($i in 1..40) {
        if (Get-Process vlc -ErrorAction SilentlyContinue) { $vlcUp = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert $vlcUp "end-to-end resume: VLC is running the long clip"
    # Seek past the 60s ask floor through VLC's own web interface. The password comes
    # from vlcrc - what the RUNNING VLC actually read at launch - not a saved pair
    # variable: earlier cases re-pair and complete pending writes, so $paired's
    # password is stale by now.
    $vlcrcNow = Join-Path $env:APPDATA 'vlc\vlcrc'
    $vlcPw = (Select-String -Path $vlcrcNow -Pattern '^http-password=(.+)$').Matches[0].Groups[1].Value.Trim()
    Assert ($vlcPw.Length -ge 16) "vlcrc carries the current VLC password for the seek"
    $vlcAuth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(':' + $vlcPw))
    $seeked = $false
    foreach ($i in 1..30) {
        try {
            $st = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/requests/status.json?command=seek&val=120' -Headers @{ Authorization = "Basic $vlcAuth" }
            if ($st.time -ge 100) { $seeked = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    Assert $seeked "end-to-end resume: VLC seeks past the ask floor"
    Start-Sleep -Seconds 7   # one 5s poller tick records the place in memory
    Get-Process vlc -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 7   # the VLC-is-down tick persists the places to disk
    Stop-HelperVerified
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart after the app's own save"
    $rResume2 = Invoke-Helper '/movies/play' '{"path":"long.mkv"}' $token
    Write-Host "end-to-end resume play answer: $($rResume2.code) $($rResume2.body)"
    Assert ($rResume2.body -match '"ask"') "end-to-end resume: the helper's own saved place survives a restart"
    Assert ($rResume2.body -match '"position":1[0-4][0-9]') "end-to-end resume: the saved position is the one VLC reported"
    Get-Process vlc -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item (Join-Path $resumeDir 'resume-places.json') -Force -ErrorAction SilentlyContinue

    # UDP discovery answers the app's broadcast.
    Write-Host "== UDP discovery =="
    $udp = New-Object Net.Sockets.UdpClient
    $udp.Client.ReceiveTimeout = 5000
    $magic = [Text.Encoding]::UTF8.GetBytes('BINGEMOTE_DISCOVER_V1')
    $udp.Send($magic, $magic.Length, '127.0.0.1', 8491) | Out-Null
    $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
    $reply = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$ep))
    $udp.Close()
    Assert ($reply -like 'ZEPHYR_VLC_REMOTE_HERE_V1|*') "discovery reply carries the Zephyr VLC Remote identity: $reply"
    Assert ($reply -like '*|8492') "discovery reply advertises HTTP port 8492: $reply"

    # A copy launched from a different folder (an "installed" copy vs a Downloads copy)
    # takes over: the running copy quits, the new one serves the same token.
    Write-Host "== update takeover across paths =="
    # Round-5 pairing guards: lockout after 5 wrong codes, and a guess while no code
    # is showing must NOT count. Fresh restart resets the in-memory counter.
    Write-Host "== pairing lockout =="
    Stop-HelperVerified
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the lockout check"
    foreach ($n in 1..5) {
        $rWrong = Invoke-Helper '/pair' '{"code":"999999"}' $null
        Assert ($rWrong.code -eq 403) "wrong code $n of 5 is 403"
    }
    $rLocked = Invoke-Helper '/pair' '{"code":"999999"}' $null
    Assert ($rLocked.code -eq 429) "the 6th wrong code is 429 (locked)"
    $rLockedRight = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rLockedRight.code -eq 429) "even the right code is 429 while locked"
    Assert ($rLocked.body -match 'Pair again') "the lockout text says press Pair again, not restart"

    # Round-5 B3: a corrupt config fails CLOSED - file preserved, no code minted,
    # pairing refused, nothing overwritten.
    Write-Host "== corrupt config fails closed =="
    Stop-HelperVerified
    $ownDir2 = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $ownDir2 | Out-Null
    $corruptPath = Join-Path $ownDir2 'config.json'
    Set-Content $corruptPath '{"phones":[{"token":"incomplete' -NoNewline
    $before = Get-Content $corruptPath -Raw
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not start on the corrupt-config run"
    $rCorrupt = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rCorrupt.code -eq 503) "a broken store refuses pairing with 503 (got $($rCorrupt.code))"
    Assert ($rCorrupt.body -match 'detail') "the broken store answers with a written-for-him detail"
    Assert ((Get-Content $corruptPath -Raw) -eq $before) "the unreadable config is preserved byte-for-byte"
    Stop-HelperVerified
    Remove-Item $ownDir2 -Recurse -Force -ErrorAction SilentlyContinue

    # Round-6: a MIXED config - one fine plain-format record plus one undecryptable
    # record - also fails the whole store closed; nothing is skipped or overwritten.
    Write-Host "== mixed config fails closed =="
    $ownDir3 = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $ownDir3 | Out-Null
    $mixPath = Join-Path $ownDir3 'config.json'
    $mixText = '{"phones":[{"token":"' + ('c3' * 32) + '","name":"plain legacy"},{"token":"dpapi:!!!!not-base64!!!!","name":"broken record"}]}'
    Set-Content $mixPath $mixText -NoNewline
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not start on the mixed-record run"
    $rMix = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rMix.code -eq 503) "one unreadable record fails the whole store closed (got $($rMix.code))"
    Assert ($rMix.body -match 'detail') "the mixed broken store answers with a written-for-him detail"
    Assert ((Get-Content $mixPath -Raw) -eq $mixText) "the mixed config is preserved byte-for-byte"
    Stop-HelperVerified
    Remove-Item $ownDir3 -Recurse -Force -ErrorAction SilentlyContinue
    # Restore a good paired state for the takeover phase.
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the takeover phase"
    $rPair2 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rPair2.code -eq 200) "re-pair after the corrupt-config phase"
    $token = ($rPair2.body | ConvertFrom-Json).token

    # RUNNER_TEMP, not TEMP: the runner's $env:TEMP is the 8.3 short form
    # (C:\Users\RUNNER~1\...) and netsh SILENTLY refused to add a rule whose
    # program path used it (diag/takeover-probe forensics: the swap's delete
    # landed, the add produced no rule, and the gate then failed the temp copy).
    $dir2 = Join-Path $env:RUNNER_TEMP "zvr-e2e-takeover"
    New-Item -ItemType Directory -Force $dir2 | Out-Null
    Copy-Item $exe $dir2 -Force
    $exe2 = Join-Path $dir2 "ZephyrVlcRemote.exe"
    # Why this rule swap exists: the startup firewall shape gate requires the UDP
    # discovery rule to name the RUNNING exe path (round-5/7/8 design: the discovery
    # socket lives in the process, and the product deliberately prompts instead of
    # silently widening a rule). A real update never hits this: the self-updater runs
    # the downloaded INSTALLER, which reinstalls over the SAME path, so the existing
    # rule stays valid. Across-paths is only the hand-run copy case, where the explain
    # prompt is the intended behavior - and it cannot be silent, it needs admin.
    # What this leg still proves: the actual mutex/update takeover (the running copy
    # quits, asserted below) and the taking-over copy serving the saved pairings.
    # What it does NOT prove: the across-paths first-launch firewall prompt - that
    # behavior is untested (the dialog renders prove appearance only).
    function Set-TakeoverUdpRule([string]$program) {
        netsh advfirewall firewall delete rule name="Zephyr VLC Remote" protocol=UDP localport=8491 | Out-Null
        $addOut = netsh advfirewall firewall add rule name="Zephyr VLC Remote" dir=in action=allow protocol=UDP localport=8491 program="$program" profile=private remoteip=localsubnet 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "netsh add UDP rule failed (exit $LASTEXITCODE): $addOut" }
        $shown = netsh advfirewall firewall show rule name="Zephyr VLC Remote" verbose | Out-String
        $udpBlock = ($shown -split "Rule Name:" | Where-Object { $_ -match "Protocol:\s+UDP" }) -join ""
        if (-not ($udpBlock -match [regex]::Escape($program))) {
            throw "UDP rule did not land for $program - actual rules:`n$shown"
        }
    }
    Set-TakeoverUdpRule $exe2
    Write-Host "takeover rule planted for: $exe2"
    $p2 = Start-Process $exe2 -PassThru
    $oldExited = $p.WaitForExit(25000)
    Assert $oldExited "running copy quits when a copy from another folder launches"
    Wait-HelperListening $p2.Id "taking-over copy did not start listening on 8492"
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "taking-over copy serves the saved token"
    $p = $p2
    # Later phases restart from the original path: point the discovery rule back.
    Set-TakeoverUdpRule $exe

    # Round-6: a failed config SAVE refuses the pair with 503 and does NOT spend the
    # code; once the file is writable again the SAME code pairs (real write/read proof).
    Write-Host "== failed save keeps the code =="
    $cfgRo = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote\config.json'
    Set-ItemProperty $cfgRo -Name IsReadOnly -Value $true
    $rFs1 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rFs1.code -eq 503) "a failed save refuses the pair with 503 (got $($rFs1.code))"
    Assert ($rFs1.body -match 'detail') "the failed save answers with a written-for-him detail"
    $rFs2 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rFs2.code -eq 503) "the failed save did not spend the code (got $($rFs2.code))"
    Set-ItemProperty $cfgRo -Name IsReadOnly -Value $false
    $rFs3 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rFs3.code -eq 200) "the same code pairs once the file is writable again"
    Assert ((($rFs3.body | ConvertFrom-Json).token).Length -eq 64) "the retried pair returns a real token"

    # Round-6: an expired code answers 410 and a guess against it is told expired (it
    # never spends a lockout try).
    Write-Host "== expired code =="
    Stop-HelperVerified
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE_EXPIRES_PAST = '1'
    $p = Start-Process $exe -PassThru
    Wait-HelperListening $p.Id "helper did not restart for the expired-code check"
    $rEx1 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rEx1.code -eq 410) "an expired code is 410 (got $($rEx1.code): $($rEx1.body))"
    $rEx2 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rEx2.code -eq 410) "a retried expired code stays 410"
    $rEx3 = Invoke-Helper '/pair' '{"code":"999999"}' $null
    Assert ($rEx3.code -eq 410) "a wrong guess on an expired code is told expired, not wrong (got $($rEx3.code): $($rEx3.body))"
    $rEx4 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rEx4.code -eq 410) "the expired code still answers 410 after the guess - no lock was spent"
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE_EXPIRES_PAST = $null

    if ($script:assertCount -lt 80) { throw "only $script:assertCount assertions ran - the harness did not exercise the full flow" }
    Write-Host "HELPER E2E PASSED ($script:assertCount assertions)"
} finally {
    if ($p -and -not $p.HasExited) { Stop-HelperVerified }
    netsh advfirewall firewall delete rule name="Zephyr VLC Remote" | Out-Null
}
