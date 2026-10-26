# Real-VLC proof for phone->VLC routing: a REAL VLC must accept the phone-style
# http MRL through in_play, play it, and attach the :sub-file option (VLC 3.0.x
# httprequests.lua). The Android-server half of the route is proven separately
# by ZephyrWireTest's fetch-back through the emulator's real StreamServer.
$ErrorActionPreference = 'Stop'
choco install vlc --no-progress -y | Out-Null

$tmp = "$env:RUNNER_TEMP\vlc-proof"; New-Item -ItemType Directory -Force $tmp | Out-Null
# 10s 440Hz WAV + a sidecar SRT, served with Range support.
python -c "import wave,struct,math; w=wave.open(r'$tmp\clip.wav','wb'); w.setnchannels(1); w.setsampwidth(2); w.setframerate(44100); w.writeframes(b''.join(struct.pack('<h',int(12000*math.sin(2*math.pi*440*i/44100))) for i in range(441000))); w.close()"
Set-Content "$tmp\clip.srt" "1`r`n00:00:00,000 --> 00:00:04,000`r`nZephyr subtitle proof`r`n"

$server = Start-Process -PassThru python -ArgumentList "scripts/range_server.py","$tmp","8123" -WindowStyle Hidden
Start-Sleep 2
$vlc = Start-Process -PassThru "C:\Program Files\VideoLAN\VLC\vlc.exe" -ArgumentList "-I","http","--http-password","zephyr","--no-video-title-show" -WindowStyle Hidden
try {
  $ok = $false
  for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    Start-Sleep 1
    try {
      $r = Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) } -UseBasicParsing -TimeoutSec 3
      $ok = $true
    } catch { }
  }
  if (-not $ok) { throw "VLC http interface never came up" }

  # Same URL shape StreamCoordinator hands over: percent-encoded path on the phone server.
  Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json?command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.wav&option=%3Asub-file%3Dhttp%3A%2F%2F127.0.0.1%3A8123%2Fclip.srt" -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) } -UseBasicParsing -TimeoutSec 5 | Out-Null

  $played = $false
  for ($i = 0; $i -lt 20 -and -not $played; $i++) {
    Start-Sleep -Milliseconds 500
    $st = (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) } -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    if ($st.state -eq "playing") { $played = $true }
  }
  if (-not $played) { throw "VLC never entered playing state on the phone-style URL" }
  Write-Output "VLC PLAYED the phone-style http URL (state=$($st.state), position=$($st.position))"

  # Subtitle attachment via :sub-file option, visible as a Subtitle stream.
  $streams = $st.information.category.PSObject.Properties.Value | ForEach-Object { $_ }
  $hasSub = $false
  foreach ($cat in $st.information.category.PSObject.Properties) {
    foreach ($stream in $cat.Value.PSObject.Properties) {
      if ($stream.Value.Type -eq "Subtitle") { $hasSub = $true }
    }
  }
  if (-not $hasSub) { throw "no Subtitle stream attached - :sub-file option did not take" }
  Write-Output "VLC attached the :sub-file subtitle (Subtitle stream present)"
} finally {
  if ($vlc -and -not $vlc.HasExited) { Stop-Process -Id $vlc.Id -Force }
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
