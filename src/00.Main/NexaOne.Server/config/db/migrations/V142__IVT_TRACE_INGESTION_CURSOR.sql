-- Owner: IVT. Keep TRACE source progress in one row per binding instead of deriving it by
-- rescanning the ever-growing projection inbox. IS_WORK_ITEM separates the small retry queue from
-- terminal inbox evidence so the filtered work index preserves global chronological order.

ALTER TABLE IVT_TRACE_PROJECTION_INBOX
    ADD IS_WORK_ITEM BIT NOT NULL DEFAULT 1;

UPDATE IVT_TRACE_PROJECTION_INBOX
   SET IS_WORK_ITEM = 0
 WHERE STATUS IN ('Applied', 'Ignored');

CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
    BINDING_ID          NVARCHAR(50)    NOT NULL,
    LAST_COLLECT_ID     NVARCHAR(50)    NOT NULL,
    LAST_COLLECTED_AT   DATETIME2       NOT NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_IVT_TRACE_INGESTION_CURSOR PRIMARY KEY (BINDING_ID),
    CONSTRAINT FK_IVT_TRACE_CURSOR_BINDING FOREIGN KEY (BINDING_ID)
        REFERENCES IVT_TRACE_CONSUMPTION_BINDING (BINDING_ID)
);

-- One-time upgrade backfill. Future progress is advanced atomically with the inbox insert.
-- ROW_NUMBER performs one ordered pass instead of a correlated anti-join over every historical
-- inbox row. COLLECT_ID is the deterministic tie-breaker used by the runtime cursor contract.
WITH RankedInbox AS (
    SELECT I.BINDING_ID,
           I.COLLECT_ID,
           I.COLLECTED_AT,
           ROW_NUMBER() OVER (
               PARTITION BY I.BINDING_ID
               ORDER BY I.COLLECTED_AT DESC, I.COLLECT_ID DESC) AS RN
      FROM IVT_TRACE_PROJECTION_INBOX I
)
INSERT INTO IVT_TRACE_INGESTION_CURSOR
    (BINDING_ID, LAST_COLLECT_ID, LAST_COLLECTED_AT,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
SELECT I.BINDING_ID, I.COLLECT_ID, I.COLLECTED_AT,
       'SYSTEM', GETUTCDATE(), 'SYSTEM', GETUTCDATE()
  FROM RankedInbox I
 WHERE I.RN = 1
   AND NOT EXISTS (
       SELECT 1
         FROM IVT_TRACE_INGESTION_CURSOR C
        WHERE C.BINDING_ID = I.BINDING_ID);

-- SQL Server requires table-qualified DROP. SQLite omits these and the shared initializer removes
-- the obsolete definitions after applying or incrementally reconciling the migration.
-- SQLITE-OMIT-BEGIN
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_IVT_TRACE_INBOX_BINDING_CURSOR'
             AND object_id = OBJECT_ID('IVT_TRACE_PROJECTION_INBOX'))
    DROP INDEX IX_IVT_TRACE_INBOX_BINDING_CURSOR ON IVT_TRACE_PROJECTION_INBOX;
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_IVT_TRACE_INBOX_WORK'
             AND object_id = OBJECT_ID('IVT_TRACE_PROJECTION_INBOX'))
    DROP INDEX IX_IVT_TRACE_INBOX_WORK ON IVT_TRACE_PROJECTION_INBOX;
-- SQLITE-OMIT-END

CREATE INDEX IX_IVT_TRACE_INBOX_READY
    ON IVT_TRACE_PROJECTION_INBOX (COLLECTED_AT, COLLECT_ID, BINDING_ID)
    WHERE IS_WORK_ITEM = 1;
