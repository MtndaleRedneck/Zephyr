$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path out | Out-Null
function Log($f, $s) { Add-Content -Path ('out/' + $f) -Value $s }
function Run($label, [scriptblock]$sb) {
  try { $r = & $sb 2>&1 | Out-String; Log '41-steps.txt' ('== ' + $label); Log '41-steps.txt' $r.TrimEnd() }
  catch { Log '41-steps.txt' ('== ' + $label + ' EXCEPTION: ' + $_.Exception.Message) }
}
$port = 48443
$proto = 'C:\proto\proto.exe'

# 0. capabilities
Log '40-caps.txt' ('os ' + [Environment]::OSVersion.VersionString)
Log '40-caps.txt' (Get-NetAdapter | Format-Table Name, InterfaceDescription, ifIndex, Status, InterfaceGuid -AutoSize | Out-String -Width 220)
Log '40-caps.txt' (Get-NetConnectionProfile | Format-List Name, InterfaceAlias, InterfaceIndex, NetworkCategory | Out-String)
Log '40-caps.txt' ('hyperv New-VMSwitch present: ' + [bool](Get-Command New-VMSwitch -ErrorAction SilentlyContinue))
Log '40-caps.txt' ('netloop.inf present: ' + (Test-Path (Join-Path $env:windir 'INF\netloop.inf')))
Log '40-caps.txt' ('dotnet ' + (dotnet --version))

# 1. build and stage the prototype
Run 'publish' { dotnet publish proto -c Release -o C:\proto }
Run 'acl' { icacls C:\proto /grant '*S-1-1-0:(OI)(CI)M' }

# 2. network setup on the existing adapter
$prof = Get-NetConnectionProfile | Select-Object -First 1
$idx = $prof.InterfaceIndex
$origCategory = [string]$prof.NetworkCategory
Log '41-steps.txt' ('adapter index ' + $idx + ' original category ' + $origCategory)
Run 'secondary ip' { New-NetIPAddress -InterfaceIndex $idx -IPAddress 10.77.0.2 -PrefixLength 24 | Out-String }
$ethIp = (Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '10.77.0.2' } | Select-Object -First 1).IPAddress
Log '41-steps.txt' ('primary ip ' + $ethIp)

# 3. certificate in the local-machine store, bound by http.sys; app runs as a non-admin user
$cert = New-SelfSignedCertificate -Subject 'CN=netproto' -KeyAlgorithm ECDSA_nistP256 -CurveExport CngNamedCurve -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(2)
$thumb = $cert.Thumbprint
$spki = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLower()
Log '41-steps.txt' ('cert thumbprint ' + $thumb + ' spki-sha256 ' + $spki)
$appid = 'appid={12345678-1234-1234-1234-123456789abc}'
Run 'sslcert v4' { netsh http add sslcert ipport=0.0.0.0:48443 certhash=$thumb $appid }
Run 'sslcert v6' { netsh http add sslcert 'ipport=[::]:48443' certhash=$thumb $appid }
Run 'sslcert coexist port' { netsh http add sslcert ipport=0.0.0.0:48444 certhash=$thumb $appid }

$pw = [Guid]::NewGuid().ToString('N') + 'aA1!'
$sec = ConvertTo-SecureString $pw -AsPlainText -Force
New-LocalUser -Name protouser -Password $sec -PasswordNeverExpires | Out-Null
$sid = (Get-LocalUser protouser).SID.Value
$sddl = 'sddl=D:(A;;GX;;;' + $sid + ')'
Run 'urlacl https' { netsh http add urlacl url=https://+:48443/ $sddl }
Log '41-steps.txt' ('protouser is administrator member: ' + [bool](Get-LocalGroupMember -Group Administrators -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*protouser' }))

# 4. start the https server as the non-admin user
$cred = New-Object System.Management.Automation.PSCredential('.\protouser', $sec)
try {
  $sp = Start-Process -FilePath $proto -ArgumentList 'serve', 'https', '48443', '75' -Credential $cred -LoadUserProfile -WorkingDirectory 'C:\proto' -RedirectStandardOutput 'C:\proto\serve.out' -RedirectStandardError 'C:\proto\serve.err' -PassThru
  Log '41-steps.txt' ('server started via Start-Process, pid ' + $sp.Id)
} catch {
  Log '41-steps.txt' ('Start-Process -Credential failed: ' + $_.Exception.Message)
}
Start-Sleep -Seconds 8
Log '42-server.txt' ('serve.out: ' + (Get-Content 'C:\proto\serve.out' -ErrorAction SilentlyContinue | Out-String))
Log '42-server.txt' ('serve.err: ' + (Get-Content 'C:\proto\serve.err' -ErrorAction SilentlyContinue | Out-String))

# 5. client checks (pin accepted / wrong pin / plaintext / each local address)
function Client($url, $pin) { $o = & $proto client $url $pin 2>&1 | Out-String; Log '43-client.txt' $o.TrimEnd() }
Client ('https://127.0.0.1:' + $port + '/') $spki
Client ('https://' + $ethIp + ':' + $port + '/') $spki
Client ('https://10.77.0.2:' + $port + '/') $spki
Client ('https://' + $ethIp + ':' + $port + '/') ('0' * 64)
Client ('http://' + $ethIp + ':' + $port + '/') $spki
Client ('https://[::1]:' + $port + '/') $spki

# 6. category change seen live by the per-request gate, no restart
Log '43-client.txt' ('-- flip category now ' + (Get-Date -Format 'HH:mm:ss.fff'))
Set-NetConnectionProfile -InterfaceIndex $idx -NetworkCategory Private
Log '43-client.txt' ('-- flip done ' + (Get-Date -Format 'HH:mm:ss.fff'))
Client ('https://' + $ethIp + ':' + $port + '/') $spki
Start-Sleep -Seconds 3
Client ('https://' + $ethIp + ':' + $port + '/') $spki
Set-NetConnectionProfile -InterfaceIndex $idx -NetworkCategory $origCategory
Client ('https://' + $ethIp + ':' + $port + '/') $spki

# 7. what the lookup reports for every local IPv4, UDP destination address, http+https same port
Run 'nlm per local address' { & $proto nlm }
Run 'udp primary' { & $proto udp $ethIp 48500 }
Run 'udp secondary' { & $proto udp 10.77.0.2 48501 }
Run 'udp loopback' { & $proto udp 127.0.0.1 48502 }
Run 'http+https same port with a cert binding' { & $proto coexist 48444 }
Run 'http+https same port without a cert binding' { & $proto coexist 48445 }

# 8. a second adapter, if Hyper-V cmdlets exist
if (Get-Command New-VMSwitch -ErrorAction SilentlyContinue) {
  Run 'create internal switch' { New-VMSwitch -Name protoswitch -SwitchType Internal | Out-String }
  Start-Sleep -Seconds 5
  $vn = Get-NetAdapter | Where-Object { $_.Name -like '*protoswitch*' } | Select-Object -First 1
  if ($vn) {
    Run 'ip on switch adapter' { New-NetIPAddress -InterfaceIndex $vn.ifIndex -IPAddress 10.88.0.2 -PrefixLength 24 | Out-String }
    Start-Sleep -Seconds 5
    Run 'profiles with second adapter' { Get-NetConnectionProfile | Format-List Name, InterfaceAlias, InterfaceIndex, NetworkCategory | Out-String }
    Run 'nlm with second adapter' { & $proto nlm }
    Client 'https://10.88.0.2:48443/' $spki
    Run 'flip only second adapter to Private' { Set-NetConnectionProfile -InterfaceIndex $vn.ifIndex -NetworkCategory Private }
    Client 'https://10.88.0.2:48443/' $spki
    Client ('https://' + $ethIp + ':' + $port + '/') $spki
  } else { Log '41-steps.txt' 'switch adapter not found' }
} else { Log '41-steps.txt' 'no Hyper-V cmdlets, second-adapter check not possible here' }

Start-Sleep -Seconds 2
Log '42-server.txt' ('serve.out final: ' + (Get-Content 'C:\proto\serve.out' -ErrorAction SilentlyContinue | Out-String))

# 9. cleanup
Stop-Process -Name proto -Force -ErrorAction SilentlyContinue
Run 'cleanup' {
  Remove-VMSwitch -Name protoswitch -Force -ErrorAction SilentlyContinue
  netsh http delete sslcert ipport=0.0.0.0:48443
  netsh http delete sslcert 'ipport=[::]:48443'
  netsh http delete sslcert ipport=0.0.0.0:48444
  netsh http delete urlacl url=https://+:48443/
  Remove-LocalUser -Name protouser
  Remove-NetIPAddress -IPAddress 10.77.0.2 -Confirm:$false
  Remove-Item ('Cert:\LocalMachine\My\' + $thumb)
  Get-NetConnectionProfile | Format-List Name, InterfaceIndex, NetworkCategory | Out-String
}
