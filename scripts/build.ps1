#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Aot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PkgForgeScriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$PkgForgeProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeScriptDirectory '..'))
$PkgForgeSolutionPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot 'PkgForge.slnx'))
if (-not (Test-Path -LiteralPath $PkgForgeSolutionPath -PathType Leaf)) {
    throw "Solution not found: $PkgForgeSolutionPath"
}

$PkgForgeBuildArguments = [System.Collections.Generic.List[string]]::new()
$PkgForgeBuildArguments.Add('build')
$PkgForgeBuildArguments.Add($PkgForgeSolutionPath)
$PkgForgeBuildArguments.Add('-c')
$PkgForgeBuildArguments.Add($Configuration)
$PkgForgeBuildArguments.Add('-p:UseSharedCompilation=false')
$PkgForgeBuildArguments.Add('-nodeReuse:false')

Write-Host "[build] dotnet $($PkgForgeBuildArguments -join ' ')"
& dotnet @PkgForgeBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Aot) {
    $PkgForgeAotOutput = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot '.artifacts/aot'))
    $PkgForgeCliProject = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeProjectRoot 'src/PkgForge.Cli/PkgForge.Cli.csproj'))
    $PkgForgePublishArguments = @(
        'publish',
        $PkgForgeCliProject,
        '-c', $Configuration,
        '-r', 'win-x64',
        '-p:PublishAot=true',
        '-p:StripSymbols=true',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false',
        '-o', $PkgForgeAotOutput
    )
    Write-Host "[aot] dotnet $($PkgForgePublishArguments -join ' ')"
    & dotnet @PkgForgePublishArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host "[aot] output: $PkgForgeAotOutput"
}
