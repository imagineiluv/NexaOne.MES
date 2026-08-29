#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw 'Version must be canonical semantic format without leading zeroes.'
}

$root = (Resolve-Path $RepositoryRoot).Path
$releaseRoot = Join-Path $root "release\$Version"
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([string]$manifest.product -cne 'NexaMES' -or [string]$manifest.version -cne $Version) {
    throw 'Release manifest product or version does not match the requested version.'
}
if ([string]$manifest.configuration -cne 'Release' -or
    [string]$manifest.commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Release manifest configuration or source commit is invalid.'
}

function Resolve-ReleasePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Replace('\', '/') -match '(^|/)\.\.(/|$)') {
        throw "Release manifest contains an unsafe path: $RelativePath"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $releaseRoot $RelativePath))
    $prefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release manifest path escapes the version directory: $RelativePath"
    }
    return $full
}

$bundle = $manifest.bundle
$bundlePath = Resolve-ReleasePath ([string]$bundle.path)
if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {
    throw "Release bundle is missing: $bundlePath"
}
$bundleFile = Get-Item -LiteralPath $bundlePath
$bundleHash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
if ($bundleFile.Length -ne [long]$bundle.fileSize -or
    $bundleHash -cne [string]$bundle.sha256) {
    throw "Release bundle size or SHA-256 does not match the manifest: $bundlePath"
}

$managedDlls = @($manifest.managedDlls)
if ($managedDlls.Count -eq 0) {
    throw 'Release manifest does not contain managed DLL records.'
}
$seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $managedDlls) {
    $name = [string]$record.fileName
    if ([string]::IsNullOrWhiteSpace($name) -or -not $seenNames.Add($name)) {
        throw "Release manifest contains a duplicate or empty DLL name: $name"
    }
    $path = Resolve-ReleasePath ([string]$record.relativePath)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Managed DLL is missing: $path"
    }
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($file.Length -ne [long]$record.bytes -or $hash -cne [string]$record.sha256) {
        throw "Managed DLL size or SHA-256 does not match the manifest: $path"
    }
}

foreach ($name in @('NexaFramework', 'NexaDB', 'NexaLogic')) {
    $property = $manifest.submodules.PSObject.Properties[$name]
    if ($null -eq $property -or [string]$property.Value -notmatch '^[0-9a-f]{40}$') {
        throw "Release manifest is missing a valid submodule pin: $name"
    }
    $submodulePath = Join-Path $root "submodules\$name"
    if (Test-Path -LiteralPath $submodulePath -PathType Container) {
        $actual = (& git -C $submodulePath rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -cne [string]$property.Value) {
        throw "Submodule pin mismatch for ${name}: expected=$($property.Value), actual=$actual"
        }
    }
}

Write-Host ("[PASS] Release $Version verified: bundle SHA-256, {0} managed DLLs and submodule pins." -f $managedDlls.Count)
