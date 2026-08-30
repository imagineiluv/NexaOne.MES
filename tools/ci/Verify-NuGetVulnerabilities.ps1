[CmdletBinding()]
param(
    [string]$SolutionPath = "NexaOne.sln",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$resolvedSolution = (Resolve-Path (Join-Path $repoRoot $SolutionPath)).Path

if (-not $NoRestore) {
    $restoreOutput = @(& dotnet restore $resolvedSolution 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet restore failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$($restoreOutput -join [Environment]::NewLine)"
    }
}

$rawOutput = @(& dotnet list $resolvedSolution package `
    --vulnerable `
    --include-transitive `
    --format json `
    --output-version 1 2>&1 | ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability scan failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$($rawOutput -join [Environment]::NewLine)"
}

try {
    $report = ($rawOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
}
catch {
    throw "NuGet vulnerability scan returned invalid JSON: $($_.Exception.Message)"
}

$findings = [System.Collections.Generic.List[object]]::new()
foreach ($project in @($report.projects)) {
    $frameworksProperty = $project.PSObject.Properties["frameworks"]
    if ($null -eq $frameworksProperty) {
        continue
    }

    foreach ($framework in @($frameworksProperty.Value)) {
        $packages = @()
        foreach ($packagePropertyName in @("topLevelPackages", "transitivePackages")) {
            $packageProperty = $framework.PSObject.Properties[$packagePropertyName]
            if ($null -ne $packageProperty) {
                $packages += @($packageProperty.Value)
            }
        }

        foreach ($package in $packages) {
            $vulnerabilitiesProperty = $package.PSObject.Properties["vulnerabilities"]
            if ($null -eq $vulnerabilitiesProperty) {
                continue
            }

            foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                $findings.Add([pscustomobject]@{
                    Project = [string]$project.path
                    Framework = [string]$framework.framework
                    Package = [string]$package.id
                    Version = [string]$package.resolvedVersion
                    Severity = [string]$vulnerability.severity
                    Advisory = [string]$vulnerability.advisoryurl
                })
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $details = $findings | ForEach-Object {
        "$($_.Project) [$($_.Framework)] $($_.Package) $($_.Version) $($_.Severity) $($_.Advisory)"
    }
    throw "Vulnerable NuGet dependencies detected ($($findings.Count)).$([Environment]::NewLine)$($details -join [Environment]::NewLine)"
}

Write-Host "NuGet vulnerability gate passed for $(@($report.projects).Count) projects."
