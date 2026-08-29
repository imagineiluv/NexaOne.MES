# run-dev.ps1 — 통합 NexaOne.Server와 React Portal 개발 서버를 함께 실행한다.
# 사용자 단일 진입점(Server): http://localhost:5173 (SQLite, admin/admin)
# Portal HMR(개발 전용): http://localhost:5174 — API/SignalR은 Server 5173으로 프록시
# 종료: 이 창에서 Ctrl+C
$ErrorActionPreference = 'Stop'

$root          = Split-Path $PSScriptRoot -Parent
$serverDir     = Join-Path $root 'src\00.Main\NexaOne.Server'
$serverProject = Join-Path $serverDir 'NexaOne.Server.csproj'
$serverOutput  = Join-Path $serverDir 'bin\Debug\net8.0'
$serverDll     = Join-Path $serverOutput 'NexaOne.Server.dll'
$portalDir     = Join-Path $serverDir 'ClientApps\Portal'
$databasePath  = Join-Path $serverDir 'nexaone_dev.db'
$serverLogDir  = Join-Path ([System.IO.Path]::GetTempPath()) 'NexaOne\run-dev'
$runStamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
$serverOutLog  = Join-Path $serverLogDir "server-$runStamp.out.log"
$serverErrLog  = Join-Path $serverLogDir "server-$runStamp.err.log"

if (-not (Test-Path -LiteralPath $serverProject)) { throw "Server project not found: $serverProject" }
if (-not (Test-Path -LiteralPath (Join-Path $portalDir 'package.json'))) { throw "Portal client not found: $portalDir" }

$serverEnv = @{
    ASPNETCORE_ENVIRONMENT                 = 'Development'
    ASPNETCORE_URLS                        = 'http://localhost:5173'
    Database__Provider                     = 'Sqlite'
    ConnectionStrings__NexaOne             = "Data Source=$($databasePath -replace '\\','/');Foreign Keys=False"
    Server__SpringConfig                   = 'config/host/server.sqlite.xml'
    Server__Port                           = '5173'
    ApiBaseUrl                             = 'http://localhost:5173/'
    Jwt__SecretKey                         = 'local-dev-design-check-secret-key-0123456789-abcd'
    Jwt__Issuer                            = 'nexaone-local-dev'
    Jwt__Audience                          = 'nexaone-local-dev'
    RateLimiting__Enabled                  = 'false'
    Events__Outbox__Enabled                = 'true'
    Worker__Sys__BatchProcess__Enabled     = 'true'
    Worker__Fdc__VirtualEvent__Enabled     = 'true'
    Worker__Outbox__Dispatch__Enabled      = 'true'
}
foreach ($key in $serverEnv.Keys) { Set-Item "env:$key" $serverEnv[$key] }

Write-Host '[run-dev] Server + Portal 번들 빌드' -ForegroundColor DarkCyan
& dotnet build $serverProject '-p:BuildSpa=true'
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $serverDll)) { throw "Server output not found: $serverDll" }

New-Item -ItemType Directory -Path $serverLogDir -Force | Out-Null
Write-Host '[run-dev] NexaOne.Server 기동: http://localhost:5173 (SQLite, admin/admin)' -ForegroundColor Cyan
$serverProc = Start-Process dotnet `
    -ArgumentList @($serverDll) `
    -WorkingDirectory $serverOutput `
    -PassThru `
    -RedirectStandardOutput $serverOutLog `
    -RedirectStandardError $serverErrLog `
    -WindowStyle Hidden

try {
    $serverReady = $false
    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        if ($serverProc.HasExited) {
            $logTail = @(
                Get-Content -LiteralPath $serverOutLog -Tail 40 -ErrorAction SilentlyContinue
                Get-Content -LiteralPath $serverErrLog -Tail 40 -ErrorAction SilentlyContinue
            ) -join [Environment]::NewLine
            throw "NexaOne.Server exited with code $($serverProc.ExitCode).$([Environment]::NewLine)$logTail"
        }
        try {
            Invoke-WebRequest -Uri 'http://localhost:5173/health' -UseBasicParsing -TimeoutSec 1 | Out-Null
            $serverReady = $true
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $serverReady) { throw 'NexaOne.Server did not become healthy within 120 seconds.' }
    Write-Host "[run-dev] Server 로그: $serverOutLog" -ForegroundColor DarkGray

    Push-Location $portalDir
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $portalDir 'node_modules'))) {
            Write-Host '[run-dev] Portal 의존성 설치(npm ci)' -ForegroundColor DarkCyan
            & npm.cmd ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
        }

        $env:VITE_API_PROXY = 'http://localhost:5173'
        Write-Host '[run-dev] 통합 테스트 URL: http://localhost:5173/ | /Designer | /Mobile | /POP' -ForegroundColor Green
        Write-Host '[run-dev] Portal HMR 기동: http://localhost:5174/' -ForegroundColor DarkGreen
        & npm.cmd run dev
        if ($LASTEXITCODE -ne 0) { throw "npm run dev failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) {
        $serverProc.Kill()
        $serverProc.WaitForExit()
    }
    Write-Host '[run-dev] 종료(Server 정리 완료)' -ForegroundColor Yellow
}
