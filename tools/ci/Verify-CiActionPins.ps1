[CmdletBinding()]
param(
    [string] $WorkflowPath = (Join-Path $PSScriptRoot '..\..\.github\workflows\ci.yml')
)

$ErrorActionPreference = 'Stop'
$resolvedWorkflowPath = (Resolve-Path -LiteralPath $WorkflowPath).Path
$workflow = (Get-Content -LiteralPath $resolvedWorkflowPath -Raw).Replace("`r`n", "`n")

$requiredPins = @{
    'actions/checkout' = [pscustomobject]@{
        Ref = '3d3c42e5aac5ba805825da76410c181273ba90b1'
        Comment = 'v7.0.1, Node 24'
        Count = 2
    }
    'actions/setup-dotnet' = [pscustomobject]@{
        Ref = 'a98b56852c35b8e3190ac28c8c2271da59106c68'
        Comment = 'v6.0.0, Node 24'
        Count = 2
    }
    'actions/setup-node' = [pscustomobject]@{
        Ref = '820762786026740c76f36085b0efc47a31fe5020'
        Comment = 'v7.0.0, Node 24'
        Count = 1
    }
    'actions/upload-artifact' = [pscustomobject]@{
        Ref = '043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'
        Comment = 'v7.0.1, Node 24'
        Count = 2
    }
}

$actionLines = [System.Text.RegularExpressions.Regex]::Matches(
    $workflow,
    '(?m)^\s*uses:\s+(?<action>actions/[A-Za-z0-9._-]+)@(?<ref>[^\s#]+)(?:\s+#\s*(?<comment>[^\n]+))?\s*$')
$officialActionLineCount = [System.Text.RegularExpressions.Regex]::Matches(
    $workflow,
    '(?m)^\s*uses:\s+actions/').Count

if ($actionLines.Count -ne $officialActionLineCount) {
    throw 'The CI workflow contains an unparseable official action reference.'
}

foreach ($actionLine in $actionLines) {
    $action = $actionLine.Groups['action'].Value
    if (-not $requiredPins.ContainsKey($action)) {
        throw "The CI workflow uses an unapproved official action: $action."
    }

    $requiredPin = $requiredPins[$action]
    $actualRef = $actionLine.Groups['ref'].Value
    $actualComment = $actionLine.Groups['comment'].Value.Trim()
    if ($actualRef -cne $requiredPin.Ref) {
        throw "$action must use immutable commit $($requiredPin.Ref); actual ref is '$actualRef'."
    }
    if ($actualComment -cne $requiredPin.Comment) {
        throw "$action must document '# $($requiredPin.Comment)'; actual comment is '$actualComment'."
    }
}

foreach ($action in $requiredPins.Keys) {
    $actualCount = @($actionLines | Where-Object { $_.Groups['action'].Value -ceq $action }).Count
    $expectedCount = $requiredPins[$action].Count
    if ($actualCount -ne $expectedCount) {
        throw "$action must appear exactly $expectedCount time(s); actual count is $actualCount."
    }
}

Write-Host 'CI action pins verified.'
