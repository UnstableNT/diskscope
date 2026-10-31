<#
.SYNOPSIS
    Compares "diskscope smart <n> --json" with Get-StorageReliabilityCounter (milestone M2). Needs admin rights.
.PARAMETER Cli
    Path to diskscope.exe.
.PARAMETER Report
    Output file for the comparison report.
#>
param(
    [Parameter(Mandatory)] [string] $Cli,
    [Parameter(Mandatory)] [string] $Report
)

$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("DiskScope SMART-Abgleich $(Get-Date -Format s)")
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$lines.Add("Administrator: $isAdmin")
$mismatches = 0

function Get-Row($result, [int] $id) { $result.attributes | Where-Object { $_.id -eq $id } | Select-Object -First 1 }

function Compare-Value([string] $name, $windows, $diskscope, [int] $tolerance = 0, [switch] $Informational) {
    if ($Informational) {
        $script:lines.Add(("  {0,-24} Windows={1,-12} DiskScope={2,-12} (Hinweis, nicht gewertet)" -f $name, $windows, $diskscope))
        return
    }
    if ($null -eq $windows -or $null -eq $diskscope) {
        $script:lines.Add(("  {0,-24} Windows={1,-12} DiskScope={2,-12} (nicht vergleichbar)" -f $name, $windows, $diskscope))
        return
    }
    $ok = [Math]::Abs([double]$windows - [double]$diskscope) -le $tolerance
    if (-not $ok) { $script:mismatches++ }
    $script:lines.Add(("  {0,-24} Windows={1,-12} DiskScope={2,-12} {3}" -f $name, $windows, $diskscope, $(if ($ok) { 'OK' } else { 'ABWEICHUNG' })))
}

foreach ($disk in Get-PhysicalDisk | Sort-Object { [int]$_.DeviceId }) {
    $number = [int]$disk.DeviceId
    $counter = $disk | Get-StorageReliabilityCounter
    $result = & $Cli smart $number --json | ConvertFrom-Json
    $lines.Add('')
    $lines.Add("Laufwerk ${number}: $($disk.FriendlyName) ($($disk.BusType)), Quelle $($result.source), Status $($result.overall)")

    if ($result.source -eq 'NvmeLog') {
        $hours = (Get-Row $result 128).rawValue
        $used = (Get-Row $result 5).rawValue
        $mediaErrors = (Get-Row $result 160).rawValue
        $cycles = (Get-Row $result 112).rawValue
    } else {
        $hours = if ($r = Get-Row $result 9) { [uint64]$r.rawValue -band 0xFFFFFFFF } else { $null }
        $used = $null
        $mediaErrors = (Get-Row $result 187).rawValue
        $cycles = (Get-Row $result 12).rawValue
    }

    # Temperature may change by a degree between the two reads.
    Compare-Value 'Temperatur (C)' $counter.Temperature $result.temperatureCelsius 2
    Compare-Value 'Betriebsstunden' $counter.PowerOnHours $hours 1
    # Windows often leaves Wear at 0 (observed for SATA and NVMe); DiskScope shows NVMe "Percentage Used".
    Compare-Value 'Verschleiss (%)' $counter.Wear $used -Informational
    Compare-Value 'Unkorr. Lesefehler' $counter.ReadErrorsUncorrected $mediaErrors
    Compare-Value 'Start/Stopp bzw. Zyklen' $counter.StartStopCycleCount $cycles
}

$lines.Add('')
$lines.Add("Abweichungen: $mismatches")
$lines | Set-Content -Path $Report -Encoding utf8
