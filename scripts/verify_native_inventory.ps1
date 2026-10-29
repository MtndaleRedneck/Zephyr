param([string]$Published='windows/out',[string]$Version='3.0.24')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$package=Join-Path $nugetRoot "videolan.libvlc.windows/$Version/videolan.libvlc.windows.$Version.nupkg"
if (-not (Test-Path $package)) { throw "Resolved native package not found: $package" }
$zip=[IO.Compression.ZipFile]::OpenRead($package)
try {
  $nativeRoots=@(Get-ChildItem $Published -Filter libvlc.dll -Recurse | ForEach-Object { $_.Directory.FullName })
  if ($nativeRoots.Count -eq 0) { throw 'No published native roots found' }
  foreach ($root in $nativeRoots) {
    $arch=switch -Regex ($root) { 'win-x64$' {'x64';break}; 'win-x86$' {'x86';break}; 'win-arm64$' {'arm64';break}; default {throw "Unknown native layout $root"} }
    $prefix="build/$arch/"
    $expected=@{}
    foreach($entry in $zip.Entries | Where-Object { $_.FullName.StartsWith($prefix) -and $_.FullName.EndsWith('.dll') }) {
      $relative=$entry.FullName.Substring($prefix.Length)
      $sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open()
      try{$expected[$relative]=[Convert]::ToHexString($sha.ComputeHash($stream))}finally{$stream.Dispose();$sha.Dispose()}
    }
    if($expected.Count -lt 100){throw "Suspicious package inventory count $($expected.Count) for $arch"}
    $actual=@{}
    foreach($dll in Get-ChildItem $root -Filter '*.dll' -Recurse) {
      $relative=[IO.Path]::GetRelativePath($root,$dll.FullName).Replace('\','/')
      $actual[$relative]=(Get-FileHash $dll.FullName -Algorithm SHA256).Hash
    }
    foreach($name in $expected.Keys){if(-not $actual.ContainsKey($name)){throw "Missing native $arch/$name"};if($actual[$name] -ne $expected[$name]){throw "Native hash mismatch $arch/$name"}}
    foreach($name in $actual.Keys){if(-not $expected.ContainsKey($name)){throw "Extra native DLL $arch/$name"}}
    $nativeVersion=(Get-Item (Join-Path $root 'libvlc.dll')).VersionInfo.FileVersion
    if($nativeVersion -ne $Version){throw "Native version mismatch $nativeVersion"}
    Write-Output "NATIVE_INVENTORY_PASS arch=$arch count=$($actual.Count) package=$Version libvlcSHA256=$($actual['libvlc.dll'])"
    $actual.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Output "$arch/$($_.Name) $($_.Value)" }
  }
  if (-not ($nativeRoots | Where-Object { $_ -match 'win-x64$' })) {throw 'Required x64 native layout absent'}
} finally {$zip.Dispose()}
