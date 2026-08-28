-- Owner: FDC. Protect durable effect transitions and bound time-series retention scans.

-- DeleteOlderThanAsync selects deterministic batches by this leading time key. The COLLECT_ID
-- tie-breaker keeps each batch stable when many samples share the same timestamp.
CREATE INDEX IX_FDC_COLLECT_RETENTION
    ON FDC_COLLECT_DATA (COLLECTED_AT, COLLECT_ID);

-- SQL Server production guard. SQLite receives equivalent canonical UPDATE/DELETE triggers from
-- SqliteSchemaInitializer because SQLite cannot add the V146 constraints incrementally.
-- A recovery reconciliation may deliberately reassert a ConditionNormalized/ReleasePending STOP
-- as Applied before trusting a fresh PLC snapshot. Other backward jumps remain invalid.
-- SQLITE-OMIT-BEGIN
EXEC(N'CREATE TRIGGER TR_FDC_INTERLOCK_EFFECT_LIFECYCLE_TRANSITION
ON FDC_INTERLOCK_HISTORY
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
          FROM deleted D
          LEFT JOIN inserted I ON I.HISTORY_ID = D.HISTORY_ID
         WHERE I.HISTORY_ID IS NULL)
        THROW 51480, ''FDC interlock effect history is append-only.'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D ON D.HISTORY_ID = I.HISTORY_ID
         WHERE I.VERSION <= D.VERSION
            OR D.EFFECT_STATE = ''Resolved''
            OR NOT (
                (D.EFFECT_STATE = ''Prepared''
                 AND I.EFFECT_STATE IN (''Prepared'', ''Applied''))
                OR (D.EFFECT_STATE = ''Applied''
                    AND I.EFFECT_STATE IN (''Applied'', ''ConditionNormalized''))
                OR (D.EFFECT_STATE = ''ConditionNormalized''
                    AND I.EFFECT_STATE IN (
                        ''Applied'', ''ConditionNormalized'', ''ReleasePending'', ''Resolved''))
                OR (D.EFFECT_STATE = ''ReleasePending''
                    AND I.EFFECT_STATE IN (''Applied'', ''ReleasePending'', ''Resolved''))))
        THROW 51481, ''FDC interlock effect lifecycle transition or version is invalid.'', 1;
END');
-- SQLITE-OMIT-END
