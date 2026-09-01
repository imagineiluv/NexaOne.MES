# 운영 MSSQL 마이그레이션 적용 러너 — config/db/migrations/V*.sql 을 순서대로 1회씩 적용한다.
# 버전 추적: SYS_SCHEMA_MIGRATION(VERSION_ID PK, CONTENT_SHA256) — 파일명·숫자 버전·내용을 함께 검증한다.
# 각 파일은 단일 트랜잭션(적용+버전 기록 원자) — 마이그레이션 파일엔 GO 배치 구분자가 없다(관례 확인됨).
# 사용:
#   .\Apply-MssqlMigrations.ps1 -ConnectionString "Server=...;Database=...;..." [-DryRun]
#   .\Apply-MssqlMigrations.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -IncludeOpsSeed
#   .\Apply-MssqlMigrations.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -AdoptMissingChecksums # 기존 이력 1회 명시 승인
#   .\Apply-MssqlMigrations.ps1 -ConnectionString $env:NEXAONE_MSSQL_CONN -ApproveHighImpactMigrations # V142/V144/V146/V147/V148/V150/V151/V152/V153/V157/V159/V160 운영 승인
#   .\Apply-MssqlMigrations.ps1 -MigrationsPath <path> -ValidateOnly
# ⚠ 접속 문자열은 env/보안 저장소에서만 — 스크립트·저장소에 하드코딩 금지.
param(
    [string]$ConnectionString,
    [string]$MigrationsPath = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\config\db\migrations'),
    [string]$OpsSqlPath = (Join-Path $PSScriptRoot '..\..\ops\sql'),
    [switch]$IncludeOpsSeed,   # 마이그레이션 후 ops/sql/*.mssql.sql(메뉴·배치 시드)도 적용
    [switch]$DryRun,           # DB 이력과 대기 목록만 조회
    [switch]$ValidateOnly,     # DB에 접속하지 않고 로컬 파일 계약·순서만 검증
    [switch]$AdoptMissingChecksums, # 구형 이력의 NULL 체크섬을 현재 소스로 채우는 명시적 1회 승인
    [switch]$ApproveHighImpactMigrations # 대형 history/inbox backfill·index build 운영 준비의 명시적 승인
)

$ErrorActionPreference = 'Stop'

# Keep CLI failures machine-readable across Windows/Linux runners. PowerShell 7 may decorate
# terminating errors with ANSI sequences when stderr is redirected, which makes contract tests and
# operational log parsers depend on the host's output mode.
if (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue) {
    $PSStyle.OutputRendering = 'PlainText'
}

if ($DryRun -and $AdoptMissingChecksums) {
    throw 'DryRun and AdoptMissingChecksums cannot be used together.'
}

function Get-MigrationHash([System.IO.FileInfo]$File) {
    # Git/OS checkout의 CRLF 차이만으로 drift가 발생하지 않도록 UTF-8 텍스트를 LF로 정규화한다.
    # 그 외 공백·주석·SQL 변경은 모두 체크섬 변경으로 검출한다.
    $utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
    $text = [System.IO.File]::ReadAllText($File.FullName, $utf8Strict)
    $canonical = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-MigrationSqlBatches([string]$Sql) {
    # SQL Server compiles a multi-statement batch before executing it. A later statement that
    # references a column added earlier in the same migration can therefore fail compilation even
    # though the column DDL appears first in the file (V090/V092/V093 are representative cases).
    # Split ordinary semicolon-terminated statements while retaining T-SQL blocks (IF/WHILE
    # BEGIN...END) and dynamic EXEC('...') bodies as one command. This keeps every command in the
    # migration transaction and preserves source order without changing immutable migration files.
    # DECLARE variables are batch-scoped. V121/V128 declare variables at column zero and use them
    # in subsequent statements/blocks, so keep declaration-bearing files as one command rather
    # than splitting the declaration away from its consumers. The full scanner still runs so a
    # declaration-bearing migration cannot bypass lexical-balance validation.
    $preserveSingleBatch = $Sql -match '(?im)^DECLARE\b'
    $batches = [System.Collections.Generic.List[string]]::new()
    $statement = [System.Text.StringBuilder]::new()
    $inString = $false
    $inDoubleQuote = $false
    $inBracketIdentifier = $false
    $inLineComment = $false
    $inBlockComment = $false
    $parenthesisDepth = 0
    $blockDepth = 0
    $caseDepth = 0
    $index = 0

    while ($index -lt $Sql.Length) {
        $current = $Sql[$index]
        $next = if ($index + 1 -lt $Sql.Length) { $Sql[$index + 1] } else { [char]0 }

        if ($inLineComment) {
            [void]$statement.Append($current)
            if ($current -eq "`r" -or $current -eq "`n") { $inLineComment = $false }
            $index++
            continue
        }
        if ($inBlockComment) {
            [void]$statement.Append($current)
            if ($current -eq '*' -and $next -eq '/') {
                [void]$statement.Append($next)
                $inBlockComment = $false
                $index += 2
            } else {
                $index++
            }
            continue
        }
        if ($inString) {
            [void]$statement.Append($current)
            if ($current -eq "'") {
                if ($next -eq "'") {
                    [void]$statement.Append($next)
                    $index += 2
                } else {
                    $inString = $false
                    $index++
                }
            } else {
                $index++
            }
            continue
        }
        if ($inDoubleQuote) {
            [void]$statement.Append($current)
            if ($current -eq '"') {
                if ($next -eq '"') {
                    [void]$statement.Append($next)
                    $index += 2
                } else {
                    $inDoubleQuote = $false
                    $index++
                }
            } else {
                $index++
            }
            continue
        }
        if ($inBracketIdentifier) {
            [void]$statement.Append($current)
            if ($current -eq ']') {
                if ($next -eq ']') {
                    [void]$statement.Append($next)
                    $index += 2
                } else {
                    $inBracketIdentifier = $false
                    $index++
                }
            } else {
                $index++
            }
            continue
        }

        if ($current -eq '-' -and $next -eq '-') {
            [void]$statement.Append($current)
            [void]$statement.Append($next)
            $inLineComment = $true
            $index += 2
            continue
        }
        if ($current -eq '/' -and $next -eq '*') {
            [void]$statement.Append($current)
            [void]$statement.Append($next)
            $inBlockComment = $true
            $index += 2
            continue
        }
        if ($current -eq "'") {
            [void]$statement.Append($current)
            $inString = $true
            $index++
            continue
        }
        if ($current -eq '"') {
            [void]$statement.Append($current)
            $inDoubleQuote = $true
            $index++
            continue
        }
        if ($current -eq '[') {
            [void]$statement.Append($current)
            $inBracketIdentifier = $true
            $index++
            continue
        }
        if ($current -eq '(') {
            [void]$statement.Append($current)
            $parenthesisDepth++
            $index++
            continue
        }
        if ($current -eq ')') {
            [void]$statement.Append($current)
            if ($parenthesisDepth -eq 0) {
                throw ("migration SQL has unmatched closing parenthesis at offset {0}" -f $index)
            }
            $parenthesisDepth--
            $index++
            continue
        }
        if ([char]::IsLetter($current) -or $current -eq '_') {
            $start = $index
            while ($index -lt $Sql.Length) {
                $tokenChar = $Sql[$index]
                if (-not ([char]::IsLetterOrDigit($tokenChar) -or $tokenChar -eq '_')) { break }
                $index++
            }
            $token = $Sql.Substring($start, $index - $start)
            [void]$statement.Append($token)
            switch ($token.ToUpperInvariant()) {
                'CASE'  { $caseDepth++ }
                'BEGIN' {
                    # BEGIN TRAN[SACTION] opens a transaction, not a BEGIN...END block. Keeping it
                    # in blockDepth would swallow every statement after a CREATE PROCEDURE body
                    # into the same command and violate SQL Server's first-statement batch rule.
                    $remaining = $Sql.Substring($index)
                    if ($remaining -notmatch '^\s+(?:DISTRIBUTED\s+)?TRAN(?:SACTION)?\b') {
                        $blockDepth++
                    }
                }
                'END'   {
                    if ($caseDepth -gt 0) { $caseDepth-- }
                    elseif ($blockDepth -gt 0) { $blockDepth-- }
                    else {
                        throw ("migration SQL has unmatched END token at offset {0}" -f $start)
                    }
                }
            }
            continue
        }
        if (-not $preserveSingleBatch -and $current -eq ';' -and
            $parenthesisDepth -eq 0 -and $blockDepth -eq 0) {
            $completed = $statement.ToString().Trim()
            if ($completed.Length -gt 0) { [void]$batches.Add($completed) }
            [void]$statement.Clear()
            $index++
            continue
        }

        [void]$statement.Append($current)
        $index++
    }

    if ($inString -or $inDoubleQuote -or $inBracketIdentifier -or $inBlockComment -or
        $parenthesisDepth -ne 0 -or $blockDepth -ne 0 -or $caseDepth -ne 0) {
        throw ("migration SQL has unbalanced lexical structure " +
            "(string={0}, quoted={1}, bracket={2}, blockComment={3}, parentheses={4}, blocks={5}, cases={6})" -f
            $inString, $inDoubleQuote, $inBracketIdentifier, $inBlockComment,
            $parenthesisDepth, $blockDepth, $caseDepth)
    }

    $remainder = $statement.ToString().Trim()
    if ($remainder.Length -gt 0) { [void]$batches.Add($remainder) }
    return $batches.ToArray()
}

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

        $sql = Get-Content -Raw -Encoding UTF8 $file.FullName
        try {
            $sqlBatches = @(Get-MigrationSqlBatches $sql)
        }
        catch {
            throw ("invalid migration SQL structure in '{0}': {1}" -f $file.Name, $_.Exception.Message)
        }
        if ($sqlBatches.Count -eq 0) {
            throw ("invalid migration SQL structure in '{0}': no executable batch" -f $file.Name)
        }

        [pscustomobject]@{
            Version = $version
            Name = $file.Name
            File = $file
            Hash = Get-MigrationHash $file
            SqlBatches = $sqlBatches
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

    $historyShape = $conn.CreateCommand()
    $historyShape.CommandText = @"
SELECT
    CAST(CASE WHEN OBJECT_ID(N'SYS_SCHEMA_MIGRATION', N'U') IS NULL THEN 0 ELSE 1 END AS INT) AS TABLE_EXISTS,
    CAST(CASE WHEN COL_LENGTH(N'SYS_SCHEMA_MIGRATION', N'CONTENT_SHA256') IS NULL THEN 0 ELSE 1 END AS INT) AS HASH_COLUMN_EXISTS;
"@
    $shapeReader = $historyShape.ExecuteReader()
    [void]$shapeReader.Read()
    $historyTableExists = $shapeReader.GetInt32(0) -eq 1
    $historyHashColumnExists = $shapeReader.GetInt32(1) -eq 1
    $shapeReader.Close()

    if ($DryRun -and -not $historyTableExists) {
        Write-Host 'migration history table is absent; read-only DryRun treats every local migration as pending.'
    }

    $applied = New-Object System.Collections.Generic.List[object]
    if ($historyTableExists) {
        $appliedCmd = $conn.CreateCommand()
        $appliedCmd.CommandText = if ($historyHashColumnExists) {
            'SELECT VERSION_ID, CONTENT_SHA256 FROM SYS_SCHEMA_MIGRATION'
        } else {
            'SELECT VERSION_ID, CAST(NULL AS CHAR(64)) AS CONTENT_SHA256 FROM SYS_SCHEMA_MIGRATION'
        }
        $reader = $appliedCmd.ExecuteReader()
        while ($reader.Read()) {
            $applied.Add([pscustomobject]@{
                Name = $reader.GetString(0)
                Hash = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1).Trim() }
            })
        }
        $reader.Close()
    }

    # VERSION_ID는 기존 배포와의 호환을 위해 파일명을 저장하되, 숫자 버전으로도
    # 이력을 재구성한다. 같은 버전의 파일 개명은 미적용으로 오인하면 안 된다.
    $appliedByVersion = @{}
    foreach ($history in $applied) {
        $versionId = [string]$history.Name
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
        $appliedByVersion[$historyVersion] = $history
    }

    # The database history must be an exact prefix of the local immutable catalog. A database-only
    # version means this runner/app is older than the schema; a hole followed by a later applied
    # version means an old migration would be replayed out of order. Both cases fail before migration
    # DDL or ops seed execution instead of allowing a downlevel binary to attach to a newer schema.
    $localByVersion = @{}
    foreach ($migration in $migrations) {
        $localByVersion[$migration.Version] = $migration
    }
    foreach ($historyVersion in $appliedByVersion.Keys) {
        if (-not $localByVersion.ContainsKey($historyVersion)) {
            $databaseOnlyName = [string]$appliedByVersion[$historyVersion].Name
            throw ("database contains migration absent from this source at version {0}: '{1}'. " -f
                $historyVersion, $databaseOnlyName) +
                'Refuse to run a downlevel application or migration catalog.'
        }
    }

    $encounteredPendingVersion = $null
    foreach ($migration in $migrations) {
        if (-not $appliedByVersion.ContainsKey($migration.Version)) {
            if ($null -eq $encounteredPendingVersion) {
                $encounteredPendingVersion = $migration.Version
            }
            continue
        }
        if ($null -ne $encounteredPendingVersion) {
            throw ("migration history is not a contiguous source prefix: version {0} is missing " -f
                $encounteredPendingVersion) +
                ("but later version {0} is already applied. Refuse out-of-order replay." -f $migration.Version)
        }
    }

    $pending = New-Object System.Collections.Generic.List[object]
    $missingChecksums = New-Object System.Collections.Generic.List[object]
    foreach ($migration in $migrations) {
        if (-not $appliedByVersion.ContainsKey($migration.Version)) {
            $pending.Add($migration)
            continue
        }

        $history = $appliedByVersion[$migration.Version]
        $recordedName = [string]$history.Name
        if (-not [string]::Equals($recordedName, $migration.Name, [System.StringComparison]::Ordinal)) {
            throw ("migration history drift at version {0}: database has '{1}', source has '{2}'" -f
                $migration.Version, $recordedName, $migration.Name)
        }

        $recordedHash = [string]$history.Hash
        if ([string]::IsNullOrWhiteSpace($recordedHash)) {
            if (-not $AdoptMissingChecksums) {
                throw ("migration history checksum missing for '{0}'. Verify the deployed schema/source, then rerun once with -AdoptMissingChecksums." -f
                    $migration.Name)
            }
            $missingChecksums.Add($migration)
            continue
        }

        if (-not [string]::Equals($recordedHash, $migration.Hash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw ("migration content drift for '{0}': database checksum {1}, source checksum {2}" -f
                $migration.Name, $recordedHash, $migration.Hash)
        }
    }

    Write-Host ("migrations: total {0}, applied {1}, pending {2}" -f $migrations.Count, $applied.Count, $pending.Count)
    if ($DryRun) { $pending | ForEach-Object { Write-Host ("  pending: " + $_.Name) }; return }

    # These migrations touch append-only operational history/inbox tables. The switch is an
    # explicit assertion that a current backup, production-sized restore rehearsal, writer
    # quiescence, maintenance window, transaction-log capacity and rollback criteria were approved.
    # It is deliberately evaluated from the pending set so already-applied databases are unaffected.
    $highImpactVersions = @(142, 144, 146, 147, 148, 150, 151, 152, 153, 157, 159, 160)
    $highImpactPending = @($pending | Where-Object { $highImpactVersions -contains $_.Version })
    if ($highImpactPending.Count -gt 0 -and -not $ApproveHighImpactMigrations) {
        $highImpactNames = ($highImpactPending | ForEach-Object Name) -join ', '
        throw ("high-impact migration approval is required for: {0}. " -f $highImpactNames) +
              'Complete backup/rehearsal/writer-quiescence/log-capacity/rollback review, then rerun with -ApproveHighImpactMigrations.'
    }

    # Only mutate migration history after every read-only catalog/prefix/checksum check and the
    # high-impact gate have passed. In particular, an older runner attached to a future schema must
    # fail without even adding CONTENT_SHA256 to the history table.
    $ensure = $conn.CreateCommand()
    $ensure.CommandText = @"
IF OBJECT_ID(N'SYS_SCHEMA_MIGRATION', N'U') IS NULL
BEGIN
    CREATE TABLE SYS_SCHEMA_MIGRATION (
        VERSION_ID     NVARCHAR(200) NOT NULL,
        CONTENT_SHA256 CHAR(64)      NOT NULL,
        APPLIED_AT     DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT PK_SYS_SCHEMA_MIGRATION PRIMARY KEY (VERSION_ID)
    );
END
ELSE IF COL_LENGTH(N'SYS_SCHEMA_MIGRATION', N'CONTENT_SHA256') IS NULL
BEGIN
    -- 기존 배포는 값의 진위를 자동 추정하지 않는다. 위 검증에서 명시적 adoption을 요구했다.
    ALTER TABLE SYS_SCHEMA_MIGRATION ADD CONTENT_SHA256 CHAR(64) NULL;
END;
"@
    [void]$ensure.ExecuteNonQuery()

    if ($missingChecksums.Count -gt 0) {
        $adoptTx = $conn.BeginTransaction()
        try {
            foreach ($migration in $missingChecksums) {
                $adopt = $conn.CreateCommand(); $adopt.Transaction = $adoptTx
                $adopt.CommandText = @"
UPDATE SYS_SCHEMA_MIGRATION
SET CONTENT_SHA256 = @hash
WHERE VERSION_ID = @version AND CONTENT_SHA256 IS NULL;
"@
                [void]$adopt.Parameters.AddWithValue('@hash', $migration.Hash)
                [void]$adopt.Parameters.AddWithValue('@version', $migration.Name)
                if ($adopt.ExecuteNonQuery() -ne 1) {
                    throw ("could not adopt migration checksum for '{0}' because its history changed concurrently" -f $migration.Name)
                }
                Write-Host ("  adopted checksum: " + $migration.Name)
            }
            $adoptTx.Commit()
        }
        catch {
            $adoptTx.Rollback()
            throw
        }
    }

    foreach ($migration in $pending) {
        $tx = $conn.BeginTransaction()
        try {
            foreach ($batch in $migration.SqlBatches) {
                $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx; $cmd.CommandTimeout = 300
                $cmd.CommandText = $batch
                [void]$cmd.ExecuteNonQuery()
            }
            $ver = $conn.CreateCommand(); $ver.Transaction = $tx
            $ver.CommandText = 'INSERT INTO SYS_SCHEMA_MIGRATION (VERSION_ID, CONTENT_SHA256) VALUES (@v, @hash)'
            [void]$ver.Parameters.AddWithValue('@v', $migration.Name)
            [void]$ver.Parameters.AddWithValue('@hash', $migration.Hash)
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
