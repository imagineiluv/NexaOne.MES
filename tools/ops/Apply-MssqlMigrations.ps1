# 운영 MSSQL 마이그레이션 적용 러너 — config/db/migrations/V*.sql 을 순서대로 1회씩 적용한다.
# 버전 추적: SYS_SCHEMA_MIGRATION(VERSION_ID PK) — 파일명과 숫자 버전을 함께 검증한다.
# 각 파일은 단일 트랜잭션(적용+버전 기록 원자) — 마이그레이션 파일엔 GO 배치 구분자가 없다(관례 확인됨).
# 사용:
#   .\Apply-MssqlMigrations.ps1 -ConnectionString "Server=...;Database=...;..." [-DryRun]
#   .\Apply-MssqlMigrations.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -IncludeOpsSeed
#   .\Apply-MssqlMigrations.ps1 -MigrationsPath <path> -ValidateOnly
# ⚠ 접속 문자열은 env/보안 저장소에서만 — 스크립트·저장소에 하드코딩 금지.
param(
    [string]$ConnectionString,
    [string]$MigrationsPath = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\config\db\migrations'),
    [string]$OpsSqlPath = (Join-Path $PSScriptRoot '..\..\ops\sql'),
    [switch]$IncludeOpsSeed,   # 마이그레이션 후 ops/sql/*.mssql.sql(메뉴·배치 시드)도 적용
    [switch]$DryRun,           # DB 이력과 대기 목록만 조회
    [switch]$ValidateOnly      # DB에 접속하지 않고 로컬 파일 계약·순서만 검증
)

$ErrorActionPreference = 'Stop'

# SQL Server 접속 전에 모든 로컬 자산을 검증한다. PowerShell의 -match는 기본적으로
# 대소문자를 구분하지 않으므로 Regex 객체를 사용해 대문자 계약까지 강제한다.
$migrationNamePattern = [System.Text.RegularExpressions.Regex]::new(
    '^V(?<Version>[0-9]{3})__(?<Description>[A-Z0-9]+(?:_[A-Z0-9]+)*)\.sql$',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
$sourceFiles = @(Get-ChildItem -LiteralPath $MigrationsPath -File |
    Where-Object { $_.Extension -ieq '.sql' })
if ($sourceFiles.Count -eq 0) { throw "no migrations found at $MigrationsPath" }

$migrations = @(
    foreach ($file in $sourceFiles) {
        $match = $migrationNamePattern.Match($file.Name)
        if (-not $match.Success) {
            throw ("invalid migration file '{0}': expected V###__UPPER_SNAKE_DESCRIPTION.sql" -f $file.Name)
        }

        $version = [int]::Parse(
            $match.Groups['Version'].Value,
            [System.Globalization.CultureInfo]::InvariantCulture)
        if ($version -le 0) {
            throw ("invalid migration version in '{0}': version must be greater than zero" -f $file.Name)
        }

        [pscustomobject]@{
            Version = $version
            Name = $file.Name
            File = $file
        }
    }
)

$duplicate = $migrations |
    Group-Object -Property Version |
    Where-Object Count -gt 1 |
    Sort-Object { [int]$_.Name } |
    Select-Object -First 1
if ($null -ne $duplicate) {
    $names = ($duplicate.Group | Sort-Object Name | ForEach-Object Name) -join ', '
    throw ("duplicate migration version {0}: {1}" -f $duplicate.Name, $names)
}

$migrations = @($migrations | Sort-Object -Property Version, Name)
if ($ValidateOnly) {
    Write-Host ("migration validation: {0} file(s)" -f $migrations.Count)
    $migrations | ForEach-Object { Write-Host ("  validated: {0} (version {1})" -f $_.Name, $_.Version) }
    return
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'ConnectionString is required unless -ValidateOnly is specified.'
}

Add-Type -AssemblyName System.Data

$conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
$conn.Open()
try {
    # 복수 러너가 동시에 DDL과 이력 기록을 수행하지 못하게 세션 단위 잠금을 획득한다.
    $lock = $conn.CreateCommand()
    $lock.CommandTimeout = 65
    $lock.CommandText = @"
DECLARE @LockResult INT;
EXEC @LockResult = sys.sp_getapplock
    @Resource = N'NexaOne.SchemaMigrations',
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 60000;
IF @LockResult < 0
    THROW 51001, 'Could not acquire NexaOne.SchemaMigrations lock.', 1;
"@
    [void]$lock.ExecuteNonQuery()

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

    # VERSION_ID는 기존 배포와의 호환을 위해 파일명을 저장하되, 숫자 버전으로도
    # 이력을 재구성한다. 같은 버전의 파일 개명은 미적용으로 오인하면 안 된다.
    $appliedByVersion = @{}
    foreach ($versionId in $applied) {
        $historyMatch = $migrationNamePattern.Match($versionId)
        if (-not $historyMatch.Success) {
            throw ("invalid migration history VERSION_ID '{0}': expected V###__UPPER_SNAKE_DESCRIPTION.sql" -f $versionId)
        }

        $historyVersion = [int]::Parse(
            $historyMatch.Groups['Version'].Value,
            [System.Globalization.CultureInfo]::InvariantCulture)
        if ($appliedByVersion.ContainsKey($historyVersion)) {
            throw ("duplicate applied migration version {0}: {1}, {2}" -f
                $historyVersion, $appliedByVersion[$historyVersion], $versionId)
        }
        $appliedByVersion[$historyVersion] = $versionId
    }

    $pending = New-Object System.Collections.Generic.List[object]
    foreach ($migration in $migrations) {
        if (-not $appliedByVersion.ContainsKey($migration.Version)) {
            $pending.Add($migration)
            continue
        }

        $recordedName = [string]$appliedByVersion[$migration.Version]
        if (-not [string]::Equals($recordedName, $migration.Name, [System.StringComparison]::Ordinal)) {
            throw ("migration history drift at version {0}: database has '{1}', source has '{2}'" -f
                $migration.Version, $recordedName, $migration.Name)
        }
    }

    Write-Host ("migrations: total {0}, applied {1}, pending {2}" -f $migrations.Count, $applied.Count, $pending.Count)
    if ($DryRun) { $pending | ForEach-Object { Write-Host ("  pending: " + $_.Name) }; return }

    foreach ($migration in $pending) {
        $sql = Get-Content -Raw -Encoding UTF8 $migration.File.FullName
        $tx = $conn.BeginTransaction()
        try {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx; $cmd.CommandTimeout = 300
            $cmd.CommandText = $sql
            [void]$cmd.ExecuteNonQuery()
            $ver = $conn.CreateCommand(); $ver.Transaction = $tx
            $ver.CommandText = 'INSERT INTO SYS_SCHEMA_MIGRATION (VERSION_ID) VALUES (@v)'
            [void]$ver.Parameters.AddWithValue('@v', $migration.Name)
            [void]$ver.ExecuteNonQuery()
            $tx.Commit()
            Write-Host ("  applied: " + $migration.Name)
        }
        catch {
            $tx.Rollback()
            throw ("migration failed at {0}: {1}" -f $migration.Name, $_.Exception.Message)
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
