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
$sw = [System.IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $sw -eq [System.IntPtr]::Zero; $i++) {
    foreach ($pr in Get-Process ZephyrPlayer -ErrorAction SilentlyContinue) {
        $pr.Refresh()
        if ($pr.MainWindowTitle -eq 'Zephyr Settings') { $sw = $pr.MainWindowHandle }
    }
    if ($sw -eq [System.IntPtr]::Zero) { Start-Sleep -Milliseconds 500 }
}
if ($sw -eq [System.IntPtr]::Zero) { $p2.Refresh(); $sw = $p2.MainWindowHandle }
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
controls_over_video_fullscreen: $(if ($vidFs -gt 500 -and $barFs -gt 60) { 'PROVEN' } else { 'NOT PROVEN - inspect images' })
verdict: $(if ($incomplete) { 'CAPTURE INCOMPLETE - one or more windows never produced pixels; this step fails' } else { 'real desktop captured, app fitted to screen' })
"@ | Set-Content $note

if ($incomplete) { exit 1 }
