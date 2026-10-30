# Subtitle POSITION reference (evidence only, CI): where real VLC desktop 3.0.24 draws an
# embedded subtitle, against Zephyr Player as it is (its current --sub-margin) and plain
# libVLC with no subtitle options (VLC's defaults), on the same clips at the same picture
# sizes. Nothing here changes Zephyr. Output: $env:RUNNER_TEMP/subref (frames, JSON, table).
param([string]$VlcVersion = '3.0.24')
$ErrorActionPreference = 'Continue'
$out = Join-Path $env:RUNNER_TEMP 'subref'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'reference-log.txt'
function Log($s) { Write-Host "SUBREF: $s"; $s | Add-Content $log }
$results = New-Object System.Collections.Generic.List[object]

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public struct SRECT { public int Left, Top, Right, Bottom; }
public struct SPOINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SDEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
}
public static class SU {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out SRECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out SRECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref SPOINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref SDEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsW(ref SDEVMODE dm, int flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    public static List<string> Modes() {
        var set = new List<string>();
        var dm = new SDEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(SDEVMODE));
        for (int i = 0; EnumDisplaySettingsW(null, i, ref dm); i++) {
            var s = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
            if (!set.Contains(s)) set.Add(s);
        }
        return set;
    }
    public static int SetMode(int w, int h) {
        var dm = new SDEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(SDEVMODE));
        EnumDisplaySettingsW(null, -1, ref dm);
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettingsW(ref dm, 0);
    }
    public static IntPtr FindByTitlePrefix(uint pid, string prefix) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(512); GetWindowTextW(h, sb, 512);
            if (sb.ToString().StartsWith(prefix)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr FindByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(512); GetWindowTextW(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static List<string> Titles(uint pid) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid) return true;
            var sb = new System.Text.StringBuilder(512); GetWindowTextW(h, sb, 512);
            list.Add((IsWindowVisible(h) ? "[visible] " : "[hidden] ") + sb.ToString());
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

function ScreenSize() { return @([SU]::GetSystemMetrics(0), [SU]::GetSystemMetrics(1)) }
function Grab($path) {
    $s = ScreenSize
    $bmp = New-Object System.Drawing.Bitmap $s[0], $s[1]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Topmost($h) { [SU]::SetWindowPos($h, [IntPtr]::new(-1), 0, 0, 0, 0, 0x13) | Out-Null }
function ClientRect($h) {
    $c = [SRECT]::new(); [void][SU]::GetClientRect($h, [ref]$c)
    $p = [SPOINT]::new(); [void][SU]::ClientToScreen($h, [ref]$p)
    return @($p.X, $p.Y, ($p.X + $c.Right), ($p.Y + $c.Bottom))
}
function Measure($png, $prefix) {
    $j = & python (Join-Path $PSScriptRoot 'subref/measure.py') $png $prefix
    try { return ($j | ConvertFrom-Json) } catch { Log "measure failed for ${png}: $j"; return $null }
}
function Park() { $s = ScreenSize; [SU]::SetCursorPos($s[0] - 1, [int]($s[1] / 2)) | Out-Null }
function Record($player, $leg, $mode, $m, $extra) {
    $o = [ordered]@{ player = $player; leg = $leg; mode = $mode; screen = (ScreenSize) -join 'x' }
    if ($m) { foreach ($p in $m.PSObject.Properties) { $o[$p.Name] = $p.Value } }
    if ($extra) { foreach ($k in $extra.Keys) { $o[$k] = $extra[$k] } }
    $results.Add([pscustomobject]$o)
    Log ("RESULT " + ([pscustomobject]$o | ConvertTo-Json -Compress -Depth 5))
}

# ---------- tools: ffmpeg, Pillow, real VLC desktop (portable zip, exact version) ----------
choco install ffmpeg --no-progress -y | Out-Null
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg -and (Test-Path 'C:\ProgramData\chocolatey\bin\ffmpeg.exe')) { $ffmpeg = 'C:\ProgramData\chocolatey\bin\ffmpeg.exe' }
if (-not $ffmpeg) { throw 'ffmpeg unavailable' }
python -m pip install --quiet pillow numpy | Out-Null

$zip = Join-Path $env:RUNNER_TEMP "vlc-$VlcVersion-win64.zip"
$base = "https://download.videolan.org/pub/videolan/vlc/$VlcVersion/win64/vlc-$VlcVersion-win64.zip"
Invoke-WebRequest -Uri $base -OutFile $zip -UseBasicParsing
Invoke-WebRequest -Uri "$base.sha256" -OutFile "$zip.sha256" -UseBasicParsing
$want = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Log "VLC desktop zip $base sha256 $got (VideoLAN's .sha256 says $want)"
if ($want -ne $got) { throw "VLC zip hash mismatch" }
Expand-Archive $zip -DestinationPath $env:RUNNER_TEMP -Force
$vlcExe = Join-Path $env:RUNNER_TEMP "vlc-$VlcVersion\vlc.exe"
$vi = (Get-Item $vlcExe).VersionInfo
Log "VLC desktop: $vlcExe ProductVersion=$($vi.ProductVersion) FileVersion=$($vi.FileVersion)"
$zephyrLibvlc = Get-ChildItem 'windows/capture-out' -Recurse -Filter libvlc.dll -ErrorAction SilentlyContinue | Select-Object -First 1
if ($zephyrLibvlc) { Log "Zephyr bundled libvlc.dll: $($zephyrLibvlc.VersionInfo.FileVersion)" }
$harnessExe = (Get-ChildItem 'scripts/subref/out' -Recurse -Filter SubRef.exe | Select-Object -First 1).FullName
Log "harness: $harnessExe"

# ---------- clips: one flat colour, embedded (muxed) SubRip track flagged default ----------
$srt = Join-Path $env:RUNNER_TEMP 'subref.srt'
[IO.File]::WriteAllText($srt, "1`r`n00:00:00,500 --> 00:02:00,000`r`nZEPHYR SUBTITLE TEST`r`n")
$srv = Join-Path $env:RUNNER_TEMP 'subref-srv/s/tok'
New-Item -ItemType Directory -Force $srv | Out-Null
$clips = @{}
foreach ($c in @(@{ n = '720'; s = '1280x720'; id = '1' }, @{ n = '1080'; s = '1920x1080'; id = '2' })) {
    $f = Join-Path $env:RUNNER_TEMP "subref-$($c.n).mkv"
    & $ffmpeg -y -f lavfi -i "color=c=0x3366AA:s=$($c.s):r=25" -i $srt -t 120 -map 0:v -map 1:s -c:v libx264 -preset ultrafast -tune stillimage -pix_fmt yuv420p -c:s srt -disposition:s:0 default -metadata:s:s:0 language=eng $f 2>$null
    Copy-Item $f (Join-Path $srv $c.id) -Force
    $clips[$c.n] = @{ file = $f; url = "http://127.0.0.1:8911/s/tok/$($c.id)" }
    Log "clip $($c.n): $f ($((Get-Item $f).Length) bytes), 120 s of #3366AA $($c.s) with one embedded SubRip line 'ZEPHYR SUBTITLE TEST' (0.5 s - 2 min, default track)"
}
$server = Start-Process python -ArgumentList "scripts/range_server.py", (Join-Path $env:RUNNER_TEMP 'subref-srv'), "8911" -PassThru -WindowStyle Hidden

# Zephyr's own setup step for its local control endpoint (the capture build's status).
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$fw = Start-Process "windows/capture-out/ZephyrPlayer.exe" -ArgumentList '--install-firewall', $sid -PassThru -Wait -WindowStyle Hidden
Log "Zephyr firewall/listen setup exit $($fw.ExitCode)"
function ZStatus() { try { return Invoke-RestMethod -Uri "http://127.0.0.1:47823/control/status" -Headers @{ Authorization = "Bearer ci-capture-token" } -TimeoutSec 5 } catch { return $null } }
function StopZephyr { Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }
function Close-FirstRunWindows($proc) {
    $seen = $false
    for ($i = 0; $i -lt 16; $i++) {
        $st = [SU]::FindByTitle($proc.Id, 'Zephyr Settings'); $pw = [SU]::FindByTitle($proc.Id, 'Zephyr Player - Pair your phone')
        if ($st -eq [IntPtr]::Zero -and $pw -eq [IntPtr]::Zero) { if ($seen -or $i -ge 7) { return }; Start-Sleep -Milliseconds 500; continue }
        $seen = $true
        if ($st -ne [IntPtr]::Zero) { [SU]::PostMessage($st, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        if ($pw -ne [IntPtr]::Zero) { [SU]::PostMessage($pw, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        Start-Sleep -Milliseconds 500
    }
}

# ---------- display modes ----------
$modes = [SU]::Modes()
Log "display at start: $((ScreenSize) -join 'x'); modes offered: $($modes -join ', ')"
function TryMode($w, $h) {
    if (-not ($modes -contains "${w}x${h}")) { Log "mode ${w}x${h} not offered"; return $false }
    $r = [SU]::SetMode($w, $h); Start-Sleep -Seconds 3
    $s = ScreenSize
    Log "ChangeDisplaySettings ${w}x${h} -> $r; screen now $($s -join 'x')"
    return ($s[0] -eq $w -and $s[1] -eq $h)
}

# ---------- the three players ----------
function Leg-Vlc($leg, $clip, [switch]$Fullscreen) {
    Park
    $va = @('--no-qt-privacy-ask', '--no-qt-updates-notif', '--no-one-instance', '--no-video-title-show', '--no-audio', '--no-qt-recentplay')
    if ($Fullscreen) { $va += '--fullscreen' }
    $va += ('"' + $clip.file + '"')
    $p = Start-Process $vlcExe -ArgumentList $va -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $h = [SU]::FindByTitlePrefix($p.Id, 'subref-') }
    if ($h -eq [IntPtr]::Zero) { Log "VLC ${leg}: no window; titles: $([SU]::Titles($p.Id) -join ' | ')"; $p | Stop-Process -Force; return }
    if (-not $Fullscreen) { [SU]::SetWindowPos($h, [IntPtr]::new(-1), 40, 20, 0, 0, 0x11) | Out-Null } else { Topmost $h }
    Start-Sleep -Seconds 9
    Park
    if (-not $Fullscreen) { Topmost $h }
    $png = Join-Path $out "vlc-$leg-screen.png"
    Grab $png
    $m = Measure $png (Join-Path $out "vlc-$leg")
    $cr = ClientRect $h
    Record 'VLC desktop' $leg $(if ($Fullscreen) { 'fullscreen' } else { 'windowed' }) $m @{ vlc_window_client = ($cr -join ','); vlc_version = $vi.ProductVersion }
    $p | Stop-Process -Force; Start-Sleep -Seconds 2
}

function Leg-Harness($name, $leg, $clip, $x, $y, $w, $hgt, $opts) {
    Park
    $state = Join-Path $out "harness-$name-$leg-state.json"
    $al = @(('"' + $clip.file + '"'), $x, $y, $w, $hgt, ('"' + $state + '"')) + ($opts | ForEach-Object { '"' + $_ + '"' })
    $p = Start-Process $harnessExe -ArgumentList $al -PassThru
    $st = $null
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; try { $st = Get-Content $state -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $st = $null }; if ($st -and $st.time_ms -ge 8000) { break } }
    Start-Sleep -Milliseconds 500
    $png = Join-Path $out "libvlc-$name-$leg-screen.png"
    Grab $png
    $m = Measure $png (Join-Path $out "libvlc-$name-$leg")
    Record "libVLC $name" $leg 'borderless window' $m @{ harness_state = $st }
    $p | Stop-Process -Force; Start-Sleep -Seconds 1
}

function Leg-Zephyr($leg, $clip, $cw, $ch, [switch]$Fullscreen) {
    StopZephyr
    Remove-Item (Join-Path $env:APPDATA 'ZephyrPlayer\settings.json') -ErrorAction SilentlyContinue
    Park
    $al = @("--play=$($clip.url)")
    if ($Fullscreen) { $al += '--fullscreen' }
    $p = Start-Process "windows/capture-out/ZephyrPlayer.exe" -ArgumentList $al -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $h = [SU]::FindByTitle($p.Id, 'Zephyr Player') }
    if ($h -eq [IntPtr]::Zero) { Log "Zephyr ${leg}: no window; titles: $([SU]::Titles($p.Id) -join ' | ')"; StopZephyr; return }
    Close-FirstRunWindows $p
    $m = $null; $tries = 0
    if (-not $Fullscreen) {
        # Size the window so its client area (the video host) is exactly the picture size.
        for ($tries = 0; $tries -lt 4; $tries++) {
            $wr = [SRECT]::new(); [void][SU]::GetWindowRect($h, [ref]$wr)
            $cr = ClientRect $h
            $dx = ($wr.Right - $wr.Left) - ($cr[2] - $cr[0]); $dy = ($wr.Bottom - $wr.Top) - ($cr[3] - $cr[1])
            [SU]::SetWindowPos($h, [IntPtr]::new(-1), 20, 10, $cw + $dx, $ch + $dy, 0) | Out-Null
            Start-Sleep -Milliseconds 800
            $cr = ClientRect $h
            if (($cr[2] - $cr[0]) -eq $cw -and ($cr[3] - $cr[1]) -eq $ch) { break }
        }
    } else { Topmost $h }
    # Wait for playback past the title OSD time, then let the bar auto-hide (3 s idle).
    for ($i = 0; $i -lt 60; $i++) { $s = ZStatus; if ($s -and $s.state -eq 'Playing' -and $s.time -ge 8) { break }; Start-Sleep -Milliseconds 500 }
    Park; Start-Sleep -Milliseconds 4500
    Topmost $h
    $png = Join-Path $out "zephyr-$leg-screen.png"
    Grab $png
    $m = Measure $png (Join-Path $out "zephyr-$leg")
    $s1 = ZStatus
    # Bar shown: a real mouse move over the picture.
    $cr = ClientRect $h
    [SU]::SetCursorPos([int](($cr[0] + $cr[2]) / 2), [int](($cr[1] + $cr[3]) / 2)) | Out-Null; Start-Sleep -Milliseconds 300
    [SU]::SetCursorPos([int](($cr[0] + $cr[2]) / 2) + 12, [int](($cr[1] + $cr[3]) / 2)) | Out-Null; Start-Sleep -Milliseconds 900
    $png2 = Join-Path $out "zephyr-$leg-bar-shown-screen.png"
    Grab $png2
    $s2 = ZStatus
    $null = Measure $png2 (Join-Path $out "zephyr-$leg-bar-shown")
    Record 'Zephyr (current)' $leg $(if ($Fullscreen) { 'fullscreen' } else { 'windowed' }) $m @{ client = ($cr -join ','); state = $s1.state; time_s = $s1.time; spu = $s1.spu; bar_hidden_opacity = $s1.ui.bar_opacity; bar_shown_opacity = $s2.ui.bar_opacity; bar_screen = ($s2.ui.bar_screen -join ','); subtitle_size = $s2.ui.subtitle_size; fullscreen = $s2.ui.fullscreen }
    StopZephyr
}

# Zephyr's subtitle options minus --sub-margin (size M at 96 DPI), copied from
# PlayerBarLogic.SubtitleOptions: what Zephyr would draw with VLC's default position.
$zephyrNoMargin = @('--sub-text-scale=62', '--freetype-font=Segoe UI', '--freetype-bold', '--freetype-color=16777215', '--freetype-outline-thickness=2', '--freetype-outline-opacity=255', '--freetype-shadow-opacity=200', '--no-audio', '--no-video-title-show')
$libvlcDefault = @('--no-audio', '--no-video-title-show')

# Phase A: 1920x1080 screen. 720p windowed (picture 1280x720), and fullscreen 1080.
if (TryMode 1920 1080) {
    Leg-Vlc '720-windowed' $clips['720']
    Leg-Zephyr '720-windowed' $clips['720'] 1280 720
    Leg-Harness 'default' '720-windowed' $clips['720'] 40 40 1280 720 $libvlcDefault
    Leg-Harness 'zephyr-size-no-margin' '720-windowed' $clips['720'] 40 40 1280 720 $zephyrNoMargin
    Leg-Vlc '1080-fullscreen' $clips['1080'] -Fullscreen
    Leg-Zephyr '1080-fullscreen' $clips['1080'] 0 0 -Fullscreen
    Leg-Harness 'default' '1080-fullscreen' $clips['1080'] 0 0 1920 1080 $libvlcDefault
    Leg-Harness 'zephyr-size-no-margin' '1080-fullscreen' $clips['1080'] 0 0 1920 1080 $zephyrNoMargin
} else { Log "1920x1080 screen not available: phase A skipped" }

# Phase B: a screen taller than 1080 so a 1920x1080 picture fits in a WINDOW.
$big = $modes | ForEach-Object { $wh = $_ -split 'x'; [pscustomobject]@{ w = [int]$wh[0]; h = [int]$wh[1] } } | Where-Object { $_.w -ge 1960 -and $_.h -ge 1250 } | Sort-Object { $_.w * $_.h } | Select-Object -First 1
if ($big -and (TryMode $big.w $big.h)) {
    Leg-Vlc '1080-windowed' $clips['1080']
    Leg-Zephyr '1080-windowed' $clips['1080'] 1920 1080
    Leg-Harness 'default' '1080-windowed' $clips['1080'] 20 20 1920 1080 $libvlcDefault
    Leg-Harness 'zephyr-size-no-margin' '1080-windowed' $clips['1080'] 20 20 1920 1080 $zephyrNoMargin
} else { Log "no display mode taller than 1250 px with width >= 1960 offered: 1080 WINDOWED is not feasible on this runner (1080 measured fullscreen only)" }

$results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'reference-results.json')
$server | Stop-Process -Force -ErrorAction SilentlyContinue
$n = ($results | Where-Object { $_.baseline_to_picture_bottom -ne $null }).Count
Log "measured legs: $n of $($results.Count)"
if ($n -lt 4) { throw "too few measured legs ($n)" }
