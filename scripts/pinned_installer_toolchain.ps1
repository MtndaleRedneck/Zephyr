$ErrorActionPreference='Stop'
$wanted='6.6.1'
$url='https://github.com/jrsoftware/issrc/releases/download/is-6_6_1/innosetup-6.6.1.exe'
$expected='d243ce440c02705530699554fb9612b9b2bd7a2a90629cdb7f41e66f5faeb91f'
$download=Join-Path $env:RUNNER_TEMP 'innosetup-6.6.1.exe'
Invoke-WebRequest $url -OutFile $download
$hash=(Get-FileHash $download -Algorithm SHA256).Hash
if($hash-ne$expected){throw "Official InnoSetup payload digest mismatch: $hash"}
# Digest independently read from official release API and its .issig sidecar.
# Hash check is enforced; issig cryptographic verification is not claimed.
$setup=Start-Process $download -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru
if(!$setup.WaitForExit(120000)-or$setup.ExitCode-ne 0){throw 'Pinned official toolchain installation failed'}
"OFFICIAL_PAYLOAD_PASS url=$url SHA256=$hash"
$iscc='C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
if(!(Test-Path $iscc)){throw 'Pinned ISCC executable missing'}
$actual=(Get-Item $iscc).VersionInfo.ProductVersion
if($actual-notmatch '^6\.6\.1(?:\D|$)'){throw "Pinned ISCC mismatch: expected=$wanted actual=$actual"}
$engine=(& $iscc /? 2>&1|Out-String)
if($LASTEXITCODE-ne 0){throw "ISCC version probe failed: exit=$LASTEXITCODE"}
if($engine-notmatch 'Compiler engine version: Inno Setup 6\.6\.1(?:\D|$)'){throw "ISCC engine probe mismatch: $engine"}
"PINNED_TOOLCHAIN_PASS productVersion=$actual ISCC_SHA256=$((Get-FileHash $iscc -Algorithm SHA256).Hash)"
$engine
