New-Item -ItemType Directory -Force -Path out | Out-Null
$f = 'out/cap.txt'
function L($s) { Add-Content $f $s }
L ('os ' + [Environment]::OSVersion.VersionString)
L ((docker version 2>&1 | Out-String))
L ((docker info 2>&1 | Select-String -Pattern 'OSType|Operating System|Server Version|Isolation|Default Runtime' | Out-String))
L ((docker images 2>&1 | Out-String))
L ('wsl: ' + (wsl --status 2>&1 | Out-String))
L ('wsl list: ' + (wsl -l -v 2>&1 | Out-String))
L ((Get-Service vmcompute, hns, LxssManager -ErrorAction SilentlyContinue | Format-Table Name, Status | Out-String))
L ((Get-NetIPAddress -AddressFamily IPv4 | Format-Table InterfaceAlias, IPAddress | Out-String))
L ('firewall: ' + (Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultInboundAction | Out-String))
L ('ssh/openssh/ubuntu on host? ' + (Get-Command ssh -ErrorAction SilentlyContinue | Out-String))
