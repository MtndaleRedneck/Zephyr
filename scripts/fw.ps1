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
Bounded 'pull nanoserver' 'docker' 'pull mcr.microsoft.com/windows/nanoserver:ltsc2025' 240
Log ((Get-NetIPAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, IPAddress, PrefixLength | Out-String))
Log ((Get-NetConnectionProfile | Format-Table Name, InterfaceAlias, NetworkCategory | Out-String))
$proto = 'C:\proto\proto.exe'
Bounded 'publish prototype' 'dotnet' 'publish proto -c Release -o C:\proto' 120
$hostIp = (Get-NetIPAddress -InterfaceAlias 'vEthernet (nat)' -AddressFamily IPv4 | Select-Object -First 1).IPAddress
Log ('host nat address ' + $hostIp)
Log ((Get-NetConnectionProfile | Format-Table Name, InterfaceAlias, NetworkCategory | Out-String))
$mk = & $proto mkcert | Out-String
$thumb = ([regex]::Match($mk, 'THUMB (\S+)')).Groups[1].Value
$app = 'appid={12345678-1234-1234-1234-123456789abc}'
netsh http add sslcert ipport=0.0.0.0:48443 certhash=$thumb $app | Out-Null
netsh http add urlacl url=https://+:48443/ user=Everyone | Out-Null
$so = Join-Path $env:TEMP 'serve.out'
$sp = Start-Process -FilePath $proto -ArgumentList 'serve https 48443 400' -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError ($so + '.err')
Start-Sleep -Seconds 5
Log ((netstat -ano -p tcp | Select-String ':48443') -join "`n")
Get-NetFirewallRule -DisplayName 'fwtest*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
function Probe([string]$label) {
  Bounded ('container -> host ' + $label) 'docker' ('run --rm mcr.microsoft.com/windows/nanoserver:ltsc2025 curl.exe -sS -k -m 8 -o NUL -w HTTP=%{http_code} https://' + $hostIp + ':48443/') 70
}
function ClearRules { Get-NetFirewallRule -DisplayName 'fwtest*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
Probe 'CASE0 no allow rule (control, expect blocked)'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest1' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -RemoteAddress LocalSubnet -Profile Any | Out-Null
Probe 'CASE1 port-only TCP, LocalSubnet, any profile'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest2' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -Program System -RemoteAddress LocalSubnet -Profile Any | Out-Null
Probe 'CASE2 program=System + port, LocalSubnet, any profile'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest3' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -Program $proto -RemoteAddress LocalSubnet -Profile Any | Out-Null
Probe 'CASE3 program=proto.exe + port (the app exe), LocalSubnet'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest4' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -RemoteAddress 10.99.99.0/24 -Profile Any | Out-Null
Probe 'CASE4 port-only, RemoteAddress other subnet (negative control, expect blocked)'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest5' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -RemoteAddress LocalSubnet -Profile Private | Out-Null
Probe 'CASE5 port-only, LocalSubnet, Private profile only'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest6' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -RemoteAddress LocalSubnet -Profile Public | Out-Null
Probe 'CASE6 port-only, LocalSubnet, Public profile only'
ClearRules
New-NetFirewallRule -DisplayName 'fwtest7a' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48443 -RemoteAddress LocalSubnet -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'fwtest7b' -Direction Inbound -Action Block -Protocol TCP -LocalPort 48443 -Profile Any | Out-Null
Probe 'CASE7 allow + explicit block same port (expect blocked: block wins)'
ClearRules
Log ('server out: ' + (Get-Content $so -Raw -ErrorAction SilentlyContinue))
& taskkill.exe /F /T /PID $sp.Id | Out-Null
netsh http delete sslcert ipport=0.0.0.0:48443 | Out-Null
netsh http delete urlacl url=https://+:48443/ | Out-Null
Log 'done'
