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
# Fresh forensics for this run: the app writes ZephyrPlayer-crash.log on any
# unhandled failure; printed below whenever a window never appears.
$crashLog = Join-Path $env:TEMP 'ZephyrPlayer-crash.log'
Remove-Item $crashLog -ErrorAction SilentlyContinue
function DumpCrashLog($why) {
    if (Test-Path $crashLog) {
        Write-Host "::error::app crash log after: $why"
        Get-Content $crashLog | ForEach-Object { Write-Host $_ }
    }
}

# Firewall + listen permission via the app's own setup step (round-6 N-8 shape:
# TCP program=System/47823/Private/LocalSubnet, UDP exe/8490/Private/LocalSubnet,
# URL ACL scoped to this user). The runner is elevated, so the same code the
# explained first-run elevation takes runs headless. (The old inline rules used
# the wrong discovery port and a program-less TCP rule that http.sys ignores.)
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$fw = Start-Process "windows/capture-out/ZephyrPlayer.exe" -ArgumentList '--install-firewall',$sid -PassThru -Wait -WindowStyle Hidden
if ($fw.ExitCode -ne 0) {
    Write-Host "passed SID: $sid"
    $aclAll = netsh http show urlacl | Out-String
    $block = ($aclAll -split "\r?\n\r?\n" | Where-Object { $_ -match ':47823/' } | Select-Object -First 1)
    Write-Host "47823 reservation after setup:"; Write-Host $block
    throw "capture firewall setup exit $($fw.ExitCode)"
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
# Top-level types (a struct declared inside -MemberDefinition becomes nested
# and cannot be referenced as [W.RECT]; that bug silently zeroed captures).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct ZRECT { public int Left, Top, Right, Bottom; }
public static class ZWU32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out ZRECT r);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, System.IntPtr w, System.IntPtr l);
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
    public static System.Collections.Generic.List<string> AllTitles(uint pid) {
        var list = new System.Collections.Generic.List<string>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            list.Add((IsWindowVisible(h) ? "[visible] " : "[hidden] ") + sb.ToString());
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static System.Collections.Generic.List<IntPtr> FindAllByTitle(uint pid, string title) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            if (sb.ToString() == title) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static System.Collections.Generic.List<IntPtr> FindAllVisible(uint pid) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            if (sb.Length > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$SWP_NOZORDER = 0x4; $MOUSEEVENTF_WHEEL = 0x0800

function CaptureWindow($hWnd, $path) {
    # Crop to the app's own window rect: no runner chrome in UI evidence.
    if ($hWnd -eq [System.IntPtr]::Zero) { Write-Host "::error::no window handle for $path - capture skipped"; return "0 x 0" }
    # CopyFromScreen grabs whatever is ON SCREEN in the rect: re-assert topmost
    # immediately before the grab, or the runner console / taskbar (also topmost)
    # lands in the evidence and covers the bar (67b6ee4 controls capture).
    [ZWU32]::SetWindowPos($hWnd, [System.IntPtr]::new(-1), 0, 0, 0, 0, 0x13) | Out-Null  # TOPMOST|NOMOVE|NOSIZE|NOACTIVATE
    Start-Sleep -Milliseconds 250
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
# Pixel metrics via LockBits (fast byte-array scans - GetPixel loops are minutes-slow).
# These judge ACTUAL content, not brightness (the 67b6ee4 brightness count mistook the
# test pattern's own rainbow strip for the bar):
#  - Measure-Bar: the bar's golden ElapsedText (#F0C23E) in the left of the band, and
#    the seek track: a long contiguous run of near-gray bright pixels. Video gradients
#    are saturated, not near-gray; solid color bars are narrower than 40% of the width.
#  - Measure-Video: SATURATED pixels (max-min channel > 60, max > 100). Black, white
#    and gray transients (unrendered window) are not saturated.
function Load-Band($path, $y0frac, $y1frac) {
    $bmp = [System.Drawing.Image]::FromFile($path)
    $w = $bmp.Width; $h = $bmp.Height
    $y0 = [int]($h * $y0frac); $hh = [int]($h * $y1frac) - $y0
    $rect = New-Object System.Drawing.Rectangle(0, $y0, $w, $hh)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $len = [math]::Abs($data.Stride) * $hh
    $bytes = [byte[]]::new($len)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
    $stride = [math]::Abs($data.Stride)
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    return @{ B = $bytes; S = $stride; W = $w; H = $hh }
}
function Measure-Video($path) {
    if (-not (Test-Path $path)) { return 0 }
    $band = Load-Band $path 0.10 0.70
    $sat = 0
    for ($y = 0; $y -lt $band.H; $y += 3) {
        for ($x = 0; $x -lt $band.W; $x += 3) {
            $o = $y * $band.S + $x * 4
            $b = $band.B[$o]; $g = $band.B[$o + 1]; $r = $band.B[$o + 2]
            $mx = [math]::Max($r, [math]::Max($g, $b)); $mn = [math]::Min($r, [math]::Min($g, $b))
            if ($mx - $mn -gt 60 -and $mx -gt 100) { $sat++ }
        }
    }
    return $sat
}
function Measure-Bar($path) {
    if (-not (Test-Path $path)) { return @(0, 0) }
    $band = Load-Band $path 0.75 1.0
    $golden = 0; $goldenBand = [int]($band.W * 0.12); $bestRun = 0
    for ($y = 0; $y -lt $band.H; $y += 2) {
        $run = 0
        for ($x = 0; $x -lt $band.W; $x++) {
            $o = $y * $band.S + $x * 4
            $b = $band.B[$o]; $g = $band.B[$o + 1]; $r = $band.B[$o + 2]
            if ($x -lt $goldenBand -and [math]::Abs($r - 240) -lt 28 -and [math]::Abs($g - 194) -lt 28 -and [math]::Abs($b - 62) -lt 40) { $golden++ }
            if ([math]::Abs($r - $g) -lt 20 -and [math]::Abs($g - $b) -lt 20 -and $r -gt 120 -and $r -lt 235) { $run++; if ($run -gt $bestRun) { $bestRun = $run } } else { $run = 0 }
        }
    }
    return @($golden, [int](100.0 * $bestRun / $band.W))
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
function Close-PairingWindow($proc) {
    # A fresh profile auto-shows the pairing screen over Home; close it so the
    # Home captures show the grid itself. WM_CLOSE keeps this a real UI close.
    $pw = [ZWU32]::FindByTitle($proc.Id, 'Zephyr Player - Pair your phone')
    if ($pw -ne [System.IntPtr]::Zero) {
        [ZWU32]::PostMessage($pw, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 600
    }
}
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
    Write-Host "::error::process $($proc.Id) had no main window after $seconds s (HasExited=$($proc.HasExited))"
    DumpCrashLog "process $($proc.Id) no main window"
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
# Resolve ffmpeg honestly: the choco shim path is not guaranteed (transient
# shim/install failures previously hard-failed here). PATH first, then the
# shim, then the package tree; a real miss throws instead of a wrong-path error.
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg -and (Test-Path 'C:\ProgramData\chocolatey\bin\ffmpeg.exe')) { $ffmpeg = 'C:\ProgramData\chocolatey\bin\ffmpeg.exe' }
if (-not $ffmpeg) {
    $ffhit = Get-ChildItem 'C:\ProgramData\chocolatey\lib' -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ffhit) { $ffmpeg = $ffhit.FullName }
}
if (-not $ffmpeg) { throw 'ffmpeg unavailable after choco install' }
$mp4 = "$env:RUNNER_TEMP/clip.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=30:size=320x240:rate=10" -pix_fmt yuv420p $mp4 2>$null

# Populated-grid evidence: distinct REAL bitmaps per tile, fed through the app's
# ZEPHYR_THUMB_FIXTURE_DIR evidence seam. Whether THIS runner's shell can produce
# real video thumbnails is OPEN: the earlier probe passed a forward-slash path and
# a zero size (E_INVALIDARG), so its "factory-unavailable" said nothing about
# handler support. The fixed probe below reports each stage's own HRESULT.
# Filenames match the library entries below: "<entry name>.png".
$thumbDir = Join-Path $env:RUNNER_TEMP 'thumb-fixture'
New-Item -ItemType Directory -Force $thumbDir | Out-Null
& $ffmpeg -y -f lavfi -i "testsrc=duration=1:size=256x256:rate=1" -frames:v 1 (Join-Path $thumbDir 'Kids.png') 2>$null
& $ffmpeg -y -f lavfi -i "smptebars=duration=1:size=256x256:rate=1" -frames:v 1 (Join-Path $thumbDir 'Big Buck Bunny (clip).mp4.png') 2>$null
& $ffmpeg -y -f lavfi -i "mandelbrot=size=256x256" -frames:v 1 (Join-Path $thumbDir 'Zephyr Demo Reel.mp4.png') 2>$null

# Honest probe: does THIS runner's shell produce a real video thumbnail? Same
# COM API the app uses (IShellItemImageFactory, THUMBNAILONLY). The result is
# recorded in the capture notes so the seam's reason is verifiable, not asserted.
$probe = Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ThumbProbe {
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c393c463"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { void GetImage(SIZE size, int flags, out IntPtr phbm); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory f);
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }
    public static string Probe(string path) {
        // Diagnosis after hr=-2147024809 (E_INVALIDARG): the probe used to pass a
        // FORWARD-SLASH path and a ZERO size - both are argument errors, not proof
        // of a missing shell handler. Report each stage's own HRESULT.
        try {
            IShellItemImageFactory f;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, new Guid("bcc18b79-ba16-442f-80c4-8a59c393c463"), out f);
            if (hr != 0 || f == null) return "factory-unavailable hr=" + hr + " path=" + path;
            IntPtr h = IntPtr.Zero;
            try { f.GetImage(new SIZE(256, 256), 0x08, out h); } // THUMBNAILONLY
            catch (Exception ex) { return "getimage-error: " + ex.GetType().Name; }
            return h == IntPtr.Zero ? "null (no thumbnail handler on this runner)" : "bitmap-produced";
        } catch (Exception ex) { return "error: " + ex.GetType().Name; }
    }
}
"@ -PassThru
$probeResult = [ThumbProbe]::Probe((Join-Path $env:RUNNER_TEMP 'clip.mp4'))
Write-Output "SHELL_THUMB_PROBE: $probeResult"

$srv = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8901" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

# 1) Home, full window fitted to the screen (no clipping, no terminal).
$p = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
$hw = Wait-MainWindow $p 15
$alive = ($hw -ne [System.IntPtr]::Zero)
Close-PairingWindow $p
FitWindow $hw
$sz1 = CaptureWindow $hw "$env:RUNNER_TEMP/pc-desktop.png"
# The app is single-instance (mutex "ZephyrPlayer"): the settings launch below
# can only open its window after this first instance exits.
StopZephyr

# 1b) Home with the designated movies folder set: the populated library view.
$libDir = Join-Path $env:RUNNER_TEMP 'zephyr-library'
New-Item -ItemType Directory -Force $libDir | Out-Null
New-Item -ItemType Directory -Force (Join-Path $libDir 'Kids') | Out-Null
Copy-Item "$env:RUNNER_TEMP/clip.mp4" (Join-Path $libDir 'Big Buck Bunny (clip).mp4')
Copy-Item "$env:RUNNER_TEMP/clip.mp4" (Join-Path $libDir 'Zephyr Demo Reel.mp4')
$cfgDir = Join-Path $env:APPDATA 'ZephyrPlayer'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$libJson = $libDir -replace '\\','\\'
[IO.File]::WriteAllText((Join-Path $cfgDir 'settings.json'), "{`"LibraryFolder`":`"$libJson`"}")
[Environment]::SetEnvironmentVariable('ZEPHYR_CI_CAPTURE', '1', 'Process')
$prevFx = [Environment]::GetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR','Process')
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $thumbDir, 'Process')
$pH = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $prevFx, 'Process')
$hwH = Wait-MainWindow $pH 15
Close-PairingWindow $pH
if ($hwH -ne [System.IntPtr]::Zero) {
    [ZWU32]::SetWindowPos($hwH, [System.IntPtr]::Zero, 30, 4, 940, 700, $SWP_NOZORDER) | Out-Null
    Start-Sleep -Milliseconds 900
}
$sz1b = CaptureWindow $hwH "$env:RUNNER_TEMP/pc-home-library.png"
StopZephyr
# Back to a clean first-run state for the sections below.
Remove-Item (Join-Path $cfgDir 'settings.json') -Force -ErrorAction SilentlyContinue

# 2) Settings over the app's own dark window (neutral backdrop), top + scrolled bottom.
$p2 = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--open-settings" -PassThru
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
[ZWU32]::SetWindowPos($sw, [System.IntPtr]::new(-1), 40, 4, 700, 690, 0) | Out-Null
Start-Sleep -Milliseconds 700
$sz3 = CaptureWindow $sw "$env:RUNNER_TEMP/pc-desktop-settings.png"
# The pinned bottom of the nav (Feedback & Updates, with the version) is a
# section SWITCH now, not a scroll: invoke the real "Updates" nav button
# through UI Automation and capture that section as the bottom evidence.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$settingsRoot = [System.Windows.Automation.AutomationElement]::FromHandle($sw)
$updCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavUpdates')
$upd = $settingsRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $updCond)
if ($upd -ne $null) {
    $upd.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
} else { Write-Host "::error::NavUpdates button not found via UIA" }
$sz4 = CaptureWindow $sw "$env:RUNNER_TEMP/pc-desktop-settings-updates.png"
StopZephyr

# 2b) Pairing screen (1.2.0: the dedicated pairing window replaced the banner).
$p2b = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--open-pairing" -PassThru
$null = Wait-MainWindow $p2b 15
$pw = [System.IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $pw -eq [System.IntPtr]::Zero; $i++) {
    $pw = [ZWU32]::FindByTitle($p2b.Id, 'Zephyr Player - Pair your phone')
    if ($pw -eq [System.IntPtr]::Zero) { Start-Sleep -Milliseconds 500 }
}
if ($pw -eq [System.IntPtr]::Zero) { Write-Host "::error::pairing window handle never appeared" }
[ZWU32]::SetForegroundWindow($pw) | Out-Null
[ZWU32]::SetWindowPos($pw, [System.IntPtr]::new(-1), 40, 4, 560, 600, 0) | Out-Null
Start-Sleep -Milliseconds 900
$sz4b = CaptureWindow $pw "$env:RUNNER_TEMP/pc-desktop-pairing.png"
StopZephyr

# 2b+) Tray ICON proof (round-3 P1): the icon must really exist in the
# notification area, not only its menu. Crop the bottom-right corner of the
# primary screen (the notification area) while the app runs.
$env:ZEPHYR_TRAY_DIAG = "$env:RUNNER_TEMP\pc-tray-diag.txt"
Remove-Item $env:ZEPHYR_TRAY_DIAG -Force -ErrorAction SilentlyContinue
$pIcon = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
Start-Sleep -Seconds 5
# NEW-C2: the pixel crop alone cannot prove the icon - assert the app's own
# IsCreated report too (hidden overflow looks identical to missing).
$diagText = if (Test-Path $env:ZEPHYR_TRAY_DIAG) { (Get-Content $env:ZEPHYR_TRAY_DIAG -Raw).Trim() } else { "diag file missing" }
Write-Host "tray diag: $diagText"
if ($diagText -ne 'IsCreated=True') { Write-Host "::error::tray icon IsCreated proof failed: $diagText" }
$sw = [ZWU32]::GetSystemMetrics(0) # SM_CXSCREEN
$sh = [ZWU32]::GetSystemMetrics(1) # SM_CYSCREEN
if ($sw -le 0) { $sw = 1920 }; if ($sh -le 0) { $sh = 1080 }
$ibmp = New-Object System.Drawing.Bitmap 460, 64
$ig = [System.Drawing.Graphics]::FromImage($ibmp)
$ig.CopyFromScreen(($sw - 460), ($sh - 64), 0, 0, $ibmp.Size)
$ibmp.Save("$env:RUNNER_TEMP/pc-tray-icon.png")
$ig.Dispose(); $ibmp.Dispose()

# 2c) Tray menu, actual pixels: the CI-only --tray-menu arg pops the strip at a
# fixed screen point; this grabs the region (dark-renderer proof).
# NEW-C1: the single-instance mutex makes a second process signal Restore and
# exit before it builds a tray - stop the icon-proof process FIRST.
Stop-Process -Id $pIcon.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Remove-Item Env:ZEPHYR_TRAY_DIAG -ErrorAction SilentlyContinue
$p2c = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--tray-menu" -PassThru
Start-Sleep -Seconds 4
$tbmp = New-Object System.Drawing.Bitmap 360, 170
$tg = [System.Drawing.Graphics]::FromImage($tbmp)
$tg.CopyFromScreen(40, 40, 0, 0, $tbmp.Size)
$tbmp.Save("$env:RUNNER_TEMP/pc-tray-menu.png")
$tg.Dispose(); $tbmp.Dispose()
StopZephyr

# 2d) PHONE-START BAR PROOF (Tab evidence ask): playback is started and paused over
# real HTTP with the CI token - NO mouse events at all. The bar must show on start,
# hide on its own ~3s timer while playing, and stay while paused.
# C-1: the cursor must NOT sit over the window - a stray mouse-move from the
# window appearing under it would reveal the bar by itself. The window is sized
# smaller than the screen and the cursor parked at (5,5), provably outside it.
[ZWU32]::SetCursorPos(5, 5) | Out-Null
$pBar = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
$hwHome = Wait-MainWindow $pBar 15
# /play opens a SEPARATE player window (App.xaml.cs ShowPlayerAndPlay); the home
# window is the process's MainWindowHandle and must NOT be topmost - put it at the
# BOTTOM of the z-order so the player window paints above it in the captures.
[ZWU32]::SetWindowPos($hwHome, [System.IntPtr]::new(1), 100, 100, 800, 500, 0) | Out-Null
# The first-run pairing window covers the video: close it like a user would.
$hwPair = [ZWU32]::FindByTitle($pBar.Id, 'Zephyr Player - Pair your phone')
if ($hwPair -ne [System.IntPtr]::Zero) { [ZWU32]::PostMessage($hwPair, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null }
$hdr = @{ Authorization = "Bearer ci-capture-token" }
# The control server comes up after the window exists: wait for a real answer
# instead of POSTing into a closed port and silently capturing the home window.
$ctrlUp = $false
foreach ($i in 1..40) {
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 47823); $c.Close(); $ctrlUp = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ctrlUp) { throw "control server did not come up for the phone-start bar proof" }
# /play is the route the real phone uses to START playback (ZephyrClient.play): it
# hides Home and SHOWS the player window. /control/play only drives the current
# session (no Show) - using it here proved nothing about the phone-start flow.
$playResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47823/play" -Headers $hdr -ContentType "application/json" -Body ('{"url":"http://127.0.0.1:8901/clip.mp4"}') -SkipHttpErrorCheck
if ($playResp.StatusCode -ne 200) { throw "phone-start play POST answered $($playResp.StatusCode): $($playResp.Content)" }
# Find the player window BY TITLE: Home and MainWindow are both titled "Zephyr
# Player", the pairing window has its own title, and any transient dialog (which
# once masqueraded as the player, then died mid-poll and left a stale handle) is
# excluded by the title match. The player window is the "Zephyr Player" window
# that is NOT the home window.
function Find-PlayerWindow($procId, $homeHwnd) {
    foreach ($h in [ZWU32]::FindAllByTitle($procId, 'Zephyr Player')) {
        if ($h -ne $homeHwnd) { return $h }
    }
    return [System.IntPtr]::Zero
}
$hwBar = [System.IntPtr]::Zero
foreach ($i in 1..40) {
    $hwBar = Find-PlayerWindow $pBar.Id $hwHome
    if ($hwBar -ne [System.IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 500
}
if ($hwBar -eq [System.IntPtr]::Zero) {
    # Diagnose, don't just fail: list every visible window this process owns,
    # grab the whole desktop, and dump the crash forensics.
    Write-Host "play POST answered $($playResp.StatusCode): $($playResp.Content)"
    Write-Host "process alive: $(-not $pBar.HasExited)"
    foreach ($h in [ZWU32]::FindAllVisible($pBar.Id)) { Write-Host "visible window hwnd=$h" }
    $titles = [ZWU32]::AllTitles($pBar.Id)
    foreach ($t in $titles) { Write-Host "window title: [$t]" }
    $fs = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $fg = [System.Drawing.Graphics]::FromImage($fs)
    $fg.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $fs.Size)
    $fs.Save("$env:RUNNER_TEMP/pc-bar-nowindow-fullscreen.png")
    $fg.Dispose(); $fs.Dispose()
    DumpCrashLog "no player window after /control/play"
    throw "phone-start bar proof: no player window opened after /control/play"
}
# C-1 invariant: the player window opens at its default 1920x1080, which COVERS the
# parked cursor at (5,5). Fit it to 100,100 800x500, so (5,5) is provably outside
# it, and keep re-fitting inside the poll: playback start can recreate the window.
[ZWU32]::SetWindowPos($hwBar, [System.IntPtr]::new(-1), 100, 100, 800, 500, 0) | Out-Null
[ZWU32]::SetCursorPos(5, 5) | Out-Null
# A 200 POST means the request was accepted, not that frames flow: poll for REAL
# SATURATED video pixels AND the bar's actual signature together (a brightness
# gate previously passed on an unrendered window). The window is RE-RESOLVED every
# poll: starting playback can recreate the OS window (a stale handle previously
# zeroed every capture). A failed capture is skipped, never counted from a stale
# file.
Remove-Item "$env:RUNNER_TEMP/pc-bar-phonestart.png" -ErrorAction SilentlyContinue
$vidStart = 0; $barStartGolden = 0; $barStartSeek = 0; $gotStart = $false
foreach ($i in 1..40) {
    $hwBar = Find-PlayerWindow $pBar.Id $hwHome
    if ($hwBar -ne [System.IntPtr]::Zero) {
        [ZWU32]::SetWindowPos($hwBar, [System.IntPtr]::new(-1), 100, 100, 800, 500, 0) | Out-Null
        $sz = CaptureWindow $hwBar "$env:RUNNER_TEMP/pc-bar-phonestart.png"
        if ($sz -ne "0 x 0") {
            $vidStart = Measure-Video "$env:RUNNER_TEMP/pc-bar-phonestart.png"
            $m = Measure-Bar "$env:RUNNER_TEMP/pc-bar-phonestart.png"
            $barStartGolden = $m[0]; $barStartSeek = $m[1]
            if ($vidStart -ge 3000 -and $barStartGolden -ge 5 -and $barStartSeek -ge 40) { $gotStart = $true; break }
        }
    }
    Start-Sleep -Milliseconds 500
}
if (-not $gotStart) { throw "phone-start bar proof never captured video AND bar together (video=$vidStart golden=$barStartGolden seekRun=$barStartSeek% after ~20s)" }
Start-Sleep -Seconds 5   # past the 3s idle timer, still playing
$szBar2 = CaptureWindow $hwBar "$env:RUNNER_TEMP/pc-bar-autohide.png"
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/pause" -Headers $hdr -ContentType "application/json" -Body '{}' | Out-Null
# Paused keeps the bar: the phone's pause must reveal/hold controls without a mouse.
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/resume" -Headers $hdr -ContentType "application/json" -Body '{}' | Out-Null
Start-Sleep -Milliseconds 700
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/pause" -Headers $hdr -ContentType "application/json" -Body '{}' | Out-Null
Start-Sleep -Seconds 5   # well past the idle timer: a paused movie never loses the bar
$szBar3 = CaptureWindow $hwBar "$env:RUNNER_TEMP/pc-bar-paused.png"
# Parked-cursor case: a cursor SITTING OVER the playing video must not pin the bar
# (only real movement reveals it). Move onto the video, wait past the idle timer.
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/resume" -Headers $hdr -ContentType "application/json" -Body '{}' | Out-Null
[ZWU32]::SetCursorPos(500, 350) | Out-Null   # window center: inside, over the video
Start-Sleep -Seconds 5
$szBar4 = CaptureWindow $hwBar "$env:RUNNER_TEMP/pc-bar-parked.png"
[ZWU32]::SetCursorPos(5, 5) | Out-Null
StopZephyr
Write-Host "phone-start bar captures: $sz / $szBar2 / $szBar3 / $szBar4"
# C-1 verdict from ACTUAL bar signatures, not brightness: the golden ElapsedText
# plus the full-width seek track mark the bar; saturated pixels mark real video.
$vidStartPx   = Measure-Video "$env:RUNNER_TEMP/pc-bar-phonestart.png"
$mStart       = Measure-Bar "$env:RUNNER_TEMP/pc-bar-phonestart.png"
$vidAutoPx    = Measure-Video "$env:RUNNER_TEMP/pc-bar-autohide.png"
$mAuto        = Measure-Bar "$env:RUNNER_TEMP/pc-bar-autohide.png"
$mPaused      = Measure-Bar "$env:RUNNER_TEMP/pc-bar-paused.png"
$vidParkedPx  = Measure-Video "$env:RUNNER_TEMP/pc-bar-parked.png"
$mParked      = Measure-Bar "$env:RUNNER_TEMP/pc-bar-parked.png"
$barOnStart  = ($mStart[0]  -ge 5 -and $mStart[1]  -ge 40)
$barOnAuto   = ($mAuto[0]   -ge 5 -and $mAuto[1]   -ge 40)
$barOnPaused = ($mPaused[0] -ge 5 -and $mPaused[1] -ge 40)
$barOnParked = ($mParked[0] -ge 5 -and $mParked[1] -ge 40)
$vidOkAuto   = ($vidAutoPx   -ge 3000)
$vidOkParked = ($vidParkedPx -ge 3000)

# 3) Real video, real mouse event, controls shown - WINDOWED.
$p3 = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/clip.mp4" -PassThru
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
# Seek by DRAGGING the real thumb. Fraction-guessed coordinates miss the
# thumb (a track press only steps LargeChange ~1%, invisible at second
# granularity), so locate the controls through UI Automation: x:Name becomes
# AutomationId in WPF.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Find-ByAutoId($rootEl, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function DragPixels($x0, $y0, $x1, $y1) {
    [ZWU32]::SetCursorPos([int]$x0, [int]$y0) | Out-Null
    Start-Sleep -Milliseconds 250
    [ZWU32]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero)  # LEFTDOWN
    for ($i = 1; $i -le 8; $i++) {
        [ZWU32]::SetCursorPos([int]($x0 + ($x1 - $x0) * $i / 8), [int]$y0) | Out-Null
        Start-Sleep -Milliseconds 90
    }
    [ZWU32]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)  # LEFTUP
    Start-Sleep -Milliseconds 700
}
$rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($hw3)
# Reveal and hold the floating overlay before finding physical thumb bounds.
WakeControls
$sliderEl = Find-ByAutoId $rootEl 'SeekBar' 
if ($sliderEl -ne $null) {
    $thumbCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Thumb)
    $thumbEl = $sliderEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $thumbCond)
    if ($thumbEl -ne $null) {
        $tr = $thumbEl.Current.BoundingRectangle
        $sr = $sliderEl.Current.BoundingRectangle
        Write-Host "SEEK before: thumb=$tr slider=$sr value=$($sliderEl.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value)"
        DragPixels ($tr.X + $tr.Width / 2) ($tr.Y + $tr.Height / 2) ($sr.X + $sr.Width * 0.6) ($tr.Y + $tr.Height / 2)
        Write-Host "SEEK after: value=$($sliderEl.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value)"
    } else { Write-Host "::warning::SeekBar thumb not found via UIA" }
} else { Write-Host "::warning::SeekBar not found via UIA" }
$null = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-seeked.png"
$fnSeek = CountDiff "$env:RUNNER_TEMP/pc-smoke-paused.png" "$env:RUNNER_TEMP/pc-smoke-seeked.png" 0.02 0.10 0.82 0.87
# Fullscreen toggle: invoke the real button through UIA (a blind click can
# land while controls are fading), verdict = bounds GROW to the virtual
# screen (the fitted window is already near-full-size).
$rBefore = [ZRECT]::new()
[ZWU32]::DwmGetWindowAttribute($hw3, 9, [ref]$rBefore, 16) | Out-Null
$fsEl = Find-ByAutoId $rootEl 'FullscreenButton'
if ($fsEl -ne $null) {
    $fsEl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
} else {
    Write-Host "::warning::FullscreenButton not found via UIA; falling back to click"
    ClickAt 0.94 0.90
}
Start-Sleep -Milliseconds 800
$r = [ZRECT]::new()
if ([ZWU32]::DwmGetWindowAttribute($hw3, 9, [ref]$r, 16) -eq 0) {
    $grewW = ($r.Right - $r.Left) - ($rBefore.Right - $rBefore.Left)
    if (($r.Right - $r.Left) -ge $vs.Width -and ($r.Bottom - $r.Top) -ge $vs.Height -and $grewW -gt 5) { $fnFs = 1 }
}
StopZephyr

# 4) Real video, real mouse event, controls shown - FULLSCREEN.
$p4 = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/clip.mp4","--fullscreen" -PassThru
$hw4 = Wait-MainWindow $p4 15
# Fullscreen transition and floating VideoView overlay can lag HWND creation.
# Position the already-fullscreen window on the captured monitor and wait for
# both host and overlay layout to settle. Do not crop an offscreen rectangle.
[ZWU32]::SetWindowPos($hw4, [System.IntPtr]::new(-1), $vs.Left, $vs.Top, $vs.Width, $vs.Height, 0) | Out-Null
Start-Sleep -Seconds 2
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

$incomplete = ($sz1 -eq '0 x 0') -or ($sz1b -eq '0 x 0') -or ($sz3 -eq '0 x 0') -or ($sz4 -eq '0 x 0') -or ($sz5 -eq '0 x 0') -or ($sz6 -eq '0 x 0')

@"
exe_started_alive_after_10s: $alive
virtual_screen: $($vs.Width)x$($vs.Height)
shell_thumbnail_probe: $probeResult
home_library_thumbnails: fixture bitmaps via ZEPHYR_THUMB_FIXTURE_DIR evidence seam (real PNG pixels through the same Peek/Thumb pipeline); the production path is the Windows shell thumbnail API, which the probe above reports for this runner
window_sizes: home_empty=$sz1 home_library=$sz1b settings=$sz3 settings_bottom=$sz4 player_windowed=$sz5 player_fullscreen=$sz6
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
bar_phonestart: video=$vidStartPx golden=$($mStart[0]) seekRun=$($mStart[1])% bar=$barOnStart
bar_autohide: video=$vidAutoPx golden=$($mAuto[0]) seekRun=$($mAuto[1])% bar=$barOnAuto
bar_paused: golden=$($mPaused[0]) seekRun=$($mPaused[1])% bar=$barOnPaused
bar_parked_cursor_over_video: video=$vidParkedPx golden=$($mParked[0]) seekRun=$($mParked[1])% bar=$barOnParked
phone_start_bar: $(if ($barOnStart -and $vidOkAuto -and -not $barOnAuto -and $barOnPaused) { 'PROVEN (bar signature present on phone-start over real video, gone after the idle timer while video kept playing, back while paused; judged on golden ElapsedText + seek-track signature, cursor parked outside the 100,100 800x500 window)' } else { 'NOT PROVEN - inspect pc-bar-*.png' })
parked_cursor_autohide: $(if ($vidOkParked -and -not $barOnParked) { 'PROVEN (cursor moved onto the playing video, then left still: bar hid on the idle timer - only real movement reveals it)' } else { 'NOT PROVEN - inspect pc-bar-parked.png' })
verdict: $(if ($incomplete) { 'CAPTURE INCOMPLETE - one or more windows never produced pixels; this step fails' } else { 'real desktop captured, app fitted to screen' })
"@ | Set-Content $note

if ($incomplete) { exit 1 }
