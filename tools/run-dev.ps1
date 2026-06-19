# run-dev.ps1 — 외부 DB 없이 디자인 확인용 개발 실행.
# API(NexaOne.API, http://localhost:5181)를 SQLite로, Blazor 웹(NexaOne.Web, http://localhost:5180)을 디자인 클라이언트로 띄운다.
# 빈 DB면 API가 스키마를 부트스트랩하고 기본 관리자(admin/admin)를 시드한다(V001). 브라우저에서 http://localhost:5180 → 로그인 admin/admin.
# (React SPA 골격을 보려면: cd src/01.Web/NexaOne.Spa; npm run dev → http://localhost:5173/spa/)
# 종료: 이 창에서 Ctrl+C (웹 종료 시 API도 함께 정리).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$api  = Join-Path $root 'src\02.Backend\NexaOne.API'
$web  = Join-Path $root 'src\01.Web\NexaOne.Web'

# --- API (서버, SQLite) ---
$apiEnv = @{
    ASPNETCORE_ENVIRONMENT     = 'Development'
    ASPNETCORE_URLS            = 'http://localhost:5181'
    Database__Provider         = 'Sqlite'
    ConnectionStrings__NexaOne = "Data Source=$($api -replace '\\','/')/nexaone_dev.db;Foreign Keys=False"
    Jwt__SecretKey             = 'local-dev-design-check-secret-key-0123456789-abcd'
    RateLimiting__Enabled      = 'false'
}
foreach ($k in $apiEnv.Keys) { Set-Item "env:$k" $apiEnv[$k] }

Write-Host '[run-dev] API 기동: http://localhost:5181 (SQLite, admin/admin)' -ForegroundColor Cyan
$apiProc = Start-Process dotnet -ArgumentList @('run','--project', $api, '--no-launch-profile') -PassThru -WindowStyle Minimized

try {
    # --- Blazor 웹 (디자인 클라이언트) — API로 ApiBaseUrl 지정 ---
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS        = 'http://localhost:5180'
    $env:ApiBaseUrl             = 'http://localhost:5181'
    Write-Host '[run-dev] Blazor 웹 기동: http://localhost:5180  ← 브라우저에서 열기 (로그인 admin / admin)' -ForegroundColor Green
    dotnet run --project $web --no-launch-profile   # 포그라운드 — Ctrl+C로 종료
}
finally {
    if ($apiProc -and -not $apiProc.HasExited) { $apiProc.Kill(); $apiProc.WaitForExit() }
    Write-Host '[run-dev] 종료(API/웹 정리 완료).' -ForegroundColor Yellow
}
