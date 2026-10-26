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
  # CI-observed (VLC 3.0.21, -vv log): a fully percent-encoded option value
  # reached the core with the sub URL still encoded (loaded as a fake local
  # path). The lua parser (parse_url_request) splits on literal & and = and
  # decode_uri's each value, so the correct encoding is not obvious: raw breaks
  # the parser, full-encode was observed failing. Run the truth-table matrix
  # and let VLC itself name the encoding that attaches a subtitle ES.
  $auth = @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(":zephyr")) }
  function Test-SubStream($status) {
    foreach ($cat in $status.information.category.PSObject.Properties) {
      foreach ($stream in $cat.Value.PSObject.Properties) {
        if ($stream.Value.Type -eq "Subtitle") { return $true }
      }
    }
    return $false
  }
  function Get-Status { (Invoke-WebRequest -Uri "http://127.0.0.1:8080/requests/status.json" -Headers $auth -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json }
  function Wait-Sub($seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
      Start-Sleep -Milliseconds 500
      if (Test-SubStream (Get-Status)) { return $true }
    }
    return $false
  }
  function Send-Vlc($query) {
    Invoke-WebRequest -Uri ("http://127.0.0.1:8080/requests/status.json?" + $query) -Headers $auth -UseBasicParsing -TimeoutSec 5 | Out-Null
  }

  # Start playback (input value percent-encoded: this half was already proven).
  Send-Vlc "command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4"
  $played = $false
  for ($i = 0; $i -lt 20 -and -not $played; $i++) {
    Start-Sleep -Milliseconds 500
    $st = Get-Status
    if ($st.state -eq "playing") { $played = $true }
  }
  if (-not $played) { throw "VLC never entered playing state on the phone-style URL" }
  Write-Output "VLC PLAYED the phone-style http URL (state=$($st.state), position=$($st.position))"

  # Encoding matrix. Each candidate is the exact query substring for one
  # option=/val= parameter carrying the sub URL http://127.0.0.1:8123/clip.srt.
  # VLC 3.0.x parse_url_request splits on literal & and = then decode_uri's the
  # value, so only forms whose value survives that round-trip can attach.
  $candidates = @(
    @{ name = "A_full_encode (option=%3Asub-file%3Dhttp%3A%2F%2F...)"; query = "command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4&option=%3Asub-file%3Dhttp%3A%2F%2F127.0.0.1%3A8123%2Fclip.srt" },
    @{ name = "B1_encode_inner_eq_only (option=:sub-file%3Dhttp://127.0.0.1:8123/clip.srt)"; query = "command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4&option=:sub-file%3Dhttp://127.0.0.1:8123/clip.srt" },
    @{ name = "B2_encode_eq_and_url (option=:sub-file%3Dhttp%3A%2F%2F127.0.0.1%3A8123%2Fclip.srt)"; query = "command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4&option=:sub-file%3Dhttp%3A%2F%2F127.0.0.1%3A8123%2Fclip.srt" },
    @{ name = "C_raw (option=:sub-file=http://127.0.0.1:8123/clip.srt)"; query = "command=in_play&input=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.mp4&option=:sub-file=http://127.0.0.1:8123/clip.srt" },
    @{ name = "D_addsubtitle_raw (val=http://127.0.0.1:8123/clip.srt)"; query = "command=addsubtitle&val=http://127.0.0.1:8123/clip.srt" },
    @{ name = "E_addsubtitle_encoded (val=http%3A%2F%2F...)"; query = "command=addsubtitle&val=http%3A%2F%2F127.0.0.1%3A8123%2Fclip.srt" }
  )
  $winner = $null
  foreach ($c in $candidates) {
    # Reset playback so a slow attach from the previous candidate cannot be
    # misattributed: stop, wait for the Subtitle stream to clear, then send.
    Send-Vlc "command=pl_stop"
    for ($i = 0; $i -lt 6; $i++) {
      Start-Sleep -Milliseconds 500
      if (-not (Test-SubStream (Get-Status))) { break }
    }
    Send-Vlc $c.query
    $attached = Wait-Sub 4
    Write-Output ("MATRIX " + $c.name + " => Subtitle stream: " + $attached)
    if ($attached -and -not $winner) { $winner = $c.name }
  }
  if (-not $winner) {
    Write-Output "STATUS DUMP (no encoding attached a Subtitle stream):"
    Write-Output ((Get-Status).information | ConvertTo-Json -Depth 8)
    Write-Output "VLC verbose log (sub/srt/slave/error lines):"
    if (Test-Path $vlcLog) {
      Get-Content $vlcLog | Select-String -Pattern 'sub|srt|slave|error|http' | Select-Object -Last 60 | ForEach-Object { $_.Line }
    } else {
      Write-Output "(no vlc log file produced)"
    }
    throw "no encoding candidate attached a Subtitle stream - desktop-VLC subtitle route unsupported on this VLC build"
  }
  Write-Output "MATRIX WINNER: $winner"
  Write-Output "VLC attached the subtitle (Subtitle stream present)"
} finally {
  if ($vlc -and -not $vlc.HasExited) { Stop-Process -Id $vlc.Id -Force }
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
