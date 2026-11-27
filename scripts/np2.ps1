param([string]$Part)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path out | Out-Null
$logf = 'out/' + $Part + '.txt'
function Log([string]$s) { [IO.File]::AppendAllText((Join-Path (Get-Location) $logf), ((Get-Date -Format 'HH:mm:ss.fff') + ' ' + $s + "`r`n")) }
# Bounded child process: output to files, hard kill of the whole tree on timeout.
function Bounded([string]$label, [string]$exe, [string]$argline, [int]$sec) {
  Log ('>> ' + $label + ' : ' + $exe + ' ' + $argline)
  $o = Join-Path $env:TEMP ('b' + [Guid]::NewGuid().ToString('N') + '.out')
  $e = $o + '.err'
  $p = Start-Process -FilePath $exe -ArgumentList $argline -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
  if (-not $p.WaitForExit($sec * 1000)) {
    Log ('TIMEOUT after ' + $sec + 's, killing tree pid ' + $p.Id)
    & taskkill.exe /F /T /PID $p.Id | Out-Null
  } else { Log ('exit ' + $p.ExitCode) }
  Log ((Get-Content $o -Raw -ErrorAction SilentlyContinue))
  Log ((Get-Content $e -Raw -ErrorAction SilentlyContinue))
}
function Step([string]$label, [scriptblock]$sb) {
  Log ('== ' + $label)
  try { $r = & $sb 2>&1 | Out-String; Log $r.TrimEnd() } catch { Log ('EXC ' + $_.Exception.Message) }
}
$proto = 'C:\proto\proto.exe'
Log ('os ' + [Environment]::OSVersion.VersionString + ' part ' + $Part)
Step 'adapters' { Get-NetAdapter | Format-Table Name, InterfaceDescription, ifIndex, Status, InterfaceGuid -AutoSize | Out-String -Width 220 }
Step 'profiles' { Get-NetConnectionProfile | Format-List Name, InterfaceAlias, InterfaceIndex, NetworkCategory | Out-String }
Step 'caps' { 'New-VMSwitch: ' + [bool](Get-Command New-VMSwitch -ErrorAction SilentlyContinue); 'netloop.inf: ' + (Test-Path (Join-Path $env:windir 'INF\netloop.inf')) }
Bounded 'publish' 'dotnet' 'publish proto -c Release -o C:\proto' 120
Step 'acl' { icacls C:\proto /grant '*S-1-1-0:(OI)(CI)M' }

$prof = Get-NetConnectionProfile | Select-Object -First 1
$idx = $prof.InterfaceIndex
$orig = [string]$prof.NetworkCategory
$ethIp = (Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 | Select-Object -First 1).IPAddress
Log ('primary ifIndex ' + $idx + ' category ' + $orig + ' ip ' + $ethIp)

if ($Part -eq 'nlm') {
  Bounded 'nlm enumerate' $proto 'nlm' 60
  Bounded 'udp primary' $proto ('udp ' + $ethIp + ' 48500') 30
  Bounded 'udp loopback' $proto 'udp 127.0.0.1 48502' 30
  Set-NetConnectionProfile -InterfaceIndex $idx -NetworkCategory Private
  Log 'flipped to Private'
  Start-Sleep -Seconds 2
  Bounded 'nlm after flip to Private' $proto 'nlm' 60
  Set-NetConnectionProfile -InterfaceIndex $idx -NetworkCategory $orig
  Log ('restored to ' + $orig)
  Start-Sleep -Seconds 2
  Bounded 'nlm after restore' $proto 'nlm' 60
}

if ($Part -eq 'tls') {
  # admin-run http.sys HTTPS with sslcert binding, pin accept/reject, plaintext to TLS port, coexistence
  $cert = New-SelfSignedCertificate -Subject 'CN=netproto' -KeyAlgorithm ECDSA_nistP256 -CurveExport CngNamedCurve -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(2)
  $thumb = $cert.Thumbprint
  $spki = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLower()
  Log ('cert ' + $thumb + ' spki ' + $spki)
  $app = 'appid={12345678-1234-1234-1234-123456789abc}'
  Step 'sslcert v4' { netsh http add sslcert ipport=0.0.0.0:48443 certhash=$thumb $app }
  Step 'sslcert v6' { netsh http add sslcert 'ipport=[::]:48443' certhash=$thumb $app }
  Step 'sslcert port2' { netsh http add sslcert ipport=0.0.0.0:48444 certhash=$thumb $app }
  Step 'urlacl' { netsh http add urlacl url=https://+:48443/ user=Everyone }
  $so = Join-Path $env:TEMP 'serve.out'
  $se = $so + '.err'
  $sp = Start-Process -FilePath $proto -ArgumentList 'serve https 48443 60' -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
  Start-Sleep -Seconds 6
  Bounded 'client loopback right pin' $proto ('client https://127.0.0.1:48443/ ' + $spki) 20
  Bounded 'client primary ip right pin' $proto ('client https://' + $ethIp + ':48443/ ' + $spki) 20
  Bounded 'client wrong pin' $proto ('client https://127.0.0.1:48443/ ' + ('0' * 64)) 20
  Bounded 'client plaintext to TLS port' $proto ('client http://127.0.0.1:48443/ ' + $spki) 20
  Bounded 'client ipv6 loopback' $proto ('client https://[::1]:48443/ ' + $spki) 20
  Log ('server out: ' + (Get-Content $so -Raw -ErrorAction SilentlyContinue))
  Log ('server err: ' + (Get-Content $se -Raw -ErrorAction SilentlyContinue))
  & taskkill.exe /F /T /PID $sp.Id | Out-Null
  Bounded 'http+https same port WITH cert binding' $proto 'coexist 48444' 30
  Bounded 'http+https same port NO cert binding' $proto 'coexist 48445' 30
  Step 'cleanup' { netsh http delete sslcert ipport=0.0.0.0:48443; netsh http delete sslcert 'ipport=[::]:48443'; netsh http delete sslcert ipport=0.0.0.0:48444; netsh http delete urlacl url=https://+:48443/ }
}

if ($Part -eq 'nonadmin') {
  # same serve, but as a non-admin local user via a bounded .NET process start with credentials
  $cert = New-SelfSignedCertificate -Subject 'CN=netproto' -KeyAlgorithm ECDSA_nistP256 -CurveExport CngNamedCurve -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(2)
  $thumb = $cert.Thumbprint
  $spki = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLower()
  $app = 'appid={12345678-1234-1234-1234-123456789abc}'
  Step 'sslcert v4' { netsh http add sslcert ipport=0.0.0.0:48443 certhash=$thumb $app }
  $pw = [Guid]::NewGuid().ToString('N') + 'aA1!'
  $sec = ConvertTo-SecureString $pw -AsPlainText -Force
  New-LocalUser -Name protouser -Password $sec -PasswordNeverExpires | Out-Null
  $sid = (Get-LocalUser protouser).SID.Value
  $sddl = 'sddl=D:(A;;GX;;;' + $sid + ')'
  Step 'urlacl for protouser only' { netsh http add urlacl url=https://+:48443/ $sddl }
  # control: urlacl is the only grant, no key ACL change for protouser
  $psi = New-Object Diagnostics.ProcessStartInfo
  $psi.FileName = $proto
  $psi.Arguments = 'serve https 48443 40'
  $psi.UserName = 'protouser'
  $psi.Password = $sec
  $psi.Domain = '.'
  $psi.LoadUserProfile = $false
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.WorkingDirectory = 'C:\proto'
  $pr = $null
  try { $pr = [Diagnostics.Process]::Start($psi); Log ('started pid ' + $pr.Id) } catch { Log ('start as protouser failed: ' + $_.Exception.Message) }
  if ($pr) {
    $outTask = $pr.StandardOutput.ReadLineAsync()
    Start-Sleep -Seconds 8
    Log ('first server line ready: ' + $outTask.IsCompleted + ' ' + $(if ($outTask.IsCompleted) { $outTask.Result }))
    Bounded 'client right pin' $proto ('client https://127.0.0.1:48443/ ' + $spki) 20
    Bounded 'client wrong pin' $proto ('client https://127.0.0.1:48443/ ' + ('0' * 64)) 20
    if (-not $pr.HasExited) { & taskkill.exe /F /T /PID $pr.Id | Out-Null; Log 'killed server' } else { Log ('server exited ' + $pr.ExitCode) }
  }
  Step 'cleanup' { netsh http delete sslcert ipport=0.0.0.0:48443; netsh http delete urlacl url=https://+:48443/; Remove-LocalUser -Name protouser -ErrorAction SilentlyContinue }
}

if ($Part -eq 'secondip') {
  # second address on the same adapter, last because it changes adapter config
  Step 'add 10.77.0.2' { New-NetIPAddress -InterfaceIndex $idx -IPAddress 10.77.0.2 -PrefixLength 24 | Out-String }
  Bounded 'nlm with secondary address' $proto 'nlm' 60
  Bounded 'udp to secondary' $proto 'udp 10.77.0.2 48501' 30
  Step 'remove 10.77.0.2' { Remove-NetIPAddress -IPAddress 10.77.0.2 -Confirm:$false }
}

if ($Part -eq 'hyperv') {
  if (Get-Command New-VMSwitch -ErrorAction SilentlyContinue) {
    Step 'create internal switch' { New-VMSwitch -Name protoswitch -SwitchType Internal | Out-String }
    Start-Sleep -Seconds 5
    Step 'adapters after' { Get-NetAdapter | Format-Table Name, ifIndex, Status, InterfaceGuid -AutoSize | Out-String -Width 200 }
    $vn = Get-NetAdapter | Where-Object { $_.Name -like '*protoswitch*' } | Select-Object -First 1
    if ($vn) {
      Step 'ip on switch adapter' { New-NetIPAddress -InterfaceIndex $vn.ifIndex -IPAddress 10.88.0.2 -PrefixLength 24 | Out-String }
      Start-Sleep -Seconds 5
      Step 'profiles' { Get-NetConnectionProfile | Format-List Name, InterfaceAlias, InterfaceIndex, NetworkCategory | Out-String }
      Bounded 'nlm two adapters' $proto 'nlm' 60
      Step 'flip only switch adapter to Private' { Set-NetConnectionProfile -InterfaceIndex $vn.ifIndex -NetworkCategory Private }
      Bounded 'nlm after flipping only switch adapter' $proto 'nlm' 60
    } else { Log 'switch adapter not found' }
    Step 'remove switch' { Remove-VMSwitch -Name protoswitch -Force }
  } else { Log 'no Hyper-V cmdlets on this runner' }
}
Log 'part finished'
