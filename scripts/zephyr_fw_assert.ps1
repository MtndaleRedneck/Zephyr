# Reads the four Zephyr Player rules by their real Name/DisplayName (Get-NetFirewallRule), not by netsh display text.
# Harness helper: it asserts what the product's create-only setup should leave; it does not weaken any product check.
function Get-ZRule([string]$display) {
  $r = @(Get-NetFirewallRule -DisplayName $display -ErrorAction SilentlyContinue)
  if ($r.Count -ne 1) { throw "expected exactly one rule '$display', found $($r.Count)" }
  $x = $r[0]
  $p = $x | Get-NetFirewallPortFilter; $a = $x | Get-NetFirewallAddressFilter; $ap = $x | Get-NetFirewallApplicationFilter
  [pscustomobject]@{ Name = $x.Name; Display = $x.DisplayName; Enabled = [string]$x.Enabled; Direction = [string]$x.Direction; Action = [string]$x.Action; Profile = [string]$x.Profile; Protocol = [string]$p.Protocol; LocalPort = ($p.LocalPort -join ','); Remote = ($a.RemoteAddress -join ','); Program = [string]$ap.Program }
}
function Assert-ZRule([string]$display, [string]$profile, [string]$protocol, [string]$port, [string]$remote, [string]$programLike) {
  $r = Get-ZRule $display
  if ($r.Name -notmatch '^Zephyr-[0-9a-f]{32}$') { throw "rule '$display' Name is not a unique Zephyr key: $($r.Name)" }
  if ($r.Enabled -ne 'True' -or $r.Direction -ne 'Inbound' -or $r.Action -ne 'Allow') { throw "rule '$display' is not an enabled inbound allow" }
  if ($r.Profile -ne $profile) { throw "rule '$display' profile is $($r.Profile), expected $profile" }
  if ($r.Protocol -ne $protocol -or $r.LocalPort -ne $port) { throw "rule '$display' is $($r.Protocol)/$($r.LocalPort), expected $protocol/$port" }
  if ($r.Remote -ne $remote) { throw "rule '$display' remote is $($r.Remote), expected $remote" }
  if ($r.Program -notlike $programLike) { throw "rule '$display' program is $($r.Program), expected like $programLike" }
  $r
}
function Assert-ZPlayerRules([string]$exePath) {
  Assert-ZRule 'Zephyr Player 47824 TCP (Private)' 'Private' 'TCP' '47824' 'LocalSubnet' 'System' | Out-Null
  Assert-ZRule 'Zephyr Player 47824 TCP (Public)' 'Public' 'TCP' '47824' 'LocalSubnet4' 'System' | Out-Null
  Assert-ZRule 'Zephyr Player Discovery 8490 UDP (Private)' 'Private' 'UDP' '8490' 'LocalSubnet' $exePath | Out-Null
  Assert-ZRule 'Zephyr Player Discovery 8490 UDP (Public)' 'Public' 'UDP' '8490' 'LocalSubnet4' $exePath | Out-Null
}
# Harness cleanup that touches only what is provably ours or the harness's own fixtures:
# product rules by the unique Name keys in the product's own protected journal; planted earlier-build fixtures by their exact display names AND only when their Name is not a Zephyr-<hex> product key.
function Remove-ZJournaledRules([string]$productDir) {
  $j = Join-Path $env:ProgramData "$productDir\lifecycle.json"
  if (Test-Path $j) {
    try { $o = Get-Content $j -Raw | ConvertFrom-Json; foreach ($r in @($o.Rules)) { if ($r.Key -match '^Zephyr-[0-9a-f]{32}$') { Get-NetFirewallRule -Name $r.Key -ErrorAction SilentlyContinue | Remove-NetFirewallRule } } } catch { Write-Host "journal cleanup skipped: $($_.Exception.Message)" }
  }
}
function Remove-ZPlantedLegacyRules([string[]]$exactDisplayNames) {
  foreach ($d in $exactDisplayNames) { Get-NetFirewallRule -DisplayName $d -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^Zephyr-[0-9a-f]{32}$' } | Remove-NetFirewallRule }
}
