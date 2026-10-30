param([switch]$HomeOnly)
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
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int uAction, int uParam, ref ZRECT lpvParam, int fuWinIni);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, System.IntPtr w, System.IntPtr l);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out ZRECT r, int size);
    [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attr, out int v, int size);
    public static bool HwndVisible(IntPtr h) { return IsWindowVisible(h); }
    // DWMWA_CLOAKED (14): 0 = rendering; 1/2/4 = cloaked (app/shell/inherited).
    // A cloaked window keeps sane frame/window rects but renders NOTHING, so a
    // screen grab of its rect shows whatever is behind it (run 40989952680 mode).
    public static int DwmCloaked(IntPtr h) { int v = -1; DwmGetWindowAttributeInt(h, 14, out v, 4); return v; }
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
    # Rendering guards: a hidden or DWM-cloaked window keeps SANE frame/window
    # rects but paints nothing - CopyFromScreen then grabs desktop + whatever
    # window is behind it, which is exactly the 40989952680 evidence-corruption
    # mode (offset player, clipped chrome/bar, desktop in frame). Fail closed.
    if (-not [ZWU32]::HwndVisible($hWnd)) { throw "CaptureWindow: hwnd $hWnd is not visible for $path - capture would grab other on-screen content" }
    $cloakState = [ZWU32]::DwmCloaked($hWnd)
    if ($cloakState -gt 0) { throw "CaptureWindow: hwnd $hWnd is DWM-cloaked ($cloakState) for $path - its rects are stale and the grab would show other content" }
    # CopyFromScreen grabs whatever is ON SCREEN in the rect: re-assert topmost
    # immediately before the grab, or the runner console / taskbar (also topmost)
    # lands in the evidence and covers the bar (67b6ee4 controls capture).
    [ZWU32]::SetWindowPos($hWnd, [System.IntPtr]::new(-1), 0, 0, 0, 0, 0x13) | Out-Null  # TOPMOST|NOMOVE|NOSIZE|NOACTIVATE
    Start-Sleep -Milliseconds 250
    # DWMWA_EXTENDED_FRAME_BOUNDS (9): the window's REAL visible bounds.
    # GetWindowRect includes the invisible drop-shadow ring; through that ring
    # the runner's console/taskbar pixels bleed into UI evidence.
    $r = [ZRECT]::new()
    $dwmOk = ([ZWU32]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, 16) -eq 0)
    if (-not $dwmOk) {
        if (-not [ZWU32]::GetWindowRect($hWnd, [ref]$r)) { Write-Host "::error::GetWindowRect failed for hwnd $hWnd"; return "0 x 0" }
    }
    # Geometry agreement: the frame bounds must sit inside GetWindowRect's
    # invisible-border envelope (any edge may carry the ~7px border; the
    # --fullscreen window showed 7px on all four in run 40996037753). If they
    # disagree, the DWM rect is stale (or the
    # handle is not the rendered window) and CopyFromScreen would grab desktop +
    # a shifted player - the run 40989952680 false-fail mode. One bounded
    # re-assert + re-read, then throw WITH the rects: a capture whose crop does
    # not match its window is decisive-logging-worthy, never silent evidence.
    $wr = [ZRECT]::new(); [void][ZWU32]::GetWindowRect($hWnd, [ref]$wr)
    if ($dwmOk) {
        $dL = $r.Left - $wr.Left; $dT = $r.Top - $wr.Top; $dR = $wr.Right - $r.Right; $dB = $wr.Bottom - $r.Bottom
        if ($dL -lt -2 -or $dL -gt 14 -or $dT -lt -2 -or $dT -gt 14 -or $dR -lt -2 -or $dR -gt 14 -or $dB -lt -2 -or $dB -gt 14) {
            [ZWU32]::SetWindowPos($hWnd, [System.IntPtr]::new(-1), 0, 0, 0, 0, 0x13) | Out-Null  # TOPMOST|NOMOVE|NOSIZE|NOACTIVATE
            Start-Sleep -Milliseconds 400
            $r = [ZRECT]::new()
            [void][ZWU32]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, 16)
            $wr = [ZRECT]::new(); [void][ZWU32]::GetWindowRect($hWnd, [ref]$wr)
            $dL = $r.Left - $wr.Left; $dT = $r.Top - $wr.Top; $dR = $wr.Right - $r.Right; $dB = $wr.Bottom - $r.Bottom
            if ($dL -lt -2 -or $dL -gt 14 -or $dT -lt -2 -or $dT -gt 14 -or $dR -lt -2 -or $dR -gt 14 -or $dB -lt -2 -or $dB -gt 14) {
                throw "CaptureWindow: frame/window bounds disagree for hwnd ${hWnd} ($path): frame=($($r.Left),$($r.Top),$($r.Right),$($r.Bottom)) window=($($wr.Left),$($wr.Top),$($wr.Right),$($wr.Bottom)) delta=($dL,$dT,$dR,$dB)"
            }
        }
    }
    $r.Left += 2; $r.Top += 2; $r.Right -= 2; $r.Bottom -= 2
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Write-Host "::error::zero-size window rect for hwnd $hWnd ($w x $h) - capture skipped"; return "0 x 0" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "$(Split-Path $path -Leaf): frame=($($r.Left),$($r.Top),$($r.Right),$($r.Bottom)) window=($($wr.Left),$($wr.Top),$($wr.Right),$($wr.Bottom)) size=${w}x${h}" | Add-Content "$env:RUNNER_TEMP/pc-capture-bounds.txt"
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
#  - Measure-Bar: brand-orange pixels in the left of the band (CP2: the volume fill),
#    and the seek track: a long bounded run of the track's own opaque colours (#303032
#    track, #FF9500 fill). See the function for the exact rule.
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
    # CP2 bar (render 02) signature, re-calibrated from the CP1 metric:
    #  - "golden": brand-orange #FF9500 pixels in the left 30 % of the band. The
    #    original colour set keeps orange on the volume fill (left group) and the
    #    seek fill; the 1.4-era gold #F0C23E is still accepted for old renders.
    #  - "seekRun": the longest horizontal run made of the seek track's own opaque
    #    colours - track #303032 (48,48,50) and fill #FF9500 - allowing gaps of at
    #    most 8 px (the thumb's see-through halo), bounded on both sides. The card
    #    behind it is see-through, but the 4 px track is opaque, so its colour does
    #    not depend on the video under it. Reported as % of the band width.
    # Band 0.55-0.93 as before (the taskbar separator below 0.93 is excluded).
    $band = Load-Band $path 0.55 0.93
    $golden = 0; $goldenBand = [int]($band.W * 0.30); $bestRun = 0
    for ($y = 0; $y -lt $band.H; $y++) {
        $runStart = -1; $lastHit = -100
        for ($x = 0; $x -lt $band.W; $x++) {
            $o = $y * $band.S + $x * 4
            $b = $band.B[$o]; $g = $band.B[$o + 1]; $r = $band.B[$o + 2]
            $isGold = [math]::Abs($r - 240) -lt 28 -and [math]::Abs($g - 194) -lt 28 -and [math]::Abs($b - 62) -lt 40
            $isOrange = $r -gt 227 -and [math]::Abs($g - 149) -lt 28 -and $b -lt 40
            if ($x -lt $goldenBand -and ($isGold -or $isOrange)) { $golden++ }
            $isTrack = ([math]::Abs($r - 48) -le 7 -and [math]::Abs($g - 48) -le 7 -and [math]::Abs($b - 50) -le 7) -or ($r -ge 238 -and [math]::Abs($g - 149) -le 14 -and $b -le 24)
            if ($isTrack) {
                if ($runStart -lt 0 -or ($x - $lastHit) -gt 9) {
                    # close the previous run (bounded: it did not touch the left edge)
                    if ($runStart -gt 0) { $len = $lastHit - $runStart + 1; if ($len -gt $bestRun) { $bestRun = $len } }
                    $runStart = $x
                }
                $lastHit = $x
            }
        }
        # A run still open at the right edge touched it: reject (chrome, not track).
        if ($runStart -gt 0 -and $lastHit -lt $band.W - 1) { $len = $lastHit - $runStart + 1; if ($len -gt $bestRun) { $bestRun = $len } }
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
    # captures show the grid itself. WM_CLOSE keeps this a real UI close.
    # The first-run Settings window can appear AFTER this runs (it is created on
    # the dispatcher, not necessarily before the player HWND exists), so a single
    # find-and-close can miss it and leave it covering the player for the whole
    # leg. Poll: wait for it to appear if it is going to, close every surface
    # that shows, and return only once none remains - or when none ever came.
    $seen = $false
    for ($i = 0; $i -lt 16; $i++) {
        $settings = [ZWU32]::FindByTitle($proc.Id, 'Zephyr Settings')
        $pw = [ZWU32]::FindByTitle($proc.Id, 'Zephyr Player - Pair your phone')
        if ($settings -eq [System.IntPtr]::Zero -and $pw -eq [System.IntPtr]::Zero) {
            if ($seen) { return }          # was open, now closed and stayed closed
            if ($i -ge 7) { return }       # never appeared within ~3.5s: nothing to close
            Start-Sleep -Milliseconds 500
            continue
        }
        $seen = $true
        if ($settings -ne [System.IntPtr]::Zero) { [ZWU32]::PostMessage($settings, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null }
        if ($pw -ne [System.IntPtr]::Zero) { [ZWU32]::PostMessage($pw, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null }
        Start-Sleep -Milliseconds 500
    }
}
function FitWindow($hWnd) {
    # Whole window inside the WORK AREA (screen minus taskbar), top-left aligned:
    # no part of the player - including the bottom control row - sits under the
    # taskbar, and no taskbar pixels (its top separator is a full-width light-gray
    # line) land in the evidence. 10px bottom margin keeps the window's invisible
    # DWM border off the taskbar edge; window-height variance previously decided
    # whether that separator fell inside the measured band (runs 40893158780 vs
    # 40960360181). HWND_TOPMOST (-1) kept so nothing else overlaps either.
    $wa = [ZRECT]::new()
    [ZWU32]::SystemParametersInfo(0x0030 <# SPI_GETWORKAREA #>, 0, [ref]$wa, 0) | Out-Null
    $wantW = $wa.Right - $wa.Left
    $wantH = $wa.Bottom - $wa.Top - 10
    # VERIFY the fitted geometry landed. An externally positioned WPF window can
    # be re-placed by a late dispatcher/layout pass (startup/cascade position)
    # after SetWindowPos returns; every later capture then crops the rect the
    # window USED to occupy and grabs desktop + a down-right-shifted player with
    # clipped chrome and a clipped control bar (run 40989952680, offset ~20,25px,
    # windowed mouse-recovery 20/20 false fail). Bounded re-fit, then fail
    # loudly with every rect logged instead of producing misleading evidence.
    for ($try = 0; $try -lt 4; $try++) {
        [ZWU32]::SetWindowPos($hWnd, [System.IntPtr]::new(-1), $wa.Left, $wa.Top, $wantW, $wantH, 0) | Out-Null
        Start-Sleep -Milliseconds 700
        # SetWindowPos positions the WINDOW rect; the DWM frame sits ~7px inside
        # it (run 40993947776 calibration: requested (0,0) 1024x710 settled at
        # window=(0,0,1024,710) exactly, frame=(7,0,1017,703)). Verify against
        # GetWindowRect - the rect SetWindowPos actually controls.
        $wr = [ZRECT]::new(); [void][ZWU32]::GetWindowRect($hWnd, [ref]$wr)
        if ([Math]::Abs($wr.Left - $wa.Left) -le 4 -and [Math]::Abs($wr.Top - $wa.Top) -le 4 -and
            [Math]::Abs(($wr.Right - $wr.Left) - $wantW) -le 4 -and [Math]::Abs(($wr.Bottom - $wr.Top) - $wantH) -le 4) {
            return
        }
    }
    $fr = [ZRECT]::new()
    [void][ZWU32]::DwmGetWindowAttribute($hWnd, 9, [ref]$fr, 16)
    throw "FitWindow: window did not settle at ($($wa.Left),$($wa.Top)) ${wantW}x${wantH}; window=($($wr.Left),$($wr.Top),$($wr.Right),$($wr.Bottom)) frame=($($fr.Left),$($fr.Top),$($fr.Right),$($fr.Bottom)) workarea=($($wa.Left),$($wa.Top),$($wa.Right),$($wa.Bottom))"
}
function Wait-MainWindow($proc, $seconds) {
    # WPF can take a while to create its HWND on a cold runner; poll instead
    # of assuming the window exists after a fixed sleep.
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        $proc.Refresh()
        # First-run Settings can be the process main HWND. Select the actual
        # Player/Home title, never whatever window last gained focus.
        $candidate = [ZWU32]::FindByTitle($proc.Id, 'Zephyr Player')
        if ($candidate -ne [System.IntPtr]::Zero) { return $candidate }
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
$streamFixture = Join-Path $env:RUNNER_TEMP "s/tok"
New-Item -ItemType Directory -Path $streamFixture -Force | Out-Null
# Long enough for every leg of the bar proof (a 30s clip ENDED mid-proof: the last
# phonestart poll frame already showed the counter at 29s - round-7 review C).
& $ffmpeg -y -f lavfi -i "testsrc=duration=600:size=320x240:rate=10" -pix_fmt yuv420p $mp4 2>$null

Copy-Item $mp4 (Join-Path $streamFixture "1") -Force

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
if ($HomeOnly) {
    # Test-only paired fixture avoids the intentional first-run Settings overlay.
    '[{"token":"home-visual-fixture","name":"CI phone"}]' | Set-Content (Join-Path $cfgDir 'paired.json')
    $history = @{}
    foreach ($file in Get-ChildItem $libDir -Filter '*.mp4') {
        $history[$file.FullName.ToUpperInvariant()] = @{ PositionMs=120000; DurationMs=600000; FileSize=$file.Length; ModifiedUtc=$file.LastWriteTimeUtc }
    }
    $history | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $cfgDir 'playback-history.json')
}
$pH = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $prevFx, 'Process')
$hwH = Wait-MainWindow $pH 15
Close-PairingWindow $pH
if ($hwH -ne [System.IntPtr]::Zero) {
    [ZWU32]::SetWindowPos($hwH, [System.IntPtr]::Zero, 30, 4, 940, 700, $SWP_NOZORDER) | Out-Null
    Start-Sleep -Milliseconds 900
}
[ZWU32]::SetForegroundWindow($hwH) | Out-Null
Start-Sleep -Seconds 2
Close-PairingWindow $pH
$sz1b = CaptureWindow $hwH "$env:RUNNER_TEMP/pc-home-library.png"
StopZephyr
if ($HomeOnly) {
    if ($sz1b -eq '0 x 0') { throw 'Home visual capture missing' }
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    return
}
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

# ===================== 2.0 Checkpoint 1 evidence (one shell window) =====================
# CP1 legs, judged on the real exe with real input:
#  cp1-1-home.png            library view in the ONE shell window (render 01 layout)
#  cp1-2-playing.png         double-click a library tile: the SAME window swaps to the player
#  cp1-3-subtitles.png       local sidecar subtitle (Big Buck Bunny.srt) shown (status: spu on a real track)
#  cp1-4-library-again.png   Back: the SAME window swaps to the library; overlay gone
#  cp1-5-after-tray-close.png  window X while playing: process alive, NO visible window
#                            (no bar, no overlay) - full desktop grab
#  cp1-6-restored.png        relaunch restores the same shell (library), same handle
# Keyboard: a REAL click on a bar button, then REAL Up/Down keys must move the
# player's own volume by 5 (read back from /control/status).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Find-ByAutoId($rootEl, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct ZCP1RECT { public int Left, Top, Right, Bottom; }
public static class ZCP1 {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out ZCP1RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    // Every VISIBLE top-level window of the process with a non-empty rect, titled or
    // not (LibVLCSharp's overlay window has no title).
    public static System.Collections.Generic.List<string> Visible(uint pid) {
        var list = new System.Collections.Generic.List<string>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            ZCP1RECT r; GetWindowRect(h, out r);
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
            var t = new System.Text.StringBuilder(256); GetWindowTextW(h, t, 256);
            var c = new System.Text.StringBuilder(256); GetClassNameW(h, c, 256);
            list.Add(h.ToInt64() + " title=[" + t + "] class=[" + c + "] rect=(" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + ")");
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static System.Collections.Generic.List<IntPtr> VisibleHandles(uint pid) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint wp);
            if (wp == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
# Flash sampling (CP1 repair, client-area rule since round 2): a background sampler
# reads every visible top-level window of the process about every 50 ms during the
# view switches, and grabs the shell's CONTENT area at full size: the client area
# below the 32 px caption bar (the dark caption can no longer make a blank content
# area look non-blank - round 1's whole-window 0.995 rule read an all-black client
# area as 0.967 and missed it). A frame is FLAGGED when the shell is not visible, a
# second titled/large Zephyr window is visible, the content area is BLANK (fewer
# than 0.05% of its pixels differ from its most common colour by more than 24 in
# any channel - an all-black or all-one-colour frame; any real screen, the loading
# panel with its spinner/title/bar included, has far more), or a quarter or more of
# it is one LIGHT colour. Every flagged frame is saved as a PNG together with the
# frame before and the frame after it. Flagged frames are a CP1 GATE (the verdict
# cannot read ALL PASS with one in the log). Still SAMPLING at ~50 ms: zero flagged
# frames means none observed, never a proof.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ZFlash {
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
    [StructLayout(LayoutKind.Sequential)] public struct BIH { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biX, biY; public uint biClrUsed, biClrImportant; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BIH bi, uint usage);
    const int GW = 48, GH = 30; const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    static Thread _t; static volatile bool _run; static uint _pid; static IntPtr _shell; static string _dir, _phase;
    static readonly List<string> _frames = new List<string>();
    static int _n, _shellHidden, _second, _blank, _bright, _popups, _saved, _changes; static long _t0, _tLast;
    static int _prevMode = -1; static double _prevFrac = -1;
    static int _flaggedFrames;
    static byte[] _prevBits; static int _prevW, _prevH; static long _prevT; static bool _prevSaved, _saveNext;
    [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref P p);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    const double BlankFrac = 0.0005; const int Tol = 24, CaptionDip = 32;

    // w x h pixels of the screen at (x,y), 32-bit BGRA, top-down.
    static byte[] Grab(int x, int y, int sw, int sh, int w, int h) {
        IntPtr scr = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(scr), bmp = CreateCompatibleBitmap(scr, w, h), old = SelectObject(mem, bmp);
        try {
            SetStretchBltMode(mem, 3); // COLORONCOLOR: nearest pixel, a true point grid
            StretchBlt(mem, 0, 0, w, h, scr, x, y, sw, sh, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var bi = new BIH { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            GetDIBits(mem, bmp, 0, (uint)h, bits, ref bi, 0);
            return bits;
        } finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, scr); }
    }
    static void SaveBmp(string path, byte[] bits, int w, int h) {
        using (var f = new BinaryWriter(File.Create(path))) {
            int size = 54 + bits.Length;
            f.Write((ushort)0x4D42); f.Write(size); f.Write(0); f.Write(54);
            f.Write(40); f.Write(w); f.Write(-h); f.Write((ushort)1); f.Write((ushort)32); f.Write(0); f.Write(bits.Length); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
            f.Write(bits);
        }
    }
    static void SaveFrame(string tag, long t, byte[] bits, int w, int h) {
        if (_saved >= 40) return;
        SaveBmp(Path.Combine(_dir, "cp1-flash-" + _phase + "-" + t.ToString("D5") + "ms-" + tag + ".bmp"), bits, w, h); _saved++;
    }
    public static void Start(uint pid, IntPtr shell, string dir, string phase) {
        _pid = pid; _shell = shell; _dir = dir; _phase = phase;
        lock (_frames) { _frames.Clear(); }
        _n = _shellHidden = _second = _blank = _bright = _popups = _saved = _changes = 0; _prevMode = -1; _prevFrac = -1;
        _prevBits = null; _prevSaved = false; _saveNext = false; _flaggedFrames = 0;
        _run = true; _t0 = Environment.TickCount64; _tLast = _t0;
        _t = new Thread(Loop) { IsBackground = true }; _t.Start();
    }
    static void Loop() {
        while (_run) {
            long now = Environment.TickCount64;
            try { Sample(now - _t0); } catch (Exception e) { lock (_frames) _frames.Add((now - _t0) + "ms sampler error " + e.GetType().Name); }
            _tLast = Environment.TickCount64;
            int wait = 50 - (int)(_tLast - now); if (wait > 0) Thread.Sleep(wait);
        }
    }
    static void Sample(long t) {
        var wins = new List<string>(); bool shellVis = false; int second = 0, popups = 0; R sr = new R();
        EnumWindows((h, l) => {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp != _pid || !IsWindowVisible(h)) return true;
            R r; GetWindowRect(h, out r);
            if (r.Rt - r.L <= 0 || r.B - r.T <= 0) return true;
            var tt = new StringBuilder(256); GetWindowTextW(h, tt, 256);
            var title = tt.ToString();
            string kind;
            if (h == _shell) { shellVis = true; sr = r; kind = "shell"; }
            else if (title == "LibVLCSharp.WPF") kind = "overlay";
            else {
                long area = (long)(r.Rt - r.L) * (r.B - r.T);
                if (title.Length > 0 || area >= 200L * 200L) { second++; kind = "SECOND"; } else { popups++; kind = "popup"; }
            }
            wins.Add(kind + ":" + h.ToInt64() + "[" + title + "](" + r.L + "," + r.T + "," + r.Rt + "," + r.B + ")");
            return true;
        }, IntPtr.Zero);
        string pix = "";
        bool blank = false, bright = false;
        if (shellVis) {
            // CONTENT area: the client rect below the caption bar (WindowChrome draws the
            // caption inside the client area, 32 DIP tall; full screen has none).
            R cr; GetClientRect(_shell, out cr); var o0 = new P(); ClientToScreen(_shell, ref o0);
            double scale = Math.Max(1, GetDpiForWindow(_shell)) / 96.0;
            int cap = (int)Math.Round(CaptionDip * scale);
            int cx = o0.X, cy = o0.Y + cap, cw = cr.Rt - cr.L, ch = cr.B - cr.T - cap;
            if (cw < 16 || ch < 16) { cx = sr.L; cy = sr.T; cw = sr.Rt - sr.L; ch = sr.B - sr.T; }
            // One full-size grab per frame: the rules read it and a saved frame is
            // exactly the frame that was judged.
            var px = Grab(cx, cy, cw, ch, cw, ch);
            var counts = new Dictionary<int, int>(); int best = 0, bestKey = 0;
            for (int i = 0; i < GW * GH; i++) {
                int gx = ((i % GW) * cw + cw / 2) / GW, gy = ((i / GW) * ch + ch / 2) / GH, o = (gy * cw + gx) * 4;
                int k = ((px[o+2] >> 3) << 10) | ((px[o+1] >> 3) << 5) | (px[o] >> 3);
                int c; counts.TryGetValue(k, out c); counts[k] = ++c; if (c > best) { best = c; bestKey = k; }
            }
            double frac = best / (double)(GW * GH);
            // Exact dominant colour: the true pixel value under the most common grid bucket.
            int mr = -1, mg = 0, mb = 0;
            for (int i = 0; i < GW * GH && mr < 0; i++) {
                int gx = ((i % GW) * cw + cw / 2) / GW, gy = ((i / GW) * ch + ch / 2) / GH, o = (gy * cw + gx) * 4;
                int k = ((px[o+2] >> 3) << 10) | ((px[o+1] >> 3) << 5) | (px[o] >> 3);
                if (k == bestKey) { mr = px[o+2]; mg = px[o+1]; mb = px[o]; }
            }
            long differ = 0, total = (long)cw * ch;
            for (int o = 0; o < px.Length; o += 4) {
                if (Math.Abs(px[o+2] - mr) > Tol || Math.Abs(px[o+1] - mg) > Tol || Math.Abs(px[o] - mb) > Tol) differ++;
            }
            double content = differ / (double)total;
            blank = content < BlankFrac;
            // The app is dark throughout (library, loading panel, video letterbox): a
            // quarter or more of the content in one LIGHT colour is a bright flash.
            bright = frac >= 0.25 && mr >= 0xC0 && mg >= 0xC0 && mb >= 0xC0;
            bool change = _prevMode >= 0 && (bestKey != _prevMode || Math.Abs(frac - _prevFrac) > 0.15);
            if (change) _changes++;
            pix = string.Format(" content=({0},{1},{2}x{3}) mode=#{4:X2}{5:X2}{6:X2} flat={7:0.000} differing={8:0.0000}", cx, cy, cw, ch, mr, mg, mb, frac, content);
            // Saved: every flagged frame with the frame before and after it, plus the
            // colour transitions (bounded), so a person can look at what was judged.
            bool flagged = blank || bright;
            if (flagged) {
                if (_prevBits != null && !_prevSaved) SaveFrame("before", _prevT, _prevBits, _prevW, _prevH);
                SaveFrame(blank ? "BLANK" : "BRIGHT", t, px, cw, ch); pix += " saved"; _saveNext = true;
            } else if (_saveNext) { SaveFrame("after", t, px, cw, ch); pix += " saved"; _saveNext = false; }
            else if (change && _saved < 30) { SaveFrame("change", t, px, cw, ch); pix += " saved"; }
            _prevSaved = pix.EndsWith(" saved"); _prevBits = px; _prevW = cw; _prevH = ch; _prevT = t;
            _prevMode = bestKey; _prevFrac = frac;
        }
        _n++; if (!shellVis) _shellHidden++; if (second > 0) _second++; if (blank) _blank++; if (bright) _bright++; if (popups > 0) _popups++;
        if (!shellVis || second > 0 || blank || bright) _flaggedFrames++;
        lock (_frames) _frames.Add(t + "ms" + (shellVis ? "" : " SHELL-NOT-VISIBLE") + (blank ? " BLANK" : "") + (bright ? " BRIGHT" : "") + pix + " | " + string.Join(" ; ", wins));
    }
    public static string Stop() {
        _run = false; if (_t != null) _t.Join(3000);
        long span = _tLast - _t0;
        lock (_frames) { var all = new List<string> { "== phase " + _phase + " ==" }; all.AddRange(_frames); File.AppendAllLines(Path.Combine(_dir, "cp1-flash-frames.txt"), all); }
        return string.Format("{0}: {1} frames over {2} ms (~{3} ms apart); FLAGGED {4} = shell not visible {5}, second Zephyr window {6}, BLANK client area {7}, light flash {8}; small untitled popups (tooltips, not flagged): {9}; colour transitions: {10}",
            _phase, _n, span, _n > 0 ? span / _n : 0, Bad, _shellHidden, _second, _blank, _bright, _popups, _changes);
    }
    public static int Frames { get { return _n; } }
    public static int Bad { get { return _flaggedFrames; } }
    public static int BlankFrames { get { return _blank; } }
    public static int BrightFrames { get { return _bright; } }
}
'@
$cp1FlashTotal = 0; $cp1FlashBad = 0
Remove-Item "$env:RUNNER_TEMP/cp1-flash-*" -ErrorAction SilentlyContinue
function Cp1FlashStart($phase) { [ZFlash]::Start([uint32]$pC.Id, $shellH, $env:RUNNER_TEMP, $phase) }
function Cp1FlashStop() {
    $line = [ZFlash]::Stop()
    $script:cp1FlashTotal += [ZFlash]::Frames; $script:cp1FlashBad += [ZFlash]::Bad
    Cp1Log "flash sample $line"
}
$cp1Note = "$env:RUNNER_TEMP/cp1-note.txt"
$cp1Fail = New-Object System.Collections.Generic.List[string]
function Cp1Log($s) { Write-Host "CP1: $s"; $s | Add-Content $cp1Note }
function Cp1Gate($ok, $s) { if ($ok) { Cp1Log "PASS $s" } else { Cp1Log "FAIL $s"; $cp1Fail.Add($s) } }
function Cp1Status() {
    try { return Invoke-RestMethod -Uri "http://127.0.0.1:47823/control/status" -Headers @{ Authorization = "Bearer ci-capture-token" } -TimeoutSec 5 } catch { return $null }
}
function Cp1ClickPoint($x, $y, [switch]$Double) {
    [ZWU32]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 250
    $n = if ($Double) { 2 } else { 1 }
    for ($k = 0; $k -lt $n; $k++) {
        [ZWU32]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
        [ZWU32]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
    Start-Sleep -Milliseconds 600
}
function Cp1ClickElement($hwnd, $id) {
    # The bar lives in LibVLCSharp's overlay window (a separate top-level window
    # owned by the shell): search the shell first, then every visible window.
    $el = $null
    $roots = @($hwnd) + @([ZCP1]::VisibleHandles($pC.Id) | Where-Object { $_ -ne $hwnd })
    foreach ($rh in $roots) {
        try { $rootE = [System.Windows.Automation.AutomationElement]::FromHandle($rh); $el = Find-ByAutoId $rootE $id } catch { $el = $null }
        if ($el -ne $null) { break }
    }
    if ($el -eq $null) { Cp1Log "element $id not found via UIA"; return $false }
    $b = $el.Current.BoundingRectangle
    if ($b.Width -le 0) { Cp1Log "element $id has no bounds"; return $false }
    Cp1ClickPoint ($b.X + $b.Width / 2) ($b.Y + $b.Height / 2)
    return $true
}
function Cp1DoubleClickTile($hwnd, $name) {
    $rootE = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $rootE.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el -eq $null) { Cp1Log "tile text [$name] not found via UIA"; return $false }
    $b = $el.Current.BoundingRectangle
    # The name sits under the thumbnail: double-click the thumbnail above it.
    Cp1ClickPoint ($b.X + 20) ($b.Y - 40) -Double
    return $true
}
function Cp1DesktopGrab($path) {
    $bmpD = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $gD = [System.Drawing.Graphics]::FromImage($bmpD)
    $gD.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmpD.Size)
    $bmpD.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $gD.Dispose(); $bmpD.Dispose()
}

Remove-Item $cp1Note -ErrorAction SilentlyContinue
Cp1Log "virtual screen $($vs.Width)x$($vs.Height)"
# Phone servers for the round-2 legs: 8902 dribbles the movie (about 10 KB/s for the
# first 10 s after its first request, then full speed); 8903 serves a movie whose mp4
# header passes preparation but whose body the decoder cannot use.
$brokenRoot = Join-Path $env:RUNNER_TEMP 'broken'
New-Item -ItemType Directory -Force (Join-Path $brokenRoot 's/tok') | Out-Null
$brokenBytes = New-Object byte[] (256 * 1024)
(New-Object System.Random 7).NextBytes($brokenBytes)
[byte[]]$ftyp = 0,0,0,0x18,0x66,0x74,0x79,0x70,0x69,0x73,0x6F,0x6D,0,0,2,0,0x69,0x73,0x6F,0x6D,0x69,0x73,0x6F,0x32
[Array]::Copy($ftyp, $brokenBytes, $ftyp.Length)
[IO.File]::WriteAllBytes((Join-Path $brokenRoot 's/tok/1'), $brokenBytes)
$srvSlow = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8902","10","1024" -PassThru -WindowStyle Hidden
$srvBroken = Start-Process python -ArgumentList "scripts/range_server.py","$brokenRoot","8903" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
# Library fixture shaped like render 01: folders first, then videos; the first
# video carries a same-name .srt sidecar.
$cp1Lib = Join-Path $env:RUNNER_TEMP 'Movies'
Remove-Item $cp1Lib -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $cp1Lib | Out-Null
$cp1Folders = 'Action','Classics','Comedy','Horror','Kids','Noir','Silent era','Thrillers','New downloads'
foreach ($f in $cp1Folders) {
    New-Item -ItemType Directory -Force (Join-Path $cp1Lib $f) | Out-Null
    if ($f -ne 'New downloads') { Copy-Item "$env:RUNNER_TEMP/clip.mp4" (Join-Path $cp1Lib "$f/$f feature.mp4") }
}
$cp1Videos = 'Tears of Steel.mp4','His Girl Friday.mp4','Charade.mp4','Night of the Living Dead.mp4','Sintel.mp4','Big Buck Bunny.mp4','Detour.mp4','Metropolis.mp4'
foreach ($v in $cp1Videos) { Copy-Item "$env:RUNNER_TEMP/clip.mp4" (Join-Path $cp1Lib $v) }
# One cue for the whole clip, so the capture shows it whenever it lands.
[IO.File]::WriteAllText((Join-Path $cp1Lib 'Big Buck Bunny.srt'), "1`r`n00:00:00,000 --> 00:59:00,000`r`nZephyr subtitle proof`r`n")
$cp1Thumbs = Join-Path $env:RUNNER_TEMP 'cp1-thumbs'
New-Item -ItemType Directory -Force $cp1Thumbs | Out-Null
$srcs = 'testsrc=size=320x200:rate=1','smptebars=size=320x200:rate=1','mandelbrot=size=320x200:rate=1','rgbtestsrc=size=320x200:rate=1','cellauto=size=320x200:rate=1','life=size=320x200:rate=1:mold=10','testsrc2=size=320x200:rate=1','yuvtestsrc=size=320x200:rate=1'
$ti = 0
foreach ($n in ($cp1Folders + $cp1Videos)) {
    if ($n -eq 'New downloads') { continue }
    & $ffmpeg -y -f lavfi -i $srcs[$ti % $srcs.Count] -frames:v 1 (Join-Path $cp1Thumbs "$n.png") 2>$null
    $ti++
}
$cfgDir = Join-Path $env:APPDATA 'ZephyrPlayer'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$cp1LibJson = $cp1Lib -replace '\\','\\'
[IO.File]::WriteAllText((Join-Path $cfgDir 'settings.json'), "{`"LibraryFolder`":`"$cp1LibJson`"}")
'[{"token":"cp1-fixture","name":"Pixel 8"}]' | Set-Content (Join-Path $cfgDir 'paired.json')
Remove-Item (Join-Path $cfgDir 'playback-history.json') -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable('ZEPHYR_CI_CAPTURE', '1', 'Process')
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $cp1Thumbs, 'Process')
[ZWU32]::SetCursorPos(5, 5) | Out-Null
$pC = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
$shellH = Wait-MainWindow $pC 20
Close-PairingWindow $pC
Cp1Gate ($shellH -ne [System.IntPtr]::Zero) "shell window up (hwnd $shellH)"
if ($shellH -ne [System.IntPtr]::Zero) {
    # Render 01 is 1600x1000; on a smaller runner screen, fit the work area.
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $cw = [math]::Min(1600, $wa.Width); $ch = [math]::Min(1000, $wa.Height)
    [ZWU32]::SetWindowPos($shellH, [System.IntPtr]::Zero, $wa.Left + [int](($wa.Width - $cw) / 2), $wa.Top + [int](($wa.Height - $ch) / 2), $cw, $ch, $SWP_NOZORDER) | Out-Null
    [ZWU32]::SetForegroundWindow($shellH) | Out-Null
    Start-Sleep -Seconds 3
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-1-home.png"
    Cp1Log ("windows after launch: " + ((([ZCP1]::Visible($pC.Id)) -join ' | ')))

    # Library -> player by a REAL double-click on a tile.
    Cp1FlashStart 'library-to-player'
    $ok = Cp1DoubleClickTile $shellH 'Big Buck Bunny'
    $playing = $false
    foreach ($i in 1..30) { $st = Cp1Status; if ($st -and $st.state -eq 'Playing') { $playing = $true; break }; Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1000
    Cp1FlashStop
    Cp1Gate $playing "double-click on a library tile started playback (state=$($st.state))"
    [ZWU32]::SetCursorPos(5, 5) | Out-Null
    $playerH = [ZWU32]::FindByTitle($pC.Id, 'Zephyr Player')
    $titled = [ZWU32]::FindAllByTitle($pC.Id, 'Zephyr Player').Count
    Cp1Gate (($playerH -eq $shellH) -and ($titled -eq 1)) "player opened IN the same shell window (hwnd $playerH vs $shellH, titled windows=$titled)"
    WakeControls
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-2-playing.png"
    Cp1Log ("windows while playing: " + ((([ZCP1]::Visible($pC.Id)) -join ' | ')))

    # Subtitles: the local sidecar is attached and shown.
    $subsOn = $false
    foreach ($i in 1..20) { $st = Cp1Status; if ($st -and $st.spu -ge 0 -and $st.spuTracks.Count -ge 1) { $subsOn = $true; break }; Start-Sleep -Milliseconds 500 }
    Cp1Gate $subsOn "local sidecar subtitle listed and selected (spu=$($st.spu) tracks=$(($st.spuTracks | ForEach-Object { "$($_.id):$($_.name)" }) -join ','))"
    # The bar covers the bottom band where subtitles render: park the cursor
    # outside and let the bar hide before the subtitle capture.
    [ZWU32]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Milliseconds 4500
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-3-subtitles.png"

    # Keyboard after a bar click: REAL click on the 10s skip-back button, then REAL keys.
    $st0 = Cp1Status
    $clicked = Cp1ClickElement $shellH 'SkipBackButton'
    # No SetForegroundWindow here: the keys go wherever the real click left focus.
    [ZCP1]::Key(0x28)  # Down
    Start-Sleep -Milliseconds 400
    [ZCP1]::Key(0x28)  # Down
    Start-Sleep -Milliseconds 600
    $st1 = Cp1Status
    [ZCP1]::Key(0x26)  # Up
    Start-Sleep -Milliseconds 600
    $st2 = Cp1Status
    Cp1Gate ($clicked -and $st0 -and $st1 -and $st2 -and ($st1.volume -eq [math]::Max(0, $st0.volume - 10)) -and ($st2.volume -eq [math]::Min(100, $st1.volume + 5))) "after a real bar click, Down/Down/Up moved the player volume $($st0.volume) -> $($st1.volume) -> $($st2.volume) (decoder gain $($st2.decoder_gain))"
    # Space after the bar click must pause the player (focus never stayed on the button).
    [ZCP1]::Key(0x20)
    Start-Sleep -Milliseconds 800
    $st3 = Cp1Status
    Cp1Gate ($st3 -and $st3.state -eq 'Paused') "Space after a bar click paused the player (state=$($st3.state))"
    [ZCP1]::Key(0x20)
    Start-Sleep -Milliseconds 500

    # Add-subtitle file picker, REAL UI (round 2, item 5): click the bar's subtitles
    # button, click "Add subtitle file..." in its pop-up, type a fixture .srt into the
    # Windows file dialog and press Enter; the new track must attach and be selected.
    $pickDir = Join-Path $env:RUNNER_TEMP 'picker'
    New-Item -ItemType Directory -Force $pickDir | Out-Null
    $pickSrt = Join-Path $pickDir 'Picker Fixture.srt'
    [IO.File]::WriteAllText($pickSrt, "1`r`n00:00:00,000 --> 00:59:00,000`r`nPicked through the file dialog`r`n")
    $stP0 = Cp1Status
    $tracks0 = @($stP0.spuTracks).Count
    WakeControls
    $capClicked = Cp1ClickElement $shellH 'CaptionsButton'
    $menuItem = $null
    $nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Add subtitle file...')
    foreach ($try in 1..12) {
        foreach ($rh in [ZCP1]::VisibleHandles($pC.Id)) {
            try { $menuItem = [System.Windows.Automation.AutomationElement]::FromHandle($rh).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond) } catch { $menuItem = $null }
            if ($menuItem -ne $null) { break }
        }
        if ($menuItem -ne $null) { break }
        Start-Sleep -Milliseconds 300
    }
    $pickNote = "captions button clicked=$capClicked; menu item found=$($menuItem -ne $null)"
    $dlgH = [System.IntPtr]::Zero
    if ($menuItem -ne $null) {
        # A real mouse click (a UIA Invoke would block this script inside the modal dialog).
        $mb = $menuItem.Current.BoundingRectangle
        Cp1ClickPoint ($mb.X + $mb.Width / 2) ($mb.Y + $mb.Height / 2)
        foreach ($try in 1..20) {
            $dlgLine = @([ZCP1]::Visible($pC.Id) | Where-Object { $_ -match 'class=\[#32770\]' }) | Select-Object -First 1
            if ($dlgLine) { $dlgH = [System.IntPtr]::new([int64]($dlgLine -split ' ')[0]); break }
            Start-Sleep -Milliseconds 300
        }
    }
    $pickNote += "; file dialog=" + $(if ($dlgH -ne [System.IntPtr]::Zero) { ([ZCP1]::Visible($pC.Id) | Where-Object { $_ -match '#32770' }) -join ' ' } else { 'not found' })
    $typed = $false
    $dlgOpen = { @([ZCP1]::Visible($pC.Id) | Where-Object { $_ -match 'class=\[#32770\]' }).Count -gt 0 }
    if ($dlgH -ne [System.IntPtr]::Zero) {
        # Run 41033075025: SetFocus on the found edit threw "Target element cannot
        # receive focus" (the dialog was still building). Now: let it settle, set the
        # File name box's value (no focus needed), press the dialog's Open button; if
        # it is still open, type the path into the focused File name box instead.
        Start-Sleep -Milliseconds 1200
        $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        foreach ($try in 1..5) {
            try {
                $dlgEl = [System.Windows.Automation.AutomationElement]::FromHandle($dlgH)
                $combo = Find-ByAutoId $dlgEl '1148'
                $edit = if ($combo -ne $null) { $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond) } else { $null }
                if ($edit -eq $null) { $edit = $combo }
                if ($edit -ne $null) {
                    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($pickSrt)
                    $pickNote += "; File name set via UIA ($($edit.Current.ControlType.ProgrammaticName))"
                    Start-Sleep -Milliseconds 300
                    $open = Find-ByAutoId $dlgEl '1'
                    if ($open -ne $null) { $open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $pickNote += "; Open invoked" }
                    else { [ZCP1]::Key(0x0D); $pickNote += "; Enter pressed" }
                    $typed = $true
                    break
                }
            } catch { $pickNote += "; try $try automation: $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Milliseconds 1000
        if (& $dlgOpen) {
            $pickNote += "; dialog still open, typing the path"
            [ZWU32]::SetForegroundWindow($dlgH) | Out-Null
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait($pickSrt)
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            $typed = $true
            Start-Sleep -Milliseconds 1200
        }
        if (& $dlgOpen) { $pickNote += "; dialog STILL open, pressing Escape"; [ZCP1]::Key(0x1B); Start-Sleep -Milliseconds 500 }
    }
    $attached = $false
    foreach ($i in 1..20) {
        $stP1 = Cp1Status
        if ($stP1 -and @($stP1.spuTracks).Count -eq ($tracks0 + 1)) {
            $newId = (@($stP1.spuTracks) | Select-Object -Last 1).id
            if ($stP1.spu -eq $newId) { $attached = $true; break }
        }
        Start-Sleep -Milliseconds 500
    }
    $pickNote += "; typed=$typed; tracks before=$tracks0 [$((@($stP0.spuTracks) | ForEach-Object { "$($_.id):$($_.name)" }) -join ',')] after=$(@($stP1.spuTracks).Count) [$((@($stP1.spuTracks) | ForEach-Object { "$($_.id):$($_.name)" }) -join ',')] spu=$($stP1.spu)"
    Cp1Log "picker: $pickNote"
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-9-picker-attached.png"
    Cp1Gate $attached "Add-subtitle file picker (real dialog): picked $([IO.Path]::GetFileName($pickSrt)), a new subtitle track attached and selected"
    [ZWU32]::SetCursorPos(5, 5) | Out-Null

    # Player -> library with the bar's Back (home) button: same window, overlay gone.
    WakeControls
    Cp1FlashStart 'back-to-library'
    $clickedHome = Cp1ClickElement $shellH 'HomeButton'
    [ZWU32]::SetCursorPos(5, 5) | Out-Null   # no hover tooltip popups in the window count
    Start-Sleep -Seconds 2
    Cp1FlashStop
    $libH = [ZWU32]::FindByTitle($pC.Id, 'Zephyr Player')
    $vis = [ZCP1]::Visible($pC.Id)
    Cp1Log ("windows after Back: " + ($vis -join ' | '))
    $stB = Cp1Status
    Cp1Gate ($clickedHome -and $libH -eq $shellH -and $vis.Count -eq 1) "Back returned to the library in the same shell (hwnd $libH, visible windows=$($vis.Count), state=$($stB.state))"
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-4-library-again.png"

    # Slow phone source (round 2, items 2 and 5): /play from a phone server that
    # dribbles the movie (about 10 KB/s for its first 10 s). The loading spinner must
    # show, and the slow but valid movie must NOT be stopped: it plays.
    $hdrC = @{ Authorization = "Bearer ci-capture-token" }
    Cp1FlashStart 'phone-slow-start'
    $tSlow = [Environment]::TickCount64
    $slowResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47823/play" -Headers $hdrC -ContentType "application/json" -Body ('{"url":"http://127.0.0.1:8902/s/tok/1"}') -SkipHttpErrorCheck -TimeoutSec 60
    $slowAnswerMs = [Environment]::TickCount64 - $tSlow
    Start-Sleep -Milliseconds 700
    $stSlowLoading = Cp1Status
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-7-slow-phone-spinner.png"
    # libVLC's "Playing" comes when its demuxer opens; the picture follows once
    # buffering ends. Wait for the movie's clock to move (the picture is up), and
    # keep sampling across the spinner -> picture switch (run 41035935547 stopped
    # sampling at Playing + 0.8 s, before that switch).
    $slowPlayed = $false; $slowPlayingMs = -1
    foreach ($i in 1..120) {
        $stS = Cp1Status
        if ($stS -and $stS.state -eq 'Playing' -and $slowPlayingMs -lt 0) { $slowPlayingMs = [Environment]::TickCount64 - $tSlow }
        if ($stS -and $stS.state -eq 'Playing' -and $stS.time -ge 1) { $slowPlayed = $true; break }
        Start-Sleep -Milliseconds 500
    }
    $slowMs = [Environment]::TickCount64 - $tSlow
    Start-Sleep -Milliseconds 1500
    Cp1FlashStop
    Cp1Log "slow phone start: /play answered $($slowResp.StatusCode) after $slowAnswerMs ms; status while loading: $($stSlowLoading | ConvertTo-Json -Compress -Depth 4); libVLC Playing after $slowPlayingMs ms; movie clock past 1 s after $slowMs ms; final state=$($stS.state) time=$($stS.time) title=[$($stS.title)]"
    Cp1Gate ($slowResp.StatusCode -eq 200 -and $slowPlayed) "slow phone source (dribbled) was not stopped: its clock was running after $slowMs ms (spinner frame cp1-7-slow-phone-spinner.png, picture cp1-7b-slow-phone-playing.png)"
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-7b-slow-phone-playing.png"

    # Late PC-side failure (round 2, item 2): /play of a movie whose header passes
    # preparation but which the decoder cannot start. /play answers after the movie
    # is handed to the decoder, so the failure comes later, on the PC only. Record
    # exactly what the phone can see afterwards: /control/status.
    $tLate = [Environment]::TickCount64
    $lateResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47823/play" -Headers $hdrC -ContentType "application/json" -Body ('{"url":"http://127.0.0.1:8903/s/tok/1"}') -SkipHttpErrorCheck -TimeoutSec 60
    Cp1Log "late failure: /play answered HTTP $($lateResp.StatusCode) after $([Environment]::TickCount64 - $tLate) ms, body=[$($lateResp.Content)]"
    $lastLine = ''
    foreach ($i in 1..24) {
        $stL = Cp1Status
        $line = if ($stL) { $stL | Select-Object state, time, length, title, volume, spu | ConvertTo-Json -Compress } else { 'no answer' }
        if ($line -ne $lastLine) { Cp1Log ("late failure: +$([Environment]::TickCount64 - $tLate) ms /control/status " + $line); $lastLine = $line }
        Start-Sleep -Milliseconds 500
    }
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp1-8-late-failure.png"
    Cp1Gate ($stL -and $stL.state -ne 'Playing') "late PC-side failure: the decoder never played it; the phone's /control/status reads state=$($stL.state) title=[$($stL.title)] (no failed-start HTTP answer on this path: /play said $($lateResp.StatusCode))"
    # Back to the library from the error panel's own button.
    # CP2: the item-68 error state replaced CP1's error panel; its Back is ErrorBackButton.
    $backOk = Cp1ClickElement $shellH 'ErrorBackButton'
    if (-not $backOk) { $backOk = Cp1ClickElement $shellH 'HomeButton' }
    [ZWU32]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Seconds 2
    $visLB = [ZCP1]::Visible($pC.Id)
    Cp1Gate ($backOk -and $visLB.Count -eq 1) "error panel's Back returned to the library (visible windows=$($visLB.Count))"

    # Play again, then the window X while playing: to the tray, nothing left on screen.
    Cp1FlashStart 'library-to-player-again'
    $null = Cp1DoubleClickTile $shellH 'Big Buck Bunny'
    foreach ($i in 1..30) { $st = Cp1Status; if ($st -and $st.state -eq 'Playing') { break }; Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1000
    Cp1FlashStop
    WakeControls
    $visPlay = [ZCP1]::Visible($pC.Id)
    Cp1Log ("windows before X (playing=$($st.state)): " + ($visPlay -join ' | '))
    [ZWU32]::PostMessage($shellH, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2
    $pC.Refresh()
    $visGone = [ZCP1]::Visible($pC.Id)
    $stG = Cp1Status
    Cp1Log ("windows after X: " + ($visGone -join ' | '))
    Cp1Gate ((-not $pC.HasExited) -and $visGone.Count -eq 0) "window X while playing: process alive=$(-not $pC.HasExited), visible windows=$($visGone.Count) (bar/overlay gone), playback state=$($stG.state)"
    Cp1DesktopGrab "$env:RUNNER_TEMP/cp1-5-after-tray-close.png"

    # Relaunch = tray restore through the single-instance signal: the same shell returns.
    $pR = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
    Start-Sleep -Seconds 4
    $resH = [ZWU32]::FindByTitle($pC.Id, 'Zephyr Player')
    $visR = [ZCP1]::Visible($pC.Id)
    Cp1Log ("windows after restore: " + ($visR -join ' | '))
    Cp1Gate ($resH -eq $shellH -and $visR.Count -eq 1) "relaunch restored the same shell window (hwnd $resH vs $shellH, visible=$($visR.Count))"
    if ($resH -ne [System.IntPtr]::Zero) { $null = CaptureWindow $resH "$env:RUNNER_TEMP/cp1-6-restored.png" }
}
StopZephyr
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $null, 'Process')
# Flash sampling is GATED since round 2: a flagged frame (blank client area, light
# flash, hidden shell, second window) is a FAIL line, so the verdict below cannot
# read ALL PASS while the frame log holds one. Zero flagged frames is still only
# "none observed in N sampled frames", not a proof.
Cp1Gate ($cp1FlashTotal -gt 0 -and $cp1FlashBad -eq 0) "flash sampler: $cp1FlashBad of $cp1FlashTotal sampled frames flagged (client-area rule; see cp1-flash-frames.txt)"
Cp1Log ("CP1 verdict: " + $(if ($cp1Fail.Count -eq 0) { "ALL GATES PASS, including the flash sampler (0 flagged of $cp1FlashTotal frames)" } else { "FAILED: " + ($cp1Fail -join '; ') }))
foreach ($bmp in (Get-ChildItem "$env:RUNNER_TEMP/cp1-flash-*.bmp" -ErrorAction SilentlyContinue)) {
    try { $img = [System.Drawing.Image]::FromFile($bmp.FullName); $img.Save(($bmp.FullName -replace '\.bmp$', '.png'), [System.Drawing.Imaging.ImageFormat]::Png); $img.Dispose(); Remove-Item $bmp.FullName } catch { }
}
if ($cp1FlashTotal -gt 0 -and $cp1FlashBad -eq 0) { Cp1Log "flash verdict: 0 of $cp1FlashTotal sampled frames flagged by the client-area rule (blank or all-one-colour content, light flash, hidden shell, second window); sampling at ~50 ms, not a proof; raw frames in cp1-flash-frames.txt" }
else { Cp1Log "flash verdict: $cp1FlashBad of $cp1FlashTotal sampled frames FLAGGED by the client-area rule (blank or all-one-colour content, light flash, hidden shell, second window); each saved as cp1-flash-*-BLANK/BRIGHT.png with its before/after frames; see cp1-flash-frames.txt" }
# =====================================================================================
# CP2 (renders 02 / 03 / 04): real-desktop proof of the new bar, its pop-ups, the
# fullscreen pill clamp, the error state, auto-hide, click-to-seek, subtitles above
# the bar, and the Opening reset. Its own app launch and fixtures; its own gates
# (cp2-note.txt). Flash sampling continues on one CP2 library->player phase with
# the same client-area rule; its frames are counted apart and gated the same way.
# =====================================================================================
$cp2Note = "$env:RUNNER_TEMP/cp2-note.txt"
$cp2Fail = New-Object System.Collections.Generic.List[string]
Remove-Item $cp2Note -ErrorAction SilentlyContinue
function Cp2Log($s) { Write-Host "CP2: $s"; $s | Add-Content $cp2Note }
function Cp2Gate($ok, $s) { if ($ok) { Cp2Log "PASS $s" } else { Cp2Log "FAIL $s"; $cp2Fail.Add($s) } }
function Cp2Ui() { $st = Cp1Status; if ($st) { return $st.ui } else { return $null } }
function Cp2Json($o) { if ($o -eq $null) { return 'null' }; return ($o | ConvertTo-Json -Compress -Depth 4) }
function Cp2FindName($name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($rh in [ZCP1]::VisibleHandles($pC.Id)) {
        try { $el = [System.Windows.Automation.AutomationElement]::FromHandle($rh).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) } catch { $el = $null }
        if ($el -ne $null -and $el.Current.BoundingRectangle.Width -gt 0) { return $el }
    }
    return $null
}
function Cp2FindId($id) {
    foreach ($rh in [ZCP1]::VisibleHandles($pC.Id)) {
        try { $el = Find-ByAutoId ([System.Windows.Automation.AutomationElement]::FromHandle($rh)) $id } catch { $el = $null }
        if ($el -ne $null -and $el.Current.BoundingRectangle.Width -gt 0) { return $el }
    }
    return $null
}
function Cp2ClickName($name) {
    $el = Cp2FindName $name
    if ($el -eq $null) { Cp2Log "element named [$name] not found via UIA"; return $false }
    $b = $el.Current.BoundingRectangle
    Cp1ClickPoint ($b.X + $b.Width / 2) ($b.Y + $b.Height / 2)
    return $true
}
function Cp2WaitUi($test, $ms) {
    $until = [Environment]::TickCount64 + $ms
    do { $u = Cp2Ui; if ($u -ne $null -and (& $test $u)) { return $u }; Start-Sleep -Milliseconds 150 } while ([Environment]::TickCount64 -lt $until)
    return (Cp2Ui)
}
function Cp2Post($url) {
    # /play answers only after the hand-off, so it runs as a job: the script keeps
    # sampling the Opening state while the phone start is in flight.
    return Start-Job -ScriptBlock { param($u) try { (Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47823/play" -Headers @{ Authorization = "Bearer ci-capture-token" } -ContentType "application/json" -Body ('{"url":"' + $u + '"}') -SkipHttpErrorCheck -TimeoutSec 90).StatusCode } catch { "error: $($_.Exception.Message)" } } -ArgumentList $url
}
function Cp2CropRect($hWnd) {
    $r = [ZRECT]::new(); [void][ZWU32]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, 16)
    return @(($r.Left + 2), ($r.Top + 2), ($r.Right - 2), ($r.Bottom - 2))
}
# Rows of near-white subtitle text (all channels >= 200) in the capture, between
# $yMin and $yMax (capture pixels), x in the middle 70 %, rows inside the bar's rect
# skipped. Returns @(firstRow, lastRow, rows) or @(-1, -1, 0).
function Cp2SubtitleRows($path, $yMin, $yMax, $barTop, $barBottom) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = [math]::Abs($data.Stride); $bytes = [byte[]]::new($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data); $bmp.Dispose()
    $first = -1; $last = -1; $rows = 0
    $x0 = [int]($w * 0.15); $x1 = [int]($w * 0.85)
    for ($y = [math]::Max(0, $yMin); $y -lt [math]::Min($h, $yMax); $y++) {
        if ($y -ge $barTop -and $y -le $barBottom) { continue }
        $n = 0
        for ($x = $x0; $x -lt $x1; $x++) {
            $o = $y * $stride + $x * 4
            if ($bytes[$o] -ge 200 -and $bytes[$o + 1] -ge 200 -and $bytes[$o + 2] -ge 200) { $n++ }
        }
        if ($n -ge 3) { if ($first -lt 0) { $first = $y }; $last = $y; $rows++ }
    }
    return @($first, $last, $rows)
}

# Fixtures: a 600 s test pattern (the capture clip) and a 600 s dark 16:9 movie with a
# whole-length subtitle cue, so subtitle pixels are the only near-white pixels in the
# picture. Two fresh dribbling phone servers (the CP1 one is past its slow phase).
$cp2Lib = Join-Path $env:RUNNER_TEMP 'Movies-cp2'
Remove-Item $cp2Lib -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $cp2Lib | Out-Null
Copy-Item "$env:RUNNER_TEMP/clip.mp4" (Join-Path $cp2Lib 'Big Buck Bunny.mp4')
& $ffmpeg -y -f lavfi -i "color=c=0x1c2833:s=640x360:r=10" -t 600 -c:v libx264 -preset ultrafast -pix_fmt yuv420p (Join-Path $cp2Lib 'Night Drive.mp4') 2>$null
[IO.File]::WriteAllText((Join-Path $cp2Lib 'Night Drive.srt'), "1`r`n00:00:00,000 --> 00:59:00,000`r`nZephyr subtitle above the bar`r`n")
$cp2Thumbs = Join-Path $env:RUNNER_TEMP 'cp2-thumbs'
New-Item -ItemType Directory -Force $cp2Thumbs | Out-Null
& $ffmpeg -y -f lavfi -i 'testsrc=size=320x200:rate=1' -frames:v 1 (Join-Path $cp2Thumbs 'Big Buck Bunny.png') 2>$null
& $ffmpeg -y -f lavfi -i 'color=c=0x1c2833:s=320x200' -frames:v 1 (Join-Path $cp2Thumbs 'Night Drive.png') 2>$null
$srvSlow2 = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8904","10","1024" -PassThru -WindowStyle Hidden
$srvSlow3 = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8905","10","1024" -PassThru -WindowStyle Hidden
$cp2LibJson = $cp2Lib -replace '\\','\\'
[IO.File]::WriteAllText((Join-Path $cfgDir 'settings.json'), "{`"LibraryFolder`":`"$cp2LibJson`"}")
'[{"token":"cp1-fixture","name":"Pixel 8"}]' | Set-Content (Join-Path $cfgDir 'paired.json')
Remove-Item (Join-Path $cfgDir 'playback-history.json') -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $cp2Thumbs, 'Process')
[ZWU32]::SetCursorPos(5, 5) | Out-Null
$cp2FlashBase = $cp1FlashTotal; $cp2FlashBadBase = $cp1FlashBad
$pC = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
$shellH = Wait-MainWindow $pC 20
Close-PairingWindow $pC
Cp2Gate ($shellH -ne [System.IntPtr]::Zero) "CP2 shell window up (hwnd $shellH)"
$times = [char]0x00D7
if ($shellH -ne [System.IntPtr]::Zero) {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $cw = [math]::Min(1600, $wa.Width); $ch = [math]::Min(1000, $wa.Height)
    [ZWU32]::SetWindowPos($shellH, [System.IntPtr]::Zero, $wa.Left + [int](($wa.Width - $cw) / 2), $wa.Top + [int](($wa.Height - $ch) / 2), $cw, $ch, $SWP_NOZORDER) | Out-Null
    [ZWU32]::SetForegroundWindow($shellH) | Out-Null
    Start-Sleep -Seconds 3
    Cp2Log "shell ${cw}x${ch} on a $($vs.Width)x$($vs.Height) screen"

    # --- library -> player (flash-sampled) ---
    Cp1FlashStart 'cp2-library-to-player'
    $null = Cp1DoubleClickTile $shellH 'Big Buck Bunny'
    $st = $null; foreach ($i in 1..30) { $st = Cp1Status; if ($st -and $st.state -eq 'Playing' -and $st.time -ge 1) { break }; Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1000
    Cp1FlashStop
    Cp2Gate ($st -and $st.state -eq 'Playing') "Big Buck Bunny playing from a library double-click (state=$($st.state) length=$($st.length))"

    # --- click-to-seek (item 50): a REAL click at 75 % of the seek track ---
    WakeControls
    $seekEl = Cp2FindId 'SeekBar'
    if ($seekEl -ne $null) {
        $sb = $seekEl.Current.BoundingRectangle
        $len = [double]$st.length
        $clickX = $sb.X + $sb.Width * 0.75
        # IsMoveToPointEnabled puts the 18 px thumb's centre on the click: the value is
        # (x - left - 9) / (width - 18) of the track.
        $expFrac = (($clickX - $sb.X) - 9) / ($sb.Width - 18)
        $before = (Cp1Status).time
        Cp1ClickPoint $clickX ($sb.Y + $sb.Height / 2)
        Start-Sleep -Milliseconds 1200
        $after = Cp1Status
        $expected = $expFrac * $len
        $tol = [math]::Max(6, 0.015 * $len)
        $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-seek-click.png"
        Cp2Gate ($after -and [math]::Abs($after.time - $expected) -le $tol -and [math]::Abs($after.time - $before) -gt 60) ("click-to-seek: real click at 75 % of the track ({0:N0},{1:N0}; track {2:N0} px) landed at {3} s, expected {4:N1} s +/- {5:N0} (was {6} s; last_seek_fraction={7}, expected {8:N4})" -f $clickX, ($sb.Y + $sb.Height / 2), $sb.Width, $after.time, $expected, $tol, $before, $after.ui.last_seek_fraction, $expFrac)
    } else { Cp2Gate $false "click-to-seek: SeekBar not found via UIA" }

    # --- Opening reset (carry-forward a): a slow phone start over the playing movie ---
    $job = Cp2Post "http://127.0.0.1:8904/s/tok/1"
    $seen = $null; $shot = $false; $samples = 0
    $until = [Environment]::TickCount64 + 9000
    while ([Environment]::TickCount64 -lt $until) {
        $u = Cp2Ui; $samples++
        if ($u -and $u.opening) {
            if ($seen -eq $null) { $seen = $u }
            if (-not $shot) { Start-Sleep -Milliseconds 600; $u2 = Cp2Ui; if ($u2 -and $u2.opening) { $seen = $u2; $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-opening.png"; $shot = $true } }
        }
        if ($shot) { break }
        Start-Sleep -Milliseconds 120
    }
    Cp2Log ("opening sample: " + (Cp2Json ($seen | Select-Object opening, loading, title_text, elapsed_text, remaining_text, total_text, seek_value, meta_text)))
    Cp2Gate ($seen -and $seen.elapsed_text -eq '0:00' -and $seen.remaining_text -eq '-0:00' -and $seen.total_text -eq '0:00 total' -and [double]$seen.seek_value -eq 0 -and $seen.title_text -eq 'Movie from your phone' -and $seen.loading) "Opening over a playing movie shows the NEW start: 0:00 / -0:00 / 0:00 total, thumb at 0, title [$($seen.title_text)] (cp2-opening.png)"
    $stS = $null; foreach ($i in 1..60) { $stS = Cp1Status; if ($stS -and $stS.state -eq 'Playing' -and $stS.time -ge 1) { break }; Start-Sleep -Milliseconds 500 }
    $jobOut = Receive-Job $job -Wait -AutoRemoveJob
    Cp2Log "slow phone start /play answered $jobOut; then state=$($stS.state) time=$($stS.time) title=[$($stS.title)]"

    # --- error state (item 68) + Try again re-runs the same start ---
    $job = Cp2Post "http://127.0.0.1:8903/s/tok/1"
    $uE = Cp2WaitUi { param($u) $u.error } 30000
    $null = Receive-Job $job -Wait -AutoRemoveJob
    Start-Sleep -Milliseconds 500
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-error.png"
    $retryEl = Cp2FindId 'ErrorRetryButton'; $backEl = Cp2FindId 'ErrorBackButton'
    Cp2Gate ($uE -and $uE.error -and $uE.error_message -and $retryEl -ne $null -and $backEl -ne $null -and -not $uE.bar_visible) "error state: message [$($uE.error_message)] with 'Back to library' and 'Try again' (bar stepped aside: bar_visible=$($uE.bar_visible)) (cp2-error.png)"
    $starts0 = $uE.start_count; $url0 = $uE.last_start_url
    $clickedRetry = Cp1ClickElement $shellH 'ErrorRetryButton'
    $uR = Cp2WaitUi { param($u) $u.retry_count -ge 1 -and $u.error } 30000
    Cp2Gate ($clickedRetry -and $uR -and $uR.retry_count -eq 1 -and $uR.start_count -eq ($starts0 + 1) -and $uR.last_start_url -eq $url0) "real click on Try again re-ran the same start (retry_count=$($uR.retry_count), starts $starts0 -> $($uR.start_count), url $($uR.last_start_url)) and it failed the same way (error=$($uR.error))"
    # The next start's Opening never shows the previous failure.
    $job = Cp2Post "http://127.0.0.1:8905/s/tok/1"
    $uO = Cp2WaitUi { param($u) $u.opening } 8000
    Start-Sleep -Milliseconds 400; $uO2 = Cp2Ui
    if ($uO2 -and $uO2.opening) { $uO = $uO2; $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-opening-after-error.png" }
    Cp2Gate ($uO -and $uO.opening -and -not $uO.error -and $uO.title_text -eq 'Movie from your phone' -and (Cp1Status).title -ne $uE.error_message -and $uO.elapsed_text -eq '0:00') "Opening after a failure: title [$($uO.title_text)], error=$($uO.error), elapsed $($uO.elapsed_text) - the failure text is gone (cp2-opening-after-error.png)"
    $null = Receive-Job $job -Wait -AutoRemoveJob
    foreach ($i in 1..60) { $stS = Cp1Status; if ($stS -and $stS.state -eq 'Playing' -and $stS.time -ge 1) { break }; Start-Sleep -Milliseconds 500 }

    # --- the subtitle movie: windowed bar (vs render 02), subtitle above the bar ---
    WakeControls
    $null = Cp1ClickElement $shellH 'HomeButton'
    [ZWU32]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Seconds 2
    $null = Cp1DoubleClickTile $shellH 'Night Drive'
    $stN = $null; foreach ($i in 1..30) { $stN = Cp1Status; if ($stN -and $stN.state -eq 'Playing' -and $stN.spu -ge 0 -and $stN.time -ge 2) { break }; Start-Sleep -Milliseconds 500 }
    Cp2Gate ($stN -and $stN.state -eq 'Playing' -and $stN.spu -ge 0) "Night Drive playing with its sidecar subtitle selected (spu=$($stN.spu))"
    WakeControls
    $uB = Cp2Ui
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-windowed.png"
    Cp2Log ("windowed bar: " + (Cp2Json ($uB | Select-Object bar_opacity, bar_screen, title_text, meta_text, elapsed_text, remaining_text, total_text, reflow, rate, sleep_minutes)))
    $crop = Cp2CropRect $shellH
    if ($uB -and $uB.bar_screen) {
        $barTop = $uB.bar_screen[1] - $crop[1]; $barBottom = $uB.bar_screen[3] - $crop[1]
        $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-subtitle-above-bar.png"
        $subShown = Cp2SubtitleRows "$env:RUNNER_TEMP/cp2-subtitle-above-bar.png" 180 ($crop[3] - $crop[1]) $barTop $barBottom
        [ZWU32]::SetCursorPos(5, 5) | Out-Null
        Start-Sleep -Milliseconds 4500
        $uH = Cp2Ui
        $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-subtitle-bar-hidden.png"
        $subHidden = Cp2SubtitleRows "$env:RUNNER_TEMP/cp2-subtitle-bar-hidden.png" 180 ($crop[3] - $crop[1]) -1 -1
        Cp2Log "subtitle rows with the bar shown: $($subShown -join ',') (bar top $barTop, bottom $barBottom in capture px); with the bar hidden (opacity $($uH.bar_opacity)): $($subHidden -join ',')"
        Cp2Gate ($subShown[2] -ge 6 -and $subShown[1] -lt ($barTop - 4)) "subtitle drawn ABOVE the visible bar: text rows $($subShown[0])-$($subShown[1]) end above the bar top at $barTop (cp2-subtitle-above-bar.png)"
        Cp2Gate ($subHidden[2] -ge 6 -and [math]::Abs($subHidden[1] - $subShown[1]) -le 4) "the same subtitle with the bar hidden sits at rows $($subHidden[0])-$($subHidden[1]) (static lift, not tied to the bar: a stated deviation)"
    } else { Cp2Gate $false "no bar rect in /control/status ui (got $(Cp2Json $uB))" }

    # --- pop-ups (item 59-62, 66): real clicks, one open at a time ---
    WakeControls
    $okS = Cp1ClickElement $shellH 'SpeedButton'
    $uP = Cp2WaitUi { param($u) $u.popup -eq 'speed' } 3000
    $okChip = Cp2ClickName ("1.25" + $times)
    $uP2 = Cp2WaitUi { param($u) [double]$u.rate -eq 1.25 } 3000
    Start-Sleep -Milliseconds 300
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-popup-speed.png"
    $stRate = Cp1Status
    Cp2Gate ($okS -and $uP.popup -eq 'speed' -and $okChip -and [double]$uP2.rate -eq 1.25 -and $uP2.speed_badge -eq ("1.25" + $times) -and $uP2.popup -eq 'speed') "speed pop-up: opened by a real click; the 1.25$times chip set rate $($stRate.rate) with the bar badge [$($uP2.speed_badge)] (cp2-popup-speed.png)"
    $okL = Cp1ClickElement $shellH 'SleepButton'
    $uQ = Cp2WaitUi { param($u) $u.popup -eq 'sleep' } 3000
    $okRow = Cp2ClickName '30 minutes'
    $uQ2 = Cp2WaitUi { param($u) $u.sleep_minutes -eq 30 } 3000
    Start-Sleep -Milliseconds 300
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-popup-sleep.png"
    Cp2Gate ($okL -and $uQ.popup -eq 'sleep' -and $okRow -and $uQ2.sleep_minutes -eq 30 -and ($uQ2.sleep_badge -eq '30m' -or $uQ2.sleep_badge -eq '29m')) "sleep pop-up: opening it closed the speed pop-up (popup=$($uQ.popup)); '30 minutes' set the timer, bar badge [$($uQ2.sleep_badge)] (cp2-popup-sleep.png)"
    $okC = Cp1ClickElement $shellH 'CaptionsButton'
    $uC = Cp2WaitUi { param($u) $u.popup -eq 'subtitles' } 3000
    Start-Sleep -Milliseconds 300
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-popup-subtitles.png"
    $addRow = Cp2FindId 'AddSubtitleRow'
    Cp2Gate ($okC -and $uC.popup -eq 'subtitles' -and $addRow -ne $null) "subtitles pop-up: opened by a real click (only it open: popup=$($uC.popup)), with the 'Add subtitle file...' row (cp2-popup-subtitles.png)"
    [ZCP1]::Key(0x1B)  # Esc
    $uX = Cp2WaitUi { param($u) $u.popup -eq $null } 2000
    Cp2Gate ($uX -and $uX.popup -eq $null -and -not $uX.fullscreen) "Esc closed the pop-up (popup=$($uX.popup)) and stayed windowed"

    # --- auto-hide (item 65): gone after idle, back on a mouse move ---
    [ZWU32]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Milliseconds 4500
    $uA = Cp2Ui
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-autohide-hidden.png"
    WakeControls
    $uA2 = Cp2Ui
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-autohide-shown.png"
    Cp2Gate ($uA -and [double]$uA.bar_opacity -eq 0 -and [double]$uA.top_opacity -eq 0 -and $uA2 -and [double]$uA2.bar_opacity -eq 1) "auto-hide: bar and top overlay at opacity $($uA.bar_opacity)/$($uA.top_opacity) after 4.5 s idle while playing, back to $($uA2.bar_opacity) on a real mouse move (cp2-autohide-hidden.png, cp2-autohide-shown.png)"

    # --- fullscreen pill (render 03, U2): 780 px max, clamped to the screen ---
    $okF = Cp1ClickElement $shellH 'FullscreenButton'
    Start-Sleep -Milliseconds 1500
    WakeControls
    $uF = Cp2Ui
    $null = CaptureWindow $shellH "$env:RUNNER_TEMP/cp2-fullscreen.png"
    if ($uF -and $uF.bar_screen) {
        $bw = $uF.bar_screen[2] - $uF.bar_screen[0]
        $want = [math]::Min(780, $vs.Width - 48)
        $gapL = $uF.bar_screen[0] - $vs.Left; $gapR = ($vs.Left + $vs.Width) - $uF.bar_screen[2]
        $gapB = ($vs.Top + $vs.Height) - $uF.bar_screen[3]
        Cp2Gate ($okF -and $uF.fullscreen -and [math]::Abs($bw - $want) -le 3 -and [math]::Abs($gapL - $gapR) -le 3 -and [math]::Abs($gapB - 28) -le 4) "fullscreen pill: drawn $bw px wide (want min(780, $($vs.Width) - 48) = $want), gaps left/right $gapL/$gapR, bottom $gapB (want 28) (cp2-fullscreen.png)"
    } else { Cp2Gate $false "fullscreen: no bar rect (ui=$(Cp2Json $uF))" }
    [ZCP1]::Key(0x1B)
    Start-Sleep -Milliseconds 1200
    $uW = Cp2Ui
    Cp2Gate ($uW -and -not $uW.fullscreen) "Esc left full screen (fullscreen=$($uW.fullscreen))"
}
StopZephyr
Stop-Process -Id $srvSlow2.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $srvSlow3.Id -Force -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $null, 'Process')
$cp2Frames = $cp1FlashTotal - $cp2FlashBase; $cp2Bad = $cp1FlashBad - $cp2FlashBadBase
Cp2Gate ($cp2Frames -gt 0 -and $cp2Bad -eq 0) "flash sampler (CP2 library->player phase): $cp2Bad of $cp2Frames sampled frames flagged; CP1 + CP2 total: $cp1FlashBad of $cp1FlashTotal"
foreach ($bmp in (Get-ChildItem "$env:RUNNER_TEMP/cp1-flash-*.bmp" -ErrorAction SilentlyContinue)) {
    try { $img = [System.Drawing.Image]::FromFile($bmp.FullName); $img.Save(($bmp.FullName -replace '\.bmp$', '.png'), [System.Drawing.Imaging.ImageFormat]::Png); $img.Dispose(); Remove-Item $bmp.FullName } catch { }
}
Cp2Log ("CP2 verdict: " + $(if ($cp2Fail.Count -eq 0) { "ALL GATES PASS" } else { "FAILED: " + ($cp2Fail -join '; ') }))
# Back to a clean first-run state for the legacy legs below.
Remove-Item (Join-Path $cfgDir 'settings.json') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $cfgDir 'paired.json') -Force -ErrorAction SilentlyContinue

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
# The first-run pairing/Settings window covers the video: close it like a user
# would (2.0: Settings is owned by the one shell, so it would sit over the movie;
# the app also closes it on a playback start).
Close-PairingWindow $pBar
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
$playResp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47823/play" -Headers $hdr -ContentType "application/json" -Body ('{"url":"http://127.0.0.1:8901/s/tok/1"}') -SkipHttpErrorCheck
if ($playResp.StatusCode -ne 200) { throw "phone-start play POST answered $($playResp.StatusCode): $($playResp.Content)" }
# Find the player window BY TITLE: Home and MainWindow are both titled "Zephyr
# Player", the pairing window has its own title, and any transient dialog (which
# once masqueraded as the player, then died mid-poll and left a stale handle) is
# excluded by the title match. The player window is the "Zephyr Player" window
# that is NOT the home window.
function Find-PlayerWindow($procId, $homeHwnd) {
    # 2.0: ONE shell window hosts the library and the player, so the player IS the
    # "Zephyr Player" window (the same handle as Home). It is only visible once
    # playback put it on screen (a tray-closed shell is hidden).
    foreach ($h in [ZWU32]::FindAllByTitle($procId, 'Zephyr Player')) { return $h }
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
# Positive control first: Measure-Bar must detect a KNOWN bar. CI's own control-bar
# render (from the Player test suite, 1920 wide at 96 DPI) is composited at the bar's
# real position over a black frame the size of the player capture; a metric that
# cannot see a certain bar would otherwise report "no bar" for any reason (round-7 C).
$ctrlRender = "$env:RUNNER_TEMP/pc-controlbar-render.png"
if (Test-Path $ctrlRender) {
    $base = [System.Drawing.Bitmap]::new(782, 489)
    $gBase = [System.Drawing.Graphics]::FromImage($base)
    $gBase.Clear([System.Drawing.Color]::FromArgb(27, 27, 30))
    $render = [System.Drawing.Image]::FromFile($ctrlRender)
    $scale = 782.0 / $render.Width
    $dh = [int]($render.Height * $scale)
    $gBase.DrawImage($render, 0, 489 - $dh - 10, 782, $dh)
    $gBase.Dispose(); $render.Dispose()
    $base.Save("$env:RUNNER_TEMP/pc-bar-positivecontrol.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $base.Dispose()
    $mCtrl = Measure-Bar "$env:RUNNER_TEMP/pc-bar-positivecontrol.png"
    Write-Host "bar metric positive control: golden=$($mCtrl[0]) seekRun=$($mCtrl[1])%"
    if (-not ($mCtrl[0] -ge 2 -and $mCtrl[1] -ge 40)) { throw "bar metric positive control FAILED: a known bar render scored golden=$($mCtrl[0]) seekRun=$($mCtrl[1])% - the metric is blind, no bar verdict is meaningful" }
    # The default-style render must score too: a metric that only sees gold would
    # pass this control and still be blind to what users actually get.
    $ctrlOrange = "$env:RUNNER_TEMP/pc-controlbar-render-orange.png"
    if (Test-Path $ctrlOrange) {
        $baseO = [System.Drawing.Bitmap]::new(782, 489)
        $gO = [System.Drawing.Graphics]::FromImage($baseO)
        $gO.Clear([System.Drawing.Color]::FromArgb(27, 27, 30))
        $renderO = [System.Drawing.Image]::FromFile($ctrlOrange)
        $dhO = [int]($renderO.Height * $scale)
        $gO.DrawImage($renderO, 0, 489 - $dhO - 10, 782, $dhO)
        $gO.Dispose(); $renderO.Dispose()
        $baseO.Save("$env:RUNNER_TEMP/pc-bar-positivecontrol-orange.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $baseO.Dispose()
        $mCtrlO = Measure-Bar "$env:RUNNER_TEMP/pc-bar-positivecontrol-orange.png"
        Write-Host "bar metric orange positive control: golden=$($mCtrlO[0]) seekRun=$($mCtrlO[1])%"
        if (-not ($mCtrlO[0] -ge 2 -and $mCtrlO[1] -ge 40)) { throw "bar metric ORANGE positive control FAILED: the default-style render scored golden=$($mCtrlO[0]) seekRun=$($mCtrlO[1])% - the metric is blind to the default style" }
    } else { Write-Host "::warning::orange control-bar render missing - orange positive control skipped" }
} else { Write-Host "::warning::control-bar render missing - positive control skipped" }
Remove-Item "$env:RUNNER_TEMP/pc-bar-phonestart.png" -ErrorAction SilentlyContinue
Remove-Item "$env:RUNNER_TEMP/pc-bar-poll.png" -ErrorAction SilentlyContinue
$vidStart = 0; $barStartGolden = 0; $barStartSeek = 0; $gotStart = $false
$swStart = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($i in 1..40) {
    $hwBar = Find-PlayerWindow $pBar.Id $hwHome
    if ($hwBar -ne [System.IntPtr]::Zero) {
        [ZWU32]::SetWindowPos($hwBar, [System.IntPtr]::new(-1), 100, 100, 800, 500, 0) | Out-Null
        $sz = CaptureWindow $hwBar "$env:RUNNER_TEMP/pc-bar-poll.png"
        if ($sz -ne "0 x 0") {
            $vidStart = Measure-Video "$env:RUNNER_TEMP/pc-bar-poll.png"
            $m = Measure-Bar "$env:RUNNER_TEMP/pc-bar-poll.png"
            $barStartGolden = $m[0]; $barStartSeek = $m[1]
            Write-Host "phone-start poll ${i}: video=$vidStart golden=$barStartGolden seekRun=$barStartSeek%"
            if ($vidStart -ge 3000 -and $barStartGolden -ge 2 -and $barStartSeek -ge 40) {
                # The FIRST frame that has real video AND the bar is the proof; later
                # polls must not overwrite it with a post-autohide frame (round-7 C).
                Copy-Item "$env:RUNNER_TEMP/pc-bar-poll.png" "$env:RUNNER_TEMP/pc-bar-phonestart.png" -Force
                $gotStart = $true; break
            }
        }
    }
    Start-Sleep -Milliseconds 500
}
if (-not $gotStart) {
    # Keep the last frame for inspection - honestly the LAST poll, not a mislabeled proof.
    if (Test-Path "$env:RUNNER_TEMP/pc-bar-poll.png") { Copy-Item "$env:RUNNER_TEMP/pc-bar-poll.png" "$env:RUNNER_TEMP/pc-bar-phonestart.png" -Force }
    throw "phone-start bar proof never captured video AND bar together (video=$vidStart golden=$barStartGolden seekRun=$barStartSeek% after $([int]$swStart.Elapsed.TotalSeconds)s of polling)"
}
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
# Close the real player to return Home, then start again through the phone route.
[ZWU32]::PostMessage($hwBar, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 800
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/play" -Headers $hdr -ContentType "application/json" -Body ('{"url":"http://127.0.0.1:8901/s/tok/1"}') | Out-Null
Start-Sleep -Seconds 2
$hwRestart = Find-PlayerWindow $pBar.Id $hwHome
if ($hwRestart -eq [System.IntPtr]::Zero) { throw "Home/replay did not restore player" }
[ZWU32]::SetWindowPos($hwRestart, [System.IntPtr]::new(-1), 0, 0, $vs.Width, $vs.Height, 0) | Out-Null
[ZWU32]::SetCursorPos(2, 2) | Out-Null
Start-Sleep -Seconds 4
$null = CaptureWindow $hwRestart "$env:RUNNER_TEMP/pc-replay-hidden.png"
$restartHidden = Measure-Bar "$env:RUNNER_TEMP/pc-replay-hidden.png"
WakeControls
$null = CaptureWindow $hwRestart "$env:RUNNER_TEMP/pc-replay-shown.png"
$restartShown = Measure-Bar "$env:RUNNER_TEMP/pc-replay-shown.png"
if ($restartHidden[1] -ge 40 -or $restartShown[0] -lt 2 -or $restartShown[1] -lt 40) { throw "Mouse recovery failed after Home/replay" }
"Home/replay mouse-only recovery passed" | Add-Content "$env:RUNNER_TEMP/pc-mouse-recovery.txt"
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
$barOnStart  = ($mStart[0]  -ge 2 -and $mStart[1]  -ge 40)
$barOnAuto   = ($mAuto[0]   -ge 2 -and $mAuto[1]   -ge 40)
$barOnPaused = ($mPaused[0] -ge 2 -and $mPaused[1] -ge 40)
$barOnParked = ($mParked[0] -ge 2 -and $mParked[1] -ge 40)
$vidOkAuto   = ($vidAutoPx   -ge 3000)
$vidOkParked = ($vidParkedPx -ge 3000)

# 3) Real video, real mouse event, controls shown - WINDOWED.
$p3 = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/s/tok/1" -PassThru
$hw3 = Wait-MainWindow $p3 15
Close-PairingWindow $p3
FitWindow $hw3
WakeControls
$sz5 = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-playing-controls-window.png"
# Mouse-only recovery, twenty independent hides/reveals; no phone commands to rescue it.
function Prove-MouseRecovery($handle, $mode) {
    $failures = 0
    for ($cycle = 1; $cycle -le 20; $cycle++) {
        [ZWU32]::SetCursorPos(2, 2) | Out-Null
        Start-Sleep -Milliseconds 3500
        $null = CaptureWindow $handle "$env:RUNNER_TEMP/pc-cycle-hidden.png"
        $hidden = Measure-Bar "$env:RUNNER_TEMP/pc-cycle-hidden.png"
        WakeControls
        $null = CaptureWindow $handle "$env:RUNNER_TEMP/pc-cycle-shown.png"
        $shown = Measure-Bar "$env:RUNNER_TEMP/pc-cycle-shown.png"
        $ok = ($hidden[1] -lt 40 -and $shown[0] -ge 2 -and $shown[1] -ge 40)
        "$mode cycle=$cycle hidden=$($hidden[1]) shown=$($shown[0])/$($shown[1]) ok=$ok" | Add-Content "$env:RUNNER_TEMP/pc-mouse-recovery.txt"
        if (-not $ok) { $failures++ }
    }
    if ($failures -gt 0) { throw "$mode mouse recovery failed $failures of20cycles" }
}
Prove-MouseRecovery $hw3 'windowed'


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
# CP2: the bar's layout changed (render 02), so the click points and the diff boxes
# come from UI Automation (the element's real screen rect, converted to the
# capture's fractions), not from fractions measured on the old bar. A missed
# click still shows up as a NOT PROVEN verdict, never a silent pass.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function SmokeRect($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    foreach ($rh in @($hw3) + @([ZCP1]::VisibleHandles($p3.Id) | Where-Object { $_ -ne $hw3 })) {
        try { $el = [System.Windows.Automation.AutomationElement]::FromHandle($rh).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) } catch { $el = $null }
        if ($el -ne $null) { $b = $el.Current.BoundingRectangle; if ($b.Width -gt 0) { return $b } }
    }
    return $null
}
function SmokeFrac($b) {
    # Screen rect -> fractions of the CaptureWindow crop (DWM frame bounds inset by 2).
    $r = [ZRECT]::new(); [void][ZWU32]::DwmGetWindowAttribute($hw3, 9, [ref]$r, 16)
    $L = $r.Left + 2; $T = $r.Top + 2; $W = ($r.Right - 2) - $L; $H = ($r.Bottom - 2) - $T
    return @((($b.X - $L) / $W), (($b.X + $b.Width - $L) / $W), (($b.Y - $T) / $H), (($b.Y + $b.Height - $T) / $H))
}
function SmokeClick($b) { if ($b -ne $null) { ClickPx ($b.X + $b.Width / 2) ($b.Y + $b.Height / 2) } }
function ClickPx($x, $y) {
    [ZWU32]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 250
    [ZWU32]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [ZWU32]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}
$fnPauseGlyph = -1; $fnPauseClock = -1; $fnSeek = -1; $fnFs = 0
WakeControls
$playR = SmokeRect 'PlayPauseButton'; $elapsedR = SmokeRect 'ElapsedText'
Write-Host "smoke: PlayPauseButton=$playR ElapsedText=$elapsedR"
$pf = if ($playR) { SmokeFrac $playR } else { @(0.47, 0.53, 0.84, 0.88) }
$ef = if ($elapsedR) { SmokeFrac $elapsedR } else { @(0.02, 0.10, 0.78, 0.82) }
$null = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-before.png"
SmokeClick $playR
$szPause = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-paused.png"
# Keep the bar from auto-hiding between frames (mouse nudge, not a click).
Start-Sleep -Milliseconds 800
if ($playR) { [ZWU32]::SetCursorPos([int]($playR.X + $playR.Width / 2), [int]($playR.Y - 30)) | Out-Null }
Start-Sleep -Milliseconds 700
$null = CaptureWindow $hw3 "$env:RUNNER_TEMP/pc-smoke-paused2.png"
$fnPauseGlyph = CountDiff "$env:RUNNER_TEMP/pc-smoke-before.png" "$env:RUNNER_TEMP/pc-smoke-paused.png" $pf[0] $pf[1] $pf[2] $pf[3]
$fnPauseClock = CountDiff "$env:RUNNER_TEMP/pc-smoke-paused.png" "$env:RUNNER_TEMP/pc-smoke-paused2.png" $ef[0] $ef[1] $ef[2] $ef[3]
SmokeClick $playR   # resume
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
$fnSeek = CountDiff "$env:RUNNER_TEMP/pc-smoke-paused.png" "$env:RUNNER_TEMP/pc-smoke-seeked.png" $ef[0] $ef[1] $ef[2] $ef[3]
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
$p4 = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/s/tok/1","--fullscreen" -PassThru
$hw4 = Wait-MainWindow $p4 15
Close-PairingWindow $p4
# Fullscreen transition and floating VideoView overlay can lag HWND creation.
# Position the already-fullscreen window on the captured monitor and wait for
# both host and overlay layout to settle. Do not crop an offscreen rectangle.
[ZWU32]::SetWindowPos($hw4, [System.IntPtr]::new(-1), $vs.Left, $vs.Top, $vs.Width, $vs.Height, 0) | Out-Null
Start-Sleep -Seconds 2
WakeControls
$sz6 = CaptureWindow $hw4 "$env:RUNNER_TEMP/pc-playing-controls-fullscreen.png"
Prove-MouseRecovery $hw4 'fullscreen'
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

# C-1 legs are GATES, not notes: a failed autohide/paused/parked leg fails the step
# (round-7 review C: NOT-PROVEN silently passed before).
$barGateFailed = $false
if (-not ($vidOkAuto -and -not $barOnAuto)) { Write-Host "::error::autohide leg failed (video=$vidAutoPx golden=$($mAuto[0]) seekRun=$($mAuto[1])%) - the bar must hide on the idle timer while video keeps playing"; $barGateFailed = $true }
if (-not $barOnPaused) { Write-Host "::error::paused leg failed (golden=$($mPaused[0]) seekRun=$($mPaused[1])%) - a paused movie must keep the bar"; $barGateFailed = $true }
if (-not ($vidOkParked -and -not $barOnParked)) { Write-Host "::error::parked-cursor leg failed (video=$vidParkedPx golden=$($mParked[0]) seekRun=$($mParked[1])%) - a still cursor over the video must not pin the bar"; $barGateFailed = $true }
if ($cp1Fail.Count -gt 0) { Write-Host "::error::CP1 evidence gates failed: $($cp1Fail -join '; ')" }
if ($cp2Fail.Count -gt 0) { Write-Host "::error::CP2 evidence gates failed: $($cp2Fail -join '; ')" }
if ($incomplete -or $barGateFailed -or $cp1Fail.Count -gt 0 -or $cp2Fail.Count -gt 0) { exit 1 }
