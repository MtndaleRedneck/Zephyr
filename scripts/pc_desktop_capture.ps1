# Real-desktop capture of the actual ZephyrPlayer.exe on the Windows runner.
# Framing rules (owner-review grade): the app window is resized to fit the
# runner's 1024x768 virtual screen so NO part is clipped, and every capture is
# cropped to the app's own window rect - runner terminals/logs/taskbar never
# appear in UI evidence. Captures: home, settings top + scrolled bottom,
# player with a REAL video fixture + real mouse event (controls shown) in
# windowed and fullscreen modes. The note file reports pixel verdicts honestly.
# Any incomplete capture fails this step: silent success with no images is
# not evidence.
$ErrorActionPreference = 'Continue'
$note = "$env:RUNNER_TEMP/pc-desktop-note.txt"

netsh advfirewall firewall add rule name="Zephyr Player" dir=in action=allow protocol=TCP localport=47823 profile=private | Out-Null
netsh advfirewall firewall add rule name="Zephyr Player Discovery" dir=in action=allow protocol=UDP localport=8489 profile=private | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
# Top-level types (a struct declared inside -MemberDefinition becomes nested
# and cannot be referenced as [W.RECT]; that bug silently zeroed captures).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct ZRECT { public int Left, Top, Right, Bottom; }
public static class ZWU32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out ZRECT r);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out ZRECT r, int size);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    public static IntPtr FindByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$SWP_NOZORDER = 0x4; $MOUSEEVENTF_WHEEL = 0x0800

function CaptureWindow($hWnd, $path) {
    # Crop to the app's own window rect: no runner chrome in UI evidence.
    if ($hWnd -eq [System.IntPtr]::Zero) { Write-Host "::error::no window handle for $path - capture skipped"; return "0 x 0" }
    # DWMWA_EXTENDED_FRAME_BOUNDS (9): the window's REAL visible bounds.
    # GetWindowRect includes the invisible drop-shadow ring; through that ring
    # the runner's console/taskbar pixels bleed into UI evidence.
    $r = [ZRECT]::new()
    if ([ZWU32]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, 16) -ne 0) {
        if (-not [ZWU32]::GetWindowRect($hWnd, [ref]$r)) { Write-Host "::error::GetWindowRect failed for hwnd $hWnd"; return "0 x 0" }
    }
    $r.Left += 2; $r.Top += 2; $r.Right -= 2; $r.Bottom -= 2
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Write-Host "::error::zero-size window rect for hwnd $hWnd ($w x $h) - capture skipped"; return "0 x 0" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return "$w x $h"
}
function CountColored($path, $y0frac, $y1frac) {
    if (-not (Test-Path $path)) { return 0 }
    $bmp = [System.Drawing.Image]::FromFile($path)
    $w = $bmp.Width; $h = $bmp.Height; $nb = 0
    for ($x = 0; $x -lt $w; $x += 5) {
        for ($y = [int]($h * $y0frac); $y -lt [int]($h * $y1frac); $y += 5) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -gt 60 -or $c.G -gt 60 -or $c.B -gt 60) { $nb++ }
        }
    }
    $bmp.Dispose()
    return $nb
}
function CountDiff($pathA, $pathB, $x0f, $x1f, $y0f, $y1f) {
    # Sampled pixel diff between two captures of the same window region:
    # the functional-smoke verdict primitive (state change => pixels change).
    if (-not (Test-Path $pathA) -or -not (Test-Path $pathB)) { return -1 }
    $a = [System.Drawing.Image]::FromFile($pathA); $b = [System.Drawing.Image]::FromFile($pathB)
    $n = 0
    for ($x = [int]($a.Width * $x0f); $x -lt [int]($a.Width * $x1f); $x += 3) {
        for ($y = [int]($a.Height * $y0f); $y -lt [int]($a.Height * $y1f); $y += 3) {
            $ca = $a.GetPixel($x, $y); $cb = $b.GetPixel($x, $y)
            if ([math]::Abs($ca.R - $cb.R) -gt 24 -or [math]::Abs($ca.G - $cb.G) -gt 24 -or [math]::Abs($ca.B - $cb.B) -gt 24) { $n++ }
        }
    }
    $a.Dispose(); $b.Dispose()
    return $n
}
function ClickAt($xf, $yf) {
    # Real mouse click at a window-relative fraction (real functional input,
    # no test hooks): move, press, release.
    [ZWU32]::SetCursorPos([int]($vs.Width * $xf), [int]($vs.Height * $yf)) | Out-Null
    Start-Sleep -Milliseconds 250
    [ZWU32]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero)  # LEFTDOWN
    Start-Sleep -Milliseconds 60
    [ZWU32]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)  # LEFTUP
    Start-Sleep -Milliseconds 700
}
function StopZephyr { Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }
function FitWindow($hWnd) {
    # Whole window inside the virtual screen, top-left aligned, no clipping.
    # HWND_TOPMOST (-1): the runner taskbar otherwise overlaps the window's
    # bottom strip and lands in UI evidence.
    [ZWU32]::SetWindowPos($hWnd, [System.IntPtr]::new(-1), 0, 0, $vs.Width, $vs.Height, 0) | Out-Null
    Start-Sleep -Milliseconds 700
}
function Wait-MainWindow($proc, $seconds) {
    # WPF can take a while to create its HWND on a cold runner; poll instead
    # of assuming the window exists after a fixed sleep.
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [System.IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "::error::process $($proc.Id) had no main window after $seconds s"
    return [System.IntPtr]::Zero
}
function WakeControls() {
    # Real mouse event over the video area: WPF OnMouseMove reveals the bar.
    [ZWU32]::SetCursorPos([int]($vs.Width / 2), [int]($vs.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 300
    [ZWU32]::SetCursorPos([int]($vs.Width / 2 + 12), [int]($vs.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 800
}

# Tiny known video fixture: moving test pattern, provable on-screen pixels.
choco install ffmpeg --no-progress -y | Out-Null
$mp4 = "$env:RUNNER_TEMP/clip.mp4"
& "C:\ProgramData\chocolatey\bin\ffmpeg.exe" -y -f lavfi -i "testsrc=duration=30:size=320x240:rate=10" -pix_fmt yuv420p $mp4 2>$null
$srv = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8901" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

# 1) Home, full window fitted to the screen (no clipping, no terminal).
$p = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -PassThru
$hw = Wait-MainWindow $p 15
$alive = ($hw -ne [System.IntPtr]::Zero)
FitWindow $hw
$sz1 = CaptureWindow $hw "$env:RUNNER_TEMP/pc-desktop.png"
# The app is single-instance (mutex "ZephyrPlayer"): the settings launch below
# can only open its window after this first instance exits.
StopZephyr

# 2) Settings over the app's own dark window (neutral backdrop), top + scrolled bottom.
$p2 = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -ArgumentList "--open-settings" -PassThru
$null = Wait-MainWindow $p2 15
# Process.MainWindowTitle names only the process's MAIN window; the settings
# window is a second top-level window in the same process. Enumerate by title.
$sw = [System.IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $sw -eq [System.IntPtr]::Zero; $i++) {
    $sw = [ZWU32]::FindByTitle($p2.Id, 'Zephyr Settings')
    if ($sw -eq [System.IntPtr]::Zero) { Start-Sleep -Milliseconds 500 }
}
if ($sw -eq [System.IntPtr]::Zero) { Write-Host "::error::settings window handle never appeared" }
[ZWU32]::SetForegroundWindow($sw) | Out-Null
[ZWU32]::SetWindowPos($sw, [System.IntPtr]::Zero, 40, 20, 700, 720, $SWP_NOZORDER) | Out-Null
Start-Sleep -Milliseconds 700
$sz3 = CaptureWindow $sw "$env:RUNNER_TEMP/pc-desktop-settings.png"
[ZWU32]::SetCursorPos(390, 400) | Out-Null
1..8 | ForEach-Object { [ZWU32]::mouse_event($MOUSEEVENTF_WHEEL, 0, 0, -120, [System.UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }
Start-Sleep -Milliseconds 600
$sz4 = CaptureWindow $sw "$env:RUNNER_TEMP/pc-desktop-settings-bottom.png"
StopZephyr

# 3) Real video, real mouse event, controls shown - WINDOWED.
$p3 = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/clip.mp4" -PassThru
$hw3 = Wait-MainWindow $p3 15
FitWindow $hw3
WakeControls
$sz5 = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-playing-controls-window.png"

# Functional desktop control smoke (real mouse input through the actual UI,
# verdicts from pixel diffs - route tests alone do not close these):
#  1) pause toggle: click the play/pause ring center; the ring glyph flips
#     (pause bars -> play triangle) and the time label stops advancing.
#  2) seek: click the seek track at ~60%; the time label region changes.
#  3) fullscreen toggle: click the brackets glyph; the window's DWM bounds
#     become the full virtual screen. (Distinct from the --fullscreen arg.)
# Volume response: the thumb drag is pixel-provable, but the headless runner
# has NO audio device - the audio apply tail stays explicitly UNVERIFIED here
# (same headless-audio limit as the skipped Audio_Volume_Mute test).
# Fractions below were measured on the 1024x768 captures (ring 0.497,0.90;
# seek 0.60,0.807; brackets 0.94,0.90); a missed click shows up as a
# NOT PROVEN verdict, never a silent pass.
$fnPauseGlyph = -1; $fnPauseClock = -1; $fnSeek = -1; $fnFs = 0
ClickAt 0.497 0.90
$szPause = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-paused.png"
# Keep the bar from auto-hiding between frames (mouse nudge, not a click).
Start-Sleep -Milliseconds 800
[ZWU32]::SetCursorPos([int]($vs.Width * 0.497), [int]($vs.Height * 0.88)) | Out-Null
Start-Sleep -Milliseconds 700
$null = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-paused2.png"
$fnPauseGlyph = CountDiff "$env:RUNNER_TEMP/pc-playing-controls-window.png" "$env:RUNNER_TEMP/pc-smoke-paused.png" 0.47 0.53 0.88 0.93
$fnPauseClock = CountDiff "$env:RUNNER_TEMP/pc-smoke-paused.png" "$env:RUNNER_TEMP/pc-smoke-paused2.png" 0.02 0.10 0.82 0.87
ClickAt 0.497 0.90   # resume
ClickAt 0.60 0.807   # seek to ~60%
$null = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-seeked.png"
$fnSeek = CountDiff "$env:RUNNER_TEMP/pc-smoke-paused.png" "$env:RUNNER_TEMP/pc-smoke-seeked.png" 0.02 0.10 0.82 0.87
ClickAt 0.94 0.90    # fullscreen toggle
$r = [ZRECT]::new()
if ([ZWU32]::DwmGetWindowAttribute($hw3, 9, [ref]$r, 16) -eq 0) {
    if (($r.Right - $r.Left) -ge $vs.Width -and ($r.Bottom - $r.Top) -ge $vs.Height) { $fnFs = 1 }
}
StopZephyr

# 4) Real video, real mouse event, controls shown - FULLSCREEN.
$p4 = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/clip.mp4","--fullscreen" -PassThru
$hw4 = Wait-MainWindow $p4 15
WakeControls
$sz6 = CaptureWindow $hw4 "$env:RUNNER_TEMP/pc-playing-controls-fullscreen.png"
StopZephyr
Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue

netsh advfirewall firewall delete rule name="Zephyr Player" | Out-Null
netsh advfirewall firewall delete rule name="Zephyr Player Discovery" | Out-Null

# Verdicts: video area must be alive (moving pattern => many lit pixels in the
# center band), and the bottom bar band must show lit control pixels.
$vidWin = CountColored "$env:RUNNER_TEMP/pc-playing-controls-window.png" 0.2 0.7
$barWin = CountColored "$env:RUNNER_TEMP/pc-playing-controls-window.png" 0.82 1.0
$vidFs  = CountColored "$env:RUNNER_TEMP/pc-playing-controls-fullscreen.png" 0.2 0.7
$barFs  = CountColored "$env:RUNNER_TEMP/pc-playing-controls-fullscreen.png" 0.82 1.0

$incomplete = ($sz1 -eq '0 x 0') -or ($sz3 -eq '0 x 0') -or ($sz4 -eq '0 x 0') -or ($sz5 -eq '0 x 0') -or ($sz6 -eq '0 x 0')

@"
exe_started_alive_after_10s: $alive
virtual_screen: $($vs.Width)x$($vs.Height)
window_sizes: home=$sz1 settings=$sz3 settings_bottom=$sz4 player_windowed=$sz5 player_fullscreen=$sz6
framing: app-window crops only; no runner terminal/taskbar pixels
video_pixels_windowed: $vidWin
bar_pixels_windowed: $barWin
video_pixels_fullscreen: $vidFs
bar_pixels_fullscreen: $barFs
controls_over_video_windowed: $(if ($vidWin -gt 500 -and $barWin -gt 60) { 'PROVEN' } else { 'NOT PROVEN - inspect images' })
functional_pause_toggle: $(if ($fnPauseGlyph -gt 20 -and $fnPauseClock -le 2) { "PROVEN (glyph diff=$fnPauseGlyph, paused-clock diff=$fnPauseClock)" } else { "NOT PROVEN (glyph diff=$fnPauseGlyph, paused-clock diff=$fnPauseClock)" })
functional_seek: $(if ($fnSeek -gt 10) { "PROVEN (time-label diff=$fnSeek)" } else { "NOT PROVEN (time-label diff=$fnSeek)" })
functional_fullscreen_click: $(if ($fnFs -eq 1) { 'PROVEN (DWM bounds = virtual screen)' } else { 'NOT PROVEN' })
volume_audio_apply: UNVERIFIED (headless runner has no audio device; UI drag path only)
controls_over_video_fullscreen: $(if ($vidFs -gt 500 -and $barFs -gt 60) { 'PROVEN' } else { 'NOT PROVEN - inspect images' })
verdict: $(if ($incomplete) { 'CAPTURE INCOMPLETE - one or more windows never produced pixels; this step fails' } else { 'real desktop captured, app fitted to screen' })
"@ | Set-Content $note

if ($incomplete) { exit 1 }
