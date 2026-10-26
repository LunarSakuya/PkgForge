#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]{0,31}$')]
    [string]$Version = '0.4.2',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PkgForgeReleaseScriptDirectoryPath = [System.IO.Path]::GetFullPath($PSScriptRoot)
$PkgForgeReleaseProjectRootPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseScriptDirectoryPath '..'))
$PkgForgeReleaseArtifactsRootPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseProjectRootPath '.artifacts'))
$PkgForgeReleaseOutputRootPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseProjectRootPath 'releases'))
$PkgForgeReleaseVersionRootPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath "v$Version"))
$PkgForgeReleaseBinaryDirectoryPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseVersionRootPath $Runtime))
$PkgForgeReleaseBinaryZipPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath "pkgforge-$Version-$Runtime.zip"))
$PkgForgeReleaseSourceZipPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath "pkgforge-$Version-source.zip"))
$PkgForgeReleaseManifestPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath 'release-manifest.json'))
$PkgForgeReleaseChecksumsPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath 'SHA256SUMS.txt'))
$PkgForgeReleaseTimestamp = [System.DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
$PkgForgeReleasePublishDirectoryPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseArtifactsRootPath "release-publish-$PkgForgeReleaseTimestamp"))
$PkgForgeReleaseBinaryStagingDirectoryPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseOutputRootPath ".staging-binary-$PkgForgeReleaseTimestamp"))
$PkgForgeReleaseSourceStagingParentPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseArtifactsRootPath "source-staging-$PkgForgeReleaseTimestamp"))
$PkgForgeReleaseSourceRootPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseSourceStagingParentPath "pkgforge-$Version"))
$PkgForgeReleaseSourceManifestPath = [System.IO.Path]::GetFullPath((Join-Path $PkgForgeReleaseSourceRootPath 'SOURCE-MANIFEST.txt'))

function Assert-PkgForgeReleasePathUnder {
    param(
        [Parameter(Mandatory = $true)][string]$PkgForgeCandidatePath,
        [Parameter(Mandatory = $true)][string]$PkgForgeAllowedRootPath,
        [Parameter(Mandatory = $true)][string]$PkgForgePathLabel
    )

    $candidate = [System.IO.Path]::GetFullPath($PkgForgeCandidatePath)
    $root = [System.IO.Path]::GetFullPath($PkgForgeAllowedRootPath)
    $prefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not ($candidate.Equals($root, [System.StringComparison]::OrdinalIgnoreCase) -or
              $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing $PkgForgePathLabel outside its allowed root: $candidate"
    }
}

function Copy-PkgForgeFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Release source file is missing: $SourcePath"
    }

    $parent = [System.IO.Path]::GetDirectoryName($DestinationPath)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "Release destination has no parent: $DestinationPath"
    }

    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
}

function Get-PkgForgeHashEntries {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $RootPath -Recurse -File | Sort-Object FullName) {
        $relative = [System.IO.Path]::GetRelativePath($RootPath, $file.FullName).Replace('\', '/')
        $entries.Add([ordered]@{
            path = $relative
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    return $entries
}

function Write-PkgForgeChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Entries
    )

    $lines = $Entries | ForEach-Object { "$($_.sha256)  $($_.path)" }
    Set-Content -LiteralPath $Path -Value $lines -Encoding utf8NoBOM
}

Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseVersionRootPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'version directory'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseBinaryDirectoryPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'binary directory'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseBinaryZipPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'binary ZIP'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseSourceZipPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'source ZIP'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseManifestPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'release manifest'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseChecksumsPath -PkgForgeAllowedRootPath $PkgForgeReleaseOutputRootPath -PkgForgePathLabel 'release checksums'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleasePublishDirectoryPath -PkgForgeAllowedRootPath $PkgForgeReleaseArtifactsRootPath -PkgForgePathLabel 'publish directory'
Assert-PkgForgeReleasePathUnder -PkgForgeCandidatePath $PkgForgeReleaseSourceStagingParentPath -PkgForgeAllowedRootPath $PkgForgeReleaseArtifactsRootPath -PkgForgePathLabel 'source staging directory'

foreach ($existing in @(
             $PkgForgeReleaseVersionRootPath,
             $PkgForgeReleaseBinaryZipPath,
             $PkgForgeReleaseSourceZipPath,
             $PkgForgeReleaseManifestPath,
             $PkgForgeReleaseChecksumsPath
         )) {
    if (Test-Path -LiteralPath $existing) {
        throw "Release output already exists; refusing to overwrite: $existing"
    }
}

New-Item -ItemType Directory -Path $PkgForgeReleaseArtifactsRootPath -Force | Out-Null
New-Item -ItemType Directory -Path $PkgForgeReleaseOutputRootPath -Force | Out-Null
New-Item -ItemType Directory -Path $PkgForgeReleaseSourceRootPath -Force | Out-Null

$PkgForgeReleasePublishArguments = @(
    'publish',
    (Join-Path $PkgForgeReleaseProjectRootPath 'src/PkgForge.Cli/PkgForge.Cli.csproj'),
    '-c', 'Release',
    '-r', $Runtime,
    '-p:PublishAot=true',
    '-p:OptimizationPreference=Size',
    '-p:NativeDebugSymbols=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false',
    '-o', $PkgForgeReleasePublishDirectoryPath
)
Write-Host "[release] dotnet $($PkgForgeReleasePublishArguments -join ' ')"
& dotnet @PkgForgeReleasePublishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$PkgForgeReleasePublishedExecutablePath = Join-Path $PkgForgeReleasePublishDirectoryPath 'pkgforge.exe'
$PkgForgeReleasePublishedSqlitePath = Join-Path $PkgForgeReleasePublishDirectoryPath 'e_sqlite3.dll'
if (-not (Test-Path -LiteralPath $PkgForgeReleasePublishedExecutablePath -PathType Leaf)) {
    throw "AOT executable missing: $PkgForgeReleasePublishedExecutablePath"
}
if (-not (Test-Path -LiteralPath $PkgForgeReleasePublishedSqlitePath -PathType Leaf)) {
    throw "SQLite native library missing: $PkgForgeReleasePublishedSqlitePath"
}

$PkgForgeReleaseVersionOutput = & $PkgForgeReleasePublishedExecutablePath --version
if ($LASTEXITCODE -ne 0) {
    throw "AOT executable failed --version with exit code $LASTEXITCODE"
}
if (-not ($PkgForgeReleaseVersionOutput -eq "pkgforge $Version")) {
    throw "AOT executable version is '$PkgForgeReleaseVersionOutput', expected 'pkgforge $Version'"
}

$PkgForgeReleaseBinaryFiles = [ordered]@{
    'pkgforge.exe' = $PkgForgeReleasePublishedExecutablePath
    'e_sqlite3.dll' = $PkgForgeReleasePublishedSqlitePath
    'pkgforge.config.example.json' = (Join-Path $PkgForgeReleaseProjectRootPath 'pkgforge.config.example.json')
    'README.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'docs/RELEASE-README.md')
    'SECURITY.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'SECURITY.md')
    'LEGAL.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'LEGAL.md')
    'PRIVACY.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'PRIVACY.md')
    'NOTICE.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'NOTICE.md')
    'THIRD-PARTY-NOTICES.md' = (Join-Path $PkgForgeReleaseProjectRootPath 'THIRD-PARTY-NOTICES.md')
    'LICENSE' = (Join-Path $PkgForgeReleaseProjectRootPath 'LICENSE')
    'licenses/Apache-2.0.txt' = (Join-Path $PkgForgeReleaseProjectRootPath 'licenses/Apache-2.0.txt')
    'licenses/MIT-Microsoft.Data.Sqlite.txt' = (Join-Path $PkgForgeReleaseProjectRootPath 'licenses/MIT-Microsoft.Data.Sqlite.txt')
}

foreach ($relativePath in $PkgForgeReleaseBinaryFiles.Keys) {
    Copy-PkgForgeFile -SourcePath $PkgForgeReleaseBinaryFiles[$relativePath] `
        -DestinationPath (Join-Path $PkgForgeReleaseBinaryStagingDirectoryPath $relativePath)
}

$PkgForgeReleaseBinaryEntries = Get-PkgForgeHashEntries -RootPath $PkgForgeReleaseBinaryStagingDirectoryPath
Write-PkgForgeChecksums -Path (Join-Path $PkgForgeReleaseBinaryStagingDirectoryPath 'SHA256SUMS.txt') `
    -Entries $PkgForgeReleaseBinaryEntries
$PkgForgeReleaseBinaryManifest = [ordered]@{
    product = 'pkgforge'
    version = $Version
    runtime = $Runtime
    builtAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    license = 'AGPL-3.0-or-later'
    files = $PkgForgeReleaseBinaryEntries
}
$PkgForgeReleaseBinaryManifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $PkgForgeReleaseBinaryStagingDirectoryPath 'release-manifest.json') `
        -Encoding utf8NoBOM

New-Item -ItemType Directory -Path $PkgForgeReleaseVersionRootPath -Force | Out-Null
Move-Item -LiteralPath $PkgForgeReleaseBinaryStagingDirectoryPath -Destination $PkgForgeReleaseBinaryDirectoryPath
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PkgForgeReleaseBinaryDirectoryPath,
    $PkgForgeReleaseBinaryZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$PkgForgeReleaseRootFiles = @(
    'README.md',
    'LICENSE',
    'LEGAL.md',
    'PRIVACY.md',
    'NOTICE.md',
    'SECURITY.md',
    'THIRD-PARTY-NOTICES.md',
    'CHANGELOG.md',
    'Directory.Build.props',
    'global.json',
    'PkgForge.slnx',
    '.editorconfig',
    '.gitattributes',
    '.gitignore',
    'pkgforge.config.example.json'
)
$PkgForgeReleaseDocs = @(
    'docs/ARCHITECTURE.md',
    'docs/OPERATIONS.md',
    'docs/TEST-MATRIX.md',
    'docs/RELEASE-README.md'
)
$PkgForgeReleaseScripts = @(
    'scripts/build.ps1',
    'scripts/test.ps1',
    'scripts/release.ps1'
)

foreach ($relativePath in $PkgForgeReleaseRootFiles + $PkgForgeReleaseDocs + $PkgForgeReleaseScripts) {
    Copy-PkgForgeFile -SourcePath (Join-Path $PkgForgeReleaseProjectRootPath $relativePath) `
        -DestinationPath (Join-Path $PkgForgeReleaseSourceRootPath $relativePath)
}

foreach ($directory in @('src', 'tests', 'schemas', 'licenses')) {
    $sourceDirectory = Join-Path $PkgForgeReleaseProjectRootPath $directory
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        throw "Release source directory is missing: $sourceDirectory"
    }

    foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($PkgForgeReleaseProjectRootPath, $file.FullName)
        $segments = $relative.Split([System.IO.Path]::DirectorySeparatorChar)
        if ($segments -contains 'bin' -or $segments -contains 'obj') {
            continue
        }

        $extension = $file.Extension.ToLowerInvariant()
        $allowed = switch ($directory) {
            'src' { $extension -in @('.cs', '.csproj') }
            'tests' { $extension -in @('.cs', '.csproj') }
            'schemas' { $extension -in @('.sql', '.md') }
            'licenses' { $extension -eq '.txt' }
            default { $false }
        }
        if (-not $allowed) {
            continue
        }

        Copy-PkgForgeFile -SourcePath $file.FullName `
            -DestinationPath (Join-Path $PkgForgeReleaseSourceRootPath $relative.Replace('\', '/'))
    }
}

$PkgForgeReleaseSourceEntries = Get-PkgForgeHashEntries -RootPath $PkgForgeReleaseSourceRootPath
Write-PkgForgeChecksums -Path $PkgForgeReleaseSourceManifestPath -Entries $PkgForgeReleaseSourceEntries
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PkgForgeReleaseSourceStagingParentPath,
    $PkgForgeReleaseSourceZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$PkgForgeReleaseArtifactEntries = [System.Collections.Generic.List[object]]::new()
foreach ($artifactPath in @($PkgForgeReleaseBinaryZipPath, $PkgForgeReleaseSourceZipPath)) {
    $artifactFile = Get-Item -LiteralPath $artifactPath
    $PkgForgeReleaseArtifactEntries.Add([ordered]@{
        kind = if ($artifactPath -eq $PkgForgeReleaseBinaryZipPath) { 'binary' } else { 'source' }
        path = $artifactFile.Name
        size = $artifactFile.Length
        sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$PkgForgeReleaseRootManifest = [ordered]@{
    product = 'pkgforge'
    version = $Version
    runtime = $Runtime
    builtAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    license = 'AGPL-3.0-or-later'
    artifacts = $PkgForgeReleaseArtifactEntries
}
$PkgForgeReleaseRootManifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $PkgForgeReleaseManifestPath -Encoding utf8NoBOM
Write-PkgForgeChecksums -Path $PkgForgeReleaseChecksumsPath -Entries $PkgForgeReleaseArtifactEntries

Write-Host "[release] binary folder : $PkgForgeReleaseBinaryDirectoryPath"
Write-Host "[release] binary zip    : $PkgForgeReleaseBinaryZipPath"
Write-Host "[release] source zip    : $PkgForgeReleaseSourceZipPath"
Write-Host "[release] manifest      : $PkgForgeReleaseManifestPath"
Write-Host "[release] checksums     : $PkgForgeReleaseChecksumsPath"
Write-Host "[release] source files  : $($PkgForgeReleaseSourceEntries.Count)"
Write-Host "[release] binary exe    : $((Get-Item -LiteralPath (Join-Path $PkgForgeReleaseBinaryDirectoryPath 'pkgforge.exe')).Length) bytes"
