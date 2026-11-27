New-Item -ItemType Directory -Force -Path out | Out-Null
$logf = 'out/fw.txt'
function Log([string]$s) { [IO.File]::AppendAllText((Join-Path (Get-Location) $logf), ((Get-Date -Format 'HH:mm:ss.fff') + ' ' + $s + "`r`n")) }
function Bounded([string]$label, [string]$exe, [string]$argline, [int]$sec) {
  Log ('>> ' + $label + ' : ' + $exe + ' ' + $argline)
  $o = Join-Path $env:TEMP ('b' + [Guid]::NewGuid().ToString('N') + '.out')
  $p = Start-Process -FilePath $exe -ArgumentList $argline -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
  if (-not $p.WaitForExit($sec * 1000)) { Log ('TIMEOUT ' + $sec + 's'); & taskkill.exe /F /T /PID $p.Id | Out-Null } else { Log ('exit ' + $p.ExitCode) }
  Log ((Get-Content $o -Raw -ErrorAction SilentlyContinue))
  Log ((Get-Content ($o + '.err') -Raw -ErrorAction SilentlyContinue))
}
Log ('os ' + [Environment]::OSVersion.VersionString)
Log ((Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultInboundAction | Out-String))
Log ((Get-Service docker, vmcompute, hns -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType | Out-String))
Start-Service docker -ErrorAction SilentlyContinue
Start-Sleep -Seconds 8
Bounded 'docker info' 'docker' 'info --format {{.OSType}}/{{.Isolation}}/{{.OperatingSystem}}' 40
Bounded 'docker network ls' 'docker' 'network ls' 30
Bounded 'pull servercore' 'docker' 'pull mcr.microsoft.com/windows/servercore:ltsc2025' 400
$proto = 'C:\proto\proto.exe'
Bounded 'publish prototype' 'dotnet' 'publish proto -c Release -o C:\proto' 120
$hostIp = (Get-NetIPAddress -InterfaceAlias 'vEthernet (nat)' -AddressFamily IPv4 | Select-Object -First 1).IPAddress
Log ('host nat address ' + $hostIp)
Log ((Get-NetConnectionProfile | Format-Table Name, InterfaceAlias, NetworkCategory | Out-String))
function ClearRules { Get-NetFirewallRule -DisplayName 'fwtest*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
function UdpCase([string]$label) {
  $o = Join-Path $env:TEMP ('u' + [Guid]::NewGuid().ToString('N') + '.out')
  $rp = Start-Process -FilePath $proto -ArgumentList 'udprecv 48600 28' -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
  Start-Sleep -Seconds 2
  Bounded ('container sends UDP ' + $label) 'docker' ('run --rm mcr.microsoft.com/windows/servercore:ltsc2025 powershell -NoProfile -Command "$c = New-Object Net.Sockets.UdpClient; $b = [Text.Encoding]::ASCII.GetBytes(\"ping\"); 1..5 | ForEach-Object { [void]$c.Send($b, $b.Length, \"' + $hostIp + '\", 48600); Start-Sleep -Milliseconds 300 }; \"sent\""') 120
  if (-not $rp.WaitForExit(40000)) { & taskkill.exe /F /T /PID $rp.Id | Out-Null; Log 'receiver killed' }
  Log ('host receiver: ' + (Get-Content $o -Raw -ErrorAction SilentlyContinue))
}
ClearRules
UdpCase 'CASE-U0 no rule (control)'
ClearRules
New-NetFirewallRule -DisplayName 'fwtestU1' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 48600 -Program $proto -RemoteAddress LocalSubnet -Profile Any | Out-Null
UdpCase 'CASE-U1 exe + port UDP, LocalSubnet, Any'
ClearRules
New-NetFirewallRule -DisplayName 'fwtestU2' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 48600 -RemoteAddress LocalSubnet -Profile Any | Out-Null
UdpCase 'CASE-U2 port-only UDP, LocalSubnet, Any'
ClearRules
New-NetFirewallRule -DisplayName 'fwtestU3' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 48600 -Program $proto -RemoteAddress 10.99.99.0/24 -Profile Any | Out-Null
UdpCase 'CASE-U3 exe + port UDP, other subnet (control)'
ClearRules
Log 'done'
