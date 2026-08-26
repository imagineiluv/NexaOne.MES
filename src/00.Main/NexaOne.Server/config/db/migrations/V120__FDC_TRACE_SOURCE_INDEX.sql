-- FDC owns the persisted TRACE source and the access path used by IFdcTraceSource.
-- The deterministic COLLECT_ID suffix keeps equal-timestamp paging stable on MSSQL and SQLite.
CREATE INDEX IX_FDC_DATA_TRACE_SOURCE
    ON FDC_COLLECT_DATA (EQUIPMENT_ID, PARAMETER_ID, COLLECTED_AT, COLLECT_ID);
