# Pure ShellBounds classifier for pub202_real_data.ps1 (no I/O, no UI, no registry). Dot-sourced by the real-data step
# and by shellbounds_selftest.ps1. Mirrors WindowGeometry.ParseBounds / ChooseBounds in the app.
# Result: Class, Sane, Expected, Fail (a finding that fails the step), Proven (geometry preserved, string equality only).
function Get-ShellBoundsVerdict {
    param([string]$BeforeS, [string]$AfterS, [double]$VsLeft, [double]$VsTop, [double]$VsWidth, [double]$VsHeight, [string]$Dpi)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $VsRight = $VsLeft + $VsWidth; $VsBottom = $VsTop + $VsHeight
    function Parse-Bounds([string]$t) {
        $p = $t.Split(',')
        if ($p.Count -ne 5) { return $null }
        $n = @(); foreach ($i in 0..3) { $d = 0.0; if (-not [double]::TryParse($p[$i], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d) -or [double]::IsNaN($d) -or [double]::IsInfinity($d)) { return $null }; $n += $d }
        return [pscustomobject]@{ L = $n[0]; T = $n[1]; W = $n[2]; H = $n[3]; Max = $p[4] }
    }
    $r = [pscustomobject]@{ Class = $null; Sane = $false; Expected = 'n/a'; Fail = $false; Proven = $false }
    $bEmpty = [string]::IsNullOrWhiteSpace($BeforeS); $aEmpty = [string]::IsNullOrWhiteSpace($AfterS)
    # Equal-empty first: the old app persisted no geometry and the candidate wrote none. Nothing to restore, so this
    # is NOT a failure, but it is also NOT proof that a saved geometry is restored (never Proven).
    if ($bEmpty -and $aEmpty) { $r.Class = 'not-exercised-empty-both'; return $r }
    if ($aEmpty) { $r.Class = 'emptied-after'; $r.Fail = $true; return $r }
    $bb = if ($bEmpty) { $null } else { Parse-Bounds $BeforeS }
    $aa = Parse-Bounds $AfterS
    if (-not $aa) { $r.Class = 'unparseable-after'; $r.Fail = $true; return $r }
    # Same acceptance the app uses (width >= 200, height >= 150, <= 20000) and the same visibility rule
    # (at least 160 x 120 of the rect on the runner's virtual screen, both axes).
    $ix = [Math]::Min($aa.L + $aa.W, $VsRight) - [Math]::Max($aa.L, $VsLeft); $iy = [Math]::Min($aa.T + $aa.H, $VsBottom) - [Math]::Max($aa.T, $VsTop)
    $ints = ($AfterS -match '^-?\d+,-?\d+,\d+,\d+,[01]$')
    $r.Sane = ($ints -and $aa.W -ge 200 -and $aa.H -ge 150 -and $aa.W -le 20000 -and $aa.H -le 20000 -and $ix -ge 160 -and $iy -ge 120)
    if ($AfterS -eq $BeforeS) { $r.Class = 'unchanged' }
    elseif ($bb) {
        $w = [Math]::Min($bb.W, $VsWidth); $h = [Math]::Min($bb.H, $VsHeight)
        $left = [Math]::Max($VsLeft, [Math]::Min($bb.L, $VsRight - $w)); $top = [Math]::Max($VsTop, [Math]::Min($bb.T, $VsBottom - $h))
        $r.Expected = (($left, $top, $w, $h | ForEach-Object { [Math]::Round($_).ToString($inv) }) -join ',') + ',' + $bb.Max
        if ($AfterS -eq $r.Expected) {
            if ("$Dpi" -eq '96') { $r.Class = 'reapplied-clamped-to-this-desktop' } else { $r.Class = "reapplied-clamped-NOT-QUALIFIED-at-dpi-$Dpi" }
        }
    }
    if (-not $r.Class) {
        # HomeWindow.xaml default is Width 1280 x Height 800 (CenterScreen); the player window default is 1920 x 1080.
        $rw = [Math]::Round($aa.W); $rh = [Math]::Round($aa.H)
        if (($rw -eq 1280 -and $rh -eq 800) -or ($rw -eq 1920 -and $rh -eq 1080)) { $r.Class = 'RESET-TO-DEFAULT-SIZE' } else { $r.Class = 'resized-or-moved' }
    }
    if ($r.Class -notin 'unchanged', 'reapplied-clamped-to-this-desktop' -or -not $r.Sane) { $r.Fail = $true } else { $r.Proven = $true }
    return $r
}
