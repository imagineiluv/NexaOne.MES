#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw 'Version must be canonical semantic format without leading zeroes (for example, 1.0.0).'
}
if ($Configuration -cne 'Release') {
    throw "Release bundles require Configuration=Release; received '$Configuration'."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$releaseRoot = Join-Path $repositoryRoot "release\$Version"
$dllRoot = Join-Path $releaseRoot 'dll'
$artifactRoot = Join-Path $releaseRoot 'artifacts'
$bundleName = "NexaMES.$Version.zip"
$bundlePath = Join-Path $artifactRoot $bundleName
$publishRoot = Join-Path $env:TEMP ("nexames-release-publish-" + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $repositoryRoot 'src\00.Main\NexaOne.Server\NexaOne.Server.csproj'

function Assert-CleanSourceTree {
    param([Parameter(Mandatory = $true)][string]$Phase)

    $status = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the source tree during $Phase."
    }
    if ($status.Count -ne 0) {
        throw "Release source tree must be clean during $Phase. Commit or remove all changes before packaging."
    }
}

function Get-SubmodulePins {
    $pins = [ordered]@{}
    foreach ($name in @('NexaFramework', 'NexaDB', 'NexaLogic')) {
        $path = Join-Path $repositoryRoot "submodules\$name"
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Required submodule is missing: $name"
        }
        $commit = (& git -C $path rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
            throw "Unable to read the pinned commit for submodule $name."
        }
        $pins[$name] = $commit
    }
    return $pins
}

Assert-CleanSourceTree -Phase 'release preflight'
if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release output already exists; refusing to overwrite version '$Version': $releaseRoot"
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Server project was not found: $project"
}

try {
    Write-Host "Run publish smoke gate for version $Version."
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-Publish.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Test-Publish.ps1 failed; release bundle was not created.'
    }

    New-Item -ItemType Directory -Force -Path $dllRoot, $artifactRoot | Out-Null
    $publishArguments = @($project, '-c', $Configuration, '-o', $publishRoot, '--nologo', '-v', 'q')
    if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
        $publishArguments += @('-r', $Runtime)
    }
    & dotnet publish @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    $files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Force | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw 'Published output is empty.'
    }

    $managedDlls = @($files | Where-Object {
        $_.Extension -ceq '.dll' -and
        $_.Name -match '^(?:NexaOne\.|NexaFramework\.|NexaDB(?:\.|$)|NexaLogic\.)'
    })
    if ($managedDlls.Count -eq 0) {
        throw 'No first-party NexaMES/Framework/DB/Logic DLLs were found in publish output.'
    }
    $duplicateNames = @($managedDlls | Group-Object Name | Where-Object Count -gt 1)
    if ($duplicateNames.Count -ne 0) {
        throw "Publish output contains duplicate managed DLL names: $($duplicateNames.Name -join ', ')"
    }
    if ('NexaOne.Server.dll' -cnotin @($managedDlls.Name)) {
        throw 'Canonical publish output is missing NexaOne.Server.dll.'
    }

    foreach ($file in $managedDlls) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dllRoot $file.Name)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundleStream = [System.IO.File]::Open(
        $bundlePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $archive = [System.IO.Compression.ZipArchive]::new(
        $bundleStream,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $entryPath = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
            $entry = $archive.CreateEntry($entryPath, [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $bundleStream.Dispose()
    }

    $sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the MES source commit for release metadata.'
    }
    $submodulePins = Get-SubmodulePins
    $manifest = [ordered]@{
        product = 'NexaMES'
        version = $Version
        builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        configuration = $Configuration
        runtime = if ([string]::IsNullOrWhiteSpace($Runtime)) { 'framework-dependent' } else { $Runtime }
        commit = $sourceCommit
        runtimeProfile = 'Simulation'
        hardwareCommandsEnabled = $false
        submodules = $submodulePins
        bundle = [ordered]@{
            fileName = $bundleName
            fileSize = (Get-Item -LiteralPath $bundlePath).Length
            sha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
            path = "artifacts/$bundleName"
        }
        managedDlls = @($managedDlls | ForEach-Object {
            $managedPath = Join-Path $dllRoot $_.Name
            [ordered]@{
                fileName = $_.Name
                relativePath = "dll/$($_.Name)"
                bytes = (Get-Item -LiteralPath $managedPath).Length
                sha256 = (Get-FileHash -LiteralPath $managedPath -Algorithm SHA256).Hash
            }
        })
    } | ConvertTo-Json -Depth 6
    $manifestPath = Join-Path $releaseRoot 'release-manifest.json'
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8 -NoNewline

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Verify-ReleaseBundle.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw 'Verify-ReleaseBundle.ps1 failed; release bundle is not valid.'
    }

    Write-Host "[PASS] Release bundle saved: $bundlePath"
    Write-Host "[PASS] Manifest saved: $manifestPath"
    Write-Host ("Managed DLLs: {0}" -f $managedDlls.Count)
}
finally {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
