# Environment commissioning for the V160 trusted-authority writer boundary.
# This script never creates a login/user/password. Database users must already exist and their
# credentials stay in the deployment secret store. No switch means read-only validation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,
    [Parameter(Mandatory = $true)]
    [string]$RuntimeDatabaseUser,
    [string]$RmsWriterDatabaseUser,
    [string]$SysWriterDatabaseUser,
    [string]$EquipmentId,
    [string]$OperationKey,
    [string]$ArtifactId,
    [string]$ProductProfileId,
    [string]$PluginId,
    [string]$ProductDefinitionVersion,
    [string]$ProgramVersion,
    [string]$ProgramSchema,
    [string]$ProgramHash,
    [string]$BoundRecipeSnapshotSchema,
    [string]$BoundRecipeSnapshotHash,
    [switch]$ValidateOnly,
    [switch]$Apply,
    [switch]$WriterBootstrapOnly,
    [switch]$Decommission,
    [switch]$DecommissionAllBindings,
    [string]$ApprovedReleasePrincipalSidSha256,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
if (@($ValidateOnly, $Apply, $Decommission).Where({ $_ }).Count -gt 1) {
    throw 'ValidateOnly, Apply, and Decommission are mutually exclusive.'
}
if ($WriterBootstrapOnly -and -not $Apply) {
    throw 'WriterBootstrapOnly is valid only with Apply.'
}
if ($DecommissionAllBindings -and -not $Decommission) {
    throw 'DecommissionAllBindings is valid only with Decommission.'
}
$historicalReleaseApprovalProvided =
    $PSBoundParameters.ContainsKey('ApprovedReleasePrincipalSidSha256')
if ($historicalReleaseApprovalProvided) {
    if ($ApprovedReleasePrincipalSidSha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw 'ApprovedReleasePrincipalSidSha256 must be exact uppercase SHA-256 hex.'
    }
    if (-not $Apply -or $WriterBootstrapOnly) {
        throw 'ApprovedReleasePrincipalSidSha256 is valid only for a full Apply.'
    }
}

$principalNames = if ($Decommission) {
    @($RuntimeDatabaseUser)
} else {
    @($RuntimeDatabaseUser, $RmsWriterDatabaseUser, $SysWriterDatabaseUser)
}
if ($principalNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }) {
    throw $(if ($Decommission) {
        'Runtime database user is required for decommission.'
    } else {
        'Runtime, RMS writer, and SYS writer database users are all required.'
    })
}
if ($principalNames | Where-Object {
        $_ -ne $_.Trim() -or $_.Length -gt 128 -or $_ -match '[\x00-\x1F\x7F]' }) {
    throw 'Database user names cannot be padded, oversized, or contain control characters.'
}
$distinctNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($principalName in $principalNames) { [void]$distinctNames.Add($principalName) }
if (-not $Decommission -and $distinctNames.Count -ne 3) {
    throw 'Runtime, RMS writer, and SYS writer database users must be distinct.'
}
$coordinateValues = @(
    $EquipmentId,
    $OperationKey,
    $ArtifactId,
    $ProductProfileId,
    $PluginId,
    $ProductDefinitionVersion,
    $ProgramVersion,
    $ProgramSchema,
    $ProgramHash,
    $BoundRecipeSnapshotSchema,
    $BoundRecipeSnapshotHash
)
$requiresCoordinate = -not $WriterBootstrapOnly -and -not $Decommission
$requiresArtifactOnly = $Decommission -and -not $DecommissionAllBindings
if ($requiresArtifactOnly -and
    ([string]::IsNullOrWhiteSpace($ArtifactId) -or $ArtifactId -ne $ArtifactId.Trim() -or
     $ArtifactId.Length -gt 200 -or $ArtifactId -match '[\x00-\x1F\x7F]')) {
    throw 'ArtifactId is required for artifact-scoped decommission.'
}
if ($requiresCoordinate) {
    if ($coordinateValues | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ne $_.Trim() }) {
        throw 'The active product coordinate fields are required and cannot have boundary whitespace.'
    }
    if ($coordinateValues | Where-Object { $_ -match '[\x00-\x1F\x7F]' }) {
        throw 'The active product coordinate fields cannot contain control characters.'
    }
    if ($EquipmentId.Length -gt 100 -or $OperationKey.Length -gt 200 -or
        $ArtifactId.Length -gt 200 -or $ProductProfileId.Length -gt 100 -or
        $PluginId.Length -gt 200 -or $ProductDefinitionVersion.Length -gt 100 -or
        $ProgramVersion.Length -gt 100 -or $ProgramSchema.Length -gt 100 -or
        $BoundRecipeSnapshotSchema.Length -gt 100) {
        throw 'One or more active product coordinate fields exceed the V160 schema length.'
    }
    if ($ProgramHash -cnotmatch '^[0-9A-F]{64}$' -or
        $BoundRecipeSnapshotHash -cnotmatch '^[0-9A-F]{64}$') {
        throw 'ProgramHash and BoundRecipeSnapshotHash must be uppercase SHA-256 hex.'
    }
}

Add-Type -AssemblyName System.Data
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
if ([string]::IsNullOrWhiteSpace($builder.InitialCatalog)) {
    throw 'ConnectionString must name the target database explicitly.'
}

$mode = if ($Apply -and $WriterBootstrapOnly) { 'WriterBootstrapOnly' }
    elseif ($Apply) { 'Apply' }
    elseif ($Decommission -and $DecommissionAllBindings) { 'DecommissionAllBindings' }
    elseif ($Decommission) { 'Decommission' }
    else { 'ValidateOnly' }
$runId = [Guid]::NewGuid().ToString('N')
$startedAt = [DateTime]::UtcNow
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path (Get-Location) (
        'artifacts/commissioning/trusted-authority-security-{0}.json' -f $runId)
}
$absoluteEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
$evidenceParent = [System.IO.Path]::GetDirectoryName($absoluteEvidencePath)
if (-not [string]::IsNullOrWhiteSpace($evidenceParent)) {
    [void][System.IO.Directory]::CreateDirectory($evidenceParent)
}
# Reserve immutable evidence before opening the database. CreateNew makes an operator-supplied path
# single-use and keeps a process-crash marker rather than ever overwriting an earlier audit record.
$evidenceStream = [System.IO.FileStream]::new(
    $absoluteEvidencePath,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)

function Add-Parameter(
    [System.Data.SqlClient.SqlCommand]$Command,
    [string]$Name,
    [object]$Value) {
    [void]$Command.Parameters.AddWithValue(
        $Name,
        $(if ($null -eq $Value) { [DBNull]::Value } else { $Value }))
}

function Invoke-Table(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [string]$Sql,
    [hashtable]$Parameters = @{},
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $command = $Connection.CreateCommand()
    if ($null -ne $Transaction) { $command.Transaction = $Transaction }
    $command.CommandTimeout = 60
    $command.CommandText = $Sql
    foreach ($entry in $Parameters.GetEnumerator()) {
        Add-Parameter $command ("@" + $entry.Key) $entry.Value
    }
    $table = [System.Data.DataTable]::new()
    $reader = $command.ExecuteReader()
    try { $table.Load($reader) } finally { $reader.Dispose(); $command.Dispose() }
    return (, $table)
}

function Invoke-NonQuery(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [string]$Sql,
    [hashtable]$Parameters = @{},
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $command = $Connection.CreateCommand()
    if ($null -ne $Transaction) { $command.Transaction = $Transaction }
    $command.CommandTimeout = 60
    $command.CommandText = $Sql
    foreach ($entry in $Parameters.GetEnumerator()) {
        Add-Parameter $command ("@" + $entry.Key) $entry.Value
    }
    try { [void]$command.ExecuteNonQuery() } finally { $command.Dispose() }
}

function Get-SidSha256([byte[]]$Sid) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Sid))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Get-TextSha256([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Set-ReleaseProvenanceEvidence(
    [System.Collections.IDictionary]$Evidence,
    [System.Data.DataRow]$Row,
    [bool]$ApprovalProvided,
    [string]$ApprovedDigest,
    [bool]$ExistingExactBinding,
    [bool]$EvaluateNewBinding) {
    $releaseSidDigest = (Get-SidSha256 ([byte[]]$Row.RELEASED_DATABASE_PRINCIPAL_SID)).ToUpperInvariant()
    $currentWriterMatch = [int]$Row.CURRENT_WRITER_MATCH -eq 1
    $approvalMatched = $ApprovalProvided -and
        [string]::Equals($ApprovedDigest, $releaseSidDigest, [StringComparison]::Ordinal)
    $approvalRequired = $EvaluateNewBinding -and -not $ExistingExactBinding -and -not $currentWriterMatch
    $releasedAt = [DateTime]::SpecifyKind(
        [DateTime]$Row.RELEASED_AT,
        [DateTimeKind]::Utc).ToString('O')
    $Evidence.ReleaseProvenance = [ordered]@{
        Evaluated = $true
        PrincipalName = [string]$Row.RELEASED_DATABASE_PRINCIPAL_NAME
        PrincipalSidSha256 = $releaseSidDigest
        ReleasedAtUtc = $releasedAt
        MatchesCurrentSysWriter = $currentWriterMatch
        ExistingExactBinding = $ExistingExactBinding
        HistoricalApprovalRequired = $approvalRequired
        HistoricalApprovalProvided = $ApprovalProvided
        HistoricalApprovalMatched = $approvalMatched
    }
    return [ordered]@{
        SidSha256 = $releaseSidDigest
        ApprovalRequired = $approvalRequired
        ApprovalMatched = $approvalMatched
    }
}

function Get-PrincipalAudit(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [string]$Name,
    [string]$Runtime,
    [string]$RmsWriter,
    [string]$SysWriter) {
    $principal = Invoke-Table $Connection @'
SELECT P.principal_id AS PRINCIPAL_ID, P.type_desc AS TYPE_DESC, P.sid AS SID,
       SUSER_SNAME(P.sid) AS LOGIN_NAME,
       CAST(CASE WHEN EXISTS (
           SELECT 1 FROM sys.schemas S WHERE S.principal_id=P.principal_id) THEN 1 ELSE 0 END AS INT) AS OWNS_SCHEMA,
       CAST(CASE WHEN EXISTS (
           SELECT 1 FROM sys.objects O WHERE O.principal_id=P.principal_id) THEN 1 ELSE 0 END AS INT) AS OWNS_OBJECT,
       CAST(ISNULL(IS_ROLEMEMBER(N'db_owner', P.name), 0) AS INT) AS IS_DB_OWNER,
       CAST(ISNULL(IS_ROLEMEMBER(N'db_ddladmin', P.name), 0) AS INT) AS IS_DB_DDLADMIN,
       CAST(ISNULL(IS_ROLEMEMBER(N'db_securityadmin', P.name), 0) AS INT) AS IS_DB_SECURITYADMIN,
       CAST(ISNULL(IS_SRVROLEMEMBER(N'sysadmin', SUSER_SNAME(P.sid)), 0) AS INT) AS IS_SYSADMIN
  FROM sys.database_principals P
 WHERE P.name COLLATE Latin1_General_100_BIN2=@name COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @name));
'@ @{ name = $Name }
    if ($principal.Rows.Count -ne 1) {
        throw "Database user '$Name' does not exist exactly once. This script never creates users."
    }
    $row = $principal.Rows[0]
    $allowedTypes = @('SQL_USER', 'WINDOWS_USER', 'EXTERNAL_USER')
    if ($allowedTypes -notcontains [string]$row.TYPE_DESC) {
        throw "Database principal '$Name' has unsupported type '$($row.TYPE_DESC)'."
    }

    $effective = Invoke-Table $Connection @'
DECLARE @Sql NVARCHAR(MAX) =
    N'EXECUTE AS USER = ' + QUOTENAME(@name, '''') + N';
      SELECT
        CAST(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''CONTROL'') AS INT) AS CONTROL_DATABASE,
        CAST(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''ALTER ANY ROLE'') AS INT) AS ALTER_ANY_ROLE,
        CAST(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''ALTER ANY USER'') AS INT) AS ALTER_ANY_USER,
        CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''IMPERSONATE ANY USER''), 0) AS INT) AS IMPERSONATE_ANY_USER,
        CAST(HAS_PERMS_BY_NAME(' + QUOTENAME(@runtime, '''') + N', N''USER'', N''IMPERSONATE'') AS INT) AS IMPERSONATE_RUNTIME,
        CAST(HAS_PERMS_BY_NAME(' + QUOTENAME(@rmsWriter, '''') + N', N''USER'', N''IMPERSONATE'') AS INT) AS IMPERSONATE_RMS,
        CAST(HAS_PERMS_BY_NAME(' + QUOTENAME(@sysWriter, '''') + N', N''USER'', N''IMPERSONATE'') AS INT) AS IMPERSONATE_SYS,
        CAST(CASE WHEN
          HAS_PERMS_BY_NAME(N''dbo'', N''SCHEMA'', N''CONTROL'')=1 OR
          HAS_PERMS_BY_NAME(N''dbo'', N''SCHEMA'', N''ALTER'')=1 OR
          HAS_PERMS_BY_NAME(N''dbo'', N''SCHEMA'', N''TAKE OWNERSHIP'')=1 OR
          EXISTS (
            SELECT 1 FROM (VALUES
              (N''dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE''),
              (N''dbo.SYS_RELEASED_PROGRAM_ARTIFACT''),
              (N''dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION''),
              (N''dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE''),
              (N''dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY''),
              (N''dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING''),
              (N''dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY''),
              (N''dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE''),
              (N''dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE''),
              (N''dbo.SYS_RELEASE_PROGRAM_ARTIFACT''),
              (N''dbo.SYS_REVOKE_PROGRAM_ARTIFACT''),
              (N''dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY''),
              (N''dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE''),
              (N''dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE'')) O(NAME)
             WHERE HAS_PERMS_BY_NAME(O.NAME, N''OBJECT'', N''CONTROL'')=1
                OR HAS_PERMS_BY_NAME(O.NAME, N''OBJECT'', N''ALTER'')=1
                OR HAS_PERMS_BY_NAME(O.NAME, N''OBJECT'', N''TAKE OWNERSHIP'')=1)
          OR EXISTS (
            SELECT 1 FROM (VALUES
              (N''NexaOneProjectionRuntime''),
              (N''NexaOneRmsEvidenceWriter''),
              (N''NexaOneSysReleaseWriter'')) R(NAME)
             WHERE HAS_PERMS_BY_NAME(R.NAME, N''ROLE'', N''CONTROL'')=1
                OR HAS_PERMS_BY_NAME(R.NAME, N''ROLE'', N''ALTER'')=1)
          THEN 1 ELSE 0 END AS INT) AS UNSAFE_SECURITY_SCOPE;
      REVERT;';
EXEC sys.sp_executesql @Sql;
'@ @{ name = $Name; runtime = $Runtime; rmsWriter = $RmsWriter; sysWriter = $SysWriter }
    $permissions = $effective.Rows[0]

    $serverControl = 0
    $serverImpersonate = 0
    $serverAlter = 0
    $loginName = [string]$row.LOGIN_NAME
    if (-not [string]::IsNullOrWhiteSpace($loginName)) {
        # Fail closed if the commissioning operator cannot evaluate the mapped login. A database
        # user without a mapped login has no server permission surface to audit here.
        $serverEffective = Invoke-Table $Connection @'
DECLARE @Sql NVARCHAR(MAX) =
    N'EXECUTE AS LOGIN = ' + QUOTENAME(@login, '''') + N';
      SELECT
        CAST(ISNULL(HAS_PERMS_BY_NAME(NULL, N''SERVER'', N''CONTROL SERVER''), 0) AS INT) AS CONTROL_SERVER,
        CAST(ISNULL(HAS_PERMS_BY_NAME(NULL, N''SERVER'', N''IMPERSONATE ANY LOGIN''), 0) AS INT) AS IMPERSONATE_ANY_LOGIN,
        CAST(CASE WHEN ISNULL(HAS_PERMS_BY_NAME(NULL, N''SERVER'', N''ALTER ANY LOGIN''), 0)=1
                    OR ISNULL(HAS_PERMS_BY_NAME(NULL, N''SERVER'', N''ALTER ANY SERVER ROLE''), 0)=1
                  THEN 1 ELSE 0 END AS INT) AS UNSAFE_SERVER_ALTER;
      REVERT;';
EXEC sys.sp_executesql @Sql;
'@ @{ login = $loginName }
        $serverControl = [int]$serverEffective.Rows[0].CONTROL_SERVER
        $serverImpersonate = [int]$serverEffective.Rows[0].IMPERSONATE_ANY_LOGIN
        $serverAlter = [int]$serverEffective.Rows[0].UNSAFE_SERVER_ALTER
    }

    $privileged = @(@(
        [int]$row.OWNS_SCHEMA,
        [int]$row.OWNS_OBJECT,
        [int]$row.IS_DB_OWNER,
        [int]$row.IS_DB_DDLADMIN,
        [int]$row.IS_DB_SECURITYADMIN,
        [int]$row.IS_SYSADMIN,
        [int]$permissions.CONTROL_DATABASE,
        [int]$permissions.ALTER_ANY_ROLE,
        [int]$permissions.ALTER_ANY_USER,
        [int]$permissions.IMPERSONATE_ANY_USER,
        $(if (-not [string]::Equals($Name, $Runtime, [StringComparison]::Ordinal)) {
            [int]$permissions.IMPERSONATE_RUNTIME } else { 0 }),
        $(if (-not [string]::Equals($Name, $RmsWriter, [StringComparison]::Ordinal)) {
            [int]$permissions.IMPERSONATE_RMS } else { 0 }),
        $(if (-not [string]::Equals($Name, $SysWriter, [StringComparison]::Ordinal)) {
            [int]$permissions.IMPERSONATE_SYS } else { 0 }),
        [int]$permissions.UNSAFE_SECURITY_SCOPE,
        $serverControl,
        $serverImpersonate,
        $serverAlter
    ) | Where-Object { $_ -ne 0 })
    if ($privileged.Count -gt 0) {
        throw "Database principal '$Name' is privileged/owner/impersonating and cannot cross the V160 DENY boundary."
    }

    $sidBytes = [byte[]]$row.SID
    return [ordered]@{
        Name = $Name
        Type = [string]$row.TYPE_DESC
        SidSha256 = Get-SidSha256 $sidBytes
    }
}

function Set-RoleMemberships(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction,
    [bool]$Remove,
    [bool]$WritersOnly) {
    Invoke-NonQuery $Connection @'
DECLARE @Desired TABLE
  (ROLE_NAME SYSNAME NOT NULL, USER_NAME SYSNAME NOT NULL, PRIMARY KEY (ROLE_NAME, USER_NAME));
IF @remove=0
BEGIN
  IF @writersOnly=0
    INSERT INTO @Desired (ROLE_NAME, USER_NAME)
      VALUES (N'NexaOneProjectionRuntime', @runtime);
  INSERT INTO @Desired (ROLE_NAME, USER_NAME)
    VALUES (N'NexaOneRmsEvidenceWriter', @rms),
           (N'NexaOneSysReleaseWriter', @sys);
END;

-- Full Apply/Decommission owns all three roles. Writer bootstrap owns only the two writer roles and
-- leaves runtime membership untouched, but it still removes stale/nested/cross-role writer grants.
DECLARE @Role SYSNAME, @User SYSNAME, @Sql NVARCHAR(MAX);
DECLARE DropCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT R.name, M.name
    FROM sys.database_role_members RM
    JOIN sys.database_principals R ON R.principal_id=RM.role_principal_id
    JOIN sys.database_principals M ON M.principal_id=RM.member_principal_id
   WHERE R.name IN (N'NexaOneProjectionRuntime', N'NexaOneRmsEvidenceWriter', N'NexaOneSysReleaseWriter')
     AND (@writersOnly=0 OR R.name<>N'NexaOneProjectionRuntime')
     AND NOT EXISTS (
         SELECT 1 FROM @Desired D
          WHERE D.ROLE_NAME COLLATE Latin1_General_100_BIN2=R.name COLLATE Latin1_General_100_BIN2
            AND D.USER_NAME COLLATE Latin1_General_100_BIN2=M.name COLLATE Latin1_General_100_BIN2)
   ORDER BY R.name, M.name;
OPEN DropCursor;
FETCH NEXT FROM DropCursor INTO @Role, @User;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql=N'ALTER ROLE '+QUOTENAME(@Role)+N' DROP MEMBER '+QUOTENAME(@User)+N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM DropCursor INTO @Role, @User;
END;
CLOSE DropCursor;
DEALLOCATE DropCursor;

DECLARE AddCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT D.ROLE_NAME, D.USER_NAME
    FROM @Desired D
   WHERE IS_ROLEMEMBER(D.ROLE_NAME, D.USER_NAME)<>1
   ORDER BY D.ROLE_NAME, D.USER_NAME;
OPEN AddCursor;
FETCH NEXT FROM AddCursor INTO @Role, @User;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql=N'ALTER ROLE '+QUOTENAME(@Role)+N' ADD MEMBER '+QUOTENAME(@User)+N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM AddCursor INTO @Role, @User;
END;
CLOSE AddCursor;
DEALLOCATE AddCursor;
'@ @{
        runtime = $RuntimeDatabaseUser
        rms = [string]$RmsWriterDatabaseUser
        sys = [string]$SysWriterDatabaseUser
        remove = if ($Remove) { 1 } else { 0 }
        writersOnly = if ($WritersOnly) { 1 } else { 0 }
    } $Transaction
}

function Assert-DistinctPrincipalSids(
    [System.Data.SqlClient.SqlConnection]$Connection) {
    $counts = Invoke-Table $Connection @'
SELECT COUNT(*) AS PRINCIPAL_COUNT,
       COUNT(DISTINCT CONVERT(VARCHAR(170), P.sid, 2)) AS DISTINCT_SID_COUNT
  FROM sys.database_principals P
 WHERE (P.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
    OR (P.name COLLATE Latin1_General_100_BIN2=@rms COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @rms)))
    OR (P.name COLLATE Latin1_General_100_BIN2=@sys COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @sys)));
'@ @{ runtime=$RuntimeDatabaseUser; rms=$RmsWriterDatabaseUser; sys=$SysWriterDatabaseUser }
    if ([int]$counts.Rows[0].PRINCIPAL_COUNT -ne 3 -or
        [int]$counts.Rows[0].DISTINCT_SID_COUNT -ne 3) {
        throw 'Runtime, RMS writer, and SYS writer database principals must have three distinct SIDs.'
    }
}

function Get-ReleaseProvenanceForBinding(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction) {
    $table = Invoke-Table $Connection @'
DECLARE @runtimeSid VARBINARY(85) = (
  SELECT P.sid FROM sys.database_principals P
   WHERE P.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)));
IF @runtimeSid IS NULL THROW 51620, 'Runtime database principal disappeared during commissioning', 1;

DECLARE @sysWriterSid VARBINARY(85) = (
  SELECT P.sid FROM sys.database_principals P
   WHERE P.name COLLATE Latin1_General_100_BIN2=@sysWriter COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @sysWriter)));
IF @sysWriterSid IS NULL THROW 51620, 'SYS writer database principal disappeared during commissioning', 1;

DECLARE @Release TABLE (
  RELEASED_DATABASE_PRINCIPAL_NAME NVARCHAR(128) NOT NULL,
  RELEASED_DATABASE_PRINCIPAL_SID VARBINARY(85) NOT NULL,
  RELEASED_AT DATETIME2(7) NOT NULL);
INSERT INTO @Release
  (RELEASED_DATABASE_PRINCIPAL_NAME, RELEASED_DATABASE_PRINCIPAL_SID, RELEASED_AT)
SELECT A.RELEASED_DATABASE_PRINCIPAL_NAME, A.RELEASED_DATABASE_PRINCIPAL_SID, A.RELEASED_AT
  FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
 WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
   AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@equipment COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @equipment))
   AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@operation COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @operation))
   AND A.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@productProfile COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_PROFILE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @productProfile))
   AND A.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@plugin COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @plugin))
   AND A.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2
         =@productDefinitionVersion COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_DEFINITION_VERSION))
         =DATALENGTH(CONVERT(NVARCHAR(MAX), @productDefinitionVersion))
   AND A.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@programVersion COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @programVersion))
   AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@programSchema COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @programSchema))
   AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@programHash COLLATE Latin1_General_100_BIN2
   AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2
         =@recipeSchema COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))
         =DATALENGTH(CONVERT(NVARCHAR(MAX), @recipeSchema))
   AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2
         =@recipeHash COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(A.RELEASED_DATABASE_PRINCIPAL_NAME)>0
   AND DATALENGTH(A.RELEASED_DATABASE_PRINCIPAL_SID)>0;
IF @@ROWCOUNT<>1
  THROW 51622, 'Commissioned product coordinate is not an exact released artifact', 1;

IF EXISTS (
  SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION V WITH (UPDLOCK, HOLDLOCK)
   WHERE V.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), V.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact)))
  THROW 51623, 'A revoked program artifact cannot be commissioned', 1;

SELECT R.RELEASED_DATABASE_PRINCIPAL_NAME,
       R.RELEASED_DATABASE_PRINCIPAL_SID,
       R.RELEASED_AT,
       CAST(CASE WHEN
         R.RELEASED_DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
           =@sysWriter COLLATE Latin1_General_100_BIN2
         AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.RELEASED_DATABASE_PRINCIPAL_NAME))
           =DATALENGTH(CONVERT(NVARCHAR(MAX), @sysWriter))
         AND R.RELEASED_DATABASE_PRINCIPAL_SID=@sysWriterSid
         THEN 1 ELSE 0 END AS INT) AS CURRENT_WRITER_MATCH,
       CAST(CASE WHEN EXISTS (
         SELECT 1 FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
          WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime))
            AND B.DATABASE_PRINCIPAL_SID=@runtimeSid
            AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
            AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@equipment COLLATE Latin1_General_100_BIN2
            AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@operation COLLATE Latin1_General_100_BIN2
            AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@productProfile COLLATE Latin1_General_100_BIN2
            AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@plugin COLLATE Latin1_General_100_BIN2
            AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2=@productDefinitionVersion COLLATE Latin1_General_100_BIN2
            AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@programVersion COLLATE Latin1_General_100_BIN2
            AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@programSchema COLLATE Latin1_General_100_BIN2
            AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@programHash COLLATE Latin1_General_100_BIN2
            AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@recipeSchema COLLATE Latin1_General_100_BIN2
            AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@recipeHash COLLATE Latin1_General_100_BIN2)
         THEN 1 ELSE 0 END AS INT) AS EXACT_BINDING_EXISTS
  FROM @Release R;
'@ @{
        runtime = $RuntimeDatabaseUser
        sysWriter = [string]$SysWriterDatabaseUser
        equipment = [string]$EquipmentId
        operation = [string]$OperationKey
        artifact = [string]$ArtifactId
        productProfile = [string]$ProductProfileId
        plugin = [string]$PluginId
        productDefinitionVersion = [string]$ProductDefinitionVersion
        programVersion = [string]$ProgramVersion
        programSchema = [string]$ProgramSchema
        programHash = [string]$ProgramHash
        recipeSchema = [string]$BoundRecipeSnapshotSchema
        recipeHash = [string]$BoundRecipeSnapshotHash
    } $Transaction
    return (, $table)
}

function Set-RuntimeProductBinding(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction,
    [bool]$Remove,
    [bool]$RemoveAll) {
    Invoke-NonQuery $Connection @'
DECLARE @sid VARBINARY(85) = (
  SELECT P.sid FROM sys.database_principals P
   WHERE P.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)));
IF @remove=1
BEGIN
  DELETE B
    FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B
   WHERE ((B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
             = @runtime COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))
                 =DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
          OR (@sid IS NOT NULL AND B.DATABASE_PRINCIPAL_SID=@sid))
     AND (@removeAll=1 OR (
          B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
          AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))
                =DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))));
  RETURN;
END;

IF @sid IS NULL THROW 51620, 'Runtime database principal disappeared during commissioning', 1;
IF @remove=0 AND NOT EXISTS (
  SELECT 1
    FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
   WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
     AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@equipment COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @equipment))
     AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@operation COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @operation))
     AND A.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@productProfile COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_PROFILE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @productProfile))
     AND A.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@plugin COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @plugin))
     AND A.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2
           =@productDefinitionVersion COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_DEFINITION_VERSION))
           =DATALENGTH(CONVERT(NVARCHAR(MAX), @productDefinitionVersion))
     AND A.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@programVersion COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @programVersion))
     AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@programSchema COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @programSchema))
     AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@programHash COLLATE Latin1_General_100_BIN2
     AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2
           =@recipeSchema COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))
           =DATALENGTH(CONVERT(NVARCHAR(MAX), @recipeSchema))
     AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2
           =@recipeHash COLLATE Latin1_General_100_BIN2)
  THROW 51622, 'Commissioned product coordinate is not an exact released artifact', 1;

IF @remove=0 AND EXISTS (
  SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION V WITH (UPDLOCK, HOLDLOCK)
   WHERE V.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), V.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
  THROW 51623, 'A revoked program artifact cannot be commissioned', 1;

IF @remove=0 AND EXISTS (
  SELECT 1 FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
   WHERE (B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
          OR B.DATABASE_PRINCIPAL_SID=@sid)
     AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2)
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
     WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime))
       AND B.DATABASE_PRINCIPAL_SID=@sid
       AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
       AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@equipment COLLATE Latin1_General_100_BIN2
       AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@operation COLLATE Latin1_General_100_BIN2
       AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@productProfile COLLATE Latin1_General_100_BIN2
       AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@plugin COLLATE Latin1_General_100_BIN2
       AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2=@productDefinitionVersion COLLATE Latin1_General_100_BIN2
       AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@programVersion COLLATE Latin1_General_100_BIN2
       AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@programSchema COLLATE Latin1_General_100_BIN2
       AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@programHash COLLATE Latin1_General_100_BIN2
       AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@recipeSchema COLLATE Latin1_General_100_BIN2
       AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@recipeHash COLLATE Latin1_General_100_BIN2)
    THROW 51624, 'Existing runtime artifact binding conflicts with the requested exact coordinate', 1;
END
ELSE IF @remove=0
  INSERT INTO dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING
    (DATABASE_PRINCIPAL_NAME, DATABASE_PRINCIPAL_SID, EQUIPMENT_ID, OPERATION_KEY,
     ARTIFACT_ID, PRODUCT_PROFILE_ID, PLUGIN_ID, PRODUCT_DEFINITION_VERSION,
     PROGRAM_VERSION, PROGRAM_SCHEMA, PROGRAM_HASH, BOUND_RECIPE_SNAPSHOT_SCHEMA,
     BOUND_RECIPE_SNAPSHOT_HASH,
     COMMISSIONED_AT, COMMISSIONED_BY)
  VALUES
    (@runtime, @sid, @equipment, @operation, @artifact, @productProfile, @plugin,
     @productDefinitionVersion, @programVersion, @programSchema, @programHash,
     @recipeSchema, @recipeHash, SYSUTCDATETIME(), ORIGINAL_LOGIN());
'@ @{
        runtime = $RuntimeDatabaseUser
        equipment = [string]$EquipmentId
        operation = [string]$OperationKey
        artifact = [string]$ArtifactId
        productProfile = [string]$ProductProfileId
        plugin = [string]$PluginId
        productDefinitionVersion = [string]$ProductDefinitionVersion
        programVersion = [string]$ProgramVersion
        programSchema = [string]$ProgramSchema
        programHash = [string]$ProgramHash
        recipeSchema = [string]$BoundRecipeSnapshotSchema
        recipeHash = [string]$BoundRecipeSnapshotHash
        remove = if ($Remove) { 1 } else { 0 }
        removeAll = if ($RemoveAll) { 1 } else { 0 }
    } $Transaction
}

function Get-RuntimeProductBinding(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $table = Invoke-Table $Connection @'
SELECT B.DATABASE_PRINCIPAL_SID, B.COMMISSIONED_AT, B.COMMISSIONED_BY,
       A.RELEASED_DATABASE_PRINCIPAL_NAME,
       A.RELEASED_DATABASE_PRINCIPAL_SID,
       A.RELEASED_AT,
       CAST(CASE WHEN
         A.RELEASED_DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
           =@sysWriter COLLATE Latin1_General_100_BIN2
         AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.RELEASED_DATABASE_PRINCIPAL_NAME))
           =DATALENGTH(CONVERT(NVARCHAR(MAX), @sysWriter))
         AND A.RELEASED_DATABASE_PRINCIPAL_SID=(
           SELECT SP.sid FROM sys.database_principals SP
            WHERE SP.name COLLATE Latin1_General_100_BIN2=@sysWriter COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(CONVERT(NVARCHAR(MAX), SP.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @sysWriter)))
         THEN 1 ELSE 0 END AS INT) AS CURRENT_WRITER_MATCH
  FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B
  JOIN sys.database_principals P
    ON P.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime))
   AND P.sid=B.DATABASE_PRINCIPAL_SID
  JOIN dbo.SYS_RELEASED_PROGRAM_ARTIFACT A
    ON A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=B.ARTIFACT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))
   AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=B.EQUIPMENT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID))
   AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2=B.OPERATION_KEY
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY))
   AND A.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=B.PRODUCT_PROFILE_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_PROFILE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID))
   AND A.PLUGIN_ID COLLATE Latin1_General_100_BIN2=B.PLUGIN_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID))
   AND A.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2=B.PRODUCT_DEFINITION_VERSION
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_DEFINITION_VERSION))
         =DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION))
   AND A.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=B.PROGRAM_VERSION
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION))
   AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=B.PROGRAM_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA))
   AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=B.PROGRAM_HASH
   AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=B.BOUND_RECIPE_SNAPSHOT_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))
         =DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA))
   AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=B.BOUND_RECIPE_SNAPSHOT_HASH
 WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
         = @runtime COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime))
   AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @equipment COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @equipment))
   AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @operation COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @operation))
   AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @artifact COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))
   AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2
         = @productProfile COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @productProfile))
   AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2 = @plugin COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @plugin))
   AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2
         = @productDefinitionVersion COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @productDefinitionVersion))
   AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2 = @programVersion COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @programVersion))
   AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = @programSchema COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @programSchema))
   AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = @programHash COLLATE Latin1_General_100_BIN2
   AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2
         = @recipeSchema COLLATE Latin1_General_100_BIN2
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA))
         = DATALENGTH(CONVERT(NVARCHAR(MAX), @recipeSchema))
    AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2
          = @recipeHash COLLATE Latin1_General_100_BIN2;
'@ @{
        runtime = $RuntimeDatabaseUser
        sysWriter = [string]$SysWriterDatabaseUser
        equipment = $EquipmentId
        operation = $OperationKey
        artifact = [string]$ArtifactId
        productProfile = $ProductProfileId
        plugin = $PluginId
        productDefinitionVersion = $ProductDefinitionVersion
        programVersion = $ProgramVersion
        programSchema = $ProgramSchema
        programHash = $ProgramHash
        recipeSchema = $BoundRecipeSnapshotSchema
        recipeHash = $BoundRecipeSnapshotHash
    } $Transaction
    return (, $table)
}

function Get-RuntimePrincipalBinding(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null,
    [bool]$AllArtifacts = $false,
    [bool]$LockRows = $false) {
    $table = Invoke-Table $Connection @'
DECLARE @sid VARBINARY(85) = (
  SELECT P.sid FROM sys.database_principals P
   WHERE P.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
     AND DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)));
IF @lockRows=1
  SELECT B.DATABASE_PRINCIPAL_SID, B.ARTIFACT_ID, B.PROGRAM_HASH,
         B.BOUND_RECIPE_SNAPSHOT_HASH
    FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
   WHERE ((B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
             = @runtime COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
          OR (@sid IS NOT NULL AND B.DATABASE_PRINCIPAL_SID=@sid))
     AND (@allArtifacts=1 OR (
         B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
         AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))
               =DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))));
ELSE
  SELECT B.DATABASE_PRINCIPAL_SID, B.ARTIFACT_ID, B.PROGRAM_HASH,
         B.BOUND_RECIPE_SNAPSHOT_HASH
    FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B
   WHERE ((B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
             = @runtime COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
          OR (@sid IS NOT NULL AND B.DATABASE_PRINCIPAL_SID=@sid))
    AND (@allArtifacts=1 OR (
       B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@artifact COLLATE Latin1_General_100_BIN2
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))
              =DATALENGTH(CONVERT(NVARCHAR(MAX), @artifact))));
'@ @{
        runtime = $RuntimeDatabaseUser
        artifact = $ArtifactId
        allArtifacts = if ($AllArtifacts) { 1 } else { 0 }
        lockRows = if ($LockRows) { 1 } else { 0 }
    } $Transaction
    return (, $table)
}

function Get-PermissionMatrix(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [string]$Name,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $table = Invoke-Table $Connection @'
DECLARE @Sql NVARCHAR(MAX) =
    N'EXECUTE AS USER = ' + QUOTENAME(@name, '''') + N';
      SELECT
        CAST(IS_ROLEMEMBER(N''NexaOneProjectionRuntime'') AS INT) AS RUNTIME_ROLE,
        CAST(IS_ROLEMEMBER(N''NexaOneRmsEvidenceWriter'') AS INT) AS RMS_ROLE,
        CAST(IS_ROLEMEMBER(N''NexaOneSysReleaseWriter'') AS INT) AS SYS_ROLE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE'', N''OBJECT'', N''SELECT'') AS INT) AS RMS_SELECT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE'', N''OBJECT'', N''INSERT'') AS INT) AS RMS_INSERT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.SYS_RELEASED_PROGRAM_ARTIFACT'', N''OBJECT'', N''INSERT'') AS INT) AS SYS_INSERT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION'', N''OBJECT'', N''INSERT'') AS INT) AS REVOKE_INSERT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY'', N''OBJECT'', N''INSERT'') AS INT) AS AUTHORITY_INSERT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'', N''OBJECT'', N''SELECT'') AS INT) AS BINDING_SELECT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'', N''OBJECT'', N''INSERT'') AS INT) AS BINDING_INSERT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'', N''OBJECT'', N''UPDATE'') AS INT) AS BINDING_UPDATE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'', N''OBJECT'', N''DELETE'') AS INT) AS BINDING_DELETE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY'', N''OBJECT'', N''SELECT'') AS INT) AS ACTIVE_AUTHORITY_SELECT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE'', N''OBJECT'', N''SELECT'') AS INT) AS AUTHORITY_FENCE_SELECT,
        CAST(HAS_PERMS_BY_NAME(N''dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE'', N''OBJECT'', N''EXECUTE'') AS INT) AS CAPTURE_EXECUTE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.SYS_RELEASE_PROGRAM_ARTIFACT'', N''OBJECT'', N''EXECUTE'') AS INT) AS RELEASE_EXECUTE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.SYS_REVOKE_PROGRAM_ARTIFACT'', N''OBJECT'', N''EXECUTE'') AS INT) AS REVOKE_EXECUTE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY'', N''OBJECT'', N''EXECUTE'') AS INT) AS AUTHORITY_EXECUTE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE'', N''OBJECT'', N''EXECUTE'') AS INT) AS AUTHORITY_LOCK_EXECUTE,
        CAST(HAS_PERMS_BY_NAME(N''dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE'', N''OBJECT'', N''EXECUTE'') AS INT) AS LINEAGE_EXECUTE;
      REVERT;';
EXEC sys.sp_executesql @Sql;
'@ @{ name = $Name } $Transaction
    $row = $table.Rows[0]
    return [ordered]@{
        RuntimeRole = [int]$row.RUNTIME_ROLE
        RmsRole = [int]$row.RMS_ROLE
        SysRole = [int]$row.SYS_ROLE
        RmsSelect = [int]$row.RMS_SELECT
        RmsInsert = [int]$row.RMS_INSERT
        SysInsert = [int]$row.SYS_INSERT
        RevocationInsert = [int]$row.REVOKE_INSERT
        AuthorityInsert = [int]$row.AUTHORITY_INSERT
        BindingSelect = [int]$row.BINDING_SELECT
        BindingInsert = [int]$row.BINDING_INSERT
        BindingUpdate = [int]$row.BINDING_UPDATE
        BindingDelete = [int]$row.BINDING_DELETE
        ActiveAuthoritySelect = [int]$row.ACTIVE_AUTHORITY_SELECT
        AuthorityFenceSelect = [int]$row.AUTHORITY_FENCE_SELECT
        CaptureExecute = [int]$row.CAPTURE_EXECUTE
        ReleaseExecute = [int]$row.RELEASE_EXECUTE
        RevokeExecute = [int]$row.REVOKE_EXECUTE
        AuthorityExecute = [int]$row.AUTHORITY_EXECUTE
        AuthorityLockExecute = [int]$row.AUTHORITY_LOCK_EXECUTE
        LineageExecute = [int]$row.LINEAGE_EXECUTE
    }
}

function Assert-AuthorityExecuteAcl(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $drift = Invoke-Table $Connection @'
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)<>1
   AND ISNULL(HAS_PERMS_BY_NAME(NULL, N'SERVER', N'CONTROL SERVER'), 0)<>1
   AND ISNULL(HAS_PERMS_BY_NAME(NULL, N'SERVER', N'VIEW ANY DEFINITION'), 0)<>1
  THROW 51629, 'Commissioning principal cannot audit server impersonation permissions completely', 1;

;WITH ProcedureAllowlist AS (
  SELECT OBJECT_ID(N'dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE') AS OBJECT_ID,
         N'NexaOneRmsEvidenceWriter' AS PRINCIPAL_NAME
  UNION ALL SELECT OBJECT_ID(N'dbo.SYS_RELEASE_PROGRAM_ARTIFACT'), N'NexaOneSysReleaseWriter'
  UNION ALL SELECT OBJECT_ID(N'dbo.SYS_REVOKE_PROGRAM_ARTIFACT'), N'NexaOneSysReleaseWriter'
  UNION ALL SELECT OBJECT_ID(N'dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY'), N'NexaOneProjectionRuntime'
  UNION ALL SELECT OBJECT_ID(N'dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE'), N'NexaOneProjectionRuntime'
  UNION ALL SELECT OBJECT_ID(N'dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE'), N'NexaOneProjectionRuntime'
), UnsafeGrant AS (
  SELECT CONCAT(N'database:', P.name) AS DRIFT
    FROM sys.database_permissions D
    JOIN sys.database_principals P ON P.principal_id=D.grantee_principal_id
   WHERE D.class=0
     AND D.permission_name IN (
       N'EXECUTE', N'CONTROL', N'ALTER ANY ROLE', N'ALTER ANY USER',
       N'ALTER ANY DATABASE SCOPED CONFIGURATION', N'IMPERSONATE ANY USER')
     AND D.state IN ('G','W') AND P.name<>N'dbo'
  UNION ALL
  SELECT CONCAT(N'schema:', P.name)
    FROM sys.database_permissions D
    JOIN sys.database_principals P ON P.principal_id=D.grantee_principal_id
    JOIN sys.schemas S ON S.schema_id=D.major_id
   WHERE D.class=3 AND S.principal_id=DATABASE_PRINCIPAL_ID(N'dbo')
     AND D.permission_name IN (N'EXECUTE', N'SELECT', N'CONTROL', N'ALTER', N'TAKE OWNERSHIP')
     AND D.state IN ('G','W') AND P.name<>N'dbo'
  UNION ALL
  SELECT CONCAT(N'object:', OBJECT_SCHEMA_NAME(D.major_id), N'.', OBJECT_NAME(D.major_id), N':', P.name)
    FROM sys.database_permissions D
    JOIN sys.database_principals P ON P.principal_id=D.grantee_principal_id
    JOIN ProcedureAllowlist A ON A.OBJECT_ID=D.major_id
   WHERE D.class=1 AND D.state IN ('G','W')
     AND (D.permission_name IN (N'CONTROL', N'ALTER', N'TAKE OWNERSHIP')
          OR (D.permission_name=N'EXECUTE'
              AND (P.name COLLATE Latin1_General_100_BIN2
                     <> A.PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
                   OR DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))
                     <>DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRINCIPAL_NAME)))))
  UNION ALL
  SELECT CONCAT(N'impersonate-user:', T.name, N':', G.name)
    FROM sys.database_permissions D
    JOIN sys.database_principals G ON G.principal_id=D.grantee_principal_id
    JOIN sys.database_principals T ON T.principal_id=D.major_id
   WHERE D.class=4 AND D.state IN ('G','W')
     AND D.permission_name IN (N'IMPERSONATE', N'CONTROL', N'ALTER', N'TAKE OWNERSHIP')
     AND G.principal_id<>1
     AND (
       (T.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), T.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
       OR (T.name COLLATE Latin1_General_100_BIN2=@rms COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), T.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @rms)))
       OR (T.name COLLATE Latin1_General_100_BIN2=@sys COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), T.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @sys))))
  UNION ALL
  SELECT CONCAT(N'server:', G.name)
    FROM sys.server_permissions D
    JOIN sys.server_principals G ON G.principal_id=D.grantee_principal_id
   WHERE D.class=100
     AND D.state COLLATE Latin1_General_100_BIN2 IN (N'G',N'W')
     AND D.permission_name COLLATE Latin1_General_100_BIN2 IN (
       N'CONTROL SERVER', N'IMPERSONATE ANY LOGIN', N'ALTER ANY LOGIN', N'ALTER ANY SERVER ROLE')
     AND G.principal_id<>1
     AND (
       G.name COLLATE Latin1_General_100_BIN2
         <>N'sysadmin' COLLATE Latin1_General_100_BIN2
       OR DATALENGTH(CONVERT(NVARCHAR(MAX), G.name))<>DATALENGTH(N'sysadmin'))
     -- SQL Server creates this exact certificate-mapped login so signed Policy-Based Management
     -- modules can cross their server boundary. It cannot authenticate as an ordinary login. Keep
     -- the exception pinned to the built-in name, certificate type, CONTROL SERVER, and system
     -- grantor; every user-defined certificate/login/role with broad permission remains unsafe.
     AND NOT (
       D.permission_name COLLATE Latin1_General_100_BIN2
         =N'CONTROL SERVER' COLLATE Latin1_General_100_BIN2
       AND D.grantor_principal_id=1
       AND G.type COLLATE Latin1_General_100_BIN2
         =N'C' COLLATE Latin1_General_100_BIN2
       AND G.name COLLATE Latin1_General_100_BIN2
         =N'##MS_PolicySigningCertificate##' COLLATE Latin1_General_100_BIN2
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), G.name))
         =DATALENGTH(N'##MS_PolicySigningCertificate##'))
  UNION ALL
  SELECT CONCAT(N'impersonate-login:', T.name, N':', G.name)
    FROM sys.server_permissions D
    JOIN sys.server_principals G ON G.principal_id=D.grantee_principal_id
    JOIN sys.server_principals T ON T.principal_id=D.major_id
    JOIN sys.database_principals U ON U.sid=T.sid
   WHERE D.class=101
     AND D.state COLLATE Latin1_General_100_BIN2 IN (N'G',N'W')
     AND D.permission_name COLLATE Latin1_General_100_BIN2
       IN (N'IMPERSONATE', N'CONTROL', N'ALTER')
     AND G.principal_id<>1
     AND (
       G.name COLLATE Latin1_General_100_BIN2
         <>N'sysadmin' COLLATE Latin1_General_100_BIN2
       OR DATALENGTH(CONVERT(NVARCHAR(MAX), G.name))<>DATALENGTH(N'sysadmin'))
     AND (
       (U.name COLLATE Latin1_General_100_BIN2=@runtime COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @runtime)))
       OR (U.name COLLATE Latin1_General_100_BIN2=@rms COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @rms)))
       OR (U.name COLLATE Latin1_General_100_BIN2=@sys COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.name))=DATALENGTH(CONVERT(NVARCHAR(MAX), @sys))))
)
SELECT DRIFT FROM UnsafeGrant;
'@ @{
        runtime = $RuntimeDatabaseUser
        rms = $RmsWriterDatabaseUser
        sys = $SysWriterDatabaseUser
    } $Transaction
    if ($drift.Rows.Count -ne 0) {
        $driftIdentifiers = @(
            $drift.Rows |
                ForEach-Object { [string]$_.DRIFT } |
                Sort-Object -Unique)
        throw ("Broad or unexpected EXECUTE/IMPERSONATE GRANT can bypass the V160 " +
            "trusted-writer role boundary. Drift: {0}" -f ($driftIdentifiers -join ', '))
    }
}

function Assert-AuthorityDatabaseBoundary(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $drift = Invoke-Table $Connection @'
DECLARE @BoundaryDrift TABLE (DRIFT NVARCHAR(517) NOT NULL);

IF EXISTS (
    SELECT 1
      FROM sys.databases
     WHERE database_id=DB_ID()
       AND (is_trustworthy_on<>0 OR is_db_chaining_on<>0))
  INSERT INTO @BoundaryDrift (DRIFT) VALUES (N'database:TRUSTWORTHY_OR_DB_CHAINING');

IF NOT EXISTS (
    SELECT 1
      FROM sys.configurations
     WHERE name=N'cross db ownership chaining'
       AND CONVERT(INT, value_in_use)=0)
  INSERT INTO @BoundaryDrift (DRIFT) VALUES (N'server:CROSS_DB_OWNERSHIP_CHAINING');

DECLARE @TrustedTables TABLE (OBJECT_ID INT NOT NULL PRIMARY KEY);
INSERT INTO @TrustedTables (OBJECT_ID) VALUES
  (OBJECT_ID(N'dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE')),
  (OBJECT_ID(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT')),
  (OBJECT_ID(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION')),
  (OBJECT_ID(N'dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE')),
  (OBJECT_ID(N'dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY')),
  (OBJECT_ID(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'));

;WITH LocalSynonymEdge AS (
  SELECT S.object_id AS SYNONYM_ID,
         OBJECT_ID(
           QUOTENAME(COALESCE(PARSENAME(S.base_object_name, 2), N'dbo')) + N'.' +
           QUOTENAME(PARSENAME(S.base_object_name, 1))) AS TARGET_OBJECT_ID
    FROM sys.synonyms S
   WHERE (PARSENAME(S.base_object_name, 3) IS NULL
          OR (PARSENAME(S.base_object_name, 3) COLLATE Latin1_General_100_BIN2
                =DB_NAME() COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(CONVERT(NVARCHAR(MAX), PARSENAME(S.base_object_name, 3)))
                =DATALENGTH(CONVERT(NVARCHAR(MAX), DB_NAME()))))
     AND (PARSENAME(S.base_object_name, 4) IS NULL
          OR (PARSENAME(S.base_object_name, 4) COLLATE Latin1_General_100_BIN2
                =CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ServerName')) COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(CONVERT(NVARCHAR(MAX), PARSENAME(S.base_object_name, 4)))
                =DATALENGTH(CONVERT(NVARCHAR(MAX), SERVERPROPERTY(N'ServerName')))))
), SynonymReach AS (
  SELECT E.SYNONYM_ID, E.TARGET_OBJECT_ID,
         CAST(N'/' + CONVERT(NVARCHAR(20), E.SYNONYM_ID) + N'/' AS NVARCHAR(MAX)) AS PATH
    FROM LocalSynonymEdge E
   WHERE E.TARGET_OBJECT_ID IS NOT NULL
  UNION ALL
  SELECT R.SYNONYM_ID, E.TARGET_OBJECT_ID,
         CAST(R.PATH + CONVERT(NVARCHAR(20), E.SYNONYM_ID) + N'/' AS NVARCHAR(MAX))
    FROM SynonymReach R
    JOIN LocalSynonymEdge E ON E.SYNONYM_ID=R.TARGET_OBJECT_ID
   WHERE E.TARGET_OBJECT_ID IS NOT NULL
     AND CHARINDEX(N'/' + CONVERT(NVARCHAR(20), E.SYNONYM_ID) + N'/', R.PATH)=0
)
INSERT INTO @BoundaryDrift (DRIFT)
SELECT DISTINCT CONCAT(N'synonym:', QUOTENAME(OBJECT_SCHEMA_NAME(R.SYNONYM_ID)),
                       N'.', QUOTENAME(OBJECT_NAME(R.SYNONYM_ID)))
  FROM SynonymReach R
  JOIN @TrustedTables T ON T.OBJECT_ID=R.TARGET_OBJECT_ID
OPTION (MAXRECURSION 64);

SELECT DRIFT FROM @BoundaryDrift;
'@ @{} $Transaction
    if ($drift.Rows.Count -ne 0) {
        throw 'TRUSTWORTHY, cross-database ownership chaining, or a trusted-table synonym can bypass the V160 boundary.'
    }
}

function Assert-AuthorityModuleClosure(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction = $null) {
    $drift = Invoke-Table $Connection @'
DECLARE @TrustedTables TABLE (OBJECT_ID INT NOT NULL PRIMARY KEY);
INSERT INTO @TrustedTables (OBJECT_ID) VALUES
  (OBJECT_ID(N'dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE')),
  (OBJECT_ID(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT')),
  (OBJECT_ID(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION')),
  (OBJECT_ID(N'dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE')),
  (OBJECT_ID(N'dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY')),
  (OBJECT_ID(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING'));

DECLARE @AllowedModules TABLE
  (MODULE_ID INT NULL, MODULE_NAME NVARCHAR(517) NOT NULL,
   ALLOWED_PERMISSION SYSNAME NULL, ALLOWED_PRINCIPAL SYSNAME NULL);
INSERT INTO @AllowedModules
  (MODULE_ID, MODULE_NAME, ALLOWED_PERMISSION, ALLOWED_PRINCIPAL) VALUES
  (OBJECT_ID(N'dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE'), N'dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE', N'EXECUTE', N'NexaOneRmsEvidenceWriter'),
  (OBJECT_ID(N'dbo.SYS_RELEASE_PROGRAM_ARTIFACT'), N'dbo.SYS_RELEASE_PROGRAM_ARTIFACT', N'EXECUTE', N'NexaOneSysReleaseWriter'),
  (OBJECT_ID(N'dbo.SYS_REVOKE_PROGRAM_ARTIFACT'), N'dbo.SYS_REVOKE_PROGRAM_ARTIFACT', N'EXECUTE', N'NexaOneSysReleaseWriter'),
  (OBJECT_ID(N'dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY'), N'dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY', N'EXECUTE', N'NexaOneProjectionRuntime'),
  (OBJECT_ID(N'dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE'), N'dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE', N'EXECUTE', N'NexaOneProjectionRuntime'),
  (OBJECT_ID(N'dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE'), N'dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE', N'EXECUTE', N'NexaOneProjectionRuntime'),
  (OBJECT_ID(N'dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY'), N'dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY', N'SELECT', N'NexaOneProjectionRuntime'),
  (OBJECT_ID(N'dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE'), N'dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE', N'SELECT', N'NexaOneProjectionRuntime'),
  (OBJECT_ID(N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_GUARD'), N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_GUARD', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY'), N'dbo.TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY'), N'dbo.TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY'), N'dbo.TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE_APPEND_ONLY'), N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE_APPEND_ONLY', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_RMS_CANONICAL_RECIPE_EXECUTION_PRISTINE'), N'dbo.TR_RMS_CANONICAL_RECIPE_EXECUTION_PRISTINE', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUSTED_EVIDENCE'), N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUSTED_EVIDENCE', NULL, NULL),
  (OBJECT_ID(N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_PRINCIPAL_PROVENANCE'), N'dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_PRINCIPAL_PROVENANCE', NULL, NULL);

;WITH TextualOrDeclaredReference AS (
  SELECT DISTINCT D.referencing_id AS MODULE_ID
    FROM sys.sql_expression_dependencies D
    JOIN @TrustedTables T ON T.OBJECT_ID=D.referenced_id
    JOIN sys.sql_modules RM ON RM.object_id=D.referencing_id
  UNION
  SELECT M.object_id
    FROM sys.sql_modules M
   WHERE M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE%'
      OR M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%SYS_RELEASED_PROGRAM_ARTIFACT%'
      OR M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE%'
      OR M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%POM_WORK_SCOPE_PROJECTION_AUTHORITY%'
      OR M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%POM_PROJECTION_RUNTIME_PRODUCT_BINDING%'
), NonStaticModule AS (
  SELECT M.object_id AS MODULE_ID
    FROM sys.sql_modules M
   WHERE M.definition IS NULL
      OR M.execute_as_principal_id IS NOT NULL
      OR EXISTS (
          SELECT 1 FROM sys.crypt_properties C
           WHERE C.class=1 AND C.major_id=M.object_id)
      OR M.definition COLLATE Latin1_General_100_BIN2 LIKE N'%sp_executesql%'
      OR REPLACE(REPLACE(REPLACE(M.definition, N' ', N''), NCHAR(13), N''), NCHAR(10), N'')
           COLLATE Latin1_General_100_BIN2 LIKE N'%EXEC(%'
      OR REPLACE(REPLACE(REPLACE(M.definition, N' ', N''), NCHAR(13), N''), NCHAR(10), N'')
           COLLATE Latin1_General_100_BIN2 LIKE N'%EXECUTE(%'
), ExternallyExecutableModule AS (
  SELECT DISTINCT D.major_id AS MODULE_ID
    FROM sys.database_permissions D
    JOIN sys.sql_modules M ON M.object_id=D.major_id
    JOIN sys.objects O ON O.object_id=M.object_id
    JOIN sys.schemas S ON S.schema_id=O.schema_id
    JOIN sys.database_principals P ON P.principal_id=D.grantee_principal_id
   WHERE D.class=1 AND D.state IN ('G','W')
     AND D.permission_name IN (N'EXECUTE', N'SELECT', N'CONTROL')
     AND P.principal_id<>1
     AND COALESCE(O.principal_id, S.principal_id)=DATABASE_PRINCIPAL_ID(N'dbo')
), ReachableModule AS (
  SELECT E.MODULE_ID, CAST(N'/' + CONVERT(NVARCHAR(20), E.MODULE_ID) + N'/' AS NVARCHAR(MAX)) AS PATH
    FROM ExternallyExecutableModule E
  UNION ALL
  SELECT D.referenced_id,
         CAST(R.PATH + CONVERT(NVARCHAR(20), D.referenced_id) + N'/' AS NVARCHAR(MAX))
    FROM ReachableModule R
    JOIN sys.sql_expression_dependencies D ON D.referencing_id=R.MODULE_ID
    JOIN sys.sql_modules M ON M.object_id=D.referenced_id
   WHERE CHARINDEX(N'/' + CONVERT(NVARCHAR(20), D.referenced_id) + N'/', R.PATH)=0
), UnsafeModule AS (
  SELECT CONCAT(N'missing:', A.MODULE_NAME) AS DRIFT
    FROM @AllowedModules A WHERE A.MODULE_ID IS NULL
  UNION ALL
  SELECT CONCAT(N'unexpected-reference:', QUOTENAME(OBJECT_SCHEMA_NAME(R.MODULE_ID)), N'.', QUOTENAME(OBJECT_NAME(R.MODULE_ID)))
    FROM TextualOrDeclaredReference R
   WHERE NOT EXISTS (SELECT 1 FROM @AllowedModules A WHERE A.MODULE_ID=R.MODULE_ID)
  UNION ALL
  SELECT CONCAT(N'non-static-allowed:', A.MODULE_NAME)
    FROM @AllowedModules A
    JOIN NonStaticModule D ON D.MODULE_ID=A.MODULE_ID
  UNION ALL
  SELECT CONCAT(N'unexpected-executable:', QUOTENAME(OBJECT_SCHEMA_NAME(E.MODULE_ID)), N'.', QUOTENAME(OBJECT_NAME(E.MODULE_ID)))
    FROM ExternallyExecutableModule E
   WHERE NOT EXISTS (SELECT 1 FROM @AllowedModules A WHERE A.MODULE_ID=E.MODULE_ID)
  UNION ALL
  SELECT CONCAT(N'unexpected-reachable:', QUOTENAME(OBJECT_SCHEMA_NAME(R.MODULE_ID)), N'.', QUOTENAME(OBJECT_NAME(R.MODULE_ID)))
    FROM ReachableModule R
   WHERE NOT EXISTS (SELECT 1 FROM @AllowedModules A WHERE A.MODULE_ID=R.MODULE_ID)
  UNION ALL
  SELECT CONCAT(N'unexpected-module-grant:', A.MODULE_NAME, N':', P.name)
    FROM @AllowedModules A
    JOIN sys.database_permissions D ON D.class=1 AND D.major_id=A.MODULE_ID AND D.state IN ('G','W')
    JOIN sys.database_principals P ON P.principal_id=D.grantee_principal_id
   WHERE A.ALLOWED_PERMISSION IS NULL
      OR D.minor_id<>0
      OR D.permission_name<>A.ALLOWED_PERMISSION
      OR P.name COLLATE Latin1_General_100_BIN2<>A.ALLOWED_PRINCIPAL COLLATE Latin1_General_100_BIN2
      OR DATALENGTH(CONVERT(NVARCHAR(MAX), P.name))
           <>DATALENGTH(CONVERT(NVARCHAR(MAX), A.ALLOWED_PRINCIPAL))
  UNION ALL
  SELECT CONCAT(N'reachable-non-static:', QUOTENAME(OBJECT_SCHEMA_NAME(R.MODULE_ID)), N'.', QUOTENAME(OBJECT_NAME(R.MODULE_ID)))
    FROM ReachableModule R
    JOIN NonStaticModule M ON M.MODULE_ID=R.MODULE_ID
)
SELECT DRIFT FROM UnsafeModule OPTION (MAXRECURSION 64);
'@ @{} $Transaction
    if ($drift.Rows.Count -ne 0) {
        throw 'Unexpected trusted-table module, ownership impersonation, dynamic SQL, or module grant can bypass the V160 boundary.'
    }
}

function Assert-AuthorityRoleMemberships(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [System.Data.SqlClient.SqlTransaction]$Transaction,
    [bool]$WritersOnly,
    [bool]$Removed) {
    $members = Invoke-Table $Connection @'
SELECT R.name AS ROLE_NAME, M.name AS MEMBER_NAME, M.type_desc AS MEMBER_TYPE
  FROM sys.database_role_members RM
  JOIN sys.database_principals R ON R.principal_id=RM.role_principal_id
  JOIN sys.database_principals M ON M.principal_id=RM.member_principal_id
 WHERE R.name IN (N'NexaOneProjectionRuntime', N'NexaOneRmsEvidenceWriter', N'NexaOneSysReleaseWriter');
'@ @{} $Transaction
    $actual = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [StringComparer]::Ordinal)
    foreach ($row in $members.Rows) {
        if ([string]$row.MEMBER_TYPE -eq 'DATABASE_ROLE') {
            throw 'Nested roles are forbidden in V160 trusted-authority roles.'
        }
        $key = '{0}|{1}' -f [string]$row.ROLE_NAME, [string]$row.MEMBER_NAME
        $actual[$key] = $true
    }
    $allowed = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [StringComparer]::Ordinal)
    if (-not $Removed) {
        if (-not $WritersOnly -or $actual.ContainsKey(
                ('NexaOneProjectionRuntime|{0}' -f $RuntimeDatabaseUser))) {
            $allowed['NexaOneProjectionRuntime|' + $RuntimeDatabaseUser] = $true
        }
        $allowed['NexaOneRmsEvidenceWriter|' + $RmsWriterDatabaseUser] = $true
        $allowed['NexaOneSysReleaseWriter|' + $SysWriterDatabaseUser] = $true
    }
    foreach ($key in $actual.Keys) {
        if (-not $allowed.ContainsKey($key)) {
            throw "Unexpected member '$key' exists in a V160 trusted-authority role."
        }
    }
    if (-not $WritersOnly -and -not $Removed) {
        foreach ($key in $allowed.Keys) {
            if (-not $actual.ContainsKey($key)) {
                throw "Required V160 trusted-authority role member '$key' is missing."
            }
        }
    }
    elseif ($WritersOnly -and -not $Removed) {
        foreach ($key in @(
            'NexaOneRmsEvidenceWriter|' + $RmsWriterDatabaseUser,
            'NexaOneSysReleaseWriter|' + $SysWriterDatabaseUser)) {
            if (-not $actual.ContainsKey($key)) {
                throw "Required V160 writer-bootstrap role member '$key' is missing."
            }
        }
    }
}

function Assert-Matrix(
    [string]$Label,
    [System.Collections.IDictionary]$Actual,
    [System.Collections.IDictionary]$Expected) {
    foreach ($key in $Expected.Keys) {
        if ([int]$Actual[$key] -ne [int]$Expected[$key]) {
            throw "$Label effective permission '$key' was $($Actual[$key]); expected $($Expected[$key])."
        }
    }
}

$connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
$evidence = [ordered]@{
    RunId = $runId
    Mode = $mode
    StartedAtUtc = $startedAt.ToString('O')
    Database = $builder.InitialCatalog
    Migration = 'V160__TRUSTED_AUTHORITY_WRITER_SECURITY.sql'
    Success = $false
    Principals = @()
    PermissionMatrix = [ordered]@{}
    ActiveProductBinding = [ordered]@{
        CoordinateSha256 = if ($requiresCoordinate) {
            Get-TextSha256 (($coordinateValues | ForEach-Object {
                '{0}:{1}' -f $_.Length, $_ }) -join '|')
        } elseif ($requiresArtifactOnly) {
            Get-TextSha256 ("{0}:{1}" -f $ArtifactId.Length, $ArtifactId)
        } else { $null }
        ExactMatch = $false
        PrincipalSidSha256 = $null
    }
    ReleaseProvenance = [ordered]@{
        Evaluated = $false
        PrincipalName = $null
        PrincipalSidSha256 = $null
        ReleasedAtUtc = $null
        MatchesCurrentSysWriter = $false
        ExistingExactBinding = $false
        HistoricalApprovalRequired = $false
        HistoricalApprovalProvided = $historicalReleaseApprovalProvided
        HistoricalApprovalMatched = $false
    }
    RemovedBindings = @()
    ExecuteAclExact = $false
    DatabaseBoundaryExact = $false
    ModuleClosureExact = $false
    RoleMembershipExact = $false
    PrincipalSidsDistinct = $false
    SecurityAuditDisposition = if ($Decommission) {
        'Fail-safe decommission skips principal/ACL audits so missing or compromised writer users cannot block binding removal.'
    } else { 'Evaluated' }
    RoleMembershipDisposition = if ($Decommission -and -not $DecommissionAllBindings) {
        'Artifact-scoped decommission does not change trusted role membership.'
    } else { 'Evaluated' }
    Error = $null
}
$securityTransaction = $null
$releaseProvenanceEvaluated = $false
try {
    $connection.Open()
    $shape = Invoke-Table $connection @'
SELECT
  CAST(CASE WHEN EXISTS (
      SELECT 1 FROM dbo.SYS_SCHEMA_MIGRATION
       WHERE VERSION_ID=N'V160__TRUSTED_AUTHORITY_WRITER_SECURITY.sql') THEN 1 ELSE 0 END AS INT) AS MIGRATION_APPLIED,
  CAST(CASE WHEN DATABASE_PRINCIPAL_ID(N'NexaOneProjectionRuntime') IS NOT NULL
             AND DATABASE_PRINCIPAL_ID(N'NexaOneRmsEvidenceWriter') IS NOT NULL
             AND DATABASE_PRINCIPAL_ID(N'NexaOneSysReleaseWriter') IS NOT NULL THEN 1 ELSE 0 END AS INT) AS ROLES_EXIST,
  CAST(CASE WHEN OBJECT_ID(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'U') IS NOT NULL
             AND OBJECT_ID(N'dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY', N'V') IS NOT NULL
             AND OBJECT_ID(N'dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE', N'V') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY', N'PROVISIONED_DATABASE_PRINCIPAL_SID') IS NOT NULL
             AND COL_LENGTH(N'dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE', N'CAPTURED_DATABASE_PRINCIPAL_SID') IS NOT NULL
             AND COL_LENGTH(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT', N'RELEASED_DATABASE_PRINCIPAL_SID') IS NOT NULL
             AND COL_LENGTH(N'dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION', N'REVOKED_DATABASE_PRINCIPAL_SID') IS NOT NULL
            THEN 1 ELSE 0 END AS INT) AS SECURITY_SCHEMA_EXISTS,
  CAST(CASE WHEN OBJECT_ID(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'U') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'DATABASE_PRINCIPAL_NAME') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'DATABASE_PRINCIPAL_SID') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'ARTIFACT_ID') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'PROGRAM_HASH') IS NOT NULL
             AND COL_LENGTH(N'dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING', N'BOUND_RECIPE_SNAPSHOT_HASH') IS NOT NULL
            THEN 1 ELSE 0 END AS INT) AS DECOMMISSION_SCHEMA_EXISTS,
  CAST(CASE WHEN OBJECT_ID(N'dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE', N'P') IS NOT NULL
             AND OBJECT_ID(N'dbo.SYS_RELEASE_PROGRAM_ARTIFACT', N'P') IS NOT NULL
              AND OBJECT_ID(N'dbo.SYS_REVOKE_PROGRAM_ARTIFACT', N'P') IS NOT NULL
              AND OBJECT_ID(N'dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY', N'P') IS NOT NULL
              AND OBJECT_ID(N'dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE', N'P') IS NOT NULL
              AND OBJECT_ID(N'dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE', N'P') IS NOT NULL THEN 1 ELSE 0 END AS INT) AS PROCEDURES_EXIST;
'@
    if ($Decommission) {
        if ([int]$shape.Rows[0].MIGRATION_APPLIED -ne 1 -or
            [int]$shape.Rows[0].DECOMMISSION_SCHEMA_EXISTS -ne 1) {
            throw 'V160 migration or the core runtime binding table is missing; fail-safe decommission cannot identify its target safely.'
        }
    }
    elseif ([int]$shape.Rows[0].MIGRATION_APPLIED -ne 1 -or
            [int]$shape.Rows[0].ROLES_EXIST -ne 1 -or
            [int]$shape.Rows[0].SECURITY_SCHEMA_EXISTS -ne 1 -or
            [int]$shape.Rows[0].PROCEDURES_EXIST -ne 1) {
        throw 'V160 migration, roles, or procedures are missing.'
    }

    if (-not $Decommission) {
        $evidence.Principals = @(
            Get-PrincipalAudit $connection $RuntimeDatabaseUser $RuntimeDatabaseUser $RmsWriterDatabaseUser $SysWriterDatabaseUser
            Get-PrincipalAudit $connection $RmsWriterDatabaseUser $RuntimeDatabaseUser $RmsWriterDatabaseUser $SysWriterDatabaseUser
            Get-PrincipalAudit $connection $SysWriterDatabaseUser $RuntimeDatabaseUser $RmsWriterDatabaseUser $SysWriterDatabaseUser
        )
        Assert-DistinctPrincipalSids $connection
        $evidence.PrincipalSidsDistinct = $true
        Assert-AuthorityExecuteAcl $connection
        $evidence.ExecuteAclExact = $true
        Assert-AuthorityDatabaseBoundary $connection
        $evidence.DatabaseBoundaryExact = $true
        Assert-AuthorityModuleClosure $connection
        $evidence.ModuleClosureExact = $true
    }

    if ($Apply -or $Decommission) {
        $securityTransaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
        if ($Apply -and $WriterBootstrapOnly) {
            Set-RoleMemberships $connection $securityTransaction $false $true
        }
        elseif ($Apply) {
            $release = Get-ReleaseProvenanceForBinding $connection $securityTransaction
            $releaseRow = $release.Rows[0]
            $existingExactBinding = [int]$releaseRow.EXACT_BINDING_EXISTS -eq 1
            $releaseApproval = Set-ReleaseProvenanceEvidence `
                $evidence $releaseRow $historicalReleaseApprovalProvided `
                $ApprovedReleasePrincipalSidSha256 $existingExactBinding $true
            $releaseProvenanceEvaluated = $true
            if ($historicalReleaseApprovalProvided -and -not $releaseApproval.ApprovalMatched) {
                throw 'ApprovedReleasePrincipalSidSha256 does not match the server-read historical release principal SID.'
            }
            if ($releaseApproval.ApprovalRequired -and -not $releaseApproval.ApprovalMatched) {
                throw 'A new binding for historical release provenance requires the exact ApprovedReleasePrincipalSidSha256.'
            }
            Set-RuntimeProductBinding $connection $securityTransaction $false $false
            Set-RoleMemberships $connection $securityTransaction $false $false
        }
        else {
            $preDeleteBindings = Get-RuntimePrincipalBinding `
                $connection $securityTransaction $DecommissionAllBindings $true
            $evidence.RemovedBindings = @(
                foreach ($row in $preDeleteBindings.Rows) {
                    [ordered]@{
                        PrincipalSidSha256 = Get-SidSha256 ([byte[]]$row.DATABASE_PRINCIPAL_SID)
                        ArtifactIdSha256 = Get-TextSha256 ([string]$row.ARTIFACT_ID)
                        ProgramHash = [string]$row.PROGRAM_HASH
                        BoundRecipeSnapshotHash = [string]$row.BOUND_RECIPE_SNAPSHOT_HASH
                    }
                }
            )
            if ($DecommissionAllBindings) {
                Set-RoleMemberships $connection $securityTransaction $true $false
            }
            Set-RuntimeProductBinding $connection $securityTransaction $true $DecommissionAllBindings
        }
    }
    elseif (-not $WriterBootstrapOnly) {
        # No mode switch and explicit ValidateOnly are the same activation gate. Reuse the
        # full-Apply artifact -> revocation -> binding lock order in one serializable transaction
        # so a binding that was revoked after commissioning cannot be reported as active.
        $securityTransaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
        $release = Get-ReleaseProvenanceForBinding $connection $securityTransaction
        $releaseRow = $release.Rows[0]
        $existingExactBinding = [int]$releaseRow.EXACT_BINDING_EXISTS -eq 1
        [void](Set-ReleaseProvenanceEvidence `
            $evidence $releaseRow $false $null $existingExactBinding $false)
        $releaseProvenanceEvaluated = $true
    }

    if ($Decommission) {
        $binding = Get-RuntimePrincipalBinding `
            $connection $securityTransaction $DecommissionAllBindings $false
        if ($binding.Rows.Count -ne 0) {
            throw 'Runtime active-product binding still exists after decommission.'
        }
        $evidence.PermissionMatrix = [ordered]@{
            Evaluation = 'Skipped for fail-safe decommission; binding removal does not depend on writer-user availability or ACL health.'
        }
        if ($DecommissionAllBindings) {
            Assert-AuthorityRoleMemberships $connection $securityTransaction $false $true
            $evidence.RoleMembershipExact = $true
        }
    }
    else {
        $runtimeMatrix = Get-PermissionMatrix $connection $RuntimeDatabaseUser $securityTransaction
        $rmsMatrix = Get-PermissionMatrix $connection $RmsWriterDatabaseUser $securityTransaction
        $sysMatrix = Get-PermissionMatrix $connection $SysWriterDatabaseUser $securityTransaction
        $binding = if ($WriterBootstrapOnly) { $null }
            else { Get-RuntimeProductBinding $connection $securityTransaction }
        if (-not $WriterBootstrapOnly -and -not $releaseProvenanceEvaluated -and
            $binding.Rows.Count -eq 1) {
            [void](Set-ReleaseProvenanceEvidence `
                $evidence $binding.Rows[0] $false $null $true $false)
            $releaseProvenanceEvaluated = $true
        }
        $evidence.PermissionMatrix = [ordered]@{
            Runtime = $runtimeMatrix
            RmsWriter = $rmsMatrix
            SysWriter = $sysMatrix
        }
        Assert-AuthorityRoleMemberships $connection $securityTransaction $WriterBootstrapOnly $false
        $evidence.RoleMembershipExact = $true

        if ($WriterBootstrapOnly) {
            Assert-Matrix 'runtime writer separation' $runtimeMatrix @{
                RmsRole=0; SysRole=0; CaptureExecute=0; ReleaseExecute=0; RevokeExecute=0
            }
            Assert-Matrix 'RMS writer bootstrap' $rmsMatrix @{
                RuntimeRole=0; RmsRole=1; SysRole=0; RmsInsert=0; SysInsert=0; RevocationInsert=0; AuthorityInsert=0;
                BindingSelect=0; BindingInsert=0; BindingUpdate=0; BindingDelete=0;
                ActiveAuthoritySelect=0; AuthorityFenceSelect=0; CaptureExecute=1; ReleaseExecute=0; RevokeExecute=0; AuthorityExecute=0; AuthorityLockExecute=0; LineageExecute=0
            }
            Assert-Matrix 'SYS writer bootstrap' $sysMatrix @{
                RuntimeRole=0; RmsRole=0; SysRole=1; RmsInsert=0; SysInsert=0; RevocationInsert=0; AuthorityInsert=0;
                BindingSelect=0; BindingInsert=0; BindingUpdate=0; BindingDelete=0;
                ActiveAuthoritySelect=0; AuthorityFenceSelect=0; CaptureExecute=0; ReleaseExecute=1; RevokeExecute=1; AuthorityExecute=0; AuthorityLockExecute=0; LineageExecute=0
            }
        }
        else {
            Assert-Matrix 'runtime' $runtimeMatrix @{
                RuntimeRole=1; RmsSelect=1; RmsInsert=0; SysInsert=0; RevocationInsert=0;
                AuthorityInsert=0; BindingSelect=0; BindingInsert=0; BindingUpdate=0; BindingDelete=0;
                ActiveAuthoritySelect=1; AuthorityFenceSelect=1; CaptureExecute=0; ReleaseExecute=0; RevokeExecute=0; AuthorityExecute=1; AuthorityLockExecute=1; LineageExecute=1
            }
            Assert-Matrix 'RMS writer' $rmsMatrix @{
                RmsRole=1; RmsInsert=0; SysInsert=0; RevocationInsert=0; AuthorityInsert=0;
                BindingSelect=0; BindingInsert=0; BindingUpdate=0; BindingDelete=0;
                ActiveAuthoritySelect=0; AuthorityFenceSelect=0; CaptureExecute=1; ReleaseExecute=0; RevokeExecute=0; AuthorityExecute=0; AuthorityLockExecute=0; LineageExecute=0
            }
            Assert-Matrix 'SYS writer' $sysMatrix @{
                SysRole=1; RmsInsert=0; SysInsert=0; RevocationInsert=0; AuthorityInsert=0;
                BindingSelect=0; BindingInsert=0; BindingUpdate=0; BindingDelete=0;
                ActiveAuthoritySelect=0; AuthorityFenceSelect=0; CaptureExecute=0; ReleaseExecute=1; RevokeExecute=1; AuthorityExecute=0; AuthorityLockExecute=0; LineageExecute=0
            }
            if ($binding.Rows.Count -ne 1) {
                throw 'Runtime active-product binding is missing or does not exactly match the requested deployment.'
            }
            $evidence.ActiveProductBinding.ExactMatch = $true
            $evidence.ActiveProductBinding.PrincipalSidSha256 =
                Get-SidSha256 ([byte[]]$binding.Rows[0].DATABASE_PRINCIPAL_SID)
        }
    }

    if ($null -ne $securityTransaction -and -not $Decommission) {
        # Re-read catalog closure inside the same transaction as membership/binding changes so a
        # concurrent ACL or module drift cannot be blessed by preflight-only evidence.
        Assert-AuthorityExecuteAcl $connection $securityTransaction
        Assert-AuthorityDatabaseBoundary $connection $securityTransaction
        Assert-AuthorityModuleClosure $connection $securityTransaction
    }
    if ($null -ne $securityTransaction) {
        $securityTransaction.Commit()
        $securityTransaction.Dispose()
        $securityTransaction = $null
    }
    $evidence.Success = $true
    Write-Host "trusted-authority security commissioning: $mode passed"
}
catch {
    if ($null -ne $securityTransaction) {
        try {
            if ($securityTransaction.Connection -ne $null) { $securityTransaction.Rollback() }
        }
        finally {
            $securityTransaction.Dispose()
            $securityTransaction = $null
        }
    }
    $evidence.Error = $_.Exception.Message
    throw
}
finally {
    if ($connection.State -ne [System.Data.ConnectionState]::Closed) { $connection.Close() }
    $evidence.CompletedAtUtc = [DateTime]::UtcNow.ToString('O')
    $evidenceWriter = [System.IO.StreamWriter]::new(
        $evidenceStream,
        [System.Text.UTF8Encoding]::new($false),
        4096,
        $false)
    try {
        $evidenceWriter.Write(($evidence | ConvertTo-Json -Depth 8))
        $evidenceWriter.Flush()
    }
    finally {
        $evidenceWriter.Dispose()
        $evidenceStream = $null
    }
    Write-Host "commissioning evidence: $absoluteEvidencePath"
}
