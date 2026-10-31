<#
.SYNOPSIS
    Runs "diskscope bench" and "diskscope random" twice per drive (read-only) and reports the deviation
    between the runs (milestone M3). Needs admin rights.
#>
param(
    [Parameter(Mandatory)] [string] $Cli,
    [Parameter(Mandatory)] [int[]] $Drives,
    [Parameter(Mandatory)] [string] $Report,
    [double] $Tolerance = 0.25
)

$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("DiskScope Reproduzierbarkeit $(Get-Date -Format s), Toleranz $([int]($Tolerance * 100)) %")
$failures = 0

function Add-Comparison([string] $name, [double] $a, [double] $b, [string] $unit) {
    $deviation = if ([Math]::Max($a, $b) -gt 0) { [Math]::Abs($a - $b) / [Math]::Max($a, $b) } else { 0 }
    $ok = $deviation -le $Tolerance
    if (-not $ok) { $script:failures++ }
    $script:lines.Add(("  {0,-22} {1,12:N3} {2,12:N3} {3,-5} Abweichung {4,5:P1} {5}" -f $name, $a, $b, $unit, $deviation, $(if ($ok) { 'OK' } else { 'ZU GROSS' })))
}

foreach ($drive in $Drives) {
    $runs = 1..2 | ForEach-Object { & $Cli bench $drive --json | ConvertFrom-Json }
    $random = 1..2 | ForEach-Object { & $Cli random $drive --duration 2s --json | ConvertFrom-Json }
    $lines.Add('')
    $lines.Add("Laufwerk ${drive}                  Lauf 1       Lauf 2")
    Add-Comparison 'Transfer Mittel' $runs[0].averageMBps $runs[1].averageMBps 'MB/s'
    Add-Comparison 'Transfer Minimum' $runs[0].minimumMBps $runs[1].minimumMBps 'MB/s'
    Add-Comparison 'Zugriffszeit' $runs[0].accessTimeMs $runs[1].accessTimeMs 'ms'
    Add-Comparison 'Burst-Rate' $runs[0].burstMBps $runs[1].burstMBps 'MB/s'
    for ($i = 0; $i -lt $random[0].sizes.Count; $i++) {
        $size = $random[0].sizes[$i].size
        $label = if ($size -eq 0) { 'IOPS zufaellig' } else { "IOPS $size B" }
        Add-Comparison $label $random[0].sizes[$i].operationsPerSecond $random[1].sizes[$i].operationsPerSecond '1/s'
    }
    $lines.Add("  Lesefehler: $($runs[0].readErrors + $runs[1].readErrors + $random[0].readErrors + $random[1].readErrors)")
}

$lines.Add('')
$lines.Add("Werte ausserhalb der Toleranz: $failures")
$lines | Set-Content -Path $Report -Encoding utf8
