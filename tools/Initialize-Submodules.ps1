[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Environment', 'GitHubCli', 'GitCredentialManager')]
    [string] $CredentialSource = 'Auto',
    [string] $ConfigPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $root 'config\submodules.local.json'
}

function Read-LocalConfig {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Invalid submodule local config '$Path': $($_.Exception.Message)"
    }

    if ($config.credentialSource) {
        $allowed = @('Auto', 'Environment', 'GitHubCli', 'GitCredentialManager')
        if ($allowed -notcontains [string] $config.credentialSource) {
            throw "Unsupported credentialSource '$($config.credentialSource)' in '$Path'."
        }
    }

    return $config
}

$localConfig = Read-LocalConfig -Path $ConfigPath
if ($CredentialSource -eq 'Auto' -and $localConfig?.credentialSource) {
    $CredentialSource = [string] $localConfig.credentialSource
}

$tokenVariable = if ($localConfig?.tokenEnvironmentVariable) {
    [string] $localConfig.tokenEnvironmentVariable
} else {
    'NEXA_SUBMODULE_TOKEN'
}

function Get-GitHubCliToken {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        return $null
    }

    $token = (& gh auth token --hostname github.com 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        return $null
    }

    return ([string] $token).Trim()
}

$token = $null
switch ($CredentialSource) {
    'Environment' {
        $token = [Environment]::GetEnvironmentVariable($tokenVariable)
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "Environment variable '$tokenVariable' is empty. Set it for this process only; do not commit it."
        }
    }
    'GitHubCli' {
        $token = Get-GitHubCliToken
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "GitHub CLI is not authenticated. Run 'gh auth login --hostname github.com' first."
        }
    }
    'GitCredentialManager' {
        # Leave authentication to the configured Git credential helper.
    }
    'Auto' {
        $token = [Environment]::GetEnvironmentVariable($tokenVariable)
        if ([string]::IsNullOrWhiteSpace($token)) {
            $token = Get-GitHubCliToken
        }
        # If neither source is available, Git Credential Manager/SSH configuration is tried by Git.
    }
}

Push-Location $root
$savedConfig = @{}
try {
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        # Configure an ephemeral child-process-only HTTP header. The token is never written to
        # .git/config, the local config file, or command-line arguments.
        $tokenBytes = [Text.Encoding]::UTF8.GetBytes("x-access-token:$token")
        $authorization = [Convert]::ToBase64String($tokenBytes)
        foreach ($name in @('GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0')) {
            $savedConfig[$name] = [Environment]::GetEnvironmentVariable($name)
        }
        $env:GIT_CONFIG_COUNT = '1'
        $env:GIT_CONFIG_KEY_0 = 'http.extraHeader'
        $env:GIT_CONFIG_VALUE_0 = "AUTHORIZATION: basic $authorization"
        Write-Host "[submodules] using ephemeral token from $CredentialSource ($tokenVariable)."
    }
    else {
        Write-Host '[submodules] using the configured Git credential helper/SSH agent.'
    }

    & git submodule sync --recursive
    if ($LASTEXITCODE -ne 0) { throw "git submodule sync failed with exit code $LASTEXITCODE." }
    & git submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw "git submodule update failed with exit code $LASTEXITCODE." }
    Write-Host '[submodules] all configured submodules are initialized at the parent pins.' -ForegroundColor Green
}
finally {
    foreach ($name in @('GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0')) {
        [Environment]::SetEnvironmentVariable($name, $savedConfig[$name])
    }
    Pop-Location
}
