-- Owner: POM/RMS/SYS integration boundary (ADR-0005).
-- Security principals are environment-owned. This migration creates only fixed database roles,
-- static same-owner procedures and object permissions; it never creates a login/user/password or
-- assigns an environment user to a role.

-- SQL Server database principals have no SQLite equivalent. SQLite keeps the V159 structural and
-- append-only guards, but this commissioning boundary is deliberately a SQL Server-only no-op.
-- SQLITE-OMIT-BEGIN

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

-- V159 rows written before this security cutover have no database-principal provenance. Never
-- bless them implicitly. The high-impact commissioning runbook must reconcile them explicitly.
IF EXISTS (SELECT 1 FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE)
   OR EXISTS (SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT)
   OR EXISTS (SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION)
   OR EXISTS (SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY)
  THROW 51600, 'V160 refuses trusted-authority rows written before the DB writer boundary', 1;

CREATE ROLE NexaOneProjectionRuntime AUTHORIZATION dbo;
CREATE ROLE NexaOneRmsEvidenceWriter AUTHORIZATION dbo;
CREATE ROLE NexaOneSysReleaseWriter AUTHORIZATION dbo;

-- This is an administrator-owned active-product attestation, not application configuration.
-- A runtime caller can mint authority only for the exact released coordinate commissioned for
-- its immutable database SID. Name and SID are both retained so drop/recreate and rename cannot
-- silently inherit the old binding.
CREATE TABLE dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING (
    DATABASE_PRINCIPAL_NAME       NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DATABASE_PRINCIPAL_SID        VARBINARY(85) NOT NULL,
    EQUIPMENT_ID                  NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OPERATION_KEY                 NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ARTIFACT_ID                   NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PRODUCT_PROFILE_ID            NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PLUGIN_ID                     NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PRODUCT_DEFINITION_VERSION    NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROGRAM_VERSION               NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROGRAM_SCHEMA                NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROGRAM_HASH                  CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    BOUND_RECIPE_SNAPSHOT_SCHEMA  NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    BOUND_RECIPE_SNAPSHOT_HASH    CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    COMMISSIONED_AT               DATETIME2(7) NOT NULL,
    COMMISSIONED_BY               NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_POM_PROJECTION_RUNTIME_PRODUCT_BINDING
      PRIMARY KEY (DATABASE_PRINCIPAL_NAME, ARTIFACT_ID),
    CONSTRAINT UQ_POM_PROJECTION_RUNTIME_PRODUCT_BINDING_SID
      UNIQUE (DATABASE_PRINCIPAL_SID, ARTIFACT_ID),
    CONSTRAINT FK_POM_PROJECTION_RUNTIME_PRODUCT_BINDING_ARTIFACT
      FOREIGN KEY (ARTIFACT_ID) REFERENCES dbo.SYS_RELEASED_PROGRAM_ARTIFACT(ARTIFACT_ID),
    CONSTRAINT CK_POM_PROJECTION_RUNTIME_PRODUCT_BINDING_IDENTITIES CHECK
      (DATALENGTH(DATABASE_PRINCIPAL_NAME)>0
       AND DATALENGTH(DATABASE_PRINCIPAL_NAME)=DATALENGTH(LTRIM(RTRIM(DATABASE_PRINCIPAL_NAME)))
       AND DATALENGTH(DATABASE_PRINCIPAL_SID)>0
       AND DATALENGTH(EQUIPMENT_ID)>0
       AND DATALENGTH(EQUIPMENT_ID)=DATALENGTH(LTRIM(RTRIM(EQUIPMENT_ID)))
       AND DATALENGTH(OPERATION_KEY)>0
       AND DATALENGTH(OPERATION_KEY)=DATALENGTH(LTRIM(RTRIM(OPERATION_KEY)))
       AND DATALENGTH(ARTIFACT_ID)>0
       AND DATALENGTH(ARTIFACT_ID)=DATALENGTH(LTRIM(RTRIM(ARTIFACT_ID)))
       AND DATALENGTH(PRODUCT_PROFILE_ID)>0
       AND DATALENGTH(PRODUCT_PROFILE_ID)=DATALENGTH(LTRIM(RTRIM(PRODUCT_PROFILE_ID)))
       AND DATALENGTH(PLUGIN_ID)>0
       AND DATALENGTH(PLUGIN_ID)=DATALENGTH(LTRIM(RTRIM(PLUGIN_ID)))
       AND DATALENGTH(PRODUCT_DEFINITION_VERSION)>0
       AND DATALENGTH(PRODUCT_DEFINITION_VERSION)=DATALENGTH(LTRIM(RTRIM(PRODUCT_DEFINITION_VERSION)))
       AND DATALENGTH(PROGRAM_VERSION)>0
       AND DATALENGTH(PROGRAM_VERSION)=DATALENGTH(LTRIM(RTRIM(PROGRAM_VERSION)))
       AND DATALENGTH(PROGRAM_SCHEMA)>0
       AND DATALENGTH(PROGRAM_SCHEMA)=DATALENGTH(LTRIM(RTRIM(PROGRAM_SCHEMA)))
       AND LEN(PROGRAM_HASH)=64 AND PROGRAM_HASH NOT LIKE '%[^0-9A-F]%'
       AND DATALENGTH(BOUND_RECIPE_SNAPSHOT_SCHEMA)>0
       AND DATALENGTH(BOUND_RECIPE_SNAPSHOT_SCHEMA)
             =DATALENGTH(LTRIM(RTRIM(BOUND_RECIPE_SNAPSHOT_SCHEMA)))
       AND LEN(BOUND_RECIPE_SNAPSHOT_HASH)=64
       AND BOUND_RECIPE_SNAPSHOT_HASH NOT LIKE '%[^0-9A-F]%'
       AND DATALENGTH(COMMISSIONED_BY)>0)
);

-- The first provisioning principal is immutable audit provenance. Exact replay after a credential
-- rotation validates the new caller binding but intentionally preserves these original values.
ALTER TABLE dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY ADD
    PROVISIONED_DATABASE_PRINCIPAL_NAME NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROVISIONED_DATABASE_PRINCIPAL_SID  VARBINARY(85) NOT NULL;
ALTER TABLE dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY ADD CONSTRAINT
    CK_POM_WORK_SCOPE_PROJECTION_AUTHORITY_DATABASE_PRINCIPAL CHECK
      (DATALENGTH(PROVISIONED_DATABASE_PRINCIPAL_NAME)>0
       AND DATALENGTH(PROVISIONED_DATABASE_PRINCIPAL_NAME)
             =DATALENGTH(LTRIM(RTRIM(PROVISIONED_DATABASE_PRINCIPAL_NAME)))
       AND DATALENGTH(PROVISIONED_DATABASE_PRINCIPAL_SID)>0);

-- Business actor strings remain useful domain audit fields, but trusted writer identity is always
-- derived from SQL Server's current database principal and cannot be supplied by the caller.
ALTER TABLE dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE ADD
    CAPTURED_DATABASE_PRINCIPAL_NAME NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CAPTURED_DATABASE_PRINCIPAL_SID  VARBINARY(85) NOT NULL;
ALTER TABLE dbo.SYS_RELEASED_PROGRAM_ARTIFACT ADD
    RELEASED_DATABASE_PRINCIPAL_NAME NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RELEASED_DATABASE_PRINCIPAL_SID  VARBINARY(85) NOT NULL;
ALTER TABLE dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION ADD
    REVOKED_DATABASE_PRINCIPAL_NAME NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    REVOKED_DATABASE_PRINCIPAL_SID  VARBINARY(85) NOT NULL;
ALTER TABLE dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE ADD CONSTRAINT
    CK_RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE_WRITER_PRINCIPAL CHECK
      (DATALENGTH(CAPTURED_DATABASE_PRINCIPAL_NAME)>0
       AND DATALENGTH(CAPTURED_DATABASE_PRINCIPAL_SID)>0);
ALTER TABLE dbo.SYS_RELEASED_PROGRAM_ARTIFACT ADD CONSTRAINT
    CK_SYS_RELEASED_PROGRAM_ARTIFACT_WRITER_PRINCIPAL CHECK
      (DATALENGTH(RELEASED_DATABASE_PRINCIPAL_NAME)>0
       AND DATALENGTH(RELEASED_DATABASE_PRINCIPAL_SID)>0);
ALTER TABLE dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION ADD CONSTRAINT
    CK_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_WRITER_PRINCIPAL CHECK
      (DATALENGTH(REVOKED_DATABASE_PRINCIPAL_NAME)>0
       AND DATALENGTH(REVOKED_DATABASE_PRINCIPAL_SID)>0);

CREATE PROCEDURE dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE
    @ExecutionId NVARCHAR(MAX),
    @WorkScopeId NVARCHAR(MAX),
    @PairRunId NVARCHAR(MAX),
    @SequenceRunId NVARCHAR(MAX),
    @EquipmentId NVARCHAR(MAX),
    @OperationKey NVARCHAR(MAX),
    @RecipeId NVARCHAR(MAX),
    @RecipeVersion INT,
    @SnapshotSchema NVARCHAR(MAX),
    @SnapshotHash VARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Reject unbounded MAX payloads before XML/control-character inspection to keep the trusted
    -- writer surface from becoming a memory-amplification path.
    IF @RecipeVersion IS NULL OR @RecipeVersion<=0
       OR @ExecutionId IS NULL OR DATALENGTH(@ExecutionId) NOT BETWEEN 2 AND 200
       OR @WorkScopeId IS NULL OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
       OR @PairRunId IS NULL OR DATALENGTH(@PairRunId) NOT BETWEEN 2 AND 200
       OR @SequenceRunId IS NULL OR DATALENGTH(@SequenceRunId) NOT BETWEEN 2 AND 200
       OR @EquipmentId IS NULL OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR @OperationKey IS NULL OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR @RecipeId IS NULL OR DATALENGTH(@RecipeId) NOT BETWEEN 2 AND 200
       OR @SnapshotSchema IS NULL OR DATALENGTH(@SnapshotSchema) NOT BETWEEN 2 AND 200
       OR @SnapshotHash IS NULL OR DATALENGTH(@SnapshotHash)<>64
      THROW 51624, 'Canonical evidence input is blank, oversized, padded, or non-canonical', 1;

    -- XML serialization rejects NUL, invalid surrogate pairs, and the remaining XML-forbidden
    -- controls before any lock/write. PATINDEX below additionally rejects TAB/LF/CR and all C0.
    DECLARE @InputCharacterProbe XML = (
        SELECT @ExecutionId AS [ExecutionId], @WorkScopeId AS [WorkScopeId],
               @PairRunId AS [PairRunId], @SequenceRunId AS [SequenceRunId],
               @EquipmentId AS [EquipmentId], @OperationKey AS [OperationKey],
               @RecipeId AS [RecipeId], @SnapshotSchema AS [SnapshotSchema]
          FOR XML PATH(N'Input'), TYPE);
    IF @RecipeVersion IS NULL OR @RecipeVersion <= 0
       OR @ExecutionId IS NULL OR @WorkScopeId IS NULL OR @PairRunId IS NULL
       OR @SequenceRunId IS NULL OR @EquipmentId IS NULL OR @OperationKey IS NULL
       OR @RecipeId IS NULL OR @SnapshotSchema IS NULL OR @SnapshotHash IS NULL
       OR DATALENGTH(@ExecutionId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
       OR DATALENGTH(@PairRunId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@SequenceRunId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@RecipeId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@SnapshotSchema) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ExecutionId)<>DATALENGTH(LTRIM(RTRIM(@ExecutionId)))
       OR DATALENGTH(@WorkScopeId)<>DATALENGTH(LTRIM(RTRIM(@WorkScopeId)))
       OR DATALENGTH(@PairRunId)<>DATALENGTH(LTRIM(RTRIM(@PairRunId)))
       OR DATALENGTH(@SequenceRunId)<>DATALENGTH(LTRIM(RTRIM(@SequenceRunId)))
       OR DATALENGTH(@EquipmentId)<>DATALENGTH(LTRIM(RTRIM(@EquipmentId)))
       OR DATALENGTH(@OperationKey)<>DATALENGTH(LTRIM(RTRIM(@OperationKey)))
       OR DATALENGTH(@RecipeId)<>DATALENGTH(LTRIM(RTRIM(@RecipeId)))
       OR DATALENGTH(@SnapshotSchema)<>DATALENGTH(LTRIM(RTRIM(@SnapshotSchema)))
       OR DATALENGTH(@SnapshotHash)<>64
       OR @SnapshotHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            CONCAT(@ExecutionId, @WorkScopeId, @PairRunId, @SequenceRunId, @EquipmentId,
                   @OperationKey, @RecipeId, @SnapshotSchema)
              COLLATE Latin1_General_100_BIN2)>0
       THROW 51624, 'Canonical evidence input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @StartedTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @StartedTransaction = 1;
    END;

    BEGIN TRY
        DECLARE @WriterPrincipalName NVARCHAR(128) = USER_NAME(),
                @WriterPrincipalSid VARBINARY(85);
        SELECT @WriterPrincipalSid = P.sid FROM sys.database_principals P
         WHERE P.principal_id = DATABASE_PRINCIPAL_ID(@WriterPrincipalName);
        IF @WriterPrincipalSid IS NULL
          THROW 51621, 'Canonical evidence writer has no auditable database principal', 1;

        -- Parent first: the mutable V113 execution is frozen while the canonical child is attested.
        DECLARE @ParentExecutionId NVARCHAR(50),
                @ParentWorkScopeId NVARCHAR(50),
                @ParentEquipmentId NVARCHAR(50),
                @ParentOperationKey NVARCHAR(50),
                @ParentRecipeId NVARCHAR(50),
                @ParentRecipeVersion INT;
        SELECT @ParentExecutionId = S.EXECUTION_ID,
               @ParentWorkScopeId = S.WORK_SCOPE_ID,
               @ParentEquipmentId = S.EQUIPMENT_ID,
               @ParentOperationKey = S.PROCESS_ID,
               @ParentRecipeId = S.RECIPE_ID,
               @ParentRecipeVersion = S.RECIPE_VERSION
          FROM dbo.RMS_RECIPE_EXECUTION_SNAPSHOT S WITH (UPDLOCK, HOLDLOCK)
         WHERE S.EXECUTION_ID COLLATE Latin1_General_100_BIN2
                 = @ExecutionId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), S.EXECUTION_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @ExecutionId));

        IF @ParentExecutionId IS NULL
           OR @ParentWorkScopeId IS NULL
           OR @ParentOperationKey IS NULL
           OR @ParentWorkScopeId COLLATE Latin1_General_100_BIN2
                <> @WorkScopeId COLLATE Latin1_General_100_BIN2
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ParentWorkScopeId))
                <> DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
           OR @ParentEquipmentId COLLATE Latin1_General_100_BIN2
                <> @EquipmentId COLLATE Latin1_General_100_BIN2
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ParentEquipmentId))
                <> DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           OR @ParentOperationKey COLLATE Latin1_General_100_BIN2
                <> @OperationKey COLLATE Latin1_General_100_BIN2
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ParentOperationKey))
                <> DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           OR @ParentRecipeId COLLATE Latin1_General_100_BIN2
                <> @RecipeId COLLATE Latin1_General_100_BIN2
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ParentRecipeId))
                <> DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeId))
           OR @ParentRecipeVersion <> @RecipeVersion
          THROW 51601, 'Canonical evidence does not match its exact V113 execution parent', 1;

        -- Serialize both unique identities in lexical order so crossed execution/stream requests
        -- cannot acquire the child keys in opposite order.
        DECLARE @ExecutionResource NVARCHAR(255) =
            N'NexaOne.RmsEvidence.Execution.' + CONVERT(VARCHAR(64), HASHBYTES(
                'SHA2_256', CONCAT(DATALENGTH(@ExecutionId), N':', @ExecutionId)), 2);
        DECLARE @StreamResource NVARCHAR(255) =
            N'NexaOne.RmsEvidence.Stream.' + CONVERT(VARCHAR(64), HASHBYTES(
                'SHA2_256', CONCAT(
                    DATALENGTH(@WorkScopeId), N':', @WorkScopeId, N'|',
                    DATALENGTH(@PairRunId), N':', @PairRunId, N'|',
                    DATALENGTH(@SequenceRunId), N':', @SequenceRunId)), 2);
        DECLARE @FirstResource NVARCHAR(255) = @ExecutionResource,
                @SecondResource NVARCHAR(255) = @StreamResource,
                @SwapResource NVARCHAR(255),
                @LockResult INT;
        IF @FirstResource COLLATE Latin1_General_100_BIN2
             > @SecondResource COLLATE Latin1_General_100_BIN2
        BEGIN
            SET @SwapResource = @FirstResource;
            SET @FirstResource = @SecondResource;
            SET @SecondResource = @SwapResource;
        END;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @FirstResource, @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 60000, @DbPrincipal = 'public';
        IF @LockResult < 0
          THROW 51602, 'Could not acquire the canonical evidence identity lock', 1;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @SecondResource, @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 60000, @DbPrincipal = 'public';
        IF @LockResult < 0
          THROW 51602, 'Could not acquire the canonical evidence stream lock', 1;

        IF EXISTS (
            SELECT 1
              FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE E WITH (UPDLOCK, HOLDLOCK)
             WHERE E.EXECUTION_ID COLLATE Latin1_General_100_BIN2
                       = @ExecutionId COLLATE Latin1_General_100_BIN2
                OR (E.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                       = @WorkScopeId COLLATE Latin1_General_100_BIN2
                    AND E.PAIR_RUN_ID COLLATE Latin1_General_100_BIN2
                       = @PairRunId COLLATE Latin1_General_100_BIN2
                    AND E.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2
                       = @SequenceRunId COLLATE Latin1_General_100_BIN2))
        BEGIN
            IF EXISTS (
                SELECT 1
                  FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE E WITH (UPDLOCK, HOLDLOCK)
                 WHERE E.EXECUTION_ID COLLATE Latin1_General_100_BIN2 = @ExecutionId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.EXECUTION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ExecutionId))
                   AND E.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.WORK_SCOPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
                   AND E.PAIR_RUN_ID COLLATE Latin1_General_100_BIN2 = @PairRunId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.PAIR_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @PairRunId))
                   AND E.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2 = @SequenceRunId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.SEQUENCE_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @SequenceRunId))
                   AND E.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
                   AND E.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
                   AND E.RECIPE_ID COLLATE Latin1_General_100_BIN2 = @RecipeId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.RECIPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeId))
                   AND E.RECIPE_VERSION = @RecipeVersion
                   AND E.SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @SnapshotSchema
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @SnapshotSchema))
                   AND E.SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @SnapshotHash)
            BEGIN
                DECLARE @ExistingCapturedAt DATETIME2(7) = (
                    SELECT E.CAPTURED_AT
                      FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE E
                     WHERE E.EXECUTION_ID COLLATE Latin1_General_100_BIN2 = @ExecutionId
                       AND DATALENGTH(CONVERT(NVARCHAR(MAX), E.EXECUTION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ExecutionId)));
                IF @StartedTransaction = 1 COMMIT TRANSACTION;
                SELECT CAST(0 AS INT) AS Inserted, @ExistingCapturedAt AS RecordedAt;
                RETURN;
            END;
            THROW 51603, 'Canonical evidence identity is already bound to different content', 1;
        END;

        DECLARE @CapturedAt DATETIME2(7) = SYSUTCDATETIME();
        INSERT INTO dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
            (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
             OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT,
             CAPTURED_DATABASE_PRINCIPAL_NAME, CAPTURED_DATABASE_PRINCIPAL_SID)
        VALUES
            (@ExecutionId, @WorkScopeId, @PairRunId, @SequenceRunId, @EquipmentId,
             @OperationKey, @RecipeId, @RecipeVersion, @SnapshotSchema, @SnapshotHash, @CapturedAt,
             @WriterPrincipalName, @WriterPrincipalSid);

        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(1 AS INT) AS Inserted, @CapturedAt AS RecordedAt;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

CREATE PROCEDURE dbo.SYS_RELEASE_PROGRAM_ARTIFACT
    @ArtifactId NVARCHAR(MAX),
    @EquipmentId NVARCHAR(MAX),
    @OperationKey NVARCHAR(MAX),
    @ProductProfileId NVARCHAR(MAX),
    @PluginId NVARCHAR(MAX),
    @ProductDefinitionVersion NVARCHAR(MAX),
    @ProgramVersion NVARCHAR(MAX),
    @ProgramSchema NVARCHAR(MAX),
    @ProgramHash VARCHAR(MAX),
    @BoundRecipeSnapshotSchema NVARCHAR(MAX),
    @BoundRecipeSnapshotHash VARCHAR(MAX),
    @ReleasedBy NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    -- V159 RELEASE_COORDINATE_DIGEST is persisted and uniquely indexed. Do not inherit mutable
    -- caller options for the indexed-expression DML contract.
    SET ANSI_NULLS ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;

    IF @ArtifactId IS NULL OR DATALENGTH(@ArtifactId) NOT BETWEEN 2 AND 400
       OR @EquipmentId IS NULL OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR @OperationKey IS NULL OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR @ProductProfileId IS NULL OR DATALENGTH(@ProductProfileId) NOT BETWEEN 2 AND 200
       OR @PluginId IS NULL OR DATALENGTH(@PluginId) NOT BETWEEN 2 AND 400
       OR @ProductDefinitionVersion IS NULL OR DATALENGTH(@ProductDefinitionVersion) NOT BETWEEN 2 AND 200
       OR @ProgramVersion IS NULL OR DATALENGTH(@ProgramVersion) NOT BETWEEN 2 AND 200
       OR @ProgramSchema IS NULL OR DATALENGTH(@ProgramSchema) NOT BETWEEN 2 AND 200
       OR @ProgramHash IS NULL OR DATALENGTH(@ProgramHash)<>64
       OR @BoundRecipeSnapshotSchema IS NULL OR DATALENGTH(@BoundRecipeSnapshotSchema) NOT BETWEEN 2 AND 200
       OR @BoundRecipeSnapshotHash IS NULL OR DATALENGTH(@BoundRecipeSnapshotHash)<>64
       OR @ReleasedBy IS NULL OR DATALENGTH(@ReleasedBy) NOT BETWEEN 2 AND 100
      THROW 51625, 'Program release input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @InputCharacterProbe XML = (
        SELECT @ArtifactId AS [ArtifactId], @EquipmentId AS [EquipmentId],
               @OperationKey AS [OperationKey], @ProductProfileId AS [ProductProfileId],
               @PluginId AS [PluginId], @ProductDefinitionVersion AS [ProductDefinitionVersion],
               @ProgramVersion AS [ProgramVersion], @ProgramSchema AS [ProgramSchema],
               @BoundRecipeSnapshotSchema AS [BoundRecipeSnapshotSchema], @ReleasedBy AS [ReleasedBy]
          FOR XML PATH(N'Input'), TYPE);
    IF @ArtifactId IS NULL OR @EquipmentId IS NULL OR @OperationKey IS NULL
       OR @ProductProfileId IS NULL OR @PluginId IS NULL
       OR @ProductDefinitionVersion IS NULL OR @ProgramVersion IS NULL
       OR @ProgramSchema IS NULL OR @ProgramHash IS NULL
       OR @BoundRecipeSnapshotSchema IS NULL OR @BoundRecipeSnapshotHash IS NULL
       OR @ReleasedBy IS NULL
       OR DATALENGTH(@ArtifactId) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@ProductProfileId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@PluginId) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@ProductDefinitionVersion) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ProgramVersion) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ProgramSchema) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@BoundRecipeSnapshotSchema) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ReleasedBy) NOT BETWEEN 2 AND 100
       OR DATALENGTH(@ArtifactId)<>DATALENGTH(LTRIM(RTRIM(@ArtifactId)))
       OR DATALENGTH(@EquipmentId)<>DATALENGTH(LTRIM(RTRIM(@EquipmentId)))
       OR DATALENGTH(@OperationKey)<>DATALENGTH(LTRIM(RTRIM(@OperationKey)))
       OR DATALENGTH(@ProductProfileId)<>DATALENGTH(LTRIM(RTRIM(@ProductProfileId)))
       OR DATALENGTH(@PluginId)<>DATALENGTH(LTRIM(RTRIM(@PluginId)))
       OR DATALENGTH(@ProductDefinitionVersion)<>DATALENGTH(LTRIM(RTRIM(@ProductDefinitionVersion)))
       OR DATALENGTH(@ProgramVersion)<>DATALENGTH(LTRIM(RTRIM(@ProgramVersion)))
       OR DATALENGTH(@ProgramSchema)<>DATALENGTH(LTRIM(RTRIM(@ProgramSchema)))
       OR DATALENGTH(@BoundRecipeSnapshotSchema)<>DATALENGTH(LTRIM(RTRIM(@BoundRecipeSnapshotSchema)))
       OR DATALENGTH(@ReleasedBy)<>DATALENGTH(LTRIM(RTRIM(@ReleasedBy)))
       OR DATALENGTH(@ProgramHash)<>64
       OR @ProgramHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR DATALENGTH(@BoundRecipeSnapshotHash)<>64
       OR @BoundRecipeSnapshotHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            CONCAT(@ArtifactId, @EquipmentId, @OperationKey, @ProductProfileId, @PluginId,
                   @ProductDefinitionVersion, @ProgramVersion, @ProgramSchema,
                   @BoundRecipeSnapshotSchema, @ReleasedBy)
              COLLATE Latin1_General_100_BIN2)>0
       THROW 51625, 'Program release input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @StartedTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @StartedTransaction = 1;
    END;

    BEGIN TRY
        DECLARE @WriterPrincipalName NVARCHAR(128) = USER_NAME(),
                @WriterPrincipalSid VARBINARY(85);
        SELECT @WriterPrincipalSid = P.sid FROM sys.database_principals P
         WHERE P.principal_id = DATABASE_PRINCIPAL_ID(@WriterPrincipalName);
        IF @WriterPrincipalSid IS NULL
          THROW 51621, 'Program release writer has no auditable database principal', 1;

        DECLARE @CoordinateDigest BINARY(32) = CONVERT(BINARY(32), HASHBYTES('SHA2_256',
            CONCAT(
              N'E', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@EquipmentId)), 10), @EquipmentId,
              N'O', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@OperationKey)), 10), @OperationKey,
              N'R', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@ProductProfileId)), 10), @ProductProfileId,
              N'P', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@PluginId)), 10), @PluginId,
              N'D', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@ProductDefinitionVersion)), 10), @ProductDefinitionVersion,
              N'V', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@ProgramVersion)), 10), @ProgramVersion,
              N'S', RIGHT(REPLICATE(N'0', 10) + CONVERT(NVARCHAR(10), DATALENGTH(@ProgramSchema)), 10), @ProgramSchema)));
        DECLARE @ArtifactResource NVARCHAR(255) =
            N'NexaOne.SysRelease.Artifact.' + CONVERT(VARCHAR(64), HASHBYTES(
                'SHA2_256', CONCAT(DATALENGTH(@ArtifactId), N':', @ArtifactId)), 2);
        DECLARE @CoordinateResource NVARCHAR(255) =
            N'NexaOne.SysRelease.Coordinate.' + CONVERT(VARCHAR(64), @CoordinateDigest, 2);
        DECLARE @FirstResource NVARCHAR(255) = @ArtifactResource,
                @SecondResource NVARCHAR(255) = @CoordinateResource,
                @SwapResource NVARCHAR(255),
                @LockResult INT;
        IF @FirstResource COLLATE Latin1_General_100_BIN2
             > @SecondResource COLLATE Latin1_General_100_BIN2
        BEGIN
            SET @SwapResource = @FirstResource;
            SET @FirstResource = @SecondResource;
            SET @SecondResource = @SwapResource;
        END;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @FirstResource, @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 60000, @DbPrincipal = 'public';
        IF @LockResult < 0 THROW 51604, 'Could not acquire the program release identity lock', 1;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @SecondResource, @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 60000, @DbPrincipal = 'public';
        IF @LockResult < 0 THROW 51604, 'Could not acquire the program release coordinate lock', 1;

        IF EXISTS (
            SELECT 1
              FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
             WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId
                OR A.RELEASE_COORDINATE_DIGEST = @CoordinateDigest)
        BEGIN
            IF EXISTS (
                SELECT 1
                  FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
                 WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
                   AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
                   AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
                   AND A.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2 = @ProductProfileId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_PROFILE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProductProfileId))
                   AND A.PLUGIN_ID COLLATE Latin1_General_100_BIN2 = @PluginId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PLUGIN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @PluginId))
                   AND A.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2 = @ProductDefinitionVersion
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_DEFINITION_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProductDefinitionVersion))
                   AND A.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2 = @ProgramVersion
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramVersion))
                   AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = @ProgramSchema
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramSchema))
                   AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = @ProgramHash
                   AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @BoundRecipeSnapshotSchema
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @BoundRecipeSnapshotSchema))
                   AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @BoundRecipeSnapshotHash)
            BEGIN
                DECLARE @ExistingReleasedAt DATETIME2(7) = (
                    SELECT A.RELEASED_AT FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A
                     WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId
                       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId)));
                IF @StartedTransaction = 1 COMMIT TRANSACTION;
                SELECT CAST(0 AS INT) AS Inserted, @ExistingReleasedAt AS RecordedAt;
                RETURN;
            END;
            THROW 51605, 'Program artifact identity or release coordinate is already bound to different content', 1;
        END;

        DECLARE @ReleasedAt DATETIME2(7) = SYSUTCDATETIME();
        INSERT INTO dbo.SYS_RELEASED_PROGRAM_ARTIFACT
            (ARTIFACT_ID, EQUIPMENT_ID, OPERATION_KEY, PRODUCT_PROFILE_ID, PLUGIN_ID,
             PRODUCT_DEFINITION_VERSION, PROGRAM_VERSION, PROGRAM_SCHEMA, PROGRAM_HASH,
             BOUND_RECIPE_SNAPSHOT_SCHEMA, BOUND_RECIPE_SNAPSHOT_HASH, RELEASED_AT, RELEASED_BY,
             RELEASED_DATABASE_PRINCIPAL_NAME, RELEASED_DATABASE_PRINCIPAL_SID)
        VALUES
            (@ArtifactId, @EquipmentId, @OperationKey, @ProductProfileId, @PluginId,
             @ProductDefinitionVersion, @ProgramVersion, @ProgramSchema, @ProgramHash,
             @BoundRecipeSnapshotSchema, @BoundRecipeSnapshotHash, @ReleasedAt, @ReleasedBy,
             @WriterPrincipalName, @WriterPrincipalSid);

        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(1 AS INT) AS Inserted, @ReleasedAt AS RecordedAt;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

CREATE PROCEDURE dbo.SYS_REVOKE_PROGRAM_ARTIFACT
    @RevocationId NVARCHAR(MAX),
    @ArtifactId NVARCHAR(MAX),
    @RevokedBy NVARCHAR(MAX),
    @Reason NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RevocationId IS NULL OR DATALENGTH(@RevocationId) NOT BETWEEN 2 AND 200
       OR @ArtifactId IS NULL OR DATALENGTH(@ArtifactId) NOT BETWEEN 2 AND 400
       OR @RevokedBy IS NULL OR DATALENGTH(@RevokedBy) NOT BETWEEN 2 AND 100
       OR @Reason IS NULL OR DATALENGTH(@Reason) NOT BETWEEN 2 AND 2000
      THROW 51626, 'Program revocation input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @InputCharacterProbe XML = (
        SELECT @RevocationId AS [RevocationId], @ArtifactId AS [ArtifactId],
               @RevokedBy AS [RevokedBy], @Reason AS [Reason]
          FOR XML PATH(N'Input'), TYPE);
    IF @RevocationId IS NULL OR @ArtifactId IS NULL OR @RevokedBy IS NULL OR @Reason IS NULL
       OR DATALENGTH(@RevocationId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ArtifactId) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@RevokedBy) NOT BETWEEN 2 AND 100
       OR DATALENGTH(@Reason) NOT BETWEEN 2 AND 2000
       OR DATALENGTH(@RevocationId)<>DATALENGTH(LTRIM(RTRIM(@RevocationId)))
       OR DATALENGTH(@ArtifactId)<>DATALENGTH(LTRIM(RTRIM(@ArtifactId)))
       OR DATALENGTH(@RevokedBy)<>DATALENGTH(LTRIM(RTRIM(@RevokedBy)))
       OR DATALENGTH(@Reason)<>DATALENGTH(LTRIM(RTRIM(@Reason)))
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            CONCAT(@RevocationId, @ArtifactId, @RevokedBy, @Reason)
              COLLATE Latin1_General_100_BIN2)>0
       THROW 51626, 'Program revocation input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @StartedTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @StartedTransaction = 1;
    END;

    BEGIN TRY
        DECLARE @WriterPrincipalName NVARCHAR(128) = USER_NAME(),
                @WriterPrincipalSid VARBINARY(85);
        SELECT @WriterPrincipalSid = P.sid FROM sys.database_principals P
         WHERE P.principal_id = DATABASE_PRINCIPAL_ID(@WriterPrincipalName);
        IF @WriterPrincipalSid IS NULL
          THROW 51621, 'Program revocation writer has no auditable database principal', 1;

        -- Immutable artifact parent before the revocation child/range: this is the same order used
        -- by POM authority provisioning and therefore cannot form a parent/child lock inversion.
        DECLARE @ParentArtifactId NVARCHAR(200);
        SELECT @ParentArtifactId = A.ARTIFACT_ID
          FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
         WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId));
        IF @ParentArtifactId IS NULL
          THROW 51606, 'Program artifact revocation requires an exact released parent', 1;

        IF EXISTS (
            SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R WITH (UPDLOCK, HOLDLOCK)
             WHERE R.REVOCATION_ID COLLATE Latin1_General_100_BIN2 = @RevocationId
                OR R.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId)
        BEGIN
            IF EXISTS (
                SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R WITH (UPDLOCK, HOLDLOCK)
                 WHERE R.REVOCATION_ID COLLATE Latin1_General_100_BIN2 = @RevocationId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.REVOCATION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RevocationId))
                   AND R.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ArtifactId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
                   AND R.REVOKED_BY COLLATE Latin1_General_100_BIN2 = @RevokedBy
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.REVOKED_BY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RevokedBy))
                   AND R.REASON COLLATE Latin1_General_100_BIN2 = @Reason
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.REASON)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @Reason)))
            BEGIN
                DECLARE @ExistingRevokedAt DATETIME2(7) = (
                    SELECT R.REVOKED_AT FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
                     WHERE R.REVOCATION_ID COLLATE Latin1_General_100_BIN2 = @RevocationId
                       AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.REVOCATION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RevocationId)));
                IF @StartedTransaction = 1 COMMIT TRANSACTION;
                SELECT CAST(0 AS INT) AS Inserted, @ExistingRevokedAt AS RecordedAt;
                RETURN;
            END;
            THROW 51607, 'Program artifact revocation identity is already bound to different content', 1;
        END;

        DECLARE @RevokedAt DATETIME2(7) = SYSUTCDATETIME();
        INSERT INTO dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON,
             REVOKED_DATABASE_PRINCIPAL_NAME, REVOKED_DATABASE_PRINCIPAL_SID)
        VALUES (@RevocationId, @ArtifactId, @RevokedAt, @RevokedBy, @Reason,
                @WriterPrincipalName, @WriterPrincipalSid);

        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(1 AS INT) AS Inserted, @RevokedAt AS RecordedAt;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

CREATE PROCEDURE dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY
    @WorkScopeId NVARCHAR(MAX),
    @SourceClientId NVARCHAR(MAX),
    @EquipmentId NVARCHAR(MAX),
    @OperationKey NVARCHAR(MAX),
    @PairRunId NVARCHAR(MAX),
    @SequenceRunId NVARCHAR(MAX),
    @RecipeExecutionId NVARCHAR(MAX),
    @RecipeId NVARCHAR(MAX),
    @RecipeVersion INT,
    @RecipeSnapshotSchema NVARCHAR(MAX),
    @RecipeSnapshotHash VARCHAR(MAX),
    @ProgramArtifactId NVARCHAR(MAX),
    @ProgramSchema NVARCHAR(MAX),
    @ProgramHash VARCHAR(MAX),
    @ProvisionIdempotencyKey NVARCHAR(MAX),
    @ProvisionRequestHash VARCHAR(MAX),
    @ProvisionedBy NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RecipeVersion IS NULL OR @RecipeVersion<=0
       OR @WorkScopeId IS NULL OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
       OR @SourceClientId IS NULL OR DATALENGTH(@SourceClientId) NOT BETWEEN 2 AND 200
       OR @EquipmentId IS NULL OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR @OperationKey IS NULL OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR @PairRunId IS NULL OR DATALENGTH(@PairRunId) NOT BETWEEN 2 AND 200
       OR @SequenceRunId IS NULL OR DATALENGTH(@SequenceRunId) NOT BETWEEN 2 AND 200
       OR @RecipeExecutionId IS NULL OR DATALENGTH(@RecipeExecutionId) NOT BETWEEN 2 AND 200
       OR @RecipeId IS NULL OR DATALENGTH(@RecipeId) NOT BETWEEN 2 AND 200
       OR @RecipeSnapshotSchema IS NULL OR DATALENGTH(@RecipeSnapshotSchema) NOT BETWEEN 2 AND 200
       OR @RecipeSnapshotHash IS NULL OR DATALENGTH(@RecipeSnapshotHash)<>64
       OR @ProgramArtifactId IS NULL OR DATALENGTH(@ProgramArtifactId) NOT BETWEEN 2 AND 400
       OR @ProgramSchema IS NULL OR DATALENGTH(@ProgramSchema) NOT BETWEEN 2 AND 200
       OR @ProgramHash IS NULL OR DATALENGTH(@ProgramHash)<>64
       OR @ProvisionIdempotencyKey IS NULL OR DATALENGTH(@ProvisionIdempotencyKey) NOT BETWEEN 2 AND 200
       OR @ProvisionRequestHash IS NULL OR DATALENGTH(@ProvisionRequestHash)<>64
       OR @ProvisionedBy IS NULL OR DATALENGTH(@ProvisionedBy) NOT BETWEEN 2 AND 100
      THROW 51627, 'Projection authority input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @InputCharacterProbe XML = (
        SELECT @WorkScopeId AS [WorkScopeId], @SourceClientId AS [SourceClientId],
               @EquipmentId AS [EquipmentId], @OperationKey AS [OperationKey],
               @PairRunId AS [PairRunId], @SequenceRunId AS [SequenceRunId],
               @RecipeExecutionId AS [RecipeExecutionId], @RecipeId AS [RecipeId],
               @RecipeSnapshotSchema AS [RecipeSnapshotSchema],
               @ProgramArtifactId AS [ProgramArtifactId], @ProgramSchema AS [ProgramSchema],
               @ProvisionIdempotencyKey AS [ProvisionIdempotencyKey],
               @ProvisionedBy AS [ProvisionedBy]
          FOR XML PATH(N'Input'), TYPE);
    IF @RecipeVersion IS NULL OR @RecipeVersion<=0
       OR @WorkScopeId IS NULL OR @SourceClientId IS NULL OR @EquipmentId IS NULL
       OR @OperationKey IS NULL OR @PairRunId IS NULL OR @SequenceRunId IS NULL
       OR @RecipeExecutionId IS NULL OR @RecipeId IS NULL OR @RecipeSnapshotSchema IS NULL
       OR @RecipeSnapshotHash IS NULL OR @ProgramArtifactId IS NULL OR @ProgramSchema IS NULL
       OR @ProgramHash IS NULL OR @ProvisionIdempotencyKey IS NULL
       OR @ProvisionRequestHash IS NULL OR @ProvisionedBy IS NULL
       OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
       OR DATALENGTH(@SourceClientId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@EquipmentId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@OperationKey) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@PairRunId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@SequenceRunId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@RecipeExecutionId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@RecipeId) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@RecipeSnapshotSchema) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ProgramArtifactId) NOT BETWEEN 2 AND 400
       OR DATALENGTH(@ProgramSchema) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ProvisionIdempotencyKey) NOT BETWEEN 2 AND 200
       OR DATALENGTH(@ProvisionedBy) NOT BETWEEN 2 AND 100
       OR DATALENGTH(@WorkScopeId)<>DATALENGTH(LTRIM(RTRIM(@WorkScopeId)))
       OR DATALENGTH(@SourceClientId)<>DATALENGTH(LTRIM(RTRIM(@SourceClientId)))
       OR DATALENGTH(@EquipmentId)<>DATALENGTH(LTRIM(RTRIM(@EquipmentId)))
       OR DATALENGTH(@OperationKey)<>DATALENGTH(LTRIM(RTRIM(@OperationKey)))
       OR DATALENGTH(@PairRunId)<>DATALENGTH(LTRIM(RTRIM(@PairRunId)))
       OR DATALENGTH(@SequenceRunId)<>DATALENGTH(LTRIM(RTRIM(@SequenceRunId)))
       OR DATALENGTH(@RecipeExecutionId)<>DATALENGTH(LTRIM(RTRIM(@RecipeExecutionId)))
       OR DATALENGTH(@RecipeId)<>DATALENGTH(LTRIM(RTRIM(@RecipeId)))
       OR DATALENGTH(@RecipeSnapshotSchema)<>DATALENGTH(LTRIM(RTRIM(@RecipeSnapshotSchema)))
       OR DATALENGTH(@ProgramArtifactId)<>DATALENGTH(LTRIM(RTRIM(@ProgramArtifactId)))
       OR DATALENGTH(@ProgramSchema)<>DATALENGTH(LTRIM(RTRIM(@ProgramSchema)))
       OR DATALENGTH(@ProvisionIdempotencyKey)<>DATALENGTH(LTRIM(RTRIM(@ProvisionIdempotencyKey)))
       OR DATALENGTH(@ProvisionedBy)<>DATALENGTH(LTRIM(RTRIM(@ProvisionedBy)))
       OR DATALENGTH(@RecipeSnapshotHash)<>64
       OR @RecipeSnapshotHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR DATALENGTH(@ProgramHash)<>64
       OR @ProgramHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR DATALENGTH(@ProvisionRequestHash)<>64
       OR @ProvisionRequestHash COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            CONCAT(@WorkScopeId, @SourceClientId, @EquipmentId, @OperationKey, @PairRunId,
                   @SequenceRunId, @RecipeExecutionId, @RecipeId, @RecipeSnapshotSchema,
                   @ProgramArtifactId, @ProgramSchema, @ProvisionIdempotencyKey, @ProvisionedBy)
              COLLATE Latin1_General_100_BIN2)>0
       THROW 51627, 'Projection authority input is blank, oversized, padded, or non-canonical', 1;

    DECLARE @StartedTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @StartedTransaction = 1;
    END;

    BEGIN TRY
        -- One database procedure owns the global order: WorkScope parent -> authority identities ->
        -- RMS evidence -> SYS artifact -> revocation range -> active-product binding -> authority
        -- child insert. SQL Server repository callers perform no pre-locking outside this procedure.
        DECLARE @ScopeIdentity NVARCHAR(50), @ScopeType NVARCHAR(20), @TargetId NVARCHAR(100),
                @ProcessId NVARCHAR(50), @ScopeEquipmentId NVARCHAR(100), @ScopeRecipeId NVARCHAR(100),
                @ScopeRecipeVersion INT, @ScopeStatus NVARCHAR(20), @ScopeIsHold CHAR(1),
                @ScopeVersionNo INT, @StartQty DECIMAL(18,6), @CompleteQty DECIMAL(18,6),
                @ScrapQty DECIMAL(18,6), @HasExecution BIT;
        SELECT @ScopeIdentity = S.WORK_SCOPE_ID, @ScopeType = S.SCOPE_TYPE,
               @TargetId = S.TARGET_ID, @ProcessId = S.PROCESS_ID,
               @ScopeEquipmentId = S.EQUIPMENT_ID, @ScopeRecipeId = S.RECIPE_ID,
               @ScopeRecipeVersion = S.RECIPE_VERSION, @ScopeStatus = S.STATUS,
               @ScopeIsHold = S.IS_HOLD, @ScopeVersionNo = S.VERSION_NO,
               @StartQty = S.START_QTY, @CompleteQty = S.COMPLETE_QTY, @ScrapQty = S.SCRAP_QTY,
               @HasExecution = CASE WHEN EXISTS (
                   SELECT 1 FROM dbo.POM_WORK_SCOPE_EXECUTION E WITH (HOLDLOCK)
                    WHERE E.WORK_SCOPE_ID = S.WORK_SCOPE_ID) THEN 1 ELSE 0 END
          FROM dbo.POM_WORK_SCOPE S WITH (UPDLOCK, HOLDLOCK)
         WHERE S.WORK_SCOPE_ID = @WorkScopeId;
        IF @ScopeIdentity IS NULL THROW 51608, 'Projection authority WorkScope parent was not found', 1;

        DECLARE @ExactReplay BIT = 0, @ExistingProvisionedAt DATETIME2(7);
        IF EXISTS (
            SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A WITH (UPDLOCK, HOLDLOCK)
             WHERE A.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId)
        BEGIN
            IF EXISTS (
                SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A WITH (UPDLOCK, HOLDLOCK)
                 WHERE A.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.WORK_SCOPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
                   AND A.SOURCE_CLIENT_ID COLLATE Latin1_General_100_BIN2 = @SourceClientId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.SOURCE_CLIENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @SourceClientId))
                   AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
                   AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
                   AND A.PAIR_RUN_ID COLLATE Latin1_General_100_BIN2 = @PairRunId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PAIR_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @PairRunId))
                   AND A.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2 = @SequenceRunId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.SEQUENCE_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @SequenceRunId))
                   AND A.RECIPE_EXECUTION_ID COLLATE Latin1_General_100_BIN2 = @RecipeExecutionId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.RECIPE_EXECUTION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeExecutionId))
                   AND A.RECIPE_ID COLLATE Latin1_General_100_BIN2 = @RecipeId
                   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.RECIPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeId))
                   AND A.RECIPE_VERSION = @RecipeVersion
                   AND A.RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotSchema
                   AND A.RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotHash
                   AND A.PROGRAM_ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ProgramArtifactId
                   AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = @ProgramSchema
                   AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = @ProgramHash
                   AND A.PROVISION_IDEMPOTENCY_KEY COLLATE Latin1_General_100_BIN2 = @ProvisionIdempotencyKey
                   AND A.PROVISION_REQUEST_HASH COLLATE Latin1_General_100_BIN2 = @ProvisionRequestHash)
            BEGIN
                SET @ExistingProvisionedAt = (
                    SELECT A.PROVISIONED_AT FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A
                     WHERE A.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
                       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.WORK_SCOPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId)));
                SET @ExactReplay = 1;
            END;
            ELSE IF EXISTS (
                SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A
                 WHERE A.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
                   AND A.PROVISION_IDEMPOTENCY_KEY COLLATE Latin1_General_100_BIN2 = @ProvisionIdempotencyKey)
              THROW 51609, 'Projection authority idempotency key is already bound to different evidence', 1;
            ELSE
              THROW 51615, 'Projection authority WorkScope is already bound to different evidence', 1;
        END;

        IF @ExactReplay = 0 AND EXISTS (
            SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A WITH (UPDLOCK, HOLDLOCK)
             WHERE A.PROVISION_IDEMPOTENCY_KEY COLLATE Latin1_General_100_BIN2 = @ProvisionIdempotencyKey)
          THROW 51609, 'Projection authority idempotency key is already owned', 1;

        IF @ExactReplay = 0 AND EXISTS (
            SELECT 1 FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY A WITH (UPDLOCK, HOLDLOCK)
             WHERE A.RECIPE_EXECUTION_ID COLLATE Latin1_General_100_BIN2 = @RecipeExecutionId
                OR (A.SOURCE_CLIENT_ID COLLATE Latin1_General_100_BIN2 = @SourceClientId
                    AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
                    AND A.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2 = @SequenceRunId))
          THROW 51615, 'Projection authority evidence identity is already owned', 1;

        IF @ExactReplay = 0 AND
           (@ScopeStatus <> 'Created' OR @ScopeIsHold <> 'N' OR @ScopeVersionNo <> 1
            OR @StartQty <> 0 OR @CompleteQty <> 0 OR @ScrapQty <> 0 OR @HasExecution <> 0)
          THROW 51610, 'Projection authority requires a pristine WorkScope parent', 1;

        IF @ExactReplay = 0 AND
           (@ScopeIdentity COLLATE Latin1_General_100_BIN2 <> @WorkScopeId
            OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ScopeIdentity)) <> DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
            OR @ScopeType COLLATE Latin1_General_100_BIN2 <> N'Other'
            OR @ScopeEquipmentId IS NULL OR @ScopeEquipmentId COLLATE Latin1_General_100_BIN2 <> @EquipmentId
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ScopeEquipmentId)) <> DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           OR @ProcessId IS NULL OR @ProcessId COLLATE Latin1_General_100_BIN2 <> @OperationKey
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ProcessId)) <> DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           OR @TargetId COLLATE Latin1_General_100_BIN2 <> @PairRunId
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @TargetId)) <> DATALENGTH(CONVERT(NVARCHAR(MAX), @PairRunId))
           OR @ScopeRecipeId IS NULL OR @ScopeRecipeId COLLATE Latin1_General_100_BIN2 <> @RecipeId
           OR DATALENGTH(CONVERT(NVARCHAR(MAX), @ScopeRecipeId)) <> DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeId))
           OR @ScopeRecipeVersion <> @RecipeVersion)
          THROW 51614, 'Projection authority requires an exact WorkScope identity', 1;

        IF NOT EXISTS (
            SELECT 1 FROM dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE R WITH (UPDLOCK, HOLDLOCK)
             WHERE R.EXECUTION_ID COLLATE Latin1_General_100_BIN2 = @RecipeExecutionId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.EXECUTION_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeExecutionId))
               AND R.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.WORK_SCOPE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
               AND R.PAIR_RUN_ID COLLATE Latin1_General_100_BIN2 = @PairRunId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.PAIR_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @PairRunId))
               AND R.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2 = @SequenceRunId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.SEQUENCE_RUN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @SequenceRunId))
               AND R.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
               AND R.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
               AND R.RECIPE_ID COLLATE Latin1_General_100_BIN2 = @RecipeId
               AND R.RECIPE_VERSION = @RecipeVersion
               AND R.SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotSchema
                AND R.SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotHash)
          THROW 51611, 'Projection authority requires exact canonical recipe evidence', 1;

        -- Released-artifact values are read server-side. Product/plugin/deployment coordinates are
        -- never accepted from the application as an authority policy assertion.
        DECLARE @ArtifactProductProfileId NVARCHAR(100), @ArtifactPluginId NVARCHAR(200),
                @ArtifactProductDefinitionVersion NVARCHAR(100), @ArtifactProgramVersion NVARCHAR(100),
                @ArtifactProgramSchema NVARCHAR(100), @ArtifactProgramHash CHAR(64),
                @ArtifactBoundRecipeSchema NVARCHAR(100), @ArtifactBoundRecipeHash CHAR(64);
        SELECT @ArtifactProductProfileId = A.PRODUCT_PROFILE_ID,
               @ArtifactPluginId = A.PLUGIN_ID,
               @ArtifactProductDefinitionVersion = A.PRODUCT_DEFINITION_VERSION,
               @ArtifactProgramVersion = A.PROGRAM_VERSION,
               @ArtifactProgramSchema = A.PROGRAM_SCHEMA,
               @ArtifactProgramHash = A.PROGRAM_HASH,
               @ArtifactBoundRecipeSchema = A.BOUND_RECIPE_SNAPSHOT_SCHEMA,
               @ArtifactBoundRecipeHash = A.BOUND_RECIPE_SNAPSHOT_HASH
          FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
         WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ProgramArtifactId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramArtifactId))
           AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = @ProgramSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramSchema))
           AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = @ProgramHash
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_HASH)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramHash))
           AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotSchema))
           AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @RecipeSnapshotHash
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_HASH)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotHash));
        IF @ArtifactProductProfileId IS NULL
          THROW 51611, 'Projection authority requires an exact released program artifact', 1;

        IF @ExactReplay = 0 AND EXISTS (
            SELECT 1 FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION V WITH (UPDLOCK, HOLDLOCK)
             WHERE V.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ProgramArtifactId
                AND DATALENGTH(CONVERT(NVARCHAR(MAX), V.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramArtifactId))
            )
           THROW 51612, 'Projection authority cannot be created for a revoked program artifact', 1;

        DECLARE @RuntimePrincipalName NVARCHAR(128) = USER_NAME(),
                @RuntimePrincipalSid VARBINARY(85);
        SELECT @RuntimePrincipalSid = P.sid
          FROM sys.database_principals P
         WHERE P.principal_id = DATABASE_PRINCIPAL_ID(@RuntimePrincipalName);
        IF @RuntimePrincipalSid IS NULL
          THROW 51613, 'Projection authority caller has no auditable database principal', 1;

        IF NOT EXISTS (
            SELECT 1
              FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
             WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2 = @RuntimePrincipalName
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @RuntimePrincipalName))
               AND B.DATABASE_PRINCIPAL_SID = @RuntimePrincipalSid
               AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = @EquipmentId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
               AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = @OperationKey
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
               AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = @ProgramArtifactId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramArtifactId))
               AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2 = @ArtifactProductProfileId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductProfileId))
               AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2 = @ArtifactPluginId
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactPluginId))
               AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2 = @ArtifactProductDefinitionVersion
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductDefinitionVersion))
               AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2 = @ArtifactProgramVersion
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramVersion))
               AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = @ArtifactProgramSchema
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramSchema))
               AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = @ArtifactProgramHash
               AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = @ArtifactBoundRecipeSchema
               AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactBoundRecipeSchema))
               AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = @ArtifactBoundRecipeHash)
          THROW 51613, 'Projection authority caller is not commissioned for the released product coordinate', 1;

        IF @ExactReplay = 1
        BEGIN
            IF @StartedTransaction = 1 COMMIT TRANSACTION;
            SELECT CAST(0 AS INT) AS Inserted, @ExistingProvisionedAt AS RecordedAt;
            RETURN;
        END;

        DECLARE @ProvisionedAt DATETIME2(7) = SYSUTCDATETIME();
        INSERT INTO dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY
            (WORK_SCOPE_ID, SOURCE_CLIENT_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID,
             SEQUENCE_RUN_ID, RECIPE_EXECUTION_ID, RECIPE_ID, RECIPE_VERSION,
             RECIPE_SNAPSHOT_SCHEMA, RECIPE_SNAPSHOT_HASH, PROGRAM_ARTIFACT_ID,
             PROGRAM_SCHEMA, PROGRAM_HASH, BASELINE_VERSION_NO, LAST_APPLIED_VERSION_NO,
             PROVISION_IDEMPOTENCY_KEY, PROVISION_REQUEST_HASH, PROVISIONED_AT, PROVISIONED_BY,
             LAST_APPLIED_AT, PROVISIONED_DATABASE_PRINCIPAL_NAME,
             PROVISIONED_DATABASE_PRINCIPAL_SID)
        VALUES
            (@WorkScopeId, @SourceClientId, @EquipmentId, @OperationKey, @PairRunId,
             @SequenceRunId, @RecipeExecutionId, @RecipeId, @RecipeVersion,
             @RecipeSnapshotSchema, @RecipeSnapshotHash, @ProgramArtifactId,
               @ProgramSchema, @ProgramHash, @ScopeVersionNo, @ScopeVersionNo,
              @ProvisionIdempotencyKey, @ProvisionRequestHash, @ProvisionedAt, @ProvisionedBy, NULL,
              @RuntimePrincipalName, @RuntimePrincipalSid);

        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(1 AS INT) AS Inserted, @ProvisionedAt AS RecordedAt;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

-- Lock-sensitive ingest/commit lookup. A view hint leaves join order to the optimizer; this module
-- fixes the security-object order to authority -> released artifact -> active binding and returns
-- no row when the current principal is not commissioned for the exact immutable artifact.
CREATE PROCEDURE dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE
    @WorkScopeId NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkScopeId IS NULL OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
      THROW 51627, 'Projection authority lookup input is blank or oversized', 1;
    DECLARE @InputCharacterProbe XML = (
        SELECT @WorkScopeId AS [WorkScopeId] FOR XML PATH(N'Input'), TYPE);
    IF DATALENGTH(@WorkScopeId)<>DATALENGTH(LTRIM(RTRIM(@WorkScopeId)))
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            @WorkScopeId COLLATE Latin1_General_100_BIN2)>0
      THROW 51627, 'Projection authority lookup input is padded or non-canonical', 1;

    DECLARE @AuthorityWorkScopeId NVARCHAR(50), @ArtifactId NVARCHAR(200),
            @EquipmentId NVARCHAR(100), @OperationKey NVARCHAR(200),
            @ProgramSchema NVARCHAR(100), @ProgramHash CHAR(64),
            @RecipeSchema NVARCHAR(100), @RecipeHash CHAR(64);
    SELECT @AuthorityWorkScopeId=U.WORK_SCOPE_ID,
           @ArtifactId=U.PROGRAM_ARTIFACT_ID,
           @EquipmentId=U.EQUIPMENT_ID,
           @OperationKey=U.OPERATION_KEY,
           @ProgramSchema=U.PROGRAM_SCHEMA,
           @ProgramHash=U.PROGRAM_HASH,
           @RecipeSchema=U.RECIPE_SNAPSHOT_SCHEMA,
           @RecipeHash=U.RECIPE_SNAPSHOT_HASH
      FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY U WITH (UPDLOCK, HOLDLOCK)
     WHERE U.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
             =@WorkScopeId COLLATE Latin1_General_100_BIN2
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.WORK_SCOPE_ID))
             =DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId));
    IF @AuthorityWorkScopeId IS NULL RETURN;

    DECLARE @ArtifactProductProfileId NVARCHAR(100), @ArtifactPluginId NVARCHAR(200),
            @ArtifactProductDefinitionVersion NVARCHAR(100), @ArtifactProgramVersion NVARCHAR(100),
            @ArtifactProgramSchema NVARCHAR(100), @ArtifactProgramHash CHAR(64),
            @ArtifactRecipeSchema NVARCHAR(100), @ArtifactRecipeHash CHAR(64);
    SELECT @ArtifactProductProfileId=A.PRODUCT_PROFILE_ID,
           @ArtifactPluginId=A.PLUGIN_ID,
           @ArtifactProductDefinitionVersion=A.PRODUCT_DEFINITION_VERSION,
           @ArtifactProgramVersion=A.PROGRAM_VERSION,
           @ArtifactProgramSchema=A.PROGRAM_SCHEMA,
           @ArtifactProgramHash=A.PROGRAM_HASH,
           @ArtifactRecipeSchema=A.BOUND_RECIPE_SNAPSHOT_SCHEMA,
           @ArtifactRecipeHash=A.BOUND_RECIPE_SNAPSHOT_HASH
      FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
     WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@ArtifactId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
       AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@EquipmentId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
       AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@OperationKey
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
       AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@ProgramSchema
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramSchema))
       AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@ProgramHash
       AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@RecipeSchema
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSchema))
       AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@RecipeHash;
    IF @ArtifactProductProfileId IS NULL RETURN;

    DECLARE @RuntimePrincipalName NVARCHAR(128)=USER_NAME(), @RuntimePrincipalSid VARBINARY(85);
    SELECT @RuntimePrincipalSid=P.sid FROM sys.database_principals P
     WHERE P.principal_id=DATABASE_PRINCIPAL_ID(@RuntimePrincipalName);
    IF @RuntimePrincipalSid IS NULL RETURN;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
         WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2=@RuntimePrincipalName
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))=DATALENGTH(CONVERT(NVARCHAR(MAX), @RuntimePrincipalName))
           AND B.DATABASE_PRINCIPAL_SID=@RuntimePrincipalSid
           AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@ArtifactId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
           AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@EquipmentId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@OperationKey
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@ArtifactProductProfileId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductProfileId))
           AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@ArtifactPluginId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactPluginId))
           AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2=@ArtifactProductDefinitionVersion
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductDefinitionVersion))
           AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@ArtifactProgramVersion
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramVersion))
           AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@ArtifactProgramSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramSchema))
           AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@ArtifactProgramHash
           AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@ArtifactRecipeSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactRecipeSchema))
           AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@ArtifactRecipeHash)
      RETURN;

    SELECT U.WORK_SCOPE_ID AS WorkScopeId, U.SOURCE_CLIENT_ID AS SourceClientId,
           U.EQUIPMENT_ID AS EquipmentId, U.OPERATION_KEY AS OperationKey,
           U.PAIR_RUN_ID AS PairRunId, U.SEQUENCE_RUN_ID AS SequenceRunId,
           U.RECIPE_EXECUTION_ID AS RecipeExecutionId, U.RECIPE_ID AS RecipeId,
           U.RECIPE_VERSION AS RecipeVersion, U.RECIPE_SNAPSHOT_SCHEMA AS RecipeSnapshotSchema,
           U.RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
           U.PROGRAM_ARTIFACT_ID AS ProgramArtifactId, U.PROGRAM_SCHEMA AS ProgramSchema,
           U.PROGRAM_HASH AS ProgramHash, U.BASELINE_VERSION_NO AS BaselineVersionNo,
           U.LAST_APPLIED_VERSION_NO AS LastAppliedVersionNo,
           U.PROVISION_IDEMPOTENCY_KEY AS ProvisionIdempotencyKey,
           U.PROVISION_REQUEST_HASH AS ProvisionRequestHash,
           U.PROVISIONED_AT AS ProvisionedAt, U.PROVISIONED_BY AS ProvisionedBy,
           U.LAST_APPLIED_AT AS LastAppliedAt
      FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY U
     WHERE U.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2=@WorkScopeId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.WORK_SCOPE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId));
END;

-- The runtime never receives direct base-table UPDATE. Commit revalidates the same exact active
-- artifact binding under authority -> artifact -> binding locks, then advances only the monotonic
-- lineage columns with database UTC.
CREATE PROCEDURE dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE
    @WorkScopeId NVARCHAR(MAX),
    @ExpectedVersion INT,
    @ResultVersion INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @WorkScopeId IS NULL OR DATALENGTH(@WorkScopeId) NOT BETWEEN 2 AND 100
       OR @ExpectedVersion IS NULL OR @ExpectedVersion<1
       OR @ResultVersion IS NULL OR @ResultVersion<=@ExpectedVersion
      THROW 51628, 'Projection authority lineage input is invalid', 1;
    DECLARE @InputCharacterProbe XML = (
        SELECT @WorkScopeId AS [WorkScopeId] FOR XML PATH(N'Input'), TYPE);
    IF DATALENGTH(@WorkScopeId)<>DATALENGTH(LTRIM(RTRIM(@WorkScopeId)))
       OR PATINDEX(N'%[' + NCHAR(1) + N'-' + NCHAR(31) + NCHAR(127) + N']%',
            @WorkScopeId COLLATE Latin1_General_100_BIN2)>0
      THROW 51628, 'Projection authority lineage input is non-canonical', 1;

    DECLARE @StartedTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @StartedTransaction = 1;
    END;

    BEGIN TRY
    DECLARE @AuthorityWorkScopeId NVARCHAR(50), @ArtifactId NVARCHAR(200),
            @EquipmentId NVARCHAR(100), @OperationKey NVARCHAR(200),
            @ProgramSchema NVARCHAR(100), @ProgramHash CHAR(64),
            @RecipeSchema NVARCHAR(100), @RecipeHash CHAR(64);
    SELECT @AuthorityWorkScopeId=U.WORK_SCOPE_ID,
           @ArtifactId=U.PROGRAM_ARTIFACT_ID,
           @EquipmentId=U.EQUIPMENT_ID,
           @OperationKey=U.OPERATION_KEY,
           @ProgramSchema=U.PROGRAM_SCHEMA,
           @ProgramHash=U.PROGRAM_HASH,
           @RecipeSchema=U.RECIPE_SNAPSHOT_SCHEMA,
           @RecipeHash=U.RECIPE_SNAPSHOT_HASH
      FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY U WITH (UPDLOCK, HOLDLOCK)
     WHERE U.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2=@WorkScopeId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), U.WORK_SCOPE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
       AND U.LAST_APPLIED_VERSION_NO=@ExpectedVersion;
    IF @AuthorityWorkScopeId IS NULL
    BEGIN
        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(0 AS INT) AS AffectedRows;
        RETURN;
    END;

    DECLARE @ArtifactProductProfileId NVARCHAR(100), @ArtifactPluginId NVARCHAR(200),
            @ArtifactProductDefinitionVersion NVARCHAR(100), @ArtifactProgramVersion NVARCHAR(100),
            @ArtifactProgramSchema NVARCHAR(100), @ArtifactProgramHash CHAR(64),
            @ArtifactRecipeSchema NVARCHAR(100), @ArtifactRecipeHash CHAR(64);
    SELECT @ArtifactProductProfileId=A.PRODUCT_PROFILE_ID,
           @ArtifactPluginId=A.PLUGIN_ID,
           @ArtifactProductDefinitionVersion=A.PRODUCT_DEFINITION_VERSION,
           @ArtifactProgramVersion=A.PROGRAM_VERSION,
           @ArtifactProgramSchema=A.PROGRAM_SCHEMA,
           @ArtifactProgramHash=A.PROGRAM_HASH,
           @ArtifactRecipeSchema=A.BOUND_RECIPE_SNAPSHOT_SCHEMA,
           @ArtifactRecipeHash=A.BOUND_RECIPE_SNAPSHOT_HASH
      FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
     WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@ArtifactId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
       AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@EquipmentId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
       AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@OperationKey
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
       AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@ProgramSchema
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramSchema))
       AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@ProgramHash
       AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@RecipeSchema
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSchema))
       AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@RecipeHash;
    IF @ArtifactProductProfileId IS NULL
    BEGIN
        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(0 AS INT) AS AffectedRows;
        RETURN;
    END;

    DECLARE @RuntimePrincipalName NVARCHAR(128)=USER_NAME(), @RuntimePrincipalSid VARBINARY(85);
    SELECT @RuntimePrincipalSid=P.sid FROM sys.database_principals P
     WHERE P.principal_id=DATABASE_PRINCIPAL_ID(@RuntimePrincipalName);
    IF @RuntimePrincipalSid IS NULL OR NOT EXISTS (
        SELECT 1 FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B WITH (UPDLOCK, HOLDLOCK)
         WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2=@RuntimePrincipalName
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME))=DATALENGTH(CONVERT(NVARCHAR(MAX), @RuntimePrincipalName))
           AND B.DATABASE_PRINCIPAL_SID=@RuntimePrincipalSid
           AND B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2=@ArtifactId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactId))
           AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2=@EquipmentId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2=@OperationKey
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY))=DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2=@ArtifactProductProfileId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductProfileId))
           AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2=@ArtifactPluginId
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactPluginId))
           AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2=@ArtifactProductDefinitionVersion
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProductDefinitionVersion))
           AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2=@ArtifactProgramVersion
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramVersion))
           AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2=@ArtifactProgramSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactProgramSchema))
           AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2=@ArtifactProgramHash
           AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2=@ArtifactRecipeSchema
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA))=DATALENGTH(CONVERT(NVARCHAR(MAX), @ArtifactRecipeSchema))
           AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2=@ArtifactRecipeHash)
    BEGIN
        IF @StartedTransaction = 1 COMMIT TRANSACTION;
        SELECT CAST(0 AS INT) AS AffectedRows;
        RETURN;
    END;

    UPDATE dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY
       SET LAST_APPLIED_VERSION_NO=@ResultVersion,
           LAST_APPLIED_AT=SYSUTCDATETIME()
     WHERE WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2=@WorkScopeId
       AND DATALENGTH(CONVERT(NVARCHAR(MAX), WORK_SCOPE_ID))=DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
       AND LAST_APPLIED_VERSION_NO=@ExpectedVersion;
    DECLARE @AffectedRows INT=@@ROWCOUNT;
    IF @StartedTransaction = 1 COMMIT TRANSACTION;
    SELECT CAST(@AffectedRows AS INT) AS AffectedRows;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

-- Caller-filtered execution fence. Provisioning provenance remains immutable audit, while the
-- current principal must retain an exact deployed-artifact binding at ingest, claim, and commit
-- time. Revocation blocks new authority, while recovery/replay of existing authority remains
-- possible until commissioning removes the binding. Credential rotation does not rewrite audit.
CREATE VIEW dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY
AS
SELECT U.*
  FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY U
  JOIN dbo.SYS_RELEASED_PROGRAM_ARTIFACT A
    ON A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = U.PROGRAM_ARTIFACT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), U.PROGRAM_ARTIFACT_ID))
   AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = U.EQUIPMENT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), U.EQUIPMENT_ID))
   AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = U.OPERATION_KEY
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), U.OPERATION_KEY))
   AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = U.PROGRAM_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), U.PROGRAM_SCHEMA))
   AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = U.PROGRAM_HASH
   AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = U.RECIPE_SNAPSHOT_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), U.RECIPE_SNAPSHOT_SCHEMA))
   AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = U.RECIPE_SNAPSHOT_HASH
  JOIN dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING B
    ON B.ARTIFACT_ID COLLATE Latin1_General_100_BIN2 = A.ARTIFACT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.ARTIFACT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))
   AND B.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2 = A.EQUIPMENT_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.EQUIPMENT_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))
   AND B.OPERATION_KEY COLLATE Latin1_General_100_BIN2 = A.OPERATION_KEY
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.OPERATION_KEY)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))
   AND B.PRODUCT_PROFILE_ID COLLATE Latin1_General_100_BIN2 = A.PRODUCT_PROFILE_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_PROFILE_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_PROFILE_ID))
   AND B.PLUGIN_ID COLLATE Latin1_General_100_BIN2 = A.PLUGIN_ID
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PLUGIN_ID)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.PLUGIN_ID))
   AND B.PRODUCT_DEFINITION_VERSION COLLATE Latin1_General_100_BIN2 = A.PRODUCT_DEFINITION_VERSION
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PRODUCT_DEFINITION_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.PRODUCT_DEFINITION_VERSION))
   AND B.PROGRAM_VERSION COLLATE Latin1_General_100_BIN2 = A.PROGRAM_VERSION
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_VERSION)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_VERSION))
   AND B.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2 = A.PROGRAM_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.PROGRAM_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))
   AND B.PROGRAM_HASH COLLATE Latin1_General_100_BIN2 = A.PROGRAM_HASH
   AND B.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2 = A.BOUND_RECIPE_SNAPSHOT_SCHEMA
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))
   AND B.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2 = A.BOUND_RECIPE_SNAPSHOT_HASH
 WHERE B.DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2 = USER_NAME()
   AND DATALENGTH(CONVERT(NVARCHAR(MAX), B.DATABASE_PRINCIPAL_NAME)) = DATALENGTH(CONVERT(NVARCHAR(MAX), USER_NAME()))
    AND B.DATABASE_PRINCIPAL_SID = (
        SELECT P.sid FROM sys.database_principals P
         WHERE P.principal_id = DATABASE_PRINCIPAL_ID(USER_NAME()));

-- Aggregate mutation must remain fenced after a runtime binding is removed. Expose only the
-- immutable parent key needed by WorkScopeRepository; never reuse the caller-filtered execution
-- view for this lifetime fence or a decommission could make projection-owned scopes writable.
CREATE VIEW dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE
AS
SELECT WORK_SCOPE_ID
  FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY;

CREATE TRIGGER dbo.TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_PRINCIPAL_PROVENANCE
ON dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D ON D.WORK_SCOPE_ID = I.WORK_SCOPE_ID
         WHERE I.PROVISIONED_DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
                 <> D.PROVISIONED_DATABASE_PRINCIPAL_NAME COLLATE Latin1_General_100_BIN2
            OR DATALENGTH(CONVERT(NVARCHAR(MAX), I.PROVISIONED_DATABASE_PRINCIPAL_NAME))
                 <> DATALENGTH(CONVERT(NVARCHAR(MAX), D.PROVISIONED_DATABASE_PRINCIPAL_NAME))
            OR I.PROVISIONED_DATABASE_PRINCIPAL_SID <> D.PROVISIONED_DATABASE_PRINCIPAL_SID)
      THROW 51551, 'Projection authority provisioning database principal is immutable', 1;
END;

-- Direct writes are unavailable to every ordinary database user, including broad db_datawriter
-- members. The same-owner static procedures above cross this boundary through ownership chaining;
-- object owners and sysadmin are intentionally excluded and rejected by commissioning.
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE TO public;
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.SYS_RELEASED_PROGRAM_ARTIFACT TO public;
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION TO public;
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE TO public;
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY TO public;
DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING TO public;

GRANT SELECT ON OBJECT::dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE TO NexaOneProjectionRuntime;
GRANT SELECT ON OBJECT::dbo.SYS_RELEASED_PROGRAM_ARTIFACT TO NexaOneProjectionRuntime;
GRANT SELECT ON OBJECT::dbo.SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION TO NexaOneProjectionRuntime;
GRANT SELECT ON OBJECT::dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY_TRUST_STATE TO NexaOneProjectionRuntime;
GRANT SELECT ON OBJECT::dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY TO NexaOneProjectionRuntime;
GRANT SELECT ON OBJECT::dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE TO NexaOneProjectionRuntime;
GRANT EXECUTE ON OBJECT::dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY TO NexaOneProjectionRuntime;
GRANT EXECUTE ON OBJECT::dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE TO NexaOneProjectionRuntime;
GRANT EXECUTE ON OBJECT::dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE TO NexaOneProjectionRuntime;

GRANT EXECUTE ON OBJECT::dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE
  TO NexaOneRmsEvidenceWriter;
GRANT EXECUTE ON OBJECT::dbo.SYS_RELEASE_PROGRAM_ARTIFACT TO NexaOneSysReleaseWriter;
GRANT EXECUTE ON OBJECT::dbo.SYS_REVOKE_PROGRAM_ARTIFACT TO NexaOneSysReleaseWriter;

DENY EXECUTE ON OBJECT::dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE
  TO NexaOneProjectionRuntime, NexaOneSysReleaseWriter;
DENY EXECUTE ON OBJECT::dbo.SYS_RELEASE_PROGRAM_ARTIFACT
  TO NexaOneProjectionRuntime, NexaOneRmsEvidenceWriter;
DENY EXECUTE ON OBJECT::dbo.SYS_REVOKE_PROGRAM_ARTIFACT
  TO NexaOneProjectionRuntime, NexaOneRmsEvidenceWriter;
DENY EXECUTE ON OBJECT::dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY
  TO NexaOneRmsEvidenceWriter, NexaOneSysReleaseWriter;
DENY EXECUTE ON OBJECT::dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE
  TO NexaOneRmsEvidenceWriter, NexaOneSysReleaseWriter;
DENY EXECUTE ON OBJECT::dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE
  TO NexaOneRmsEvidenceWriter, NexaOneSysReleaseWriter;
-- SQLITE-OMIT-END
