$ErrorActionPreference='Stop'
$wanted='6.6.1'
& choco install innosetup --version=$wanted --allow-downgrade --no-progress -y
if($LASTEXITCODE-ne 0){throw "Pinned InnoSetup installation failed: exit=$LASTEXITCODE"}
$iscc='C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
if(!(Test-Path $iscc)){throw 'Pinned ISCC executable missing'}
$actual=(Get-Item $iscc).VersionInfo.ProductVersion
if($actual-notmatch '^6\.6\.1(?:\D|$)'){throw "Pinned ISCC mismatch: expected=$wanted actual=$actual"}
$engine=(& $iscc /? 2>&1|Out-String)
if($LASTEXITCODE-ne 0){throw "ISCC version probe failed: exit=$LASTEXITCODE"}
if($engine-notmatch 'Compiler engine version: Inno Setup 6\.6\.1(?:\D|$)'){throw "ISCC engine probe mismatch: $engine"}
"PINNED_TOOLCHAIN_PASS productVersion=$actual ISCC_SHA256=$((Get-FileHash $iscc -Algorithm SHA256).Hash)"
$engine
