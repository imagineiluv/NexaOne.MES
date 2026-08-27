-- Owner: IVT. TRACE projection hot-path indexes.
-- The cursor index serves the latest sample per binding. The work index replaces (rather than
-- duplicates) V114's narrower index so pending/error scans keep one write-maintained work index.

CREATE INDEX IX_IVT_TRACE_INBOX_BINDING_CURSOR
    ON IVT_TRACE_PROJECTION_INBOX (BINDING_ID, COLLECTED_AT DESC, COLLECT_ID DESC);

-- SQL Server requires the table-qualified DROP syntax. SQLite omits this statement and the shared
-- initializer compares/rebuilds the index definition on both fresh and incremental databases.
-- SQLITE-OMIT-BEGIN
DROP INDEX IX_IVT_TRACE_INBOX_WORK ON IVT_TRACE_PROJECTION_INBOX;
-- SQLITE-OMIT-END

CREATE INDEX IX_IVT_TRACE_INBOX_WORK
    ON IVT_TRACE_PROJECTION_INBOX (STATUS, COLLECTED_AT, COLLECT_ID, BINDING_ID);
