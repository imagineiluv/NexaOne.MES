[CmdletBinding()]
param(
    [string] $ReferenceRoot,
    [string] $SnapshotPath = (Join-Path $PSScriptRoot '..\..\test\contract\legacy-route-source.tsv'),
    [switch] $Update
)

# The tracked TSV contains only route declarations. Request/result semantics and replacement
# status require separate review; the external Java reference is optional for CI.
# Run with -ReferenceRoot <legacy-source-root> to check drift; add -Update only for an approved refresh.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-SnapshotText {
    param([string[]] $Rows)

    $sortedRows = [string[]] @($Rows)
    [Array]::Sort($sortedRows, [StringComparer]::Ordinal)
    return "sourcePath`tbundle`trouteId`n$([string]::Join("`n", $sortedRows))`n"
}

function Assert-Snapshot {
    param([string] $Content)

    if ($Content.Contains("`r") -or -not $Content.EndsWith("`n", [StringComparison]::Ordinal)) {
        throw 'Legacy route snapshot must use LF line endings and end with one newline.'
    }

    $lines = $Content.Substring(0, $Content.Length - 1).Split("`n")
    if ($lines[0] -cne "sourcePath`tbundle`trouteId") {
        throw 'Legacy route snapshot header is invalid.'
    }

    $rows = [string[]] @($lines | Select-Object -Skip 1)
    if ($rows.Length -ne 521) {
        throw "Legacy route snapshot must contain 521 declarations; found $($rows.Length)."
    }

    $declarations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $routes = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $columns = $row.Split("`t")
        if ($columns.Length -ne 3) {
            throw "Legacy route snapshot row must have three columns: '$row'."
        }

        $sourcePath, $bundle, $routeId = $columns
        if (($sourcePath -cnotmatch '^s-[^/]+/(?:[^/]+/)*[^/]+\.java$') -or
            $sourcePath.Contains('..') -or
            ($bundle -cne $sourcePath.Split('/')[0]) -or
            [string]::IsNullOrWhiteSpace($routeId)) {
            throw "Legacy route snapshot row is invalid: '$row'."
        }
        if (-not $declarations.Add("$sourcePath`t$routeId")) {
            throw "Legacy route declaration is duplicated: '$sourcePath' / '$routeId'."
        }

        if (-not $routes.ContainsKey($routeId)) {
            $routes.Add($routeId, [System.Collections.Generic.List[string]]::new())
        }
        $routes[$routeId].Add($bundle)
    }

    $knownAliases = [System.Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    $knownAliases.Add('/factory/3.2/core/messagetest', [string[]] @(
        's-component-factory.core', 's-component-qms.core'))
    $knownAliases.Add('/factory/system/socreator', [string[]] @(
        's-component-ees.core', 's-component-factory.core', 's-component-qms.core'))
    foreach ($routeId in $routes.Keys) {
        $actualBundles = [string[]] $routes[$routeId].ToArray()
        if ($actualBundles.Length -gt 1) {
            if (-not $knownAliases.ContainsKey($routeId)) {
                throw "Unexpected duplicate legacy route '$routeId' ($($actualBundles.Length) declarations)."
            }
            [Array]::Sort($actualBundles, [StringComparer]::Ordinal)
            if ([string]::Join("`n", $actualBundles) -cne [string]::Join("`n", $knownAliases[$routeId])) {
                throw "Known legacy alias '$routeId' has an unexpected source bundle scope."
            }
        }
    }
    foreach ($routeId in $knownAliases.Keys) {
        if (-not $routes.ContainsKey($routeId) -or $routes[$routeId].Count -ne $knownAliases[$routeId].Length) {
            throw "Known legacy alias '$routeId' is missing or changed."
        }
    }

    if ((New-SnapshotText $rows) -cne $Content) {
        throw 'Legacy route snapshot is not in deterministic source-path order.'
    }
}

function Get-SourceRows {
    param([string] $Root)

    if (-not [IO.Directory]::Exists($Root)) {
        throw "Legacy reference source directory does not exist: '$Root'."
    }

    $rows = [System.Collections.Generic.List[string]]::new()
    $bundles = Get-ChildItem -LiteralPath $Root -Directory | Where-Object Name -Like 's-*'
    foreach ($bundle in $bundles) {
        foreach ($file in (Get-ChildItem -LiteralPath $bundle.FullName -Recurse -File -Filter '*.java')) {
            $source = [IO.File]::ReadAllText($file.FullName)
            $annotationCount = [regex]::Matches($source, '(?m)^[ \t]*@AComponent\s*\(').Count
            if ($annotationCount -eq 0) {
                continue
            }

            $annotations = [regex]::Matches($source, '(?m)^[ \t]*@AComponent\s*\(\s*name\s*=\s*"(?<id>[^"]+)"\s*\)')
            if ($annotations.Count -ne $annotationCount) {
                throw "Unsupported @AComponent declaration in '$($file.FullName)'."
            }

            $sourcePath = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            foreach ($annotation in $annotations) {
                $rows.Add("$sourcePath`t$($bundle.Name)`t$($annotation.Groups['id'].Value)")
            }
        }
    }

    return [string[]] $rows.ToArray()
}

if ($Update -and [string]::IsNullOrWhiteSpace($ReferenceRoot)) {
    throw '-Update requires -ReferenceRoot. The original Java source is never committed by this script.'
}

$resolvedSnapshotPath = [IO.Path]::GetFullPath($SnapshotPath)
if ($Update) {
    $sourceText = New-SnapshotText (Get-SourceRows ([IO.Path]::GetFullPath($ReferenceRoot)))
    Assert-Snapshot $sourceText
    [IO.File]::WriteAllText($resolvedSnapshotPath, $sourceText, [Text.UTF8Encoding]::new($false))
}

if (-not [IO.File]::Exists($resolvedSnapshotPath)) {
    throw "Legacy route snapshot does not exist: '$resolvedSnapshotPath'."
}

$snapshotText = [IO.File]::ReadAllText($resolvedSnapshotPath, [Text.UTF8Encoding]::new($false, $true))
# Git may check out text files as CRLF on Windows. Compare canonical LF content.
$snapshotText = $snapshotText.Replace("`r`n", "`n")
Assert-Snapshot $snapshotText

if (-not [string]::IsNullOrWhiteSpace($ReferenceRoot)) {
    $sourceText = New-SnapshotText (Get-SourceRows ([IO.Path]::GetFullPath($ReferenceRoot)))
    Assert-Snapshot $sourceText
    if ($sourceText -cne $snapshotText) {
        throw 'Legacy reference @AComponent declarations differ from the approved snapshot.'
    }
    Write-Host 'Legacy route source snapshot matches the supplied Java reference.'
} else {
    Write-Host 'Legacy route source snapshot structure verified; Java drift check requires -ReferenceRoot.'
}
