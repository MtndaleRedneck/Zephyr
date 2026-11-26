$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path out | Out-Null
Invoke-WebRequest -Uri 'https://www.unifiedremote.com/download/windows-setup' -OutFile out/ur-setup.exe -MaximumRedirection 5 -UseBasicParsing
$h = (Get-FileHash out/ur-setup.exe -Algorithm SHA256).Hash
Add-Content out/20-installer.txt ("sha256 " + $h)
$p = Start-Process out/ur-setup.exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
$done = $p.WaitForExit(240000)
Add-Content out/20-installer.txt ("installer exited within 240s: " + $done)
Start-Sleep -Seconds 40
$dir = Join-Path $env:ProgramData 'Unified Remote'
$cfg = Join-Path $dir 'Server.config'
$log = Join-Path $dir 'Server.log'
Add-Content out/21-config.txt '== Server.config (names kept; secret-like values redacted)'
if (Test-Path $cfg) {
  $t = [IO.File]::ReadAllText($cfg)
  Add-Content out/21-config.txt ("length " + $t.Length)
  $r = [regex]::Replace($t, '(?i)(pass\w*|hash\w*|secret\w*|token\w*|key\w*)(\W{1,6})[^\s,;"<>}]+', '$1$2<redacted>')
  Add-Content out/21-config.txt $r
} else {
  Add-Content out/21-config.txt 'Server.config not found'
}
Add-Content out/22-log.txt '== Server.log lines about auth, encryption, discovery, listening (cap 80)'
if (Test-Path $log) {
  $lines = Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match '(?i)auth|encrypt|password|discover|listen|bind|started|accept|reject|connect' } | Select-Object -First 80
  foreach ($l in $lines) {
    $s = [regex]::Replace($l, '(?i)(pass\w*|hash\w*|secret\w*|token\w*|key\w*)(\W{1,6})[^\s,;"<>}]+', '$1$2<redacted>')
    if ($s.Length -gt 220) { $s = $s.Substring(0, 220) }
    Add-Content out/22-log.txt $s
  }
} else {
  Add-Content out/22-log.txt 'Server.log not found'
}
Add-Content out/23-listen.txt 'listeners'
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 9510, 9512 } | ForEach-Object { Add-Content out/23-listen.txt ($_.LocalAddress + ':' + $_.LocalPort) }
