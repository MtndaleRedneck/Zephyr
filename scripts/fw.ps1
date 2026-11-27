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
