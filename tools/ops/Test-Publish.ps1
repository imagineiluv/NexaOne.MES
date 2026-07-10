# publish 산출물 자동 스모크 — 게시→구성 검사→단독 부팅→/health→로그인까지 무인 검증.
# 배경: 모듈 Copy 누락(실사고 2026-07-09)류의 '조용한 게시 회귀'는 build 출력 부팅 스모크로는 안 잡힌다.
# 사용: .\Test-Publish.ps1            (임시 폴더에 게시·검증 후 정리)
#       .\Test-Publish.ps1 -KeepOutput  (산출물 보존 — 수동 확인용)
# 종료코드: 0=전 단계 통과, 1=실패(실패 단계 메시지 출력). CI 편입 가능 형태.
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\NexaOne.Server.csproj'),
    [int]$Port = 8098,
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $env:TEMP ("nexaone-publish-smoke-" + [Guid]::NewGuid().ToString('N'))
$db  = Join-Path $env:TEMP ("nexaone-publish-smoke-" + [Guid]::NewGuid().ToString('N') + ".db")
$proc = $null
$failed = $null

function Step([string]$name, [scriptblock]$body) {
    Write-Host ("[step] " + $name)
    & $body
}

try {
    Step 'dotnet publish (Release)' {
        dotnet publish $script:Project -c Release -o $script:out --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }
    }

    Step '산출물 구성 검사' {
        $modules = @(Get-ChildItem (Join-Path $script:out 'Modules') -Filter 'NexaOne.*.dll' -ErrorAction SilentlyContinue)
        if ($modules.Count -lt 9) { throw ("Modules/ 플러그인 부족: {0}/9 — 게시 Copy 타깃 회귀(2026-07-09 실사고 부류)" -f $modules.Count) }
        foreach ($p in 'wwwroot\spa\index.html', 'wwwroot\css\nexaone.css', 'config\host\server.sqlite.xml', 'db\migrations') {
            if (-not (Test-Path (Join-Path $script:out $p))) { throw ("산출물 누락: " + $p) }
        }
        Write-Host ("  Modules {0}개, spa/css/config/db 확인" -f $modules.Count)
    }

    Step '게시본 단독 부팅(SQLite, modules ON)' {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'dotnet'
        $psi.Arguments = '"' + (Join-Path $script:out 'NexaOne.Server.dll') + '" --urls http://localhost:' + $script:Port
        $psi.WorkingDirectory = $script:out
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'   # dev 시드(admin)로 로그인 검증
        $psi.EnvironmentVariables['Server__Modules__Enabled'] = 'true'
        $psi.EnvironmentVariables['Server__SpringConfig'] = 'config/host/server.sqlite.xml'
        $psi.EnvironmentVariables['Database__Provider'] = 'Sqlite'
        $psi.EnvironmentVariables['ConnectionStrings__NexaOne'] = 'Data Source=' + $script:db + ';Foreign Keys=False'
        $psi.EnvironmentVariables['Jwt__SecretKey'] = 'publish-smoke-jwt-secret-key-32bytes!!!'
        $psi.EnvironmentVariables['Jwt__Issuer'] = 'nexaone-publish-smoke'
        $psi.EnvironmentVariables['Jwt__Audience'] = 'nexaone-publish-smoke'
        $psi.EnvironmentVariables['RateLimiting__Enabled'] = 'false'
        $psi.EnvironmentVariables['ApiBaseUrl'] = 'http://localhost:' + $script:Port + '/'
        $script:proc = [System.Diagnostics.Process]::Start($psi)
    }

    Step '/health 대기(최대 120초)' {
        $ok = $false
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Seconds 3
            if ($script:proc.HasExited) { throw ("서버 조기 종료(exit " + $script:proc.ExitCode + ") — 게시본 부팅 실패") }
            try {
                $r = Invoke-WebRequest -Uri ("http://localhost:" + $script:Port + "/health") -UseBasicParsing -TimeoutSec 3
                if ($r.StatusCode -eq 200) { $ok = $true; break }
            } catch { }
        }
        if (-not $ok) { throw '/health 200 미도달(120초)' }
        Write-Host '  health OK'
    }

    Step '로그인(JWT 발급) 검증' {
        $body = '{"userId":"admin","password":"admin"}'
        $r = Invoke-WebRequest -Uri ("http://localhost:" + $script:Port + "/api/v1/auth/login") -Method Post `
            -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 15
        $json = $r.Content | ConvertFrom-Json
        if (-not $json.accessToken) { throw '로그인 응답에 accessToken 없음' }
        Write-Host '  login OK (JWT 발급 확인)'
    }

    Write-Host '[PASS] publish 스모크 전 단계 통과'
    exit 0
}
catch {
    $failed = $_.Exception.Message
    Write-Host ("[FAIL] " + $failed)
    exit 1
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch { } }
    if (-not $KeepOutput) {
        try { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue } catch { }
        try { Remove-Item -Force $db -ErrorAction SilentlyContinue } catch { }
    }
    else { Write-Host ("output kept: " + $out) }
}
