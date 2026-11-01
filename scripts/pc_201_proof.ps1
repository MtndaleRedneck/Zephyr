# Zephyr PC 2.0.1 proof (Ezzy's Nov 1 list), run on a real Windows desktop by
# .github/workflows/pc-201-proof.yml. Hooked CI build only (windows/capture-out).
# Evidence lands in $env:RUNNER_TEMP/proof201 with NOTES.txt (PASS/FAIL lines).
#  1. Home on a REAL library of H.264 and HEVC MKVs: tiles show real frames
#     (no fixture PNGs), the thumbnail cache fills, the path bar shows the path,
#     one phone pill (no "Pair phones" duplicate), caption glyphs.
#  2. Settings: each fixed section fits without a scroll bar.
#  3. Full screen flash: the desktop is set to solid magenta; every screen frame
#     sampled while entering and leaving full screen from a maximized player
#     is checked for magenta (= desktop showing through = the flash).
param([string]$Label = 'candidate')
$ErrorActionPreference = 'Continue'
$out = Join-Path $env:RUNNER_TEMP 'proof201'
New-Item -ItemType Directory -Force $out | Out-Null
$notes = Join-Path $out 'NOTES.txt'
"Zephyr PC 2.0.1 proof - $Label - $(Get-Date -Format o)" | Set-Content $notes
$fails = New-Object System.Collections.Generic.List[string]
function Note($s) { Write-Host $s; Add-Content $notes $s }
function Gate($ok, $s) { if ($ok) { Note "PASS $s" } else { Note "FAIL $s"; $fails.Add($s) } }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# pwsh compiles against reference assemblies: name the real homes of the drawing types.
$p201Src = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
public static class P201 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int a, int b, string c, int d);
    [DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] e, int[] v);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);

    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256); GetWindowTextW(h, sb, 256);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void SolidDesktop() {
        SystemParametersInfo(20 /* SPI_SETDESKWALLPAPER */, 0, "", 3);
        SetSysColors(1, new[] { 1 /* COLOR_DESKTOP */ }, new[] { 0x00FF00FF });
    }
    public static int W { get { return GetSystemMetrics(0); } }
    public static int H { get { return GetSystemMetrics(1); } }
    public static void Shot(string path) {
        using (var b = new Bitmap(W, H)) { using (var g = Graphics.FromImage(b)) g.CopyFromScreen(0, 0, 0, 0, b.Size); b.Save(path, ImageFormat.Png); }
    }
    public static void Crop(string src, string dst, int x, int y, int w, int h, int zoom) {
        using (var b = new Bitmap(src)) using (var c = new Bitmap(w * zoom, h * zoom)) using (var g = Graphics.FromImage(c)) {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(b, new Rectangle(0, 0, w * zoom, h * zoom), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            c.Save(dst, ImageFormat.Png);
        }
    }
    static int Magenta(Bitmap b) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int n = 0; var row = new byte[d.Stride];
            for (int y = 0; y < b.Height; y += 3) {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                for (int x = 0; x < b.Width; x += 3) { int i = x * 4; if (row[i + 2] > 200 && row[i + 1] < 70 && row[i] > 200) n++; }
            }
            return n;
        } finally { b.UnlockBits(d); }
    }
    public static IntPtr[] Handles(uint pid) {
        var arr = new IntPtr[64]; int n = 0;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && n < arr.Length) arr[n++] = h; return true; }, IntPtr.Zero);
        var res = new IntPtr[n]; Array.Copy(arr, res, n); return res;
    }
    /// Sample the whole screen on a background thread for ms milliseconds; the caller
    /// triggers the transition on its own thread meanwhile, then calls Join().
    static Thread _t; static string _res;
    public static void Begin(int ms, string dir, string phase) {
        _res = null;
        _t = new Thread(() => {
            int frames = 0, flagged = 0, worst = 0; bool saved = false; long first = -1, last = -1;
            using (var b = new Bitmap(W, H)) using (var g = Graphics.FromImage(b)) {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms) {
                    g.CopyFromScreen(0, 0, 0, 0, b.Size);
                    frames++;
                    int m = Magenta(b);
                    if (m > 30) {
                        flagged++; if (m > worst) worst = m;
                        if (first < 0) first = sw.ElapsedMilliseconds; last = sw.ElapsedMilliseconds;
                        if (!saved) { b.Save(System.IO.Path.Combine(dir, "flash-" + phase + ".png"), ImageFormat.Png); saved = true; }
                    }
                }
            }
            _res = string.Format("frames={0} flagged={1} worst_magenta_samples={2} flagged_window_ms={3}..{4} (over {5} ms)", frames, flagged, worst, first, last, ms);
        });
        _t.IsBackground = true; _t.Start();
    }
    public static string Join() { _t.Join(); return _res; }
}
'@
$compiled = $false
# Only names that exist beside this pwsh; System.Drawing.Common's version decides which
# System.Private.Windows.* assemblies it needs, and the compiler names any missing one.
$named = @('System.Drawing.Common', 'System.Drawing.Primitives', 'System.Collections', 'System.Threading.Thread', 'System.Threading',
    'System.Runtime.InteropServices', 'System.Runtime', 'System.Console', 'System.Runtime.Extensions', 'System.ComponentModel.Primitives')
$named += @(Get-ChildItem $PSHOME -Filter 'System.Private.Windows*.dll' -ErrorAction SilentlyContinue | ForEach-Object { $_.BaseName })
$named = @($named | Select-Object -Unique | Where-Object { (Test-Path (Join-Path $PSHOME "$_.dll")) -or (Test-Path (Join-Path $PSHOME "ref/$_.dll")) })
$compiled = $false
for ($attempt = 1; $attempt -le 6 -and -not $compiled; $attempt++) {
    try { Add-Type -ReferencedAssemblies $named -ErrorAction Stop -TypeDefinition $p201Src; $compiled = $true; Note "helpers compiled against: $($named -join ', ')" }
    catch {
        $msg = "$_"
        Write-Host "helper compile attempt $attempt failed: $msg"
        $miss = [regex]::Matches($msg, "reference to assembly '([^,']+)") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        if (-not $miss) { break }
        $named = @($named + $miss | Select-Object -Unique)
    }
}
if (-not $compiled) { Note "FAIL helper compile (see log)"; exit 1 }

function StopZephyr { Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }
function WaitWin($proc, $title, $sec) {
    $t = [Environment]::TickCount64
    while ([Environment]::TickCount64 - $t -lt $sec * 1000) { $h = [P201]::Find([uint32]$proc.Id, $title); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 100 }
    return [IntPtr]::Zero
}
function UiaFind($proc, $id, $sec = 8) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $t = [Environment]::TickCount64
    do {
        foreach ($h in [P201]::Handles([uint32]$proc.Id)) {
            try { $w = [System.Windows.Automation.AutomationElement]::FromHandle($h); $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c); if ($el -ne $null) { return $el } } catch { }
        }
        Start-Sleep -Milliseconds 250
    } while ([Environment]::TickCount64 - $t -lt $sec * 1000)
    return $null
}
function UiaInvoke($proc, $id) { $el = UiaFind $proc $id; if ($el -eq $null) { return $false }; try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false } }
function Status { try { return Invoke-RestMethod -Uri "http://127.0.0.1:47823/control/status" -Headers @{ Authorization = "Bearer ci-capture-token" } -TimeoutSec 5 } catch { return $null } }

$exe = (Resolve-Path 'windows/capture-out/ZephyrPlayer.exe').Path
Note "screen $([P201]::W)x$([P201]::H); exe $exe"

# ---------- a real library (H.264 + HEVC MKVs, nested, a poster) ----------
$lib = 'C:\ZLib\Users\Ezzy\Videos\Movies'
Remove-Item -Recurse -Force 'C:\ZLib' -ErrorAction SilentlyContinue
foreach ($d in @('', 'Dunkirk 2017', 'Avatar', 'MCU\Iron Man 2008', 'Lord of the Rings')) { New-Item -ItemType Directory -Force (Join-Path $lib $d) | Out-Null }
function MakeMkv($src, $codec, $path, $extra) {
    $args = @('-v', 'error', '-y', '-f', 'lavfi', '-i', "$src=size=1280x720:rate=24:duration=12")
    if ($extra) { $args += @('-vf', $extra) }
    if ($codec -eq 'hevc') { $args += @('-c:v', 'libx265', '-x265-params', 'log-level=none', '-preset', 'ultrafast', '-crf', '32') } else { $args += @('-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '30') }
    $args += @('-pix_fmt', 'yuv420p', $path)
    & ffmpeg @args
}
MakeMkv 'testsrc2' 'h264' (Join-Path $lib 'Coyote vs Acme.mkv') $null
MakeMkv 'smptehdbars' 'hevc' (Join-Path $lib 'The Sheep Detectives 2026.mkv') 'fade=in:0:72'
MakeMkv 'testsrc' 'hevc' (Join-Path $lib 'Terminator 2 Judgment Day 1991.mkv') $null
MakeMkv 'rgbtestsrc' 'hevc' (Join-Path $lib 'Dunkirk 2017\Dunkirk.mkv') $null
MakeMkv 'testsrc2' 'hevc' (Join-Path $lib 'Avatar\Avatar.mkv') 'hue=h=120'
MakeMkv 'smptebars' 'h264' (Join-Path $lib 'MCU\Iron Man 2008\Iron Man.mkv') $null
& ffmpeg -v error -y -f lavfi -i 'color=c=0x1E6FD9:size=600x900,drawbox=x=60:y=60:w=480:h=780:color=0xFFB000:t=24' -frames:v 1 (Join-Path $lib 'Avatar\poster.jpg')
$mkvs = @(Get-ChildItem $lib -Recurse -Filter *.mkv)
Gate ($mkvs.Count -eq 6) "library built: $($mkvs.Count) MKVs (3 HEVC at top level and in folders, H.264 beside them)"

$cfgDir = Join-Path $env:APPDATA 'ZephyrPlayer'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$libJson = $lib -replace '\\', '\\'
[IO.File]::WriteAllText((Join-Path $cfgDir 'settings.json'), "{`"LibraryFolder`":`"$libJson`"}")
'[{"token":"proof201-phone","name":"Ezzy"}]' | Set-Content (Join-Path $cfgDir 'paired.json')
$thumbs = Join-Path $env:LOCALAPPDATA 'Zephyr\thumbs'
Remove-Item -Recurse -Force $thumbs -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable('ZEPHYR_CI_CAPTURE', '1', 'Process')
[P201]::SolidDesktop()
Start-Sleep -Seconds 1

# ---------- 1. Home ----------
StopZephyr
$t0 = [Environment]::TickCount64
$p = Start-Process -FilePath $exe -PassThru
$hw = WaitWin $p 'Zephyr Player' 30
$startMs = [Environment]::TickCount64 - $t0
Gate ($hw -ne [IntPtr]::Zero) "main window up in $startMs ms (cold start, real library)"
[P201]::ShowWindow($hw, 3) | Out-Null   # maximized, as Ezzy runs it
[P201]::SetForegroundWindow($hw) | Out-Null
$deadline = [Environment]::TickCount64 + 60000
$n = 0
while ([Environment]::TickCount64 -lt $deadline) { $n = @(Get-ChildItem $thumbs -Filter *.jpg -ErrorAction SilentlyContinue).Count; if ($n -ge 4) { break }; Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
Note "thumbnail cache after $([math]::Round(([Environment]::TickCount64 - $t0) / 1000.0, 1)) s: $n frames in $thumbs"
Gate ($n -ge 4) "libVLC frames cached for the MKVs Windows has no thumbnail for (want >= 4: 3 top-level files + Dunkirk/MCU folders; got $n)"
[P201]::Shot("$out/home-maximized.png")
[P201]::Crop("$out/home-maximized.png", "$out/caption-buttons-maximized-x4.png", [P201]::W - 150, 0, 150, 34, 4)
[P201]::Crop("$out/home-maximized.png", "$out/top-bar-x2.png", 0, 30, [math]::Min(900, [P201]::W), 120, 2)
# Colour in the grid = real frames (placeholders are grey/orange outlines on black).
$bmp = New-Object System.Drawing.Bitmap "$out/home-maximized.png"
$colour = 0; $total = 0
for ($y = [int]($bmp.Height * 0.2); $y -lt [int]($bmp.Height * 0.95); $y += 4) { for ($x = 0; $x -lt $bmp.Width; $x += 4) {
    $c = $bmp.GetPixel($x, $y); $total++
    $mx = [math]::Max($c.R, [math]::Max($c.G, $c.B)); $mn = [math]::Min($c.R, [math]::Min($c.G, $c.B))
    $orange = ($c.R -gt 200 -and $c.G -gt 120 -and $c.G -lt 170 -and $c.B -lt 40)
    if ($mx - $mn -gt 80 -and -not $orange) { $colour++ } } }
$bmp.Dispose()
$pct = [math]::Round(100.0 * $colour / [math]::Max(1, $total), 1)
Gate ($pct -ge 4) "Home grid shows real movie frames: $pct % of the grid is picture colour (blank 2.0.0 tiles are ~0 %) (home-maximized.png)"
$pairDup = UiaFind $p 'PairButton' 2
Gate ($pairDup -eq $null) "no separate 'Pair phones' button (PairButton absent); the phone pill is the one way in"
$phone = UiaFind $p 'PhoneCard'
Gate ($phone -ne $null) "phone pill present (PhoneCard) reading '$($phone.Current.Name)'"
$crumb = UiaFind $p 'Breadcrumb'
$crumbText = ''
if ($crumb -ne $null) {
    $tw = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $names = @(); $e = $tw.GetFirstChild($crumb)
    while ($e -ne $null) { $nm = $e.Current.Name; if ($nm) { $names += $nm }; $e = $tw.GetNextSibling($e) }
    $crumbText = ($names -join ' > ')
}
if (-not $crumbText) {
    # Fallback when the ItemsControl exposes no children: look for the folder names themselves.
    $found = @(); foreach ($nm in @('C:', 'ZLib', 'Users', 'Ezzy', 'Videos', '...', 'Movies')) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $nm)
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hw)
        if ($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) -ne $null) { $found += $nm } }
    $crumbText = ($found -join ' > ')
}
Note "path bar reads: $crumbText"
Gate (($crumbText -match 'Videos' -and $crumbText -match 'Ezzy' -and $crumbText -match 'Movies') -or ($crumbText -match '\.\.\.' -and $crumbText -match 'Movies')) "path bar shows the real path (folds to ... only when too narrow): '$crumbText'"
$maxBtn = UiaFind $p 'CaptionMaximize'
Note "caption maximize/restore button found=$($maxBtn -ne $null) tooltip/name='$($maxBtn.Current.Name)'"
# Open a folder with a poster: the poster is the folder art.
StopZephyr

# ---------- 2. Settings sections ----------
$p = Start-Process -FilePath $exe -ArgumentList '--open-settings' -PassThru
$hs = WaitWin $p 'Zephyr Settings' 30
Start-Sleep -Seconds 2
foreach ($sec in @('NavVolume', 'NavSkip', 'NavBar', 'NavUpdates')) {
    $ok = UiaInvoke $p $sec
    Start-Sleep -Milliseconds 700
    [P201]::Shot("$out/settings-$sec.png")
    $r = New-Object P201+R; [P201]::GetWindowRect($hs, [ref]$r) | Out-Null
    [P201]::Crop("$out/settings-$sec.png", "$out/settings-$sec-window.png", [math]::Max(0, $r.L), [math]::Max(0, $r.T), [math]::Min([P201]::W - [math]::Max(0, $r.L), $r.Rt - $r.L), [math]::Min([P201]::H - [math]::Max(0, $r.T), $r.B - $r.T), 1)
    $sv = UiaFind $p 'SectionScroll'
    $scrollable = $null
    if ($sv -ne $null) { try { $scrollable = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } catch { $scrollable = $null } }
    Gate ($ok -and $scrollable -eq $false) "Settings $sec has no scroll bar (vertically scrollable=$scrollable) (settings-$sec-window.png)"
}
$rem = UiaFind $p 'RememberToggle'
Gate ($rem -ne $null) "Volume: 'Remember last volume' is a real switch (RememberToggle present)"
StopZephyr

# ---------- 3. Full screen flash ----------
$movie = (Join-Path $lib 'Coyote vs Acme.mkv')
$url = ([Uri]$movie).AbsoluteUri
$p = Start-Process -FilePath $exe -ArgumentList "--play=$url" -PassThru
$hw = WaitWin $p 'Zephyr Player' 30
[P201]::ShowWindow($hw, 3) | Out-Null
[P201]::SetForegroundWindow($hw) | Out-Null
Start-Sleep -Seconds 6
$st = Status
Note "player state before the flash test: state=$($st.state) fullscreen=$($st.ui.fullscreen)"
[P201]::Shot("$out/player-maximized.png")
$bad = 0; $runs = 0
for ($i = 1; $i -le 3; $i++) {
    $fsEl = UiaFind $p 'FullscreenButton' 15
    if ($fsEl -eq $null) { Note "cycle ${i}: FullscreenButton not found; windows: $(([P201]::Handles([uint32]$p.Id) | ForEach-Object { $sb = New-Object System.Text.StringBuilder 128; [void][P201]::GetWindowTextW($_, $sb, 128); '[' + $sb + ']' }) -join ' ')"; continue }
    $inv = $fsEl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    [P201]::Begin(1600, $out, "enter$i"); Start-Sleep -Milliseconds 150; $inv.Invoke(); $res = [P201]::Join()
    $st = Status; $runs++
    Note "ENTER full screen #${i}: $res; fullscreen now=$($st.ui.fullscreen)"
    if ($res -notmatch 'flagged=0 ') { $bad++ }
    if ($i -eq 1) { [P201]::Shot("$out/player-fullscreen.png") }
    Start-Sleep -Milliseconds 800
    $fsEl = UiaFind $p 'FullscreenButton'
    if ($fsEl -eq $null) { Note "cycle ${i}: FullscreenButton not found in full screen"; continue }
    $inv = $fsEl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    [P201]::Begin(1600, $out, "exit$i"); Start-Sleep -Milliseconds 150; $inv.Invoke(); $res = [P201]::Join()
    $st = Status; $runs++
    Note "EXIT full screen #${i}: $res; fullscreen now=$($st.ui.fullscreen)"
    if ($res -notmatch 'flagged=0 ') { $bad++ }
    Start-Sleep -Milliseconds 800
}
Gate ($runs -ge 6 -and $bad -eq 0) "no desktop flash entering/leaving full screen from a maximized player: $bad of $runs transitions showed the desktop"
[P201]::Crop("$out/player-fullscreen.png", "$out/player-fullscreen-bar-x2.png", [int]([P201]::W / 2 - 400), [P201]::H - 140, 800, 140, 2)
[P201]::Crop("$out/player-maximized.png", "$out/player-maximized-bar-x2.png", 0, [P201]::H - 160, [math]::Min(1024, [P201]::W), 160, 2)
StopZephyr

Note ""
if ($fails.Count -gt 0) { Note "RESULT: $($fails.Count) FAIL"; $fails | ForEach-Object { Note " - $_" }; exit 1 } else { Note "RESULT: all PASS" }
