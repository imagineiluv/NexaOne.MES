-- Owner: POM. The 작업 관리 list filters frequently by plant and scope type while
-- retaining a deterministic newest-first order. Keep the existing status/list path for
-- status-filtered reads and add this separate path for Batch/Campaign/Carrier views.
CREATE INDEX IX_POM_WORK_SCOPE_SCOPE_TYPE
    ON POM_WORK_SCOPE (PLANT_ID, SCOPE_TYPE, CREATED_AT DESC, WORK_SCOPE_ID);
