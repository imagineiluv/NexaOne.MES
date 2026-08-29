-- Owner: IVT. Enforce the retry-work projection invariant without mutating the released V142 migration.

-- Normalize any rows written by a pre-V147/direct writer before validating the constraint.
UPDATE IVT_TRACE_PROJECTION_INBOX
   SET IS_WORK_ITEM = CASE
                         WHEN STATUS IN ('Pending', 'Error') THEN 1
                         ELSE 0
                      END
 WHERE (STATUS IN ('Pending', 'Error') AND IS_WORK_ITEM <> 1)
    OR (STATUS IN ('Applied', 'Ignored') AND IS_WORK_ITEM <> 0);

-- SQLite uses the canonical INSERT/UPDATE triggers installed by SqliteSchemaInitializer because
-- its ALTER TABLE dialect cannot add this table constraint incrementally.
-- SQLITE-OMIT-BEGIN
ALTER TABLE IVT_TRACE_PROJECTION_INBOX
    ADD CONSTRAINT CK_IVT_TRACE_INBOX_WORK_STATE CHECK (
        (STATUS IN ('Pending', 'Error') AND IS_WORK_ITEM = 1)
        OR (STATUS IN ('Applied', 'Ignored') AND IS_WORK_ITEM = 0)
    );
-- SQLITE-OMIT-END
