-- Read-only operator audit for the RMS/QMS -> COM_APPROVAL transition and legacy ID rules.
-- Run against a restored backup or during a write-quiesced window. No NOLOCK: dirty rows
-- must not be mistaken for migration evidence. This file deliberately performs no repair.

-- Result 1: RMS recipes whose shared approval or source evidence needs attention.
;WITH HistorySteps AS (
    SELECT h.RECIPE_ID, h.FROM_STATE, h.TO_STATE, h.CHANGED_BY, h.CHANGED_AT,
           h.IDEMPOTENCY_KEY, h.REQUEST_HASH,
           ROW_NUMBER() OVER (PARTITION BY h.RECIPE_ID ORDER BY h.CHANGED_AT, h.HISTORY_ID) AS StepNo
    FROM RMS_RECIPE_APPROVAL_HISTORY AS h
), Evidence AS (
    SELECT RECIPE_ID, COUNT(*) AS EventCount,
           MAX(CASE WHEN StepNo = 1 THEN FROM_STATE END) AS RequestFrom,
           MAX(CASE WHEN StepNo = 1 THEN TO_STATE END) AS RequestTo,
           MAX(CASE WHEN StepNo = 1 THEN CHANGED_BY END) AS RequestedBy,
           MAX(CASE WHEN StepNo = 1 THEN CHANGED_AT END) AS RequestedAt,
           MAX(CASE WHEN StepNo = 1 THEN IDEMPOTENCY_KEY END) AS RequestKey,
           MAX(CASE WHEN StepNo = 1 THEN REQUEST_HASH END) AS RequestHash,
           MAX(CASE WHEN StepNo = 2 THEN FROM_STATE END) AS FirstFrom,
           MAX(CASE WHEN StepNo = 2 THEN TO_STATE END) AS FirstTo,
           MAX(CASE WHEN StepNo = 2 THEN CHANGED_BY END) AS FirstApprover,
           MAX(CASE WHEN StepNo = 2 THEN CHANGED_AT END) AS FirstApprovedAt,
           MAX(CASE WHEN StepNo = 3 THEN FROM_STATE END) AS FinalFrom,
           MAX(CASE WHEN StepNo = 3 THEN TO_STATE END) AS FinalTo,
           MAX(CASE WHEN StepNo = 3 THEN CHANGED_BY END) AS FinalApprover,
           MAX(CASE WHEN StepNo = 3 THEN CHANGED_AT END) AS FinalApprovedAt,
           MAX(CASE WHEN StepNo = 3 THEN IDEMPOTENCY_KEY END) AS FinalKey,
           MAX(CASE WHEN StepNo = 3 THEN REQUEST_HASH END) AS FinalHash,
           MAX(CASE WHEN StepNo = 4 THEN FROM_STATE END) AS ReleaseFrom,
           MAX(CASE WHEN StepNo = 4 THEN TO_STATE END) AS ReleaseTo
    FROM HistorySteps
    GROUP BY RECIPE_ID
), Findings AS (
    SELECT r.RECIPE_ID AS DocId, r.APPROVAL_STATE AS SourceState,
           a.STATUS AS SharedStatus, COALESCE(e.EventCount, 0) AS SourceEventCount,
           e.RequestedBy, e.RequestedAt, e.FirstApprover, e.FinalApprover,
           e.FinalApprovedAt,
           CASE
               WHEN a.APPROVAL_ID IS NOT NULL AND
                    (CASE WHEN r.APPROVAL_STATE IN ('WaitApproval', 'Approved1') THEN 'Pending'
                          WHEN r.APPROVAL_STATE IN ('Approved', 'Released') THEN 'Approved'
                          WHEN r.APPROVAL_STATE = 'Rejected' THEN 'Rejected'
                          ELSE 'None' END) <> a.STATUS THEN 'STATE_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND latest.ToStatus IS NULL
                    THEN 'SHARED_HISTORY_MISSING'
               WHEN a.APPROVAL_ID IS NOT NULL AND latest.ToStatus <> a.STATUS
                    THEN 'SHARED_HISTORY_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND e.EventCount IS NULL
                    THEN 'SOURCE_HISTORY_MISSING'
               WHEN a.APPROVAL_ID IS NOT NULL AND e.RequestedBy IS NOT NULL AND
                    a.REQUESTED_BY <> e.RequestedBy THEN 'REQUESTER_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND a.STATUS = 'Approved' AND
                    e.FinalApprover IS NOT NULL AND a.DECIDED_BY <> e.FinalApprover
                    THEN 'DECIDER_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND
                    r.APPROVAL_STATE IN ('WaitApproval', 'Approved1') AND
                    (a.IDEMPOTENCY_KEY <> e.RequestKey OR a.REQUEST_HASH <> e.RequestHash)
                    THEN 'REQUEST_EVIDENCE_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND
                    r.APPROVAL_STATE IN ('Approved', 'Released') AND
                    (a.IDEMPOTENCY_KEY <> e.FinalKey OR a.REQUEST_HASH <> e.FinalHash)
                    THEN 'DECISION_EVIDENCE_MISMATCH'
               WHEN r.APPROVAL_STATE = 'Draft' THEN 'OK'
               WHEN r.APPROVAL_STATE = 'Rejected' THEN 'TERMINAL_REVIEW'
               WHEN r.APPROVAL_STATE NOT IN ('WaitApproval', 'Approved1', 'Approved', 'Released')
                    THEN 'SOURCE_STATE_UNSUPPORTED'
               WHEN e.EventCount <> CASE r.APPROVAL_STATE
                    WHEN 'WaitApproval' THEN 1 WHEN 'Approved1' THEN 2
                    WHEN 'Approved' THEN 3 WHEN 'Released' THEN 4 END
                    OR e.EventCount IS NULL THEN 'SOURCE_CHAIN_INCOMPLETE'
               WHEN e.RequestFrom <> 'Draft' OR e.RequestTo <> 'WaitApproval'
                    OR (r.APPROVAL_STATE IN ('Approved1', 'Approved', 'Released') AND
                        (e.FirstFrom <> 'WaitApproval' OR e.FirstTo <> 'Approved1'))
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND
                        (e.FinalFrom <> 'Approved1' OR e.FinalTo <> 'Approved'))
                    OR (r.APPROVAL_STATE = 'Released' AND
                        (e.ReleaseFrom <> 'Approved' OR e.ReleaseTo <> 'Released'))
                    THEN 'SOURCE_CHAIN_INCOMPLETE'
               WHEN NULLIF(LTRIM(RTRIM(e.RequestedBy)), '') IS NULL
                    OR (r.APPROVAL_STATE IN ('Approved1', 'Approved', 'Released') AND
                        NULLIF(LTRIM(RTRIM(e.FirstApprover)), '') IS NULL)
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND
                        NULLIF(LTRIM(RTRIM(e.FinalApprover)), '') IS NULL)
                    THEN 'SOURCE_ACTOR_MISSING'
               WHEN NULLIF(LTRIM(RTRIM(e.RequestKey)), '') IS NULL
                    OR NULLIF(LTRIM(RTRIM(e.RequestHash)), '') IS NULL
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND
                        (NULLIF(LTRIM(RTRIM(e.FinalKey)), '') IS NULL OR
                         NULLIF(LTRIM(RTRIM(e.FinalHash)), '') IS NULL))
                    THEN 'SOURCE_EVENT_KEY_MISSING'
               WHEN (r.APPROVAL_STATE IN ('Approved1', 'Approved', 'Released') AND
                        (e.FirstApprover <> r.FIRST_APPROVER_ID OR r.FIRST_APPROVER_ID IS NULL))
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND
                        (e.FinalApprover <> r.SECOND_APPROVER_ID OR r.SECOND_APPROVER_ID IS NULL))
                    THEN 'SOURCE_ACTOR_MISMATCH'
               WHEN (r.APPROVAL_STATE IN ('Approved1', 'Approved', 'Released') AND
                        e.RequestedBy = e.FirstApprover)
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND
                        (e.RequestedBy = e.FinalApprover OR e.FirstApprover = e.FinalApprover))
                    THEN 'SELF_APPROVAL_CONFLICT'
               WHEN a.APPROVAL_ID IS NOT NULL THEN 'OK'
               WHEN EXISTS (SELECT 1 FROM COM_APPROVAL_HISTORY AS ah
                            WHERE ah.IDEMPOTENCY_KEY = e.RequestKey)
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND EXISTS (
                        SELECT 1 FROM COM_APPROVAL_HISTORY AS ah
                        WHERE ah.IDEMPOTENCY_KEY = e.FinalKey))
                    OR EXISTS (SELECT 1 FROM COM_APPROVAL AS other
                               WHERE other.IDEMPOTENCY_KEY = e.RequestKey)
                    OR (r.APPROVAL_STATE IN ('Approved', 'Released') AND EXISTS (
                        SELECT 1 FROM COM_APPROVAL AS other
                        WHERE other.IDEMPOTENCY_KEY = e.FinalKey))
                    THEN 'SHARED_KEY_COLLISION'
               ELSE 'SOURCE_CHAIN_COMPLETE'
           END AS Finding
    FROM RMS_RECIPE AS r
    LEFT JOIN Evidence AS e ON e.RECIPE_ID = r.RECIPE_ID
    LEFT JOIN COM_APPROVAL AS a ON a.DOC_KIND = 'Recipe' AND a.DOC_ID = r.RECIPE_ID
    OUTER APPLY (
        SELECT TOP (1) ah.TO_STATUS AS ToStatus
        FROM COM_APPROVAL_HISTORY AS ah
        WHERE ah.DOC_KIND = 'Recipe' AND ah.DOC_ID = r.RECIPE_ID
          AND ah.APPROVAL_ID = a.APPROVAL_ID
        ORDER BY ah.CHANGED_AT DESC, ah.HISTORY_ID DESC
    ) AS latest
)
SELECT 'Recipe' AS DocKind, DocId, SourceState, SharedStatus, Finding,
       SourceEventCount, RequestedBy, RequestedAt, FirstApprover,
       FinalApprover, FinalApprovedAt
FROM Findings WHERE Finding <> 'OK'
ORDER BY Finding, DocId;

-- Result 2: QMS 4M rows. The legacy row has no separate request timestamp or
-- append-only decision event, so even populated actors are review evidence,
-- never an automatic backfill authorization.
;WITH Findings AS (
    SELECT q.CHANGE_ID AS DocId, q.APPROVAL_STATUS AS SourceState,
           a.STATUS AS SharedStatus, q.REQUESTED_BY AS RequestedBy,
           q.CREATED_AT AS CreatedAt, q.APPROVED_BY AS ApprovedBy,
           q.APPROVED_AT AS ApprovedAt,
           CASE
               WHEN a.APPROVAL_ID IS NOT NULL AND q.APPROVAL_STATUS <> a.STATUS
                    THEN 'STATE_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND latest.ToStatus IS NULL
                    THEN 'SHARED_HISTORY_MISSING'
               WHEN a.APPROVAL_ID IS NOT NULL AND latest.ToStatus <> a.STATUS
                    THEN 'SHARED_HISTORY_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND
                    (NULLIF(LTRIM(RTRIM(q.REQUESTED_BY)), '') IS NULL OR
                     q.REQUESTED_BY <> a.REQUESTED_BY) THEN 'REQUESTER_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL AND q.APPROVAL_STATUS = 'Approved' AND
                    (NULLIF(LTRIM(RTRIM(q.APPROVED_BY)), '') IS NULL OR
                     q.APPROVED_BY <> a.DECIDED_BY) THEN 'DECIDER_MISMATCH'
               WHEN a.APPROVAL_ID IS NOT NULL THEN 'OK'
               WHEN q.APPROVAL_STATUS NOT IN ('Pending', 'Approved', 'Rejected')
                    THEN 'SOURCE_STATE_UNSUPPORTED'
               WHEN NULLIF(LTRIM(RTRIM(q.REQUESTED_BY)), '') IS NULL
                    THEN 'REQUESTER_MISSING'
               WHEN q.APPROVAL_STATUS = 'Rejected' THEN 'DECISION_EVIDENCE_MISSING'
               WHEN q.APPROVAL_STATUS = 'Approved' AND
                    (NULLIF(LTRIM(RTRIM(q.APPROVED_BY)), '') IS NULL OR
                     q.APPROVED_AT IS NULL) THEN 'DECISION_EVIDENCE_MISSING'
               WHEN q.APPROVAL_STATUS = 'Approved' AND q.REQUESTED_BY = q.APPROVED_BY
                    THEN 'SELF_APPROVAL_CONFLICT'
               ELSE 'REQUEST_TIME_UNPROVEN'
           END AS Finding
    FROM QMS_4M_CHANGE AS q
    LEFT JOIN COM_APPROVAL AS a
        ON a.DOC_KIND = 'FourMChange' AND a.DOC_ID = q.CHANGE_ID
    OUTER APPLY (
        SELECT TOP (1) ah.TO_STATUS AS ToStatus
        FROM COM_APPROVAL_HISTORY AS ah
        WHERE ah.DOC_KIND = 'FourMChange' AND ah.DOC_ID = q.CHANGE_ID
          AND ah.APPROVAL_ID = a.APPROVAL_ID
        ORDER BY ah.CHANGED_AT DESC, ah.HISTORY_ID DESC
    ) AS latest
)
SELECT 'FourMChange' AS DocKind, DocId, SourceState, SharedStatus, Finding,
       RequestedBy, CreatedAt, ApprovedBy, ApprovedAt
FROM Findings WHERE Finding <> 'OK'
ORDER BY Finding, DocId;

-- Result 3: ID rules that cannot safely issue under the current engine or
-- whose pre-migration issuance period is unknown. No prefix is rewritten here.
;WITH Rules AS (
    SELECT RULE_ID, PREFIX, SEQ_LENGTH, CURRENT_SEQ, SEQ_PERIOD,
           UPPER(LTRIM(RTRIM(RESET_CYCLE))) AS ResetCycle
    FROM COM_ID_RULE
), Findings AS (
    SELECT RULE_ID, PREFIX, SEQ_LENGTH, CURRENT_SEQ, SEQ_PERIOD, ResetCycle,
           CASE
               WHEN ResetCycle IS NOT NULL AND ResetCycle NOT IN
                    ('', 'NONE', 'NEVER', 'DAILY', 'MONTHLY', 'YEARLY')
                    THEN 'RESET_CYCLE_UNSUPPORTED'
               WHEN SEQ_LENGTH IS NOT NULL AND (SEQ_LENGTH < 0 OR SEQ_LENGTH > 10)
                    THEN 'SEQUENCE_LENGTH_INVALID'
               WHEN CURRENT_SEQ IS NULL OR CURRENT_SEQ < 0 THEN 'SEQUENCE_INVALID'
               WHEN ResetCycle IN ('DAILY', 'MONTHLY', 'YEARLY') AND
                    CHARINDEX('{period}', COALESCE(PREFIX, '')) = 0
                    THEN 'PERIOD_PLACEHOLDER_MISSING'
               WHEN (ResetCycle IS NULL OR ResetCycle IN ('', 'NONE', 'NEVER')) AND
                    CHARINDEX('{period}', COALESCE(PREFIX, '')) > 0
                    THEN 'PERIOD_PLACEHOLDER_UNEXPECTED'
               WHEN ResetCycle IN ('DAILY', 'MONTHLY', 'YEARLY') AND
                    SEQ_PERIOD IS NULL AND CURRENT_SEQ > 0
                    THEN 'ISSUED_PERIOD_UNKNOWN'
               WHEN ResetCycle IN ('DAILY', 'MONTHLY', 'YEARLY') AND
                    SEQ_PERIOD IS NOT NULL AND
                    (LEN(SEQ_PERIOD) <> CASE ResetCycle
                        WHEN 'DAILY' THEN 8 WHEN 'MONTHLY' THEN 6 ELSE 4 END
                     OR SEQ_PERIOD LIKE '%[^0-9]%'
                     OR TRY_CONVERT(DATE, CASE ResetCycle
                         WHEN 'DAILY' THEN STUFF(STUFF(SEQ_PERIOD, 5, 0, '-'), 8, 0, '-')
                         WHEN 'MONTHLY' THEN CONCAT(LEFT(SEQ_PERIOD, 4), '-',
                                                     RIGHT(SEQ_PERIOD, 2), '-01')
                         ELSE CONCAT(SEQ_PERIOD, '-01-01') END, 23) IS NULL)
                    THEN 'ISSUED_PERIOD_INVALID'
               WHEN ResetCycle IN ('DAILY', 'MONTHLY', 'YEARLY') AND
                    SEQ_PERIOD > CASE ResetCycle
                        WHEN 'DAILY' THEN CONVERT(VARCHAR(8), SYSUTCDATETIME(), 112)
                        WHEN 'MONTHLY' THEN LEFT(CONVERT(VARCHAR(8), SYSUTCDATETIME(), 112), 6)
                        ELSE LEFT(CONVERT(VARCHAR(8), SYSUTCDATETIME(), 112), 4) END
                    THEN 'ISSUED_PERIOD_IN_FUTURE'
               WHEN CURRENT_SEQ = 2147483647 OR
                    (SEQ_LENGTH > 0 AND
                     LEN(CONVERT(VARCHAR(20), CAST(CURRENT_SEQ AS BIGINT) + 1)) > SEQ_LENGTH)
                    THEN 'SEQUENCE_EXHAUSTED'
               ELSE 'OK'
           END AS Finding
    FROM Rules
)
SELECT RULE_ID AS RuleId, ResetCycle, PREFIX AS Prefix, SEQ_LENGTH AS SeqLength,
       CURRENT_SEQ AS CurrentSeq, SEQ_PERIOD AS SeqPeriod, Finding
FROM Findings WHERE Finding <> 'OK'
ORDER BY Finding, RuleId;
