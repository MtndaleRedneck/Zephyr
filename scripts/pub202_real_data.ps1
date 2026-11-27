# Evidence step: real app-created 2.0.2 data -> candidate. Nothing here writes settings, history or pairing
# data by hand. The PUBLISHED Zephyr-PC-2.0.2.exe is installed and driven through its own production paths:
#   pairing  : first run opens Settings > Paired phones; UI Automation types the phone name and invokes Pair;
#              the code the app shows (separators stripped, the server compares exactly) is traded at the real
#              POST /pair for a token; the app itself wraps that token with CurrentUser DPAPI into paired.json.
#   settings : the real route POST /library/folder (the app writes LibraryFolder) and a real Settings toggle
#              (ShowSpeed) invoked through UI Automation; the app writes settings.json.
#   history  : the real token plays a real local library file (file:// inside that folder), seeks, and the app's
#              own 5-second progress tick writes playback-history.json (2.0.2 only records local files).
# Then the candidate installer runs over the data and the candidate app starts on it. ALL of the following
# must hold or this step fails: pairing, settings and history are each app-created; the three files are
# byte-identical after the installer; the REAL token authenticates on the candidate (200) and a wrong one does
# not (401); the same paired entry stays DPAPI-protected; ShowSpeed and LibraryFolder are unchanged; and the
# history entry keeps the exact PositionMs, DurationMs, FileSize and ModifiedUtc the 2.0.2 app wrote.
# The token is held in memory only and is never printed or written. Cleanup always runs.
# Not covered here, by construction: a 2.0.2 history item always has DurationMs > 0, so the unknown-length
# fill branch of the candidate cannot be produced by the real 2.0.2 app (unit test only).
param(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$OldSha256,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedHead
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$report = Join-Path $env:RUNNER_TEMP 'pub202-real-data.txt'
Remove-Item $report -ErrorAction SilentlyContinue
# A stale failure image from an earlier invocation must never be mistaken for this run's evidence.
Remove-Item (Join-Path $env:RUNNER_TEMP 'pub202-failure.png') -Force -ErrorAction SilentlyContinue
function Note($k, $v) { $l = "${k}: $v"; Write-Host $l; Add-Content -Path $report -Value $l }
Add-Type -Namespace ZW -Name Inp -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, System.UIntPtr extra);
'@
function Stop-Player { Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }
function Find-InProc($procId, $id) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)) {
        $e = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ic)
        if ($e) { return $e }
    }
    return $null
}
function Wait-InProc($procId, $id, $seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) { $e = Find-InProc $procId $id; if ($e) { return $e }; Start-Sleep -Milliseconds 500 }
    return $null
}
function Invoke-Uia($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-Server($seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 47823); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}
# Every call returns its status code; callers assert the exact code they need. -1 means unreachable.
function Http($method, $path, $token, $body) {
    try {
        $h = @{}; if ($token) { $h['Authorization'] = "Bearer $token" }
        $r = Invoke-WebRequest -Method $method -Uri "http://127.0.0.1:47823$path" -Headers $h -ContentType 'application/json' -Body $body -UseBasicParsing -SkipHttpErrorCheck -TimeoutSec 10
        return $r
    } catch { return $null }
}
function Expect($what, $method, $path, $token, $body, [int]$want) {
    $r = Http $method $path $token $body
    $got = if ($r) { [int]$r.StatusCode } else { -1 }
    Note "http[$what]" "$method $path -> $got (want $want)"
    if ($got -ne $want) { throw "$what answered $got, expected $want" }
    return $r
}
# Candidate is HTTPS-only: pinned-HTTPS helpers (Get-TlsStatus) pin the identity the candidate's own setup creates.
. (Join-Path $PSScriptRoot 'zephyr_tls_pin.ps1')
function Uninstall-Player {
    Stop-Player
    $u = "$env:LOCALAPPDATA\Programs\Zephyr Player\unins000.exe"
    if (Test-Path $u) {
        $pu = Start-Process $u -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
        $null = $pu.WaitForExit(150000)
    }
    Remove-Item "$env:LOCALAPPDATA\Programs\Zephyr Player" -Recurse -Force -ErrorAction SilentlyContinue
}

[Environment]::SetEnvironmentVariable('ZEPHYR_CI_CAPTURE', $null, 'Process')
$dataDir = "$env:APPDATA\ZephyrPlayer"
$lib = Join-Path $env:RUNNER_TEMP 'pub202lib'
$srv = $null
$allAssertionsPassed = $false
try {
    # --- clean slate, published installer, sha and version ---
    Uninstall-Player
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    $dl = Join-Path $env:RUNNER_TEMP 'pub202real'
    $global:LASTEXITCODE = 0
    gh release download player-v2.0.2 -p 'Zephyr-PC-2.0.2.exe' --dir $dl --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release download exit $LASTEXITCODE" }
    $old = Join-Path $dl 'Zephyr-PC-2.0.2.exe'
    $sha = (Get-FileHash $old -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -ne $OldSha256) { throw "published 2.0.2 installer sha256 is $sha" }
    $op = Start-Process $old -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS' -PassThru
    if (-not $op.WaitForExit(150000)) { throw 'published 2.0.2 installer timed out' }
    if ($op.ExitCode -ne 0) { throw "published 2.0.2 installer exit $($op.ExitCode)" }
    $exe = "$env:LOCALAPPDATA\Programs\Zephyr Player\ZephyrPlayer.exe"
    $pv0 = (Get-Item $exe).VersionInfo.ProductVersion
    if ($pv0 -notlike '2.0.2*') { throw "installed ProductVersion is $pv0" }
    Stop-Player
    Note 'published_202_installed' "sha256 $sha, ProductVersion $pv0"
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $fw = Start-Process $exe -ArgumentList '--install-firewall', $sid -PassThru -Wait -WindowStyle Hidden
    if ($fw.ExitCode -ne 0) { throw "2.0.2 --install-firewall exit $($fw.ExitCode)" }

    # --- media: reuse the clip the capture step already made; else an ffmpeg already on the runner. No install. ---
    New-Item -ItemType Directory -Force $lib | Out-Null
    $movie = Join-Path $lib 'Real202Movie.mp4'
    $existingClip = Join-Path $env:RUNNER_TEMP 'clip.mp4'
    if (Test-Path $existingClip) { Copy-Item $existingClip $movie -Force }
    else {
        $ff = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
        if (-not $ff -and (Test-Path 'C:\ProgramData\chocolatey\bin\ffmpeg.exe')) { $ff = 'C:\ProgramData\chocolatey\bin\ffmpeg.exe' }
        if (-not $ff) { throw 'no fixture media: neither the capture clip nor an ffmpeg is present on the runner' }
        $global:LASTEXITCODE = 0
        & $ff -y -f lavfi -i 'testsrc=duration=300:size=320x240:rate=10' -pix_fmt yuv420p $movie 2>$null
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg fixture exit $LASTEXITCODE" }
    }
    if (-not (Test-Path $movie) -or (Get-Item $movie).Length -lt 10000) { throw 'fixture movie missing or too small' }

    # --- the published app, no hooks, on a data folder it creates itself ---
    if (Test-Path $dataDir) { throw 'provenance: the data folder existed before the published app first ran' }
    $p = Start-Process $exe -PassThru
    if (-not (Wait-Server 40)) { throw 'published 2.0.2 control server never came up' }
    $nameBox = Wait-InProc $p.Id 'PhoneNameBox' 40
    if (-not $nameBox) { throw 'Paired phones screen (PhoneNameBox) not found through UI Automation' }
    $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Real 202 phone')
    $pairBtn = Find-InProc $p.Id 'PairButton'
    if (-not $pairBtn) { throw 'PairButton not found' }
    Invoke-Uia $pairBtn
    $code = $null
    for ($i = 0; $i -lt 20 -and -not $code; $i++) {
        Start-Sleep -Milliseconds 500
        $ce = Find-InProc $p.Id 'PairCodeText'
        if ($ce) {
            # The app shows the code with a space and thin spaces; the server compares the exact digits.
            $digits = ($ce.Current.Name -replace '[^0-9]', '')
            if ($digits -match '^\d{4,8}$') { $code = $digits }
        }
    }
    if (-not $code) { throw 'the app never showed a pairing code' }
    $pr = Expect 'pair' 'Post' '/pair' $null (@{ code = $code } | ConvertTo-Json -Compress) 200
    $token = ($pr.Content | ConvertFrom-Json).token
    if (-not $token) { throw 'POST /pair returned no token' }
    $paired = Get-Content "$dataDir\paired.json" -Raw
    if ($paired -notmatch 'dpapi:' -or $paired.Contains($token)) { throw 'paired.json is not DPAPI-protected by the app' }
    $entries = @($paired | ConvertFrom-Json)
    if ($entries.Count -ne 1 -or $entries[0].name -ne 'Real 202 phone') { throw 'paired.json does not hold the one phone the app paired' }
    Note 'pairing_created_by_real_202' "PROVEN (paired.json written by the published app: 1 entry, DPAPI-protected, token length $($token.Length) not printed)"

    # --- settings: the app's own library route and a real Settings toggle ---
    $null = Expect 'library-folder-set' 'Post' '/library/folder' $token (@{ folder = $lib } | ConvertTo-Json -Compress) 200
    $gr = Expect 'library-folder-get' 'Get' '/library/folder' $token $null 200
    if (($gr.Content | ConvertFrom-Json).libraryFolder -ne $lib) { throw 'the app did not keep the library folder it was given' }
    $nav = Find-InProc $p.Id 'NavBar'
    if (-not $nav) { throw 'NavBar not found through UI Automation' }
    Invoke-Uia $nav
    Start-Sleep -Seconds 1
    $tg = Wait-InProc $p.Id 'SpeedToggle' 15
    if (-not $tg) { throw 'SpeedToggle not found through UI Automation' }
    $tp = $tg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $speedBefore = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    # UIA TogglePattern.Toggle() flips the checkbox but does NOT raise the WPF Click event the 2.0.2 Settings
    # window saves from, so it never persists. Drive the production Click path with a REAL mouse click on the
    # control. Coordinates: UIA BoundingRectangle and SetCursorPos are both read in this one process, the same
    # pairing scripts/pc_desktop_capture.ps1 already uses for its verified PlayPauseButton click.
    # Keyboard focus is logged for diagnosis only; it is not required.
    try {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
        $top = $tg
        while ($true) { $par = $walker.GetParent($top); if (-not $par -or [System.Windows.Automation.Automation]::Compare($par, $rootEl)) { break }; $top = $par }
        $hwnd = [IntPtr]$top.Current.NativeWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { throw 'could not find the Settings window handle for SpeedToggle' }
        $tgRid = ($tg.GetRuntimeId() -join '.')
        # Resolves the point to the exact SpeedToggle (or a descendant of it) in the app's pid.
        function Test-HitIsToggle([int]$x, [int]$y) {
            $h = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($x, $y)))
            if (-not $h) { return @($false, 'none') }
            $info = "id='$($h.Current.AutomationId)' pid=$($h.Current.ProcessId) type=$($h.Current.ControlType.ProgrammaticName)"
            $w = $h
            for ($d = 0; $d -lt 6 -and $w; $d++) {
                if (($w.GetRuntimeId() -join '.') -eq $tgRid -and $w.Current.ProcessId -eq $p.Id) { return @($true, $info) }
                $w = $walker.GetParent($w)
            }
            return @($false, $info)
        }
        $clicked = $false
        for ($try = 1; $try -le 5 -and -not $clicked; $try++) {
            [ZW.Inp]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 500
            $fgH = [ZW.Inp]::GetForegroundWindow()
            [uint32]$fgPid = 0
            [void][ZW.Inp]::GetWindowThreadProcessId($fgH, [ref]$fgPid)
            $fe = [System.Windows.Automation.AutomationElement]::FocusedElement
            # Ids, control types and pids only: element NAMES can carry the pairing code or phone name, so none are logged.
            $feInfo = if ($fe) { "id='$($fe.Current.AutomationId)' type=$($fe.Current.ControlType.ProgrammaticName) pid=$($fe.Current.ProcessId)" } else { 'none' }
            $r = $tg.Current.BoundingRectangle
            $vsb = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $finite = $true
            foreach ($n in $r.X, $r.Y, $r.Width, $r.Height) { if ([double]::IsNaN($n) -or [double]::IsInfinity($n)) { $finite = $false } }
            $okTarget = ($tg.Current.ProcessId -eq $p.Id -and -not $tg.Current.IsOffscreen -and $tg.Current.IsEnabled -and $finite -and $r.Width -gt 4 -and $r.Height -gt 4 -and `
                $r.X -ge $vsb.Left -and $r.Y -ge $vsb.Top -and ($r.X + $r.Width) -le $vsb.Right -and ($r.Y + $r.Height) -le $vsb.Bottom)
            Note 'speedtoggle_attempt' "try $try; target pid=$($tg.Current.ProcessId) (app $($p.Id)) enabled=$($tg.Current.IsEnabled) offscreen=$($tg.Current.IsOffscreen) bounds=$r (all four finite=$finite, inside virtual screen $($vsb.Left),$($vsb.Top) $($vsb.Width)x$($vsb.Height)); foreground hwnd=$($fgH.ToInt64()) pid=$fgPid (app $($p.Id)) equals the Settings window hwnd ($($hwnd.ToInt64()))=$($fgH -eq $hwnd); focused element $feInfo"
            if (-not $okTarget) { Start-Sleep -Seconds 1; continue }
            $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
            $v1 = Test-HitIsToggle $cx $cy
            Note 'speedtoggle_hit_test' "point $cx,$cy -> $($v1[1]); resolves to SpeedToggle=$($v1[0])"
            if (-not $v1[0]) { Start-Sleep -Seconds 1; continue }
            $moved = [ZW.Inp]::SetCursorPos($cx, $cy)
            Start-Sleep -Milliseconds 100
            $cp = New-Object 'ZW.Inp+POINT'
            $gotPos = [ZW.Inp]::GetCursorPos([ref]$cp)
            $posOk = ($moved -and $gotPos -and [Math]::Abs($cp.X - $cx) -le 1 -and [Math]::Abs($cp.Y - $cy) -le 1)
            # Re-verify immediately before pressing: same point, cursor really there, same target under it.
            $v2 = if ($posOk) { Test-HitIsToggle $cx $cy } else { @($false, 'cursor not at the target') }
            Note 'speedtoggle_pre_click' "SetCursorPos=$moved cursor=$($cp.X),$($cp.Y) want $cx,$cy ok=$posOk; re-hit resolves to SpeedToggle=$($v2[0]) ($($v2[1]))"
            if (-not ($posOk -and $v2[0])) { Start-Sleep -Seconds 1; continue }
            [ZW.Inp]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [ZW.Inp]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            $clicked = $true
        }
        if (-not $clicked) { throw 'no verified real click on SpeedToggle was possible (target checks or the point hit test never matched)' }
        $speedAfter = $speedBefore
        for ($i = 0; $i -lt 10 -and $speedAfter -eq $speedBefore; $i++) {
            Start-Sleep -Milliseconds 500
            $speedAfter = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        }
        Note 'speedtoggle_click' "verified real mouse click delivered; toggle $speedBefore -> $speedAfter"
        if ($speedAfter -eq $speedBefore) { throw "the ShowSpeed toggle did not change after the verified click ($speedBefore)" }
    }
    catch {
        # Exception TYPE only: a message can carry UI text. No screenshot is taken (the run artifact is public).
        Note 'speedtoggle_failure' $_.Exception.GetType().Name
        throw
    }
    Start-Sleep -Seconds 1
    $s0 = Get-Content "$dataDir\settings.json" -Raw | ConvertFrom-Json
    if ($s0.ShowSpeed -ne $speedAfter -or $s0.LibraryFolder -ne $lib) { throw "settings.json does not hold the app-made values (ShowSpeed $($s0.ShowSpeed) want $speedAfter)" }
    Note 'settings_created_by_real_202' "PROVEN (settings.json written by the published app: ShowSpeed $speedBefore -> $speedAfter, LibraryFolder set through the app's own route)"

    # --- history: a real local library file, played and seeked through the app's own routes ---
    $srv = $null
    $fileUrl = ([Uri]$movie).AbsoluteUri
    $null = Expect 'play' 'Post' '/play' $token (@{ url = $fileUrl } | ConvertTo-Json -Compress) 200
    Start-Sleep -Seconds 12
    $st = Expect 'status-playing' 'Get' '/control/status' $token $null 200
    if (($st.Content | ConvertFrom-Json).state -ne 'Playing') { throw "2.0.2 is not Playing ($((($st.Content | ConvertFrom-Json).state)))" }
    # /control/seek takes {"position": <seconds>} (2.0.2 PlaybackServer.cs:266-267, PlayerView.xaml.cs:817-821)
    $null = Expect 'seek' 'Post' '/control/seek' $token '{"position":120}' 200
    Start-Sleep -Seconds 8
    $null = Expect 'pause' 'Post' '/control/pause' $token '{}' 200
    Start-Sleep -Seconds 7
    $histPath = "$dataDir\playback-history.json"
    if (-not (Test-Path $histPath)) { throw 'the published app wrote no playback-history.json for a real local file' }
    $h0 = Get-Content $histPath -Raw | ConvertFrom-Json
    $key = $movie.ToUpperInvariant()
    $item0 = $h0.$key
    if (-not $item0) { throw 'history has no entry for the played library file' }
    if ([long]$item0.DurationMs -le 0 -or [long]$item0.PositionMs -lt 100000) { throw "history values are not the played ones (position $($item0.PositionMs) ms, duration $($item0.DurationMs) ms)" }
    Note 'history_created_by_real_202' "PROVEN (playback-history.json written by the published app: PositionMs $($item0.PositionMs), DurationMs $($item0.DurationMs))"
    Stop-Player

    # --- upgrade to the candidate and prove what carried over ---
    $files = @('paired.json', 'settings.json', 'playback-history.json')
    $before = @{}
    foreach ($f in $files) { $before[$f] = (Get-FileHash "$dataDir\$f" -Algorithm SHA256).Hash }
    & (Join-Path $PSScriptRoot 'assert_installer_sha.ps1') -Path $Candidate -Label 'player-202-real-data-upgrade'
    $up = Start-Process $Candidate -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS' -PassThru
    if (-not $up.WaitForExit(150000)) { throw 'candidate installer timed out' }
    if ($up.ExitCode -ne 0) { throw "candidate installer exit $($up.ExitCode)" }
    foreach ($f in $files) {
        if ((Get-FileHash "$dataDir\$f" -Algorithm SHA256).Hash -ne $before[$f]) { throw "the installer changed $f" }
    }
    # Installer exit 0 is not proof the exe was replaced: the installed exe must now be the candidate.
    $pvNew = (Get-Item $exe).VersionInfo.ProductVersion
    # Actual stamped format (45d46f4 evidence): "<Version>+<full 40-char head>". Require it exactly.
    $wantPv = "$ExpectedVersion+$($ExpectedHead.ToLowerInvariant())"
    if ($pvNew.ToLowerInvariant() -ne $wantPv) { throw "after the candidate installer the installed ProductVersion is $pvNew, expected exactly $wantPv" }
    Note 'candidate_installed_version' "PROVEN (installed ProductVersion equals exactly $wantPv; was $pv0)"
    Note 'installer_left_real_data_identical' "PROVEN (3 real files byte-identical after the candidate installer)"
    Stop-Player
    # The candidate is HTTPS-only (no plaintext fallback) and, like a real user, runs its own one-time setup
    # (the elevated --install-firewall pass: rules, identity, https listen permission and binding) before it can
    # listen. The 2.0.2 data is untouched by it. Setup output is kept for the log; a nonzero exit fails the step.
    $setupErr = Join-Path $env:RUNNER_TEMP 'pub202-candidate-setup.err'
    $setupOut = Join-Path $env:RUNNER_TEMP 'pub202-candidate-setup.out'
    $cs = Start-Process $exe -ArgumentList '--install-firewall', $sid -PassThru -Wait -RedirectStandardError $setupErr -RedirectStandardOutput $setupOut
    Write-Output "candidate setup exit $($cs.ExitCode)"; Write-Output '--- candidate setup stderr ---'; Get-Content $setupErr -ErrorAction SilentlyContinue
    if ($cs.ExitCode -ne 0) { throw "candidate --install-firewall exit $($cs.ExitCode)" }
    foreach ($f in $files) {
        if ((Get-FileHash "$dataDir\$f" -Algorithm SHA256).Hash -ne $before[$f]) { throw "the candidate setup changed $f" }
    }
    $null = Start-Process $exe -PassThru
    if (-not (Wait-Server 40)) { throw 'candidate control server never came up on the real 2.0.2 data' }
    Start-Sleep -Seconds 4
    $c200 = $false
    for ($i = 0; $i -lt 15 -and -not $c200; $i++) { $c200 = ((Get-TlsStatus $token) -eq 200); if (-not $c200) { Start-Sleep -Seconds 1 } }
    Note 'http[candidate-real-token]' "pinned HTTPS GET /control/status -> $(if ($c200) { 200 } else { 'not 200' }) (want 200)"
    if (-not $c200) { throw 'candidate-real-token did not answer 200 over pinned HTTPS' }
    $wrong = Get-TlsStatus 'not-a-real-token'
    Note 'http[candidate-wrong-token]' "pinned HTTPS GET /control/status -> $wrong (want 401)"
    if ($wrong -ne 401) { throw "candidate-wrong-token answered $wrong, expected 401" }
    $plain = 0
    try { $plain = [int](Invoke-WebRequest -Uri 'http://127.0.0.1:47823/control/status' -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 3 -UseBasicParsing -SkipHttpErrorCheck).StatusCode } catch {}
    Note 'http[candidate-plaintext]' "plain http GET /control/status -> $plain (want not 200)"
    if ($plain -eq 200) { throw 'PLAINTEXT 200: the candidate answered the real token over plain http' }
    Stop-Player
    $paired2 = Get-Content "$dataDir\paired.json" -Raw
    $e2 = (@($paired2 | ConvertFrom-Json))[0]
    if ($paired2 -notmatch 'dpapi:' -or $paired2.Contains($token) -or $e2.name -ne $entries[0].name -or @($paired2 | ConvertFrom-Json).Count -ne 1) { throw 'paired.json changed shape or lost the phone after the candidate ran' }
    Note 'paired_after_candidate_ran' 'PROVEN (same entry name, still DPAPI-protected, plain token absent, real token authenticated with 200)'
    $s1 = Get-Content "$dataDir\settings.json" -Raw | ConvertFrom-Json
    if ($s1.ShowSpeed -ne $speedAfter -or $s1.LibraryFolder -ne $lib) { throw "settings changed after the candidate ran (ShowSpeed $($s1.ShowSpeed) want $speedAfter)" }
    foreach ($prop in $s0.PSObject.Properties) { if ("$($s1.($prop.Name))" -ne "$($prop.Value)") { throw "settings value $($prop.Name) changed after the candidate ran" } }
    Note 'settings_after_candidate_ran' "PROVEN (ShowSpeed $speedAfter, LibraryFolder and all $($s0.PSObject.Properties.Count) values unchanged)"
    $item1 = (Get-Content $histPath -Raw | ConvertFrom-Json).$key
    if (-not $item1) { throw 'the history entry is gone after the candidate ran' }
    foreach ($field in 'PositionMs', 'DurationMs', 'FileSize', 'ModifiedUtc') {
        if ("$($item1.$field)" -ne "$($item0.$field)") { throw "history $field changed after the candidate ran ($($item0.$field) -> $($item1.$field))" }
    }
    Note 'history_after_candidate_ran' "PROVEN (PositionMs $($item1.PositionMs), DurationMs $($item1.DurationMs), FileSize and ModifiedUtc exactly as the 2.0.2 app wrote them)"
    $allAssertionsPassed = $true
}
finally {
    # Leave the runner as the next step expects it: no candidate install, no app data, no rules, no reservation.
    Uninstall-Player
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $lib -Recurse -Force -ErrorAction SilentlyContinue
    # Exit codes only (no output kept): netsh returns 1 when there is nothing left to delete.
    netsh advfirewall firewall delete rule name="Zephyr Player" 2>&1 | Out-Null
    $ec1 = $LASTEXITCODE
    netsh advfirewall firewall delete rule name="Zephyr Player Discovery" 2>&1 | Out-Null
    $ec2 = $LASTEXITCODE
    netsh http delete urlacl url=http://+:47823/ 2>&1 | Out-Null
    $ec3 = $LASTEXITCODE
    Note 'cleanup_native_exit_codes' "firewall rule 1 = $ec1, firewall rule 2 = $ec2, urlacl = $ec3"
    Note 'script_state_at_exit' "all assertions passed = $allAssertionsPassed; LASTEXITCODE before reset (reflects the last cleanup netsh call only) = $LASTEXITCODE"
    # Read-only evidence, no deletes (after the exit-code notes above so it cannot alter them): what the candidate's own uninstall left of its https listen permission and binding.
    Write-Output '--- post-uninstall state (read-only): https urlacl 47823 ---'; (netsh http show urlacl url=https://+:47823/ 2>&1 | Out-String)
    Write-Output '--- post-uninstall state (read-only): sslcert 0.0.0.0:47823 ---'; (netsh http show sslcert ipport=0.0.0.0:47823 2>&1 | Out-String)
    # Only a script whose every assertion passed may drop the leaked native status of the cleanup commands. A failure
    # (throw) is never reset: it still propagates out of this finally block.
    if ($allAssertionsPassed) { $global:LASTEXITCODE = 0 }
    Write-Host 'real-data step cleanup done'
}
