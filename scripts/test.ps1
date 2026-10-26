#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '',

    [string]$Filter = '',

    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PkgForgeScriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$PkgForgeProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeScriptDirectory '..'))
$PkgForgeSolutionPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot 'PkgForge.slnx'))
$PkgForgeCliAssembly = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot "src/PkgForge.Cli/bin/$Configuration/net10.0/pkgforge.dll"))
$PkgForgeTestAssembly = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot "tests/PkgForge.Tests/bin/$Configuration/net10.0/PkgForge.Tests.dll"))

$PkgForgeBuildArguments = @(
    'build',
    $PkgForgeSolutionPath,
    '-c', $Configuration,
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false'
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $PkgForgeBuildArguments += "-p:PkgForgeVersion=$Version"
}
Write-Host "[test] dotnet $($PkgForgeBuildArguments -join ' ')"
& dotnet @PkgForgeBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $PkgForgeCliAssembly -PathType Leaf)) {
    throw "CLI assembly not found after build: $PkgForgeCliAssembly"
}

if (-not (Test-Path -LiteralPath $PkgForgeTestAssembly -PathType Leaf)) {
    throw "Test assembly not found after build: $PkgForgeTestAssembly"
}

$env:PKGFORGE_CLI_PATH = $PkgForgeCliAssembly
if ($KeepArtifacts) {
    $env:PKGFORGE_KEEP_TESTS = '1'
}
else {
    Remove-Item Env:PKGFORGE_KEEP_TESTS -ErrorAction SilentlyContinue
}

$PkgForgeTestArguments = [System.Collections.Generic.List[string]]::new()
$PkgForgeTestArguments.Add($PkgForgeTestAssembly)
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $PkgForgeTestArguments.Add('--filter')
    $PkgForgeTestArguments.Add($Filter)
}
if ($KeepArtifacts) {
    $PkgForgeTestArguments.Add('--keep')
}

Write-Host "[test] dotnet $($PkgForgeTestArguments -join ' ')"
& dotnet @PkgForgeTestArguments
exit $LASTEXITCODE
