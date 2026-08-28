-- Owner: FDC. Persist the monotonic boundary before raw TRACE rows become retention-incomplete.

CREATE TABLE FDC_TRACE_RETENTION_STATE (
    STATE_ID                 NVARCHAR(20)    NOT NULL,
    COMPLETENESS_BOUNDARY    DATETIME2(7)    NOT NULL,
    CREATED_BY               NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT               DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
    UPDATED_BY               NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT               DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_FDC_TRACE_RETENTION_STATE PRIMARY KEY (STATE_ID),
    CONSTRAINT CK_FDC_TRACE_RETENTION_STATE_ID CHECK (STATE_ID = 'GLOBAL')
);

-- A V148-only installation may already have deleted TRACE before this state existed. Seed one
-- 100ns tick after the earliest retained timestamp, or database time when no sample remains. The
-- tick treats an only-partly-retained equal-timestamp batch as a gap instead of claiming it whole.
-- SQLite omits this DML and seeds from C# after dialect conversion so AddTicks(1) stays exact.
-- SQLITE-OMIT-BEGIN
INSERT INTO FDC_TRACE_RETENTION_STATE
    (STATE_ID, COMPLETENESS_BOUNDARY, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
SELECT
    'GLOBAL', COALESCE(DATEADD(NANOSECOND, 100, MIN(COLLECTED_AT)), SYSUTCDATETIME()),
    'SYSTEM', SYSUTCDATETIME(), 'SYSTEM', SYSUTCDATETIME()
FROM FDC_COLLECT_DATA WITH (TABLOCKX, HOLDLOCK);
-- SQLITE-OMIT-END

-- SQL Server production guard. SQLite receives equivalent canonical INSERT/UPDATE/DELETE triggers
-- from SqliteSchemaInitializer, including legacy incremental databases without CHECK constraints.
-- SQLITE-OMIT-BEGIN
EXEC(N'CREATE TRIGGER TR_FDC_TRACE_RETENTION_STATE_GUARD
ON FDC_TRACE_RETENTION_STATE
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
          FROM deleted D
          LEFT JOIN inserted I ON I.STATE_ID = D.STATE_ID
         WHERE I.STATE_ID IS NULL)
        THROW 51500, ''FDC TRACE retention completeness state is not deletable.'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D ON D.STATE_ID = I.STATE_ID
         WHERE I.STATE_ID <> ''GLOBAL''
            OR D.STATE_ID <> ''GLOBAL''
            OR (D.COMPLETENESS_BOUNDARY IS NOT NULL
                AND (I.COMPLETENESS_BOUNDARY IS NULL
                     OR I.COMPLETENESS_BOUNDARY < D.COMPLETENESS_BOUNDARY)))
        THROW 51501, ''FDC TRACE retention completeness boundary cannot move backward.'', 1;
END');

-- A downlevel binary or direct writer must not bypass the completeness state. Only rows strictly
-- older than the already-recorded boundary may be deleted; the repository advances the boundary
-- in the same transaction before issuing its bounded DELETE.
EXEC(N'CREATE TRIGGER TR_FDC_COLLECT_RETENTION_DELETE_GUARD
ON FDC_COLLECT_DATA
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
          FROM deleted D
         WHERE NOT EXISTS (
             SELECT 1
               FROM FDC_TRACE_RETENTION_STATE S
              WHERE S.STATE_ID = ''GLOBAL''
                AND D.COLLECTED_AT < S.COMPLETENESS_BOUNDARY))
        THROW 51502, ''FDC raw TRACE cannot be deleted before advancing its completeness boundary.'', 1;
END');

-- The completeness boundary is a promise that raw history before it will never appear again.
-- Reject late/backdated inserts after cutover and keep the raw TRACE ledger append-only; otherwise
-- a downlevel/direct writer could make a row silently unreachable or replay it under a new time.
EXEC(N'CREATE TRIGGER TR_FDC_COLLECT_COMPLETENESS_INSERT_GUARD
ON FDC_COLLECT_DATA
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
          FROM inserted I
         WHERE NOT EXISTS (
              SELECT 1
                FROM FDC_TRACE_RETENTION_STATE S WITH (READCOMMITTEDLOCK, HOLDLOCK)
               WHERE S.STATE_ID = ''GLOBAL''
                 AND I.COLLECTED_AT >= S.COMPLETENESS_BOUNDARY))
        THROW 51503, ''FDC raw TRACE cannot be inserted before its completeness boundary.'', 1;
END');

EXEC(N'CREATE TRIGGER TR_FDC_COLLECT_APPEND_ONLY_UPDATE
ON FDC_COLLECT_DATA
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51504, ''FDC raw TRACE is append-only.'', 1;
END');
-- SQLITE-OMIT-END
