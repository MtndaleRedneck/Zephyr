$ErrorActionPreference='Stop'
$out=Join-Path $env:RUNNER_TEMP 'motion-dpi';New-Item -ItemType Directory -Force $out|Out-Null
$installer=(Get-ChildItem "$env:RUNNER_TEMP/frozen-installer" -Filter '*.exe' -Recurse|Select-Object -First 1).FullName
if(!$installer){throw 'Frozen run40793721589 installer missing'}
"installerSHA256=$((Get-FileHash $installer -Algorithm SHA256).Hash) source=490408d3922a2e15a0fe7a878a498a27d071cf57 artifact=12862958774"|Set-Content "$out/reinstall-proof.txt"
$installed="$env:LOCALAPPDATA/Programs/Zephyr Player/ZephyrPlayer.exe"
$config=Join-Path $env:APPDATA 'ZephyrPlayer';New-Item -ItemType Directory -Force $config|Out-Null
$settings=Join-Path $config 'settings.json';$pairs=Join-Path $config 'paired.json'
function Install($tag){$p=Start-Process $installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',"/LOG=$out/reinstall-$tag.log" -PassThru;if(!$p.WaitForExit(150000)-or$p.ExitCode-ne 0){throw "Install $tag failed"}}
function StopApp{Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Stop-Process -Force;Start-Sleep -Seconds 2}
function AssertAuth{
 $ok=$false;for($i=0;$i-lt 30;$i++){try{$r=Invoke-WebRequest 'http://127.0.0.1:47823/control/status' -Headers @{Authorization='Bearer local-smoke-token'} -TimeoutSec 2;if($r.StatusCode-eq 200){$ok=$true;break}}catch{};Start-Sleep -Seconds 1};if(!$ok){throw 'Original token no longer authorized'}
 $wrong=0;try{Invoke-WebRequest 'http://127.0.0.1:47823/control/status' -Headers @{Authorization='Bearer wrong-smoke-token'} -TimeoutSec 2|Out-Null}catch{if($_.Exception.Response){$wrong=[int]$_.Exception.Response.StatusCode}};if($wrong-ne 401){throw "Wrong token status=$wrong instead401"}
 $code=0;try{Invoke-WebRequest 'http://127.0.0.1:47823/control/status' -TimeoutSec 2|Out-Null}catch{if($_.Exception.Response){$code=[int]$_.Exception.Response.StatusCode}};if($code-ne 401){throw "Unauthenticated status=$code instead401"}
}
try{
 StopApp;Install 'first';StopApp
 $sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value;$p=Start-Process $installed -ArgumentList '--install-firewall',$sid -PassThru -Wait;if($p.ExitCode-ne 0){throw 'Firewall setup failed'}
 [IO.File]::WriteAllText($settings,'{"SkipBackSeconds":10,"SkipForwardSeconds":60,"LastVolume":47}')
 [IO.File]::WriteAllText($pairs,'["local-smoke-token"]')
 if([IO.File]::ReadAllText($pairs)-ne '["local-smoke-token"]'){throw 'Legacy plaintext positive fixture missing before install'}
 'PREINSTALL: exact legacy plaintext array confirmed'|Add-Content "$out/reinstall-proof.txt"
 $settingsHash=(Get-FileHash $settings).Hash
 Install 'replacement';Start-Sleep -Seconds 5
 $running=@(Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Where-Object Path -eq $installed);if($running.Count-eq 0){throw 'Installer did not relaunch installed app'}
 AssertAuth
 if((Get-FileHash $settings).Hash-ne$settingsHash){throw 'Settings bytes changed'}
 $text=[IO.File]::ReadAllText($pairs);$entries=@($text|ConvertFrom-Json)
 if($entries.Count-ne 1-or!$entries[0].token.StartsWith('dpapi:')-or$text.Contains('local-smoke-token')){throw 'Protected pairing format not proven'}
 Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
 $bytes=[Convert]::FromBase64String($entries[0].token.Substring(6));$clear=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
 if($clear-ne 'local-smoke-token'){throw 'DPAPI token semantics changed'}
 $protectedHash=(Get-FileHash $pairs).Hash
 StopApp;Start-Process $installed;AssertAuth
 if((Get-FileHash $pairs).Hash-ne$protectedHash){throw 'Second launch rewrote protected pairing file'}
 'PASS: same exact frozen installer replacement auto-relaunch; settings byte-identical; legacy token migrated to protected-at-rest token with same value; authorized200/wrongtoken401/unauthenticated401 before and after independent restart. No re-pair.'|Tee-Object -FilePath "$out/reinstall-proof.txt" -Append
}finally{StopApp}
