# run-dev.ps1 — 외부 DB 없이 디자인 확인용 개발 실행.
# API(NexaOne.API, http://localhost:5181)를 SQLite로, SPA(NexaOne.Spa, http://localhost:5173)를 Vite dev로 띄운다.
# 빈 DB면 API가 스키마를 부트스트랩하고 기본 관리자(admin/admin)를 시드한다(V001). 브라우저에서 http://localhost:5173.
# 종료: 이 창에서 Ctrl+C (SPA 종료 시 API도 함께 정리).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$api  = Join-Path $root 'src\02.Backend\NexaOne.API'
$spa  = Join-Path $root 'src\01.Web\NexaOne.Spa'

$env:ASPNETCORE_ENVIRONMENT     = 'Development'
$env:ASPNETCORE_URLS            = 'http://localhost:5181'
$env:Database__Provider         = 'Sqlite'
$env:ConnectionStrings__NexaOne = "Data Source=$($api -replace '\\','/')/nexaone_dev.db;Foreign Keys=False"
$env:Jwt__SecretKey             = 'local-dev-design-check-secret-key-0123456789-abcd'
$env:RateLimiting__Enabled      = 'false'   # 디자인 확인 중 다수 요청이 429에 막히지 않도록 dev 한정 해제

Write-Host '[run-dev] API 기동: http://localhost:5181 (SQLite, admin/admin)' -ForegroundColor Cyan
$apiProc = Start-Process dotnet -ArgumentList @('run','--project', $api, '--no-launch-profile') -PassThru -WindowStyle Minimized
try {
    Write-Host '[run-dev] SPA 기동: http://localhost:5173  ← 브라우저에서 열기 (로그인 admin / admin)' -ForegroundColor Green
    Push-Location $spa
    if (-not (Test-Path 'node_modules')) { npm install }
    npm run dev   # 포그라운드 — Ctrl+C로 종료
}
finally {
    Pop-Location
    if ($apiProc -and -not $apiProc.HasExited) { $apiProc.Kill(); $apiProc.WaitForExit() }
    Write-Host '[run-dev] 종료(API/SPA 정리 완료).' -ForegroundColor Yellow
}
