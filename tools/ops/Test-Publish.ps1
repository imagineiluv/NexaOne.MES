# publish 산출물 자동 스모크 — 게시→구성 검사→단독 부팅→/health→로그인까지 무인 검증.
# 배경: 모듈 Copy 누락(실사고 2026-07-09)류의 '조용한 게시 회귀'는 build 출력 부팅 스모크로는 안 잡힌다.
# 사용: .\Test-Publish.ps1                         (Cleaner, 임시 폴더 검증 후 정리)
#       .\Test-Publish.ps1 -ProductProfile PomOnly (선택 프로필의 exact bundle 검증)
#       .\Test-Publish.ps1 -KeepOutput             (산출물 보존 — 수동 확인용)
# 종료코드: 0=전 단계 통과, 1=실패(실패 단계 메시지 출력). CI 편입 가능 형태.
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\NexaOne.Server.csproj'),
    [string]$ProductProfile = 'Cleaner',
    [int]$Port = 8098,
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
if ($ProductProfile -cnotmatch '^[A-Za-z][A-Za-z0-9._-]*$') {
    throw "ProductProfile must be a safe profile identifier; received '$ProductProfile'."
}
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
        dotnet publish $script:Project -c Release -o $script:out --nologo -v q `
            "-p:NexaOneProductProfile=$script:ProductProfile"
        if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }
    }

    Step '산출물 구성 검사' {
        $profileCatalog = Join-Path $script:out 'config\product-profile.manifest'
        if (-not (Test-Path -LiteralPath $profileCatalog -PathType Leaf)) {
            throw 'product profile catalog 누락: config/product-profile.manifest'
        }
        $profileLines = @(Get-Content -LiteralPath $profileCatalog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if (@($profileLines | Where-Object { $_ -ceq 'FormatVersion=1' }).Count -ne 1) {
            throw 'product profile catalog format is missing or unsupported'
        }
        $declaredProfiles = @($profileLines | Where-Object { $_.StartsWith('Profile=', [StringComparison]::Ordinal) })
        if ($declaredProfiles.Count -ne 1 -or $declaredProfiles[0].Substring('Profile='.Length) -cne $script:ProductProfile) {
            throw "product profile mismatch: requested=$script:ProductProfile catalog=$($declaredProfiles -join ',')"
        }
        if (@($profileLines | Where-Object { $_ -ceq 'ApplicationManifest=config/app.xml' }).Count -ne 1) {
            throw 'product profile catalog must select config/app.xml exactly once'
        }
        $expectedModules = @($profileLines |
            Where-Object { $_.StartsWith('Plugin=', [StringComparison]::Ordinal) } |
            ForEach-Object { $_.Substring('Plugin='.Length) + '.dll' } |
            Sort-Object -CaseSensitive)
        if ($expectedModules.Count -eq 0 -or @($expectedModules | Select-Object -Unique).Count -ne $expectedModules.Count) {
            throw 'product profile plugin catalog is empty or contains duplicates'
        }

        $modules = @(Get-ChildItem (Join-Path $script:out 'Modules') -Filter 'NexaOne.*.dll' -ErrorAction SilentlyContinue)
        $actualModules = @($modules.Name | Sort-Object -CaseSensitive)
        if (($expectedModules -join "`n") -cne ($actualModules -join "`n")) {
            throw ("Modules/ profile 불일치 — expected=[{0}] actual=[{1}]" -f
                ($expectedModules -join ','), ($actualModules -join ','))
        }
        if (@(Get-ChildItem (Join-Path $script:out 'Modules') -Filter '*.deps.json' -ErrorAction SilentlyContinue).Count -ne 0) {
            throw 'Modules/에 plugin deps.json이 포함되어 Default ALC 공유 계약을 위반합니다.'
        }
        [xml]$application = Get-Content -LiteralPath (Join-Path $script:out 'config\app.xml') -Raw
        $manifestModules = @($application.Application.Services.Service |
            ForEach-Object {
                ([string]$_.classPaths).Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
                    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Trim()) + '.dll' }
            } |
            Sort-Object -CaseSensitive)
        if (($expectedModules -join "`n") -cne ($manifestModules -join "`n")) {
            throw ("config/app.xml classPaths와 profile catalog 불일치 — expected=[{0}] manifest=[{1}]" -f
                ($expectedModules -join ','), ($manifestModules -join ','))
        }
        foreach ($p in 'wwwroot\spa\index.html', 'wwwroot\css\nexaone.css', 'config\app.xml', 'config\host\server.sqlite.xml', 'db\migrations') {
            if (-not (Test-Path (Join-Path $script:out $p))) { throw ("산출물 누락: " + $p) }
        }
        $allFiles = @(Get-ChildItem -LiteralPath $script:out -Recurse -File)
        $legacyName = @($allFiles | Where-Object {
            $_.Name -match 'NexusCom|NexusFramework|NexusLogic'
        })
        if ($legacyName.Count -gt 0) {
            throw ("legacy Nexus 파일명 잔존: " + (($legacyName | ForEach-Object FullName) -join ', '))
        }

        $runtimeText = @($allFiles | Where-Object {
            $_.Extension -in '.json', '.xml', '.config', '.props', '.targets',
                '.js', '.css', '.html', '.sql', '.txt', '.yml', '.yaml'
        })
        $legacyReference = @($runtimeText | Select-String -Pattern 'NexusCom|NexusFramework|NexusLogic' -List)
        if ($legacyReference.Count -gt 0) {
            throw ("legacy Nexus 런타임 참조 잔존: " + (($legacyReference | ForEach-Object Path) -join ', '))
        }

        Write-Host ("  Profile {0}, Files {1}개, Modules {2}개, spa/css/config/db 및 legacy 명칭 0건 확인" -f
            $script:ProductProfile, $allFiles.Count, $modules.Count)
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
