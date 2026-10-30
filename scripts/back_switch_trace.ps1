# Back-to-library ordering trace (Zephyr Player 2.0 CP2 round 5). Instinct's review of
# run 41095411723: one all-black client frame at 438 ms of Back. This leg repeats the
# real Back path (a real double-click on a library tile, the movie plays, a real click
# on the bar's Back/home button) $Reps times with two clocks on the same timeline
# (QueryPerformanceCounter):
#   - the app's own ordering trace (ZEPHYR_SWITCH_TRACE, capture build only): every
#     step the shell takes on Back, and every change of the native windows involved
#     (video host visible/hidden, libVLC's video window, the overlay window);
#   - an outside screen sampler: the shell's content area as composed on screen,
#     read back about every 3-6 ms, each sample classed (blank = all one colour by the
#     same 24-level tolerance as the CP1 flash sampler, on a 128x86 point grid).
# Nothing here gates the product; it measures. Output: $env:RUNNER_TEMP/backtrace.
param([int]$Reps = 60)
$ErrorActionPreference = 'Continue'
$out = Join-Path $env:RUNNER_TEMP 'backtrace'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'backtrace-log.txt'
function Log($m) { $l = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; Write-Output $l; Add-Content $log $l }

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class BT {
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
    [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct BIH { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biX, biY; public uint biClrUsed, biClrImportant; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out R r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref P p);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
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
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int GW = 128, GH = 86, Tol = 24;

    public static IntPtr FindByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { uint wp; GetWindowThreadProcessId(h, out wp); if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); if (sb.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    public static IntPtr FindByClass(uint pid, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { uint wp; GetWindowThreadProcessId(h, out wp); if (wp != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); if (sb.ToString() == cls) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static List<IntPtr> Visible(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint wp; GetWindowThreadProcessId(h, out wp); if (wp == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    static byte[] Grab(int x, int y, int sw, int sh, int w, int h) {
        IntPtr scr = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(scr), bmp = CreateCompatibleBitmap(scr, w, h), old = SelectObject(mem, bmp);
        try {
            SetStretchBltMode(mem, 3);
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
            f.Write((ushort)0x4D42); f.Write(54 + bits.Length); f.Write(0); f.Write(54);
            f.Write(40); f.Write(w); f.Write(-h); f.Write((ushort)1); f.Write((ushort)32); f.Write(0); f.Write(bits.Length); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
            f.Write(bits);
        }
    }
    static volatile bool _load; static List<Thread> _loaders = new List<Thread>();
    public static void Load(int n) {
        _load = n > 0;
        if (n == 0) { foreach (var t in _loaders) t.Join(1000); _loaders.Clear(); return; }
        for (int i = 0; i < n; i++) { var t = new Thread(() => { double x = 0; while (_load) { x += Math.Sqrt(x + 1); } }) { IsBackground = true }; t.Start(); _loaders.Add(t); }
    }
    static Thread _t; static volatile bool _run; static IntPtr _shell; static string _dir, _tag; static StringBuilder _sb; static int _saves, _blanks, _n;
    public static int Blanks { get { return _blanks; } }
    public static int Samples { get { return _n; } }
    public static void Start(IntPtr shell, string dir, string tag) {
        _shell = shell; _dir = dir; _tag = tag; _sb = new StringBuilder(); _blanks = 0; _n = 0; _saves = 0; _run = true;
        _t = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }; _t.Start();
    }
    public static string Stop() { _run = false; if (_t != null) _t.Join(3000); return _sb.ToString(); }
    static void Loop() {
        timeBeginPeriod(1);
        byte[] prevFull = null; int pw = 0, ph = 0; long prevQ = 0; bool saveNextFull = false;
        while (_run) {
            long q0 = Stopwatch.GetTimestamp();
            R cr; GetClientRect(_shell, out cr); var o = new P(); ClientToScreen(_shell, ref o);
            int cap = (int)Math.Round(32 * Math.Max(1, GetDpiForWindow(_shell)) / 96.0);
            int cx = o.X, cy = o.Y + cap, cw = cr.Rt - cr.L, ch = cr.B - cr.T - cap;
            if (cw < 16 || ch < 16) { Thread.Sleep(2); continue; }
            var px = Grab(cx, cy, cw, ch, GW, GH);
            long q1 = Stopwatch.GetTimestamp();
            var counts = new Dictionary<int, int>(); int best = 0, bestKey = 0;
            long sr = 0, sg = 0, sbl = 0;
            for (int i = 0; i < px.Length; i += 4) {
                int k = ((px[i+2] >> 3) << 10) | ((px[i+1] >> 3) << 5) | (px[i] >> 3);
                int c; counts.TryGetValue(k, out c); counts[k] = ++c; if (c > best) { best = c; bestKey = k; }
                sr += px[i+2]; sg += px[i+1]; sbl += px[i];
            }
            int mr = -1, mg = 0, mb = 0;
            for (int i = 0; i < px.Length && mr < 0; i += 4) {
                int k = ((px[i+2] >> 3) << 10) | ((px[i+1] >> 3) << 5) | (px[i] >> 3);
                if (k == bestKey) { mr = px[i+2]; mg = px[i+1]; mb = px[i]; }
            }
            int differ = 0;
            for (int i = 0; i < px.Length; i += 4)
                if (Math.Abs(px[i+2] - mr) > Tol || Math.Abs(px[i+1] - mg) > Tol || Math.Abs(px[i] - mb) > Tol) differ++;
            int npx = GW * GH;
            bool blank = differ == 0;
            _n++; if (blank) _blanks++;
            _sb.AppendFormat("{0} {1} {2} mode=#{3:X2}{4:X2}{5:X2} differing={6} mean={7},{8},{9}\n", q0, q1, blank ? "BLANK" : "content", mr, mg, mb, differ, sr / npx, sg / npx, sbl / npx);
            if (blank || saveNextFull) {
                // the frame as it is now, at full size, for a person to look at
                var full = Grab(cx, cy, cw, ch, cw, ch);
                long q2 = Stopwatch.GetTimestamp();
                if (_saves < 24) {
                    if (blank && prevFull != null) { SaveBmp(Path.Combine(_dir, _tag + "-" + prevQ + "-before.bmp"), prevFull, pw, ph); _saves++; }
                    SaveBmp(Path.Combine(_dir, _tag + "-" + q2 + (blank ? "-blank-fullgrab.bmp" : "-after.bmp")), full, cw, ch); _saves++;
                }
                _sb.AppendFormat("{0} {1} fullgrab-saved\n", q2, q2);
                saveNextFull = blank;
                prevFull = full; pw = cw; ph = ch; prevQ = q2;
            } else {
                // the last sample (point grid) is kept as the "before" of a blank
                prevFull = px; pw = GW; ph = GH; prevQ = q0;
            }
            Thread.Sleep(1);
        }
    }
}
'@

function ZStatus() { try { return Invoke-RestMethod -Uri "http://127.0.0.1:47823/control/status" -Headers @{ Authorization = "Bearer ci-capture-token" } -TimeoutSec 5 } catch { return $null } }
function Find-ByAutoId($rootEl, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function ClickPoint($x, $y, [switch]$Double) {
    [BT]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 250
    $n = if ($Double) { 2 } else { 1 }
    for ($k = 0; $k -lt $n; $k++) {
        [BT]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50
        [BT]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 90
    }
}
function ElementCenter($procId, $shellH, $id) {
    $roots = @($shellH) + @([BT]::Visible($procId) | Where-Object { $_ -ne $shellH })
    foreach ($rh in $roots) {
        try { $el = Find-ByAutoId ([System.Windows.Automation.AutomationElement]::FromHandle($rh)) $id } catch { $el = $null }
        if ($el -ne $null) { $b = $el.Current.BoundingRectangle; if ($b.Width -gt 0) { return @(($b.X + $b.Width / 2), ($b.Y + $b.Height / 2)) } }
    }
    return $null
}
function TileCenter($shellH, $name) {
    $rootE = [System.Windows.Automation.AutomationElement]::FromHandle($shellH)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $rootE.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el -eq $null) { return $null }
    $b = $el.Current.BoundingRectangle
    return @(($b.X + 20), ($b.Y - 40))
}

# ---------- fixtures: the CP1 library (testsrc clip copies), as in pc_desktop_capture.ps1 ----------
choco install ffmpeg --no-progress -y | Out-Null
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { $ffmpeg = 'C:\ProgramData\chocolatey\bin\ffmpeg.exe' }
$clip = Join-Path $env:RUNNER_TEMP 'bt-clip.mp4'
& $ffmpeg -y -f lavfi -i "testsrc=duration=600:size=320x240:rate=10" -pix_fmt yuv420p $clip 2>$null
$lib = Join-Path $env:RUNNER_TEMP 'bt-library'
New-Item -ItemType Directory -Force $lib | Out-Null
foreach ($v in 'Tears of Steel','His Girl Friday','Charade','Night of the Living Dead','Sintel','Big Buck Bunny','Detour','Metropolis') { Copy-Item $clip (Join-Path $lib "$v.mp4") }
# CP1's sidecar subtitle on the same tile, so a subtitle is drawn at Back as in run 41095411723.
[IO.File]::WriteAllText((Join-Path $lib 'Big Buck Bunny.srt'), "1`r`n00:00:00,000 --> 00:59:00,000`r`nZephyr subtitle proof`r`n")
$thumbs = Join-Path $env:RUNNER_TEMP 'bt-thumbs'
New-Item -ItemType Directory -Force $thumbs | Out-Null
$srcs = 'testsrc','smptebars','mandelbrot','rgbtestsrc','cellauto','testsrc2','yuvtestsrc','smptehdbars'
$i = 0
foreach ($v in 'Tears of Steel','His Girl Friday','Charade','Night of the Living Dead','Sintel','Big Buck Bunny','Detour','Metropolis') {
    & $ffmpeg -y -f lavfi -i "$($srcs[$i])=size=320x200:rate=1" -frames:v 1 (Join-Path $thumbs "$v.png") 2>$null; $i++
}
$cfgDir = Join-Path $env:APPDATA 'ZephyrPlayer'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
[IO.File]::WriteAllText((Join-Path $cfgDir 'settings.json'), "{`"LibraryFolder`":`"$($lib -replace '\\','\\')`"}")
'[{"token":"bt-fixture","name":"Pixel 8"}]' | Set-Content (Join-Path $cfgDir 'paired.json')

$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
[Environment]::SetEnvironmentVariable('ZEPHYR_CI_CAPTURE', '1', 'Process')
[Environment]::SetEnvironmentVariable('ZEPHYR_THUMB_FIXTURE_DIR', $thumbs, 'Process')
$fw = Start-Process "windows/capture-out/ZephyrPlayer.exe" -ArgumentList '--install-firewall', $sid -PassThru -Wait -WindowStyle Hidden
Log "firewall/listen setup exit $($fw.ExitCode)"
$appTrace = Join-Path $out 'app-trace.txt'
[Environment]::SetEnvironmentVariable('ZEPHYR_SWITCH_TRACE', $appTrace, 'Process')
Log "QPC frequency $([Diagnostics.Stopwatch]::Frequency)"

[BT]::SetCursorPos(5, 5) | Out-Null
$p = Start-Process -FilePath "windows/capture-out/ZephyrPlayer.exe" -PassThru
$shellH = [IntPtr]::Zero
for ($k = 0; $k -lt 40 -and $shellH -eq [IntPtr]::Zero; $k++) { Start-Sleep -Milliseconds 500; $shellH = [BT]::FindByTitle($p.Id, 'Zephyr Player') }
if ($shellH -eq [IntPtr]::Zero) { Log "no shell window"; exit 1 }
for ($k = 0; $k -lt 10; $k++) {
    foreach ($t in 'Zephyr Settings', 'Zephyr Player - Pair your phone') { $w = [BT]::FindByTitle($p.Id, $t); if ($w -ne [IntPtr]::Zero) { [BT]::PostMessage($w, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } }
    Start-Sleep -Milliseconds 500
}
# The window size of the flagged run (1024x720 at the top-left of a 1024x768 runner).
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Log "screen work area $($wa.Width)x$($wa.Height)"
[BT]::SetWindowPos($shellH, [IntPtr]::Zero, $wa.Left, $wa.Top, [math]::Min(1024, $wa.Width), [math]::Min(720, $wa.Height), 4) | Out-Null
[BT]::SetForegroundWindow($shellH) | Out-Null
Start-Sleep -Seconds 3

$summary = @()
for ($rep = 1; $rep -le $Reps; $rep++) {
    $tile = TileCenter $shellH 'Big Buck Bunny'
    if ($tile -eq $null) { Log "rep ${rep}: tile not found"; break }
    ClickPoint $tile[0] $tile[1] -Double
    $playing = $false
    foreach ($k in 1..40) { $st = ZStatus; if ($st -and $st.state -eq 'Playing' -and $st.time -ge 1.5) { $playing = $true; break }; Start-Sleep -Milliseconds 250 }
    if (-not $playing) { Log "rep ${rep}: never playing (state=$($st.state))"; [BT]::SetCursorPos(5, 5) | Out-Null; continue }
    # Wake the bar with a real mouse move over the picture, then find Back (home).
    [BT]::SetCursorPos(512, 360) | Out-Null; Start-Sleep -Milliseconds 300
    [BT]::SetCursorPos(524, 360) | Out-Null; Start-Sleep -Milliseconds 700
    $homeBtn = ElementCenter $p.Id $shellH 'HomeButton'
    if ($homeBtn -eq $null) { Log "rep ${rep}: HomeButton not found"; continue }
    # Variants (rep mod 4): 1 = playing; 2 = paused then resumed through the control
    # route (as CP1 paused/resumed before its Back); 3 = paused at Back, with 3 busy
    # threads loading the runner's CPUs during the Back; 0 = CP1's last step before its
    # Back: a subtitle file added through the real Windows file dialog (bar's subtitles
    # button, "Add subtitle file...", path typed, Enter), then Back while it plays.
    $variant = $rep % 4
    if ($variant -eq 0) {
        [BT]::SetCursorPos(512, 360) | Out-Null; Start-Sleep -Milliseconds 300
        [BT]::SetCursorPos(524, 360) | Out-Null; Start-Sleep -Milliseconds 700
        $cap = ElementCenter $p.Id $shellH 'CaptionsButton'
        $dlgNote = "captions=$($cap -ne $null)"
        if ($cap -ne $null) {
            ClickPoint $cap[0] $cap[1]
            Start-Sleep -Milliseconds 500
            $nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Add subtitle file...')
            $mi = $null
            foreach ($try in 1..10) {
                foreach ($rh in [BT]::Visible($p.Id)) { try { $mi = [System.Windows.Automation.AutomationElement]::FromHandle($rh).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond) } catch { $mi = $null }; if ($mi -ne $null) { break } }
                if ($mi -ne $null) { break }; Start-Sleep -Milliseconds 300
            }
            $dlgNote += "; menu=$($mi -ne $null)"
            if ($mi -ne $null) {
                $mb = $mi.Current.BoundingRectangle
                ClickPoint ($mb.X + $mb.Width / 2) ($mb.Y + $mb.Height / 2)
                $dlg = [IntPtr]::Zero
                foreach ($try in 1..60) { $dlg = [BT]::FindByClass($p.Id, '#32770'); if ($dlg -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 300 }
                $dlgNote += "; dialog=$($dlg -ne [IntPtr]::Zero)"
                if ($dlg -ne [IntPtr]::Zero) {
                    Start-Sleep -Milliseconds 1500
                    [BT]::SetForegroundWindow($dlg) | Out-Null; Start-Sleep -Milliseconds 300
                    $pick = Join-Path $env:RUNNER_TEMP ("bt-pick-{0:D2}.srt" -f $rep)
                    [IO.File]::WriteAllText($pick, "1`r`n00:00:00,000 --> 00:59:00,000`r`nPicked through the file dialog`r`n")
                    [System.Windows.Forms.SendKeys]::SendWait($pick); Start-Sleep -Milliseconds 300
                    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Milliseconds 1500
                    if ([BT]::FindByClass($p.Id, '#32770') -ne [IntPtr]::Zero) { $dlgNote += "; still open, Escape"; [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 800 }
                }
            }
        }
        $stD = ZStatus
        Log "rep ${rep}: file dialog step: $dlgNote; spu=$($stD.spu) tracks=$(@($stD.spuTracks).Count)"
        [BT]::SetCursorPos(5, 5) | Out-Null; Start-Sleep -Milliseconds 1200
    }
    $ctl = @{ Authorization = "Bearer ci-capture-token" }
    if ($variant -eq 2 -or $variant -eq 3) {
        try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/pause" -Headers $ctl -ContentType "application/json" -Body '{}' | Out-Null } catch { Log "rep ${rep}: pause failed $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 600
    }
    if ($variant -eq 2) {
        try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47823/control/resume" -Headers $ctl -ContentType "application/json" -Body '{}' | Out-Null } catch { }
        Start-Sleep -Milliseconds 800
    }
    $stPre = ZStatus
    $tag = "rep{0:D2}" -f $rep
    if ($variant -eq 3) { [BT]::Load(3) }
    [BT]::Start($shellH, $out, $tag)
    Start-Sleep -Milliseconds 300
    $qClick = [Diagnostics.Stopwatch]::GetTimestamp()
    ClickPoint $homeBtn[0] $homeBtn[1]
    $qUp = [Diagnostics.Stopwatch]::GetTimestamp()
    [BT]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Milliseconds 1500
    $samples = [BT]::Stop()
    [BT]::Load(0)
    Set-Content (Join-Path $out "$tag-screen.txt") ("click-down-window " + $qClick + " " + $qUp + "`n" + $samples)
    $st = ZStatus
    $line = "rep ${rep}: variant=$variant state-before=$($stPre.state) spu=$($stPre.spu) samples=$([BT]::Samples) blank=$([BT]::Blanks) state-after=$($st.state)"
    Log $line; $summary += $line
    Start-Sleep -Milliseconds 800
}
Start-Sleep -Milliseconds 600
Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force
# BMP -> PNG for the artifact
Get-ChildItem $out -Filter *.bmp | ForEach-Object {
    $b = [System.Drawing.Bitmap]::FromFile($_.FullName); $b.Save(($_.FullName -replace '\.bmp$', '.png'), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); Remove-Item $_.FullName
}
$tot = ($summary | ForEach-Object { if ($_ -match 'blank=(\d+)') { [int]$Matches[1] } else { 0 } } | Measure-Object -Sum).Sum
Log "TOTAL: $($summary.Count) Back clicks traced; blank screen samples: $tot"
