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
$fw0 = Start-Process $installed -ArgumentList '--install-firewall' -Wait -PassThru
Assert ($fw0.ExitCode -eq 0) "installed exe's elevated firewall setup succeeds"
$ruleV = netsh advfirewall firewall show rule name="Zephyr VLC Remote" verbose | Out-String
Assert ($ruleV -match 'Zephyr VLC Remote') "elevated setup adds the firewall rule"
Assert ($ruleV -match 'Program') "the firewall rule is program-scoped, not port-only"
$acl0 = netsh http show urlacl | Out-String
Assert ($acl0 -match ':8492/') "elevated setup adds the listen permission"
# Negative hook proof on the SHIPPED build: it was published without PAIR_CODE_HOOK,
# so ZEPHYR_VLC_REMOTE_PAIR_CODE must NOT produce a working pairing.
$env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
Remove-Item (Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote') -Recurse -Force -ErrorAction SilentlyContinue
Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue | Stop-Process -Force
$pClean = Start-Process $installed -PassThru
$up = $false
foreach ($i in 1..40) {
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $up) { throw "installed clean helper did not start listening" }
$rClean = Invoke-Helper '/pair' '{"code":"424242"}' $null
Assert ($rClean.code -ne 200) "the shipped build ignores the CI pairing hook (got $($rClean.code))"
Stop-Process -Id $pClean.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
# Silent uninstall: app files go away; any leftover rules are program-scoped to the
# now-deleted exe (inert) and are removed here to keep later phases clean.
$unins = Join-Path $env:LOCALAPPDATA 'Programs\Zephyr VLC Remote\unins000.exe'
& $unins /VERYSILENT /NORESTART | Out-Null
Start-Sleep -Seconds 3
Assert (-not (Test-Path $installed)) "uninstaller removes the installed exe"
netsh advfirewall firewall delete rule name="Zephyr VLC Remote" | Out-Null
netsh advfirewall firewall delete rule name="Zephyr VLC Remote VLC" | Out-Null
netsh http delete urlacl url=http://+:8492/ | Out-Null
Write-Host "ok: per-user install, clean-build hook proof, uninstall"

# A silent install now reopens the app on purpose (an update must come back up for him),
# and the silent uninstall can miss that brand-new process. Nothing from the
# install/uninstall phase may still be running once the runtime tests start their own exe.
Get-Process ZephyrVlcRemote -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# --- Part 1: the one-time elevated setup, run as-is (CI runners are already admin) ---
Write-Host "== install-firewall =="
# The helper is a WinExe, so `&` returns immediately and $LASTEXITCODE stays empty.
$fw = Start-Process $exe -ArgumentList '--install-firewall' -Wait -PassThru
$fwExit = $fw.ExitCode
Write-Host "install-firewall exit code: $fwExit"
netsh advfirewall firewall show rule name="Zephyr VLC Remote"
$aclOut = netsh http show urlacl
$aclOut | Select-String '8492'
if ($fwExit -ne 0) { throw "install-firewall failed with exit $fwExit" }
if (-not ($aclOut | Select-String ':8492/')) { throw "URL ACL for port 8492 missing after setup" }

# --- Part 2: run the helper and drive its HTTP API over loopback ---
Write-Host "== helper runtime =="
$env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
$p = Start-Process $exe -PassThru
try {
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not start listening on 8492" }
    Write-Host "helper is listening"

    # --- Part 2a: BingeMote migration on a real old config (settled: no forced re-pair) ---
    Write-Host "== BingeMote pairing migration =="
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $legacyDir = Join-Path $env:LOCALAPPDATA 'BingeMote'
    $ownDir = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $legacyDir | Out-Null
    $legacyCfg = Join-Path $legacyDir 'config.json'
    $legacyText = '{"phones":[{"token":"' + ('a1' * 32) + '","name":"CI migration phone","adoptSpent":true}],"moviesFolder":null,"declinedNetworks":["CI-CafeWiFi"],"homeNetworks":["CI-HomeLAN"]}'
    Set-Content $legacyCfg $legacyText -NoNewline
    Remove-Item $ownDir -Recurse -Force -ErrorAction SilentlyContinue
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not start on the migration run" }
    $r = Invoke-Helper '/ping' '{}' ('a1' * 32)
    Assert ($r.code -eq 200) "an imported BingeMote pairing serves immediately (no re-pair)"
    Assert ((Get-Content $legacyCfg -Raw) -eq $legacyText) "the legacy config is byte-identical after the import"
    $ownCfgText = Get-Content (Join-Path $ownDir 'config.json') -Raw
    Assert ($ownCfgText -match 'CI migration phone') "the phone name carried over"
    Assert ($ownCfgText -match 'CI-HomeLAN') "the home Wi-Fi choice carried over"
    # A second run with an existing own config must NOT re-import or overwrite.
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    Set-Content $legacyCfg ('{"phones":[{"token":"' + ('b2' * 32) + '","name":"should never import"}]}') -NoNewline
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart for the no-reimport check" }
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart" }
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '123456'
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart for second pairing" }
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart with an unacked adopt" }
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
    $cfg = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote/config.json'
    Set-Content $cfg ('{"token":"' + $token + '","moviesFolder":null}')
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart on old config" }
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
        Stop-Process -Id $p.Id -Force
        Start-Sleep -Seconds 1
        $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '555555'
        $p = Start-Process $exe -PassThru
        $up = $false
        foreach ($i in 1..40) {
            try {
                $c = New-Object Net.Sockets.TcpClient
                $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $up) { throw "helper did not restart for the pending-write test" }
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
        Stop-Process -Id $p.Id -Force
        Start-Sleep -Seconds 1
        Stop-Process -Id $fakeVlc.Id -Force
        Start-Sleep -Seconds 1
        Remove-Item Env:ZEPHYR_VLC_REMOTE_PAIR_CODE -ErrorAction SilentlyContinue
        $p = Start-Process $exe -PassThru
        $up = $false
        foreach ($i in 1..40) {
            try {
                $c = New-Object Net.Sockets.TcpClient
                $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $up) { throw "helper did not restart after VLC closed" }
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
        Stop-Process -Id $p.Id -Force
        Start-Sleep -Seconds 1
        $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '777777'
        $p = Start-Process $exe -PassThru
        $up = $false
        foreach ($i in 1..40) {
            try {
                $c = New-Object Net.Sockets.TcpClient
                $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $up) { throw "helper did not restart for the re-pair test" }
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
    $clipFull = (Join-Path $movies 'clip.mkv')
    $resumeDir = Join-Path $env:APPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $resumeDir | Out-Null
    $resumeJson = '{"' + ($clipFull -replace '\\','\\') + '":{"Position":120.0,"Length":600.0}}'
    Set-Content (Join-Path $resumeDir 'resume-places.json') $resumeJson -NoNewline
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $env:ZEPHYR_VLC_REMOTE_PAIR_CODE = '424242'
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart for the resume check" }
    $rResume = Invoke-Helper '/movies/play' '{"path":"clip.mkv"}' $token
    Assert ($rResume.body -match '"ask"') "a saved place survives a restart - play asks Resume / Start over"
    Assert ($rResume.body -match '"position":120') "the saved place's position comes back"
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart for the lockout check" }
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
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    $ownDir2 = Join-Path $env:LOCALAPPDATA 'Zephyr VLC Remote'
    New-Item -ItemType Directory -Force $ownDir2 | Out-Null
    $corruptPath = Join-Path $ownDir2 'config.json'
    Set-Content $corruptPath '{"phones":[{"token":"incomplete' -NoNewline
    $before = Get-Content $corruptPath -Raw
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not start on the corrupt-config run" }
    $rCorrupt = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rCorrupt.code -ne 200) "a broken store refuses pairing (got $($rCorrupt.code))"
    Assert ((Get-Content $corruptPath -Raw) -eq $before) "the unreadable config is preserved byte-for-byte"
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 1
    Remove-Item $ownDir2 -Recurse -Force -ErrorAction SilentlyContinue
    # Restore a good paired state for the takeover phase.
    $p = Start-Process $exe -PassThru
    $up = $false
    foreach ($i in 1..40) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "helper did not restart for the takeover phase" }
    $rPair2 = Invoke-Helper '/pair' '{"code":"424242"}' $null
    Assert ($rPair2.code -eq 200) "re-pair after the corrupt-config phase"
    $token = ($rPair2.body | ConvertFrom-Json).token

    $dir2 = Join-Path $env:TEMP "zvr-e2e-takeover"
    New-Item -ItemType Directory -Force $dir2 | Out-Null
    Copy-Item $exe $dir2 -Force
    $exe2 = Join-Path $dir2 "ZephyrVlcRemote.exe"
    $p2 = Start-Process $exe2 -PassThru
    $oldExited = $p.WaitForExit(25000)
    Assert $oldExited "running copy quits when a copy from another folder launches"
    $up = $false
    foreach ($i in 1..40) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', 8492); $c.Close(); $up = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "taking-over copy did not start listening on 8492" }
    $r = Invoke-Helper '/ping' '{}' $token
    Assert ($r.code -eq 200) "taking-over copy serves the saved token"
    $p = $p2

    if ($script:assertCount -lt 80) { throw "only $script:assertCount assertions ran - the harness did not exercise the full flow" }
    Write-Host "HELPER E2E PASSED ($script:assertCount assertions)"
} finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    netsh advfirewall firewall delete rule name="Zephyr VLC Remote" | Out-Null
}
