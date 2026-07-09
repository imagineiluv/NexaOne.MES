-- ============================================================================
-- 운영 MSSQL SYS_BATCH_PROCESS 시드 — 로그 보존 정리 배치 2종(dev SeedDevBatchDefinitionsIfEmpty 동등본).
-- 배경: dev 시드는 Development+SQLite 전용이라 운영은 SYS_APP_LOG/SYS_REQUEST_LOG가 무한 적재된다.
-- 멱등: 행 단위 IF NOT EXISTS — 운영에서 보존일수를 조정했어도 덮어쓰지 않는다.
-- 워커는 기본 OFF(Worker:Sys:BatchProcess:Enabled=true로 활성) — 수동 실행은 POST sys/admin/batch/{id}/run.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM SYS_BATCH_PROCESS WHERE BATCH_ID = N'PURGE-APP-LOG')
    INSERT INTO SYS_BATCH_PROCESS
        (BATCH_ID, BATCH_NAME, BATCH_TYPE, BATCH_RULE, BATCH_OPTIONS, BATCH_INPUTDATA, DESCRIPTION,
         VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
    VALUES (N'PURGE-APP-LOG', N'앱 로그 보존 정리(30일)', N'Interval', N'SYS.PurgeOldAppLogs', N'86400',
            N'{"retentionDays":30}', N'SYS_APP_LOG 30일 초과분 삭제 — V064 보존 정리',
            N'Valid', N'SYSTEM', GETUTCDATE(), N'SYSTEM', GETUTCDATE());

IF NOT EXISTS (SELECT 1 FROM SYS_BATCH_PROCESS WHERE BATCH_ID = N'PURGE-REQUEST-LOG')
    INSERT INTO SYS_BATCH_PROCESS
        (BATCH_ID, BATCH_NAME, BATCH_TYPE, BATCH_RULE, BATCH_OPTIONS, BATCH_INPUTDATA, DESCRIPTION,
         VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
    VALUES (N'PURGE-REQUEST-LOG', N'요청 로그 보존 정리(14일)', N'Interval', N'SYS.PurgeOldRequestLogs', N'86400',
            N'{"retentionDays":14}', N'SYS_REQUEST_LOG 14일 초과분 삭제 — V062 보존 정리',
            N'Valid', N'SYSTEM', GETUTCDATE(), N'SYSTEM', GETUTCDATE());
