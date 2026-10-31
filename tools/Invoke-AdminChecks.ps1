<#
.SYNOPSIS
    Runs the admin-only checks in one elevated session: VHD integration tests and the read-only
    reproducibility measurement on the given real drives. Results go to the artifacts folder.
#>
param(
    [Parameter(Mandatory)] [string] $Root,
    # Comma-separated drive numbers, e.g. "0;1" or "0 1" ("0,1" is ambiguous with -File on German locales).
    [string] $Drives = '',
    [switch] $SkipTests
)

[int[]] $Drives = @($Drives -split '[;\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })

$ErrorActionPreference = 'Continue'
$artifacts = Join-Path $Root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$bin = 'bin\Debug\net10.0-windows10.0.19041.0'

if (-not $SkipTests) {
    $tests = Join-Path $Root "tests\DiskScope.Integration.Tests\$bin\DiskScope.Integration.Tests.exe"
    & $tests -trait 'Category=Admin' *> (Join-Path $artifacts 'admin-tests.txt')
}

if ($Drives.Count -gt 0) {
    & (Join-Path $Root 'tools\Measure-Reproducibility.ps1') `
        -Cli (Join-Path $Root "src\DiskScope.Cli\$bin\diskscope.exe") `
        -Drives $Drives `
        -Report (Join-Path $artifacts 'reproducibility.txt') *> (Join-Path $artifacts 'reproducibility-log.txt')
}
