# Real-desktop capture of the actual ZephyrPlayer.exe on the Windows runner
# (interactive session): proves the full windows + chrome composite, which
# offscreen RenderTargetBitmap cannot (VideoView airspace blanks it - observed).
# Captures: home (fresh, pair banner), home with control-relevant state,
# settings modal, and the player with media loaded. A note file reports the
# verdict honestly: if the runner desktop is not interactive, captures come back
# blank and the note says so.
$ErrorActionPreference = 'Continue'
$note = "$env:RUNNER_TEMP/pc-desktop-note.txt"

# Mirror the installer's firewall rules so no prompt photobombs the capture.
netsh advfirewall firewall add rule name="Zephyr Player" dir=in action=allow protocol=TCP localport=47823 profile=private | Out-Null
netsh advfirewall firewall add rule name="Zephyr Player Discovery" dir=in action=allow protocol=UDP localport=8489 profile=private | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);' -Name U32 -Namespace W
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
function Capture($path) {
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $nonblack = 0
    for ($x = 0; $x -lt $vs.Width; $x += 7) {
        for ($y = 0; $y -lt $vs.Height; $y += 7) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -gt 24 -or $c.G -gt 24 -or $c.B -gt 24) { $nonblack++ }
        }
    }
    $g.Dispose(); $bmp.Dispose()
    return $nonblack
}
function StopZephyr { Get-Process ZephyrPlayer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }

# Serve a tone so --play has real media (same generator + server as the VLC proof).
$wav = "$env:RUNNER_TEMP/tone.wav"
python -c "import wave,struct,math; w=wave.open(r'$wav','wb'); w.setnchannels(1); w.setsampwidth(2); w.setframerate(44100); w.writeframes(b''.join(struct.pack('<h',int(12000*math.sin(2*math.pi*440*i/44100))) for i in range(441000))); w.close()"
$srv = Start-Process python -ArgumentList "scripts/range_server.py","$env:RUNNER_TEMP","8901" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

$p = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -PassThru
Start-Sleep -Seconds 10
$alive = -not $p.HasExited
$nb1 = Capture "$env:RUNNER_TEMP/pc-desktop.png"
[W.U32]::SetCursorPos([int]($vs.Width / 2), [int]($vs.Height - 60)) | Out-Null
Start-Sleep -Seconds 2
$nb2 = Capture "$env:RUNNER_TEMP/pc-desktop-controls.png"
StopZephyr

$p = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -ArgumentList "--open-settings" -PassThru
Start-Sleep -Seconds 8
$nb3 = Capture "$env:RUNNER_TEMP/pc-desktop-settings.png"
StopZephyr

$p = Start-Process -FilePath "windows/out/ZephyrPlayer.exe" -ArgumentList "--play=http://127.0.0.1:8901/tone.wav" -PassThru
Start-Sleep -Seconds 10
[W.U32]::SetCursorPos([int]($vs.Width / 2), [int]($vs.Height - 60)) | Out-Null
Start-Sleep -Seconds 2
$nb4 = Capture "$env:RUNNER_TEMP/pc-desktop-playing.png"
StopZephyr
Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue

netsh advfirewall firewall delete rule name="Zephyr Player" | Out-Null
netsh advfirewall firewall delete rule name="Zephyr Player Discovery" | Out-Null

@"
exe_started_alive_after_10s: $alive
virtual_screen: $($vs.Width)x$($vs.Height)
nonblack_samples_home_fresh: $nb1
nonblack_samples_home_after_mouse: $nb2
nonblack_samples_settings: $nb3
nonblack_samples_playing: $nb4
verdict: $(if ($nb1 -gt 1000) { 'real desktop captured' } else { 'BLANK - runner desktop not interactive, full-window capture unavailable in CI' })
"@ | Set-Content $note
Get-Content $note
