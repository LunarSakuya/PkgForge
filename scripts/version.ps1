#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Bump = 'patch',

    [string]$Set = '',

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PkgForgeVersionScriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$PkgForgeVersionProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeVersionScriptDirectory '..'))
$PkgForgeVersionPath = Join-Path $PkgForgeVersionProjectRoot 'VERSION'

if (-not (Test-Path -LiteralPath $PkgForgeVersionPath -PathType Leaf)) {
    throw "VERSION file not found: $PkgForgeVersionPath"
}

function ConvertTo-PkgForgeVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Version must be a full major.minor.patch value, got '$Value'."
    }

    return [System.Version]::new(
        [int]$matches[1],
        [int]$matches[2],
        [int]$matches[3])
}

$PkgForgeCurrentText = (Get-Content -LiteralPath $PkgForgeVersionPath -Raw).Trim()
$PkgForgeCurrent = ConvertTo-PkgForgeVersion -Value $PkgForgeCurrentText

if (-not [string]::IsNullOrWhiteSpace($Set)) {
    $PkgForgeNext = ConvertTo-PkgForgeVersion -Value $Set.Trim()
}
else {
    $PkgForgeNext = switch ($Bump) {
        'major' { [System.Version]::new($PkgForgeCurrent.Major + 1, 0, 0) }
        'minor' { [System.Version]::new($PkgForgeCurrent.Major, $PkgForgeCurrent.Minor + 1, 0) }
        'patch' { [System.Version]::new($PkgForgeCurrent.Major, $PkgForgeCurrent.Minor, $PkgForgeCurrent.Build + 1) }
        default { throw "Unknown version bump '$Bump'." }
    }
}

$PkgForgeNextText = '{0}.{1}.{2}' -f $PkgForgeNext.Major, $PkgForgeNext.Minor, $PkgForgeNext.Build
if ($PkgForgeNext -le $PkgForgeCurrent) {
    throw "Version must increase monotonically: '$PkgForgeCurrentText' -> '$PkgForgeNextText'."
}

if (-not $DryRun) {
    [System.IO.File]::WriteAllText(
        $PkgForgeVersionPath,
        "$PkgForgeNextText`n",
        [System.Text.UTF8Encoding]::new($false))
}

Write-Output $PkgForgeNextText
