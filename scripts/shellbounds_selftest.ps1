# Self-test for Get-ShellBoundsVerdict (pure, no app, no tools). Fails the calling step on any mismatch.
. (Join-Path $PSScriptRoot 'shellbounds_classify.ps1')
$fail = 0
function Case($name, $before, $after, $wantClass, $wantFail, $wantProven, $dpi = '96') {
    $v = Get-ShellBoundsVerdict -BeforeS $before -AfterS $after -VsLeft 0 -VsTop 0 -VsWidth 1024 -VsHeight 768 -Dpi $dpi
    $ok = ($v.Class -eq $wantClass -and $v.Fail -eq $wantFail -and $v.Proven -eq $wantProven)
    if (-not $ok) { $script:fail++ }
    Write-Host ("{0,-4} {1} before='{2}' after='{3}' -> class={4} fail={5} proven={6}" -f $(if ($ok) { 'ok' } else { 'BAD' }), $name, $before, $after, $v.Class, $v.Fail, $v.Proven)
}
Case 'equal empty (2.0.2 real run) is not exercised, not a failure, never proven' '' '' 'not-exercised-empty-both' $false $false
Case 'null-ish equal empty' $null $null 'not-exercised-empty-both' $false $false
Case 'nonempty -> empty fails' '10,20,800,600,0' '' 'emptied-after' $true $false
Case 'unchanged valid passes (3.0 real run, frozen baseline)' '-128,-40,1044,788,0' '-128,-40,1044,788,0' 'unchanged' $false $true
Case 'unchanged valid inside the screen passes' '10,20,800,600,0' '10,20,800,600,0' 'unchanged' $false $true
Case 'equal but unparseable fails' 'junk' 'junk' 'unparseable-after' $true $false
Case 'equal but offscreen (insane) fails' '5000,5000,800,600,0' '5000,5000,800,600,0' 'unchanged' $true $false
Case 'changed, exact clamp of before passes at dpi 96' '-128,-40,1044,788,0' '0,0,1024,768,0' 'reapplied-clamped-to-this-desktop' $false $true
Case 'changed, clamp at non-96 dpi is not qualified, fails' '-128,-40,1044,788,0' '0,0,1024,768,0' 'reapplied-clamped-NOT-QUALIFIED-at-dpi-144' $true $false '144'
Case 'changed to something else fails (strict)' '10,20,800,600,0' '30,40,800,600,0' 'resized-or-moved' $true $false
Case 'reset to the Home default size fails' '10,20,800,600,0' '0,0,1280,800,0' 'RESET-TO-DEFAULT-SIZE' $true $false
Case 'empty before, value written after fails (candidate must not write)' '' '10,20,800,600,0' 'resized-or-moved' $true $false
if ($fail) { throw "ShellBounds classifier self-test: $fail case(s) wrong" }
Write-Host 'SHELLBOUNDS CLASSIFIER SELFTEST PASS'
