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
$metadata=(Get-Item $iscc).VersionInfo
"ISCC_METADATA path=$iscc fileVersion=$($metadata.FileVersion) productVersion=$($metadata.ProductVersion) SHA256=$((Get-FileHash $iscc -Algorithm SHA256).Hash)"
$isccHash=(Get-FileHash $iscc -Algorithm SHA256).Hash
$engine=Join-Path (Split-Path $iscc) 'ISCmplr.dll'
if(!(Test-Path $engine)){throw 'Compiler engine DLL missing'}
$engineHash=(Get-FileHash $engine -Algorithm SHA256).Hash
$engineMetadata=(Get-Item $engine).VersionInfo
"ENGINE_METADATA path=$engine fileVersion=$($engineMetadata.FileVersion) productVersion=$($engineMetadata.ProductVersion) SHA256=$engineHash"
Get-ItemProperty 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*','HKLM:/Software/WOW6432Node/Microsoft/Windows/CurrentVersion/Uninstall/*' -ErrorAction SilentlyContinue | Where-Object DisplayName -like 'Inno Setup*' | ForEach-Object {"REGISTRY_DIAGNOSTIC name=$($_.DisplayName) DisplayVersion=$($_.DisplayVersion)"}
$isccHash | Set-Content (Join-Path $env:RUNNER_TEMP 'zephyr-iscc-sha256.txt')
$engineHash | Set-Content (Join-Path $env:RUNNER_TEMP 'zephyr-compiler-engine-sha256.txt')
"PINNED_PAYLOAD_INSTALLED officialPayloadSHA256=$hash ISCC_SHA256=$isccHash ENGINE_SHA256=$engineHash; actual6.6.1enginegateoccursinbothsuccessfulartifactcompilelogs"
