$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path out | Out-Null
Invoke-WebRequest -Uri 'https://www.unifiedremote.com/download/windows-setup' -OutFile out/ur-setup.exe -MaximumRedirection 5 -UseBasicParsing
$h = (Get-FileHash out/ur-setup.exe -Algorithm SHA256).Hash
Add-Content out/30-installer.txt ("sha256 " + $h)
$p = Start-Process out/ur-setup.exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
$done = $p.WaitForExit(240000)
Add-Content out/30-installer.txt ("installer exited within 240s: " + $done)
Start-Sleep -Seconds 40
$dir = Join-Path $env:ProgramData 'Unified Remote'
$cfg = Join-Path $dir 'Server.config'
$log = Join-Path $dir 'Server.log'
$mgr = Join-Path ${env:ProgramFiles(x86)} 'Unified Remote 3\Manager'
Add-Content out/30-manager-js.txt '== Manager UI source lines about security, password, encryption, authentication, settings (cap 60)'
$hits = Get-ChildItem $mgr -Recurse -File -Include *.js, *.html -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'jquery|lodash|ractive|bootstrap|font-awesome' } | Select-String -Pattern '(?i)password|encrypt|authent|security|require.?auth|/api|settings' | Select-Object -First 60
foreach ($h in $hits) {
  $s = $h.Line.Trim()
  if ($s.Length -gt 200) { $s = $s.Substring(0, 200) }
  Add-Content out/30-manager-js.txt ($h.Filename + ':' + $h.LineNumber + ': ' + $s)
}
Add-Content out/31-web.txt '== GET on the local manager (loopback, read only)'
foreach ($path in '/web', '/web/', '/web/settings', '/web/security', '/api/settings') {
  try {
    $r = Invoke-WebRequest -Uri ('http://127.0.0.1:9510' + $path) -UseBasicParsing -TimeoutSec 8
    $body = [string]$r.Content
    if ($body.Length -gt 400) { $body = $body.Substring(0, 400) }
    $body = [regex]::Replace($body, '(?i)(pass\w*|hash\w*|secret\w*|token\w*|key\w*)(\W{1,6})[^\s,;"<>}]+', '$1$2<redacted>')
    Add-Content out/31-web.txt ($path + ' -> ' + $r.StatusCode + ' ' + $body.Replace("`n", ' '))
  } catch {
    Add-Content out/31-web.txt ($path + ' -> failed: ' + $_.Exception.Message)
  }
}
