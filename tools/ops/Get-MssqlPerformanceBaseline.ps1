# SQL Server 조회 성능 기준선 수집기(읽기 전용).
# Query Store, 실제 index 사용량, 통계 freshness, missing-index 힌트, View/indexed-view 현황을 CSV로 남긴다.
# 물리 fragmentation은 비용이 있으므로 명시적인 -IncludePhysicalStats에서만 큰 index 상위 집합을 LIMITED로 읽는다.
# DMV 결과는 관찰 자료일 뿐이다. 이 출력만으로 index를 자동 생성·삭제하지 않는다.
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\artifacts\mssql-performance'),

    [ValidateRange(1, 365)]
    [int]$LookbackDays = 7,

    [ValidateRange(1, 1000)]
    [int]$Top = 100,

    [switch]$IncludePhysicalStats,

    [ValidateRange(1, 1000000000)]
    [int]$PhysicalStatsMinPageCount = 1000,

    [switch]$AllowPartial
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputPath)
$runId = "{0}-{1}" -f [DateTime]::UtcNow.ToString(
    'yyyyMMddTHHmmssfffZ',
    [System.Globalization.CultureInfo]::InvariantCulture), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runOutput = Join-Path $resolvedOutputRoot $runId
$reportResults = [System.Collections.Generic.List[object]]::new()

function Export-Report {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Name,
        [string]$Sql,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 60
    $command.CommandText = $Sql
    foreach ($entry in $Parameters.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value)
    }

    $table = [System.Data.DataTable]::new($Name)
    try {
        $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($command)
        [void]$adapter.Fill($table)
    }
    catch [System.Data.SqlClient.SqlException] {
        # Query Store/DMV 권한은 운영 역할에 따라 다르다. 모든 보고서 상태를 manifest에
        # 기록하고, 명시적인 -AllowPartial 없이는 마지막에 실패시킨다.
        Write-Warning ("report '{0}' was skipped (SQL error {1}): {2}" -f
            $Name, $_.Exception.Number, $_.Exception.Message)
        $reportResults.Add([pscustomobject]@{
            Name = $Name
            Success = $false
            RowCount = $null
            File = $null
            SqlErrorNumber = $_.Exception.Number
            Error = $_.Exception.Message
        })
        return $null
    }

    $path = Join-Path $runOutput "$Name.csv"
    $table | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding utf8
    $reportResults.Add([pscustomobject]@{
        Name = $Name
        Success = $true
        RowCount = $table.Rows.Count
        File = [System.IO.Path]::GetFileName($path)
        SqlErrorNumber = $null
        Error = $null
    })
    Write-Host ("{0}: {1} row(s) -> {2}" -f $Name, $table.Rows.Count, $path)
    return ,$table
}

$connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
$connection.Open()
$serverDataSource = $connection.DataSource
try {
    [System.IO.Directory]::CreateDirectory($runOutput) | Out-Null
    $databaseName = $connection.Database
    Write-Host ("collecting read-only SQL Server performance baseline for database '{0}'" -f $databaseName)

    $metadata = Export-Report -Connection $connection -Name 'server-properties' -Sql @"
SELECT
    CONVERT(NVARCHAR(128), SERVERPROPERTY('ServerName')) AS SERVER_NAME,
    CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductVersion')) AS PRODUCT_VERSION,
    CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductLevel')) AS PRODUCT_LEVEL,
    CONVERT(NVARCHAR(128), SERVERPROPERTY('Edition')) AS EDITION,
    CONVERT(INT, SERVERPROPERTY('EngineEdition')) AS ENGINE_EDITION,
    DB_NAME() AS DATABASE_NAME,
    SYSUTCDATETIME() AS COLLECTED_AT_UTC,
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS CAN_VIEW_DEFINITION,
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE') AS CAN_VIEW_DATABASE_STATE,
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE PERFORMANCE STATE') AS CAN_VIEW_DATABASE_PERFORMANCE_STATE,
    HAS_PERMS_BY_NAME(NULL, 'SERVER', 'VIEW SERVER STATE') AS CAN_VIEW_SERVER_STATE,
    HAS_PERMS_BY_NAME(NULL, 'SERVER', 'VIEW SERVER PERFORMANCE STATE') AS CAN_VIEW_SERVER_PERFORMANCE_STATE;
"@

    $canViewDefinition = if ($null -ne $metadata -and $metadata.Rows.Count -gt 0) {
        [int]($metadata.Rows[0]['CAN_VIEW_DEFINITION'])
    } else { $null }
    if ($null -ne $canViewDefinition -and $canViewDefinition -ne 1) {
        $permissionError = 'VIEW DEFINITION is required to prove complete View/index metadata visibility.'
        Write-Warning $permissionError
        $reportResults.Add([pscustomobject]@{
            Name = 'metadata-visibility-prerequisite'
            Success = $false
            RowCount = $null
            File = $null
            SqlErrorNumber = $null
            Error = $permissionError
        })
    }

    $counterReset = Export-Report -Connection $connection -Name 'dmv-counter-window' -Sql @"
SELECT sqlserver_start_time AS USAGE_COUNTERS_SINCE
FROM sys.dm_os_sys_info;
"@

    $databaseOptions = Export-Report -Connection $connection -Name 'database-options' -Sql @"
SELECT
    DB_NAME() AS DATABASE_NAME,
    d.compatibility_level AS COMPATIBILITY_LEVEL,
    d.is_read_committed_snapshot_on AS READ_COMMITTED_SNAPSHOT_ON,
    qso.actual_state_desc AS QUERY_STORE_STATE,
    qso.readonly_reason AS QUERY_STORE_READONLY_REASON,
    qso.current_storage_size_mb AS QUERY_STORE_SIZE_MB,
    qso.max_storage_size_mb AS QUERY_STORE_MAX_SIZE_MB
FROM sys.databases AS d
LEFT JOIN sys.database_query_store_options AS qso ON 1 = 1
WHERE d.database_id = DB_ID();
"@

    if ($null -ne $databaseOptions -and $databaseOptions.Rows.Count -gt 0) {
        $queryStoreState = [string]$databaseOptions.Rows[0]['QUERY_STORE_STATE']
        if (-not [string]::Equals($queryStoreState, 'READ_WRITE', [StringComparison]::OrdinalIgnoreCase)) {
            $queryStoreError = "Query Store must be READ_WRITE for a complete observation window; actual state is '$queryStoreState'."
            Write-Warning $queryStoreError
            $reportResults.Add([pscustomobject]@{
                Name = 'query-store-prerequisite'
                Success = $false
                RowCount = $null
                File = $null
                SqlErrorNumber = $null
                Error = $queryStoreError
            })
        }
    }

    $null = Export-Report -Connection $connection -Name 'query-store-top-logical-reads' -Parameters @{
        LookbackDays = $LookbackDays
        Top = $Top
    } -Sql @"
SELECT TOP (@Top)
    q.query_id AS QUERY_ID,
    OBJECT_SCHEMA_NAME(q.object_id) AS OBJECT_SCHEMA,
    OBJECT_NAME(q.object_id) AS OBJECT_NAME,
    SUM(rs.count_executions) AS EXECUTION_COUNT,
    CAST(SUM(rs.avg_logical_io_reads * rs.count_executions) AS DECIMAL(38, 2)) AS TOTAL_LOGICAL_READS,
    CAST(SUM(rs.avg_duration * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS DECIMAL(18, 2)) AS AVG_DURATION_MS,
    MAX(rs.last_execution_time) AS LAST_EXECUTION_TIME,
    LEFT(REPLACE(REPLACE(qt.query_sql_text, CHAR(13), ' '), CHAR(10), ' '), 4000) AS QUERY_SQL_TEXT
FROM sys.query_store_query_text AS qt
JOIN sys.query_store_query AS q ON q.query_text_id = qt.query_text_id
JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = p.plan_id
JOIN sys.query_store_runtime_stats_interval AS rsi ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
WHERE rsi.end_time >= DATEADD(DAY, -@LookbackDays, SYSUTCDATETIME())
GROUP BY q.query_id, q.object_id, qt.query_sql_text
ORDER BY TOTAL_LOGICAL_READS DESC;
"@

    $null = Export-Report -Connection $connection -Name 'index-usage' -Sql @"
SELECT
    s.name AS SCHEMA_NAME,
    t.name AS TABLE_NAME,
    i.name AS INDEX_NAME,
    i.type_desc AS INDEX_TYPE,
    i.is_unique AS IS_UNIQUE,
    i.has_filter AS HAS_FILTER,
    i.filter_definition AS FILTER_DEFINITION,
    COALESCE(p.ROW_COUNT, 0) AS ROW_COUNT,
    COALESCE(u.user_seeks, 0) AS USER_SEEKS,
    COALESCE(u.user_scans, 0) AS USER_SCANS,
    COALESCE(u.user_lookups, 0) AS USER_LOOKUPS,
    COALESCE(u.user_updates, 0) AS USER_UPDATES,
    u.last_user_seek AS LAST_USER_SEEK,
    u.last_user_scan AS LAST_USER_SCAN,
    u.last_user_lookup AS LAST_USER_LOOKUP,
    u.last_user_update AS LAST_USER_UPDATE
FROM sys.indexes AS i
JOIN sys.tables AS t ON t.object_id = i.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
LEFT JOIN sys.dm_db_index_usage_stats AS u
  ON u.database_id = DB_ID()
 AND u.object_id = i.object_id
 AND u.index_id = i.index_id
LEFT JOIN (
    SELECT object_id, index_id, SUM(row_count) AS ROW_COUNT
    FROM sys.dm_db_partition_stats
    GROUP BY object_id, index_id
) AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
WHERE i.index_id > 0
  AND i.is_hypothetical = 0
ORDER BY COALESCE(u.user_updates, 0) DESC,
         COALESCE(u.user_seeks, 0) + COALESCE(u.user_scans, 0) + COALESCE(u.user_lookups, 0);
"@

    $null = Export-Report -Connection $connection -Name 'index-definition-size' -Sql @"
WITH IndexColumns AS (
    SELECT
        ic.object_id,
        ic.index_id,
        STRING_AGG(
            CASE WHEN ic.is_included_column = 0
                 THEN CONVERT(NVARCHAR(MAX), QUOTENAME(c.name))
                      + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
            END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS KEY_COLUMNS,
        STRING_AGG(
            CASE WHEN ic.is_included_column = 1
                 THEN CONVERT(NVARCHAR(MAX), QUOTENAME(c.name)) END,
            ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS INCLUDED_COLUMNS
    FROM sys.index_columns AS ic
    JOIN sys.columns AS c
      ON c.object_id = ic.object_id
     AND c.column_id = ic.column_id
    GROUP BY ic.object_id, ic.index_id
), IndexSize AS (
    SELECT
        object_id,
        index_id,
        SUM(row_count) AS ROW_COUNT,
        CAST(SUM(reserved_page_count) * 8.0 / 1024.0 AS DECIMAL(18, 2)) AS RESERVED_MB,
        CAST(SUM(used_page_count) * 8.0 / 1024.0 AS DECIMAL(18, 2)) AS USED_MB
    FROM sys.dm_db_partition_stats
    GROUP BY object_id, index_id
)
SELECT
    s.name AS SCHEMA_NAME,
    t.name AS TABLE_NAME,
    i.name AS INDEX_NAME,
    i.type_desc AS INDEX_TYPE,
    i.is_unique AS IS_UNIQUE,
    cols.KEY_COLUMNS,
    cols.INCLUDED_COLUMNS,
    i.filter_definition AS FILTER_DEFINITION,
    COALESCE(sz.ROW_COUNT, 0) AS ROW_COUNT,
    COALESCE(sz.RESERVED_MB, 0) AS RESERVED_MB,
    COALESCE(sz.USED_MB, 0) AS USED_MB
FROM sys.indexes AS i
JOIN sys.tables AS t ON t.object_id = i.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
LEFT JOIN IndexColumns AS cols
  ON cols.object_id = i.object_id AND cols.index_id = i.index_id
LEFT JOIN IndexSize AS sz
  ON sz.object_id = i.object_id AND sz.index_id = i.index_id
WHERE i.index_id > 0
  AND i.type IN (1, 2) -- rowstore only; columnstore does not have ASC/DESC key semantics
  AND i.is_hypothetical = 0
ORDER BY sz.RESERVED_MB DESC, s.name, t.name, i.index_id;
"@

    $null = Export-Report -Connection $connection -Name 'statistics-freshness' -Sql @"
SELECT
    s.name AS SCHEMA_NAME,
    o.name AS OBJECT_NAME,
    CASE o.type WHEN 'U' THEN 'TABLE' WHEN 'V' THEN 'VIEW' END AS OBJECT_TYPE,
    st.stats_id AS STATISTICS_ID,
    st.name AS STATISTICS_NAME,
    st.auto_created AS IS_AUTO_CREATED,
    st.user_created AS IS_USER_CREATED,
    st.no_recompute AS NO_RECOMPUTE,
    st.has_filter AS HAS_FILTER,
    st.filter_definition AS FILTER_DEFINITION,
    props.last_updated AS LAST_UPDATED,
    props.rows AS ROW_COUNT_AT_UPDATE,
    props.rows_sampled AS ROWS_SAMPLED,
    CAST(100.0 * props.rows_sampled / NULLIF(props.rows, 0) AS DECIMAL(9, 2)) AS SAMPLE_PERCENT,
    props.steps AS HISTOGRAM_STEPS,
    props.unfiltered_rows AS UNFILTERED_ROWS,
    props.modification_counter AS MODIFICATION_COUNTER
FROM sys.stats AS st
JOIN sys.objects AS o ON o.object_id = st.object_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) AS props
WHERE o.type IN ('U', 'V')
  AND o.is_ms_shipped = 0
ORDER BY
    CASE WHEN props.last_updated IS NULL THEN 0 ELSE 1 END,
    props.last_updated,
    s.name,
    o.name,
    st.stats_id;
"@

    if ($IncludePhysicalStats) {
        $null = Export-Report -Connection $connection -Name 'index-physical-fragmentation' -Parameters @{
            Top = $Top
            MinPageCount = $PhysicalStatsMinPageCount
        } -Sql @"
WITH CandidateIndexes AS (
    SELECT TOP (@Top)
        p.object_id,
        p.index_id,
        SUM(p.used_page_count) AS USED_PAGE_COUNT
    FROM sys.dm_db_partition_stats AS p
    JOIN sys.indexes AS i
      ON i.object_id = p.object_id
     AND i.index_id = p.index_id
    JOIN sys.objects AS o ON o.object_id = p.object_id
    WHERE p.index_id > 0
      AND i.is_hypothetical = 0
      AND o.type IN ('U', 'V')
      AND o.is_ms_shipped = 0
    GROUP BY p.object_id, p.index_id
    HAVING SUM(p.used_page_count) >= @MinPageCount
    ORDER BY USED_PAGE_COUNT DESC, p.object_id, p.index_id
)
SELECT
    s.name AS SCHEMA_NAME,
    o.name AS OBJECT_NAME,
    CASE o.type WHEN 'U' THEN 'TABLE' WHEN 'V' THEN 'VIEW' END AS OBJECT_TYPE,
    i.name AS INDEX_NAME,
    physical.partition_number AS PARTITION_NUMBER,
    physical.alloc_unit_type_desc AS ALLOCATION_UNIT_TYPE,
    physical.index_type_desc AS INDEX_TYPE,
    physical.page_count AS PAGE_COUNT,
    CAST(physical.avg_fragmentation_in_percent AS DECIMAL(9, 2)) AS AVG_FRAGMENTATION_PERCENT,
    CAST(physical.avg_page_space_used_in_percent AS DECIMAL(9, 2)) AS AVG_PAGE_SPACE_USED_PERCENT,
    physical.fragment_count AS FRAGMENT_COUNT,
    physical.avg_fragment_size_in_pages AS AVG_FRAGMENT_SIZE_PAGES
FROM CandidateIndexes AS candidate
CROSS APPLY sys.dm_db_index_physical_stats(
    DB_ID(), candidate.object_id, candidate.index_id, NULL, 'LIMITED') AS physical
JOIN sys.indexes AS i
  ON i.object_id = candidate.object_id
 AND i.index_id = candidate.index_id
JOIN sys.objects AS o ON o.object_id = candidate.object_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
ORDER BY
    physical.avg_fragmentation_in_percent DESC,
    physical.page_count DESC,
    s.name,
    o.name,
    i.name,
    physical.partition_number;
"@
    }

    $null = Export-Report -Connection $connection -Name 'missing-index-hints' -Parameters @{ Top = $Top } -Sql @"
SELECT TOP (@Top)
    OBJECT_SCHEMA_NAME(d.object_id, d.database_id) AS SCHEMA_NAME,
    OBJECT_NAME(d.object_id, d.database_id) AS TABLE_NAME,
    CAST(g.avg_total_user_cost * g.avg_user_impact * (g.user_seeks + g.user_scans) AS DECIMAL(38, 2)) AS IMPROVEMENT_SCORE,
    g.user_seeks AS USER_SEEKS,
    g.user_scans AS USER_SCANS,
    CAST(g.avg_user_impact AS DECIMAL(9, 2)) AS AVG_USER_IMPACT_PERCENT,
    d.equality_columns AS EQUALITY_COLUMNS,
    d.inequality_columns AS INEQUALITY_COLUMNS,
    d.included_columns AS INCLUDED_COLUMNS,
    g.last_user_seek AS LAST_USER_SEEK
FROM sys.dm_db_missing_index_group_stats AS g
JOIN sys.dm_db_missing_index_groups AS ig ON ig.index_group_handle = g.group_handle
JOIN sys.dm_db_missing_index_details AS d ON d.index_handle = ig.index_handle
WHERE d.database_id = DB_ID()
ORDER BY IMPROVEMENT_SCORE DESC;
"@

    $null = Export-Report -Connection $connection -Name 'view-inventory' -Sql @"
SELECT
    s.name AS SCHEMA_NAME,
    v.name AS VIEW_NAME,
    OBJECTPROPERTYEX(v.object_id, 'IsSchemaBound') AS IS_SCHEMA_BOUND,
    CASE WHEN EXISTS (
        SELECT 1 FROM sys.indexes AS i WHERE i.object_id = v.object_id AND i.index_id > 0
    ) THEN 1 ELSE 0 END AS IS_INDEXED_VIEW,
    COUNT(DISTINCT d.referencing_id) AS REFERENCING_OBJECT_COUNT,
    MAX(v.modify_date) AS LAST_DEFINITION_CHANGE
FROM sys.views AS v
JOIN sys.schemas AS s ON s.schema_id = v.schema_id
LEFT JOIN sys.sql_expression_dependencies AS d ON d.referenced_id = v.object_id
GROUP BY s.name, v.name, v.object_id, v.modify_date
ORDER BY s.name, v.name;
"@

    $null = Export-Report -Connection $connection -Name 'indexed-view-index-definition' -Sql @"
WITH IndexColumns AS (
    SELECT
        ic.object_id,
        ic.index_id,
        STRING_AGG(
            CASE WHEN ic.is_included_column = 0
                 THEN CONVERT(NVARCHAR(MAX), QUOTENAME(c.name))
                      + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
            END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS KEY_COLUMNS,
        STRING_AGG(
            CASE WHEN ic.is_included_column = 1
                 THEN CONVERT(NVARCHAR(MAX), QUOTENAME(c.name)) END,
            ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS INCLUDED_COLUMNS
    FROM sys.index_columns AS ic
    JOIN sys.columns AS c
      ON c.object_id = ic.object_id
     AND c.column_id = ic.column_id
    GROUP BY ic.object_id, ic.index_id
), IndexSize AS (
    SELECT
        object_id,
        index_id,
        SUM(row_count) AS ROW_COUNT,
        CAST(SUM(reserved_page_count) * 8.0 / 1024.0 AS DECIMAL(18, 2)) AS RESERVED_MB,
        CAST(SUM(used_page_count) * 8.0 / 1024.0 AS DECIMAL(18, 2)) AS USED_MB
    FROM sys.dm_db_partition_stats
    GROUP BY object_id, index_id
)
SELECT
    s.name AS SCHEMA_NAME,
    v.name AS VIEW_NAME,
    OBJECTPROPERTYEX(v.object_id, 'IsSchemaBound') AS IS_SCHEMA_BOUND,
    i.name AS INDEX_NAME,
    i.type_desc AS INDEX_TYPE,
    i.is_unique AS IS_UNIQUE,
    i.is_primary_key AS IS_PRIMARY_KEY,
    cols.KEY_COLUMNS,
    cols.INCLUDED_COLUMNS,
    i.has_filter AS HAS_FILTER,
    i.filter_definition AS FILTER_DEFINITION,
    COALESCE(sz.ROW_COUNT, 0) AS ROW_COUNT,
    COALESCE(sz.RESERVED_MB, 0) AS RESERVED_MB,
    COALESCE(sz.USED_MB, 0) AS USED_MB,
    COALESCE(usage.user_seeks, 0) AS USER_SEEKS,
    COALESCE(usage.user_scans, 0) AS USER_SCANS,
    COALESCE(usage.user_lookups, 0) AS USER_LOOKUPS,
    COALESCE(usage.user_updates, 0) AS USER_UPDATES,
    usage.last_user_seek AS LAST_USER_SEEK,
    usage.last_user_scan AS LAST_USER_SCAN,
    usage.last_user_update AS LAST_USER_UPDATE
FROM sys.views AS v
JOIN sys.schemas AS s ON s.schema_id = v.schema_id
JOIN sys.indexes AS i
  ON i.object_id = v.object_id
 AND i.index_id > 0
LEFT JOIN IndexColumns AS cols
  ON cols.object_id = i.object_id AND cols.index_id = i.index_id
LEFT JOIN IndexSize AS sz
  ON sz.object_id = i.object_id AND sz.index_id = i.index_id
LEFT JOIN sys.dm_db_index_usage_stats AS usage
  ON usage.database_id = DB_ID()
 AND usage.object_id = i.object_id
 AND usage.index_id = i.index_id
WHERE i.is_hypothetical = 0
ORDER BY sz.RESERVED_MB DESC, s.name, v.name, i.index_id;
"@
}
finally {
    $connection.Dispose()
}

$metadataRow = if ($null -ne $metadata -and $metadata.Rows.Count -gt 0) {
    $metadata.Rows[0]
} else { $null }
$counterResetRow = if ($null -ne $counterReset -and $counterReset.Rows.Count -gt 0) {
    $counterReset.Rows[0]
} else { $null }
$failedReports = @($reportResults | Where-Object { -not $_.Success })
$manifest = [ordered]@{
    RunId = $runId
    Status = if ($failedReports.Count -eq 0) { 'Complete' } elseif ($AllowPartial) { 'PartialAllowed' } else { 'Failed' }
    CollectedAtUtc = [DateTime]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    ServerDataSource = $serverDataSource
    ServerName = if ($null -ne $metadataRow) { [string]$metadataRow['SERVER_NAME'] } else { $null }
    ProductVersion = if ($null -ne $metadataRow) { [string]$metadataRow['PRODUCT_VERSION'] } else { $null }
    ProductLevel = if ($null -ne $metadataRow) { [string]$metadataRow['PRODUCT_LEVEL'] } else { $null }
    Edition = if ($null -ne $metadataRow) { [string]$metadataRow['EDITION'] } else { $null }
    EngineEdition = if ($null -ne $metadataRow) { $metadataRow['ENGINE_EDITION'] } else { $null }
    Database = $databaseName
    LookbackDays = $LookbackDays
    Top = $Top
    IncludePhysicalStats = [bool]$IncludePhysicalStats
    PhysicalStatsMinPageCount = $PhysicalStatsMinPageCount
    AllowPartial = [bool]$AllowPartial
    UsageCountersSince = if ($null -ne $counterResetRow) {
        $counterResetRow['USAGE_COUNTERS_SINCE']
    } else { $null }
    Permissions = if ($null -ne $metadataRow) {
        [ordered]@{
            ViewDefinition = $metadataRow['CAN_VIEW_DEFINITION']
            ViewDatabaseState = $metadataRow['CAN_VIEW_DATABASE_STATE']
            ViewDatabasePerformanceState = $metadataRow['CAN_VIEW_DATABASE_PERFORMANCE_STATE']
            ViewServerState = $metadataRow['CAN_VIEW_SERVER_STATE']
            ViewServerPerformanceState = $metadataRow['CAN_VIEW_SERVER_PERFORMANCE_STATE']
        }
    } else { $null }
    Reports = @($reportResults)
}
$manifestPath = Join-Path $runOutput 'manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host ("manifest -> {0}" -f $manifestPath)

if ($failedReports.Count -gt 0 -and -not $AllowPartial) {
    $failedNames = ($failedReports | ForEach-Object Name) -join ', '
    throw "baseline collection is incomplete; failed report(s): $failedNames. Review manifest.json or rerun explicitly with -AllowPartial."
}

Write-Host ("baseline collection {0}; compare immutable run '{1}' with at least one other representative window before changing indexes." -f
    $manifest.Status, $runId)
