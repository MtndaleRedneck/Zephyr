$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path out | Out-Null
$log = 'out/v6.txt'
function L($m) { $line = ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $m); Add-Content -Path $log -Value $line; Write-Host $line }
L ('ipv6 stack: ' + [System.Net.Sockets.Socket]::OSSupportsIPv6)
Get-NetAdapter | ForEach-Object { L ('adapter ' + $_.Name + ' ' + $_.Status) }
Get-NetIPAddress -AddressFamily IPv6 | ForEach-Object { L ('v6 addr ' + $_.IPAddress + ' on ' + $_.InterfaceAlias) }

function TryGet($url) {
    try {
        $h = New-Object System.Net.Http.HttpClient
        $h.Timeout = [TimeSpan]::FromSeconds(5)
        $r = $h.GetAsync($url).GetAwaiter().GetResult()
        return ('status ' + [int]$r.StatusCode)
    } catch { return ('failed ' + $_.Exception.GetBaseException().GetType().Name + ': ' + $_.Exception.GetBaseException().Message) }
}

function Case($name, $prefix, $port) {
    L ('--- CASE ' + $name + ' prefix=' + $prefix)
    $l = New-Object System.Net.HttpListener
    try { $l.Prefixes.Add($prefix); $l.Start() } catch { L ('listener start failed: ' + $_.Exception.GetBaseException().Message); return }
    $null = $l.GetContextAsync()
    L ('v4 loopback: ' + (TryGet ('http://127.0.0.1:' + $port + '/')))
    $l.Stop(); $l.Close()
    $l2 = New-Object System.Net.HttpListener
    $l2.Prefixes.Add($prefix); $l2.Start()
    $t = $l2.GetContextAsync()
    L ('v6 loopback: ' + (TryGet ('http://[::1]:' + $port + '/')))
    L ('netstat tcp6 on port: ' + ((netstat -ano -p tcpv6 | Select-String (':' + $port + ' ')) -join ' | '))
    L ('netstat tcp on port: ' + ((netstat -ano -p tcp | Select-String (':' + $port + ' ')) -join ' | '))
    $l2.Stop(); $l2.Close()
}
Case 'A plus-wildcard' 'http://+:48701/' 48701
Case 'B explicit ipv4 any literal' 'http://0.0.0.0:48702/' 48702
Case 'C explicit 127.0.0.1' 'http://127.0.0.1:48703/' 48703

L '--- CASE D raw socket IPv4-only listener (control, how UDP discovery is built)'
$s = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, 48704)
$s.Start()
L ('v4: ' + (TryGet 'http://127.0.0.1:48704/'))
L ('v6: ' + (TryGet 'http://[::1]:48704/'))
$s.Stop()
L 'done'
