# Real-VLC proof for phone->VLC routing: a REAL VLC must accept the phone-style
# http MRL through in_play, play it, and attach the :sub-file option (VLC 3.0.x
# httprequests.lua). The Android-server half of the route is proven separately
# by ZephyrWireTest's fetch-back through the emulator's real StreamServer.
$ErrorActionPreference = 'Stop'
choco install vlc --no-progress -y | Out-Null

$tmp = "$env:RUNNER_TEMP\vlc-proof"; New-Item -ItemType Directory -Force $tmp | Out-Null
# Real video fixture (moving pattern) + a sidecar SRT, served with Range
# support. A WAV taught us: VLC does not register a Subtitle stream on
# audio-only input in status.json (CI-observed; addsubtitle probe also came
# back streamless), so the sub-file proof needs actual video.
choco install ffmpeg --no-progress -y | Out-Null
& "C:\ProgramData\chocolatey\bin\ffmpeg.exe" -y -f lavfi -i "testsrc=duration=30:size=320x240:rate=10" -pix_fmt yuv420p "$tmp\clip.mp4" 2>$null
Set-Content "$tmp\clip.srt" "1`r`n00:00:00,000 --> 00:00:04,000`r`nZephyr subtitle proof`r`n"

$server = Start-Process -PassThru python -ArgumentList "scripts/range_server.py","$tmp","8123" -WindowStyle Hidden
Start-Sleep 2
$vlcLog = "$tmp\vlc-verbose.log"
$vlc = Start-Process -PassThru "C:\Program Files\VideoLAN\VLC\vlc.exe" -ArgumentList "-I","http","--http-password","zephyr","--no-video-title-show","-vv","--file-logging","--logfile=$vlcLog" -WindowStyle Hidden
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
  # CI-observed (VLC 3.0.21, -vv log): httprequests.lua decodes the input
  # parameter but passes option values to the core VERBATIM - a percent-encoded
  # sub-file value becomes the literal relative path
  # "http%3A%2F%2F..." under the CWD (access=file, open fails). Send option
  # values raw; the values contain no & or # so query parsing is unaffected.
  Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json?command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4&option=:sub-file=http://127.0.0.1:8123/clip.srt" -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) } -UseBasicParsing -TimeoutSec 5 | Out-Null

  $played = $false
  for ($i = 0; $i -lt 20 -and -not $played; $i++) {
    Start-Sleep -Milliseconds 500
    $st = (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) } -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    if ($st.state -eq "playing") { $played = $true }
  }
  if (-not $played) { throw "VLC never entered playing state on the phone-style URL" }
  Write-Output "VLC PLAYED the phone-style http URL (state=$($st.state), position=$($st.position))"

  # Subtitle attachment via :sub-file option, visible as a Subtitle stream.
  # The stream can take a moment to register after play starts: poll, and on
  # failure dump the full status plus an addsubtitle comparison probe so the
  # log carries the cause, not just the symptom.
  $auth = @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) }
  function Test-SubStream($status) {
    foreach ($cat in $status.information.category.PSObject.Properties) {
      foreach ($stream in $cat.Value.PSObject.Properties) {
        if ($stream.Value.Type -eq "Subtitle") { return $true }
      }
    }
    return $false
  }
  $hasSub = $false
  for ($i = 0; $i -lt 10 -and -not $hasSub; $i++) {
    Start-Sleep -Milliseconds 500
    $st = (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers $auth -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    $hasSub = Test-SubStream $st
  }
  if (-not $hasSub) {
    Write-Output "STATUS DUMP (no Subtitle stream after :sub-file):"
    Write-Output ($st.information | ConvertTo-Json -Depth 8)
    Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json?command=addsubtitle&val=http://127.0.0.1:8123/clip.srt" -Headers $auth -UseBasicParsing -TimeoutSec 5 | Out-Null
    Start-Sleep 2
    $st2 = (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers $auth -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    Write-Output ("addsubtitle probe (http URL) attached a Subtitle stream: " + (Test-SubStream $st2))
    # Discriminator: same addsubtitle with a LOCAL file path. If the local path
    # attaches but the http URL does not, the failure is VLC's network subtitle
    # fetch (product-relevant: the phone serves subs over http). If neither
    # attaches, VLC's sub pipeline on this build is broken regardless of route.
    $localUri = "file:///" + (($tmp + "\clip.srt") -replace "\\", "/")
    Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json?command=addsubtitle&val=$localUri" -Headers $auth -UseBasicParsing -TimeoutSec 5 | Out-Null
    Start-Sleep 2
    $st3 = (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers $auth -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
    Write-Output ("addsubtitle probe (local file) attached a Subtitle stream: " + (Test-SubStream $st3))
    # VLC's own verbose log names the exact failure (fetch error, demux probe,
    # lua option handling). Dump the sub-relevant lines so the cause is in CI.
    Write-Output "VLC verbose log (sub/srt/slave/error lines):"
    if (Test-Path $vlcLog) {
      Get-Content $vlcLog | Select-String -Pattern 'sub|srt|slave|error|http' | Select-Object -Last 40 | ForEach-Object { $_.Line }
    } else {
      Write-Output "(no vlc log file produced)"
    }
    throw "no Subtitle stream attached - :sub-file option did not take"
  }
  Write-Output "VLC attached the :sub-file subtitle (Subtitle stream present)"
} finally {
  if ($vlc -and -not $vlc.HasExited) { Stop-Process -Id $vlc.Id -Force }
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
