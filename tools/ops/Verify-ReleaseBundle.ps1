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
$packagingProfileProperty = $manifest.PSObject.Properties['packagingProfile']
if ($null -eq $packagingProfileProperty) {
    # Manifests produced before product profiles existed did not carry this field; those releases
    # were all built from the Cleaner catalog. Keep their hash verification reproducible while
    # requiring every newly-published manifest to persist the explicit profile.
    $packagingProfile = 'Cleaner'
    $legacyPackagingProfile = $true
}
else {
    $packagingProfile = [string]$packagingProfileProperty.Value
    $legacyPackagingProfile = $false
    if ($packagingProfile -cnotmatch '^[A-Za-z][A-Za-z0-9._-]*$') {
        throw 'Release manifest packagingProfile is invalid.'
    }
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

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([long]$Entry.Length -gt $MaximumBytes) {
        throw "Release bundle $Description exceeds the $MaximumBytes-byte verification limit."
    }

    $stream = $Entry.Open()
    $reader = [IO.StreamReader]::new(
        $stream,
        [Text.UTF8Encoding]::new($false, $true),
        $true,
        4096,
        $false)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-CurrentProductProfileBundle {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$ExpectedProfile
    )

    # The release bundle hash binds these files to release-manifest.json. Validate the catalog and
    # runtime file-set in-place so packagingProfile cannot be a truthful-looking but unrelated label.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundleStream = [IO.File]::Open(
        $BundlePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $archive = $null
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $bundleStream,
            [IO.Compression.ZipArchiveMode]::Read,
            $false)
        $fileEntries = [Collections.Generic.List[object]]::new()
        $seenEntryPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $entryPath = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($entryPath)) {
                throw 'Release bundle contains an empty ZIP entry path.'
            }
            if ($entryPath.EndsWith('/', [StringComparison]::Ordinal)) {
                continue
            }
            if ($entryPath.Contains('\') -or
                [IO.Path]::IsPathRooted($entryPath) -or
                $entryPath -match '[\x00-\x1f]' -or
                @($entryPath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
                throw "Release bundle contains an unsafe ZIP entry path: $entryPath"
            }
            if (-not $seenEntryPaths.Add($entryPath)) {
                throw "Release bundle contains a duplicate or case-colliding ZIP entry path: $entryPath"
            }
            $fileEntries.Add($entry)
        }

        $catalogPath = 'config/product-profile.manifest'
        $catalogEntries = @($fileEntries | Where-Object { [string]$_.FullName -ceq $catalogPath })
        if ($catalogEntries.Count -ne 1) {
            throw "Current-profile release bundle must contain exactly one $catalogPath entry."
        }
        $catalogText = Read-ZipEntryText -Entry $catalogEntries[0] -MaximumBytes 131072 -Description $catalogPath
        $catalogLines = @($catalogText -split "\r\n|\n|\r" |
            Where-Object { -not [string]::IsNullOrEmpty($_) })
        if ($catalogLines.Count -eq 0 -or
            @($catalogLines | Where-Object { $_ -cne $_.Trim() }).Count -ne 0) {
            throw 'Release bundle product profile catalog is empty or contains non-canonical whitespace.'
        }
        $unknownLines = @($catalogLines | Where-Object {
            $_ -cne 'FormatVersion=1' -and
            $_ -cne 'ApplicationManifest=config/app.xml' -and
            -not $_.StartsWith('Profile=', [StringComparison]::Ordinal) -and
            -not $_.StartsWith('Plugin=', [StringComparison]::Ordinal)
        })
        if ($unknownLines.Count -ne 0) {
            throw "Release bundle product profile catalog contains unsupported records: $($unknownLines -join ', ')"
        }
        if (@($catalogLines | Where-Object { $_ -ceq 'FormatVersion=1' }).Count -ne 1) {
            throw 'Release bundle product profile catalog format is missing, duplicated, or unsupported.'
        }

        $profileLines = @($catalogLines | Where-Object {
            $_.StartsWith('Profile=', [StringComparison]::Ordinal)
        })
        if ($profileLines.Count -ne 1) {
            throw 'Release bundle product profile catalog must declare exactly one Profile record.'
        }
        $bundleProfile = $profileLines[0].Substring('Profile='.Length)
        if ($bundleProfile -cne $ExpectedProfile) {
            throw "Release manifest packagingProfile does not match the bundle product profile: manifest=$ExpectedProfile, bundle=$bundleProfile"
        }

        $applicationManifestLines = @($catalogLines | Where-Object {
            $_.StartsWith('ApplicationManifest=', [StringComparison]::Ordinal)
        })
        if ($applicationManifestLines.Count -ne 1 -or
            $applicationManifestLines[0] -cne 'ApplicationManifest=config/app.xml') {
            throw 'Release bundle product profile catalog must select config/app.xml exactly once.'
        }

        $pluginLines = @($catalogLines | Where-Object {
            $_.StartsWith('Plugin=', [StringComparison]::Ordinal)
        })
        if ($pluginLines.Count -eq 0) {
            throw 'Release bundle product profile catalog does not declare any plugins.'
        }
        $plugins = [Collections.Generic.List[string]]::new()
        $seenPlugins = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($pluginLine in $pluginLines) {
            $plugin = $pluginLine.Substring('Plugin='.Length)
            if ($plugin -cnotmatch '^NexaOne\.[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$' -or
                -not $seenPlugins.Add($plugin)) {
                throw "Release bundle product profile catalog contains an invalid or duplicate plugin: $plugin"
            }
            $plugins.Add($plugin)
        }

        $expectedModulePaths = @($plugins |
            ForEach-Object { "Modules/$_.dll" } |
            Sort-Object -CaseSensitive)
        $actualModulePaths = @($fileEntries |
            Where-Object { ([string]$_.FullName).StartsWith('Modules/', [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { [string]$_.FullName } |
            Sort-Object -CaseSensitive)
        if (($expectedModulePaths -join "`n") -cne ($actualModulePaths -join "`n")) {
            throw ("Release bundle Modules file-set does not match the product profile catalog: expected=[{0}], actual=[{1}]" -f
                ($expectedModulePaths -join ','), ($actualModulePaths -join ','))
        }

        $applicationEntries = @($fileEntries | Where-Object {
            [string]$_.FullName -ceq 'config/app.xml'
        })
        if ($applicationEntries.Count -ne 1) {
            throw 'Current-profile release bundle must contain exactly one config/app.xml entry.'
        }
        $applicationText = Read-ZipEntryText -Entry $applicationEntries[0] -MaximumBytes 1048576 -Description 'config/app.xml'
        if ($applicationText -match '<!DOCTYPE') {
            throw 'Release bundle config/app.xml must not contain a document type declaration.'
        }
        $application = [Xml.XmlDocument]::new()
        $application.XmlResolver = $null
        try {
            $application.LoadXml($applicationText)
        }
        catch {
            throw "Release bundle config/app.xml is invalid XML: $($_.Exception.Message)"
        }

        $manifestPluginFiles = [Collections.Generic.List[string]]::new()
        $seenManifestPluginFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($service in @($application.SelectNodes('/Application/Services/Service'))) {
            $classPaths = [string]$service.GetAttribute('classPaths')
            foreach ($classPath in $classPaths.Split(
                ';',
                [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)) {
                if ($classPath -cnotmatch '^\./Modules/(?<FileName>NexaOne\.[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*\.dll)$') {
                    throw "Release bundle config/app.xml contains a non-canonical plugin classPath: $classPath"
                }
                $fileName = [string]$Matches.FileName
                if (-not $seenManifestPluginFiles.Add($fileName)) {
                    throw "Release bundle config/app.xml contains a duplicate plugin classPath: $fileName"
                }
                $manifestPluginFiles.Add($fileName)
            }
        }
        $expectedModuleFiles = @($plugins | ForEach-Object { "$_.dll" } | Sort-Object -CaseSensitive)
        $actualManifestPluginFiles = @($manifestPluginFiles | Sort-Object -CaseSensitive)
        if (($expectedModuleFiles -join "`n") -cne ($actualManifestPluginFiles -join "`n")) {
            throw ("Release bundle config/app.xml plugin set does not match the product profile catalog: expected=[{0}], actual=[{1}]" -f
                ($expectedModuleFiles -join ','), ($actualManifestPluginFiles -join ','))
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        $bundleStream.Dispose()
    }
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
if (-not $legacyPackagingProfile) {
    Assert-CurrentProductProfileBundle -BundlePath $bundlePath -ExpectedProfile $packagingProfile
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

$profileDescription = if ($legacyPackagingProfile) {
    "$packagingProfile (legacy manifest default)"
}
else {
    $packagingProfile
}
Write-Host ("[PASS] Release $Version verified: profile {0}, bundle SHA-256, {1} managed DLLs and submodule pins." -f $profileDescription, $managedDlls.Count)
