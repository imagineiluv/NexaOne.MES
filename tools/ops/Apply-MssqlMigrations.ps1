# 운영 MSSQL 마이그레이션 적용 러너 — config/db/migrations/V*.sql 을 순서대로 1회씩 적용한다.
# 버전 추적: SYS_SCHEMA_MIGRATION(VERSION_ID PK) — 이미 적용된 파일은 건너뛴다(멱등).
# 각 파일은 단일 트랜잭션(적용+버전 기록 원자) — 마이그레이션 파일엔 GO 배치 구분자가 없다(관례 확인됨).
# 사용:
#   .\Apply-MssqlMigrations.ps1 -ConnectionString "Server=...;Database=...;..." [-DryRun]
#   .\Apply-MssqlMigrations.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -IncludeOpsSeed
# ⚠ 접속 문자열은 env/보안 저장소에서만 — 스크립트·저장소에 하드코딩 금지.
param(
    [Parameter(Mandatory = $true)][string]$ConnectionString,
    [string]$MigrationsPath = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\config\db\migrations'),
    [string]$OpsSqlPath = (Join-Path $PSScriptRoot '..\..\ops\sql'),
    [switch]$IncludeOpsSeed,   # 마이그레이션 후 ops/sql/*.mssql.sql(메뉴·배치 시드)도 적용
    [switch]$DryRun            # 적용 없이 대기 목록만 출력
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

$files = Get-ChildItem -Path $MigrationsPath -Filter 'V*.sql' | Sort-Object Name
if ($files.Count -eq 0) { throw "no migrations found at $MigrationsPath" }

$conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
$conn.Open()
try {
    # 버전 테이블 보장
    $ensure = $conn.CreateCommand()
    $ensure.CommandText = @"
IF OBJECT_ID(N'SYS_SCHEMA_MIGRATION', N'U') IS NULL
    CREATE TABLE SYS_SCHEMA_MIGRATION (
        VERSION_ID  NVARCHAR(200) NOT NULL,
        APPLIED_AT  DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT PK_SYS_SCHEMA_MIGRATION PRIMARY KEY (VERSION_ID)
    );
"@
    [void]$ensure.ExecuteNonQuery()

    $appliedCmd = $conn.CreateCommand()
    $appliedCmd.CommandText = 'SELECT VERSION_ID FROM SYS_SCHEMA_MIGRATION'
    $applied = New-Object System.Collections.Generic.HashSet[string]
    $reader = $appliedCmd.ExecuteReader()
    while ($reader.Read()) { [void]$applied.Add($reader.GetString(0)) }
    $reader.Close()

    $pending = @($files | Where-Object { -not $applied.Contains($_.Name) })
    Write-Host ("migrations: total {0}, applied {1}, pending {2}" -f $files.Count, $applied.Count, $pending.Count)
    if ($DryRun) { $pending | ForEach-Object { Write-Host ("  pending: " + $_.Name) }; return }

    foreach ($f in $pending) {
        $sql = Get-Content -Raw -Encoding UTF8 $f.FullName
        $tx = $conn.BeginTransaction()
        try {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx; $cmd.CommandTimeout = 300
            $cmd.CommandText = $sql
            [void]$cmd.ExecuteNonQuery()
            $ver = $conn.CreateCommand(); $ver.Transaction = $tx
            $ver.CommandText = 'INSERT INTO SYS_SCHEMA_MIGRATION (VERSION_ID) VALUES (@v)'
            [void]$ver.Parameters.AddWithValue('@v', $f.Name)
            [void]$ver.ExecuteNonQuery()
            $tx.Commit()
            Write-Host ("  applied: " + $f.Name)
        }
        catch {
            $tx.Rollback()
            throw ("migration failed at {0}: {1}" -f $f.Name, $_.Exception.Message)
        }
    }

    if ($IncludeOpsSeed) {
        foreach ($s in (Get-ChildItem -Path $OpsSqlPath -Filter '*.mssql.sql' | Sort-Object Name)) {
            $sql = Get-Content -Raw -Encoding UTF8 $s.FullName
            $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 300; $cmd.CommandText = $sql
            [void]$cmd.ExecuteNonQuery()   # 시드 스크립트 자체가 멱등(IF EXISTS/IF NOT EXISTS)
            Write-Host ("  ops seed: " + $s.Name)
        }
    }
    Write-Host 'done.'
}
finally { $conn.Close() }
