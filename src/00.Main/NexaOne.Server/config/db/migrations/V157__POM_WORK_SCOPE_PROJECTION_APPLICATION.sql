-- Owner: POM. Durable project-policy application state and append-only transition evidence.
-- V156 remains the immutable transport inbox/current cursor. This migration adds a separate,
-- retryable consumer boundary so project plugins can interpret accepted equipment evidence without
-- changing ingestion semantics or coupling their policy to the Cleaner transport contract.

-- A mutable WorkScope aggregate may be projected by only one live equipment stream. Fail an
-- unsupported legacy database explicitly before installing the physical race fence. The BIN2
-- collation keeps WorkScope identity semantics aligned with the transport stream keys.
-- SQLITE-OMIT-BEGIN
IF EXISTS (
    SELECT C.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
      FROM POM_WORK_SCOPE_PROJECTION_CURRENT C
     GROUP BY C.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
    HAVING COUNT_BIG(*) > 1)
    THROW 51541, 'POM work-scope projection current contains duplicate WorkScope bindings', 1;

ALTER TABLE POM_WORK_SCOPE_PROJECTION_CURRENT
    ALTER COLUMN WORK_SCOPE_ID NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NOT NULL;

CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE
    ON POM_WORK_SCOPE_PROJECTION_CURRENT (WORK_SCOPE_ID);
-- SQLITE-OMIT-END

CREATE TABLE POM_WORK_SCOPE_PROJECTION_APPLICATION (
    SOURCE_CLIENT_ID    NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_ID            NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    WORK_SCOPE_ID       NVARCHAR(50)   NOT NULL,
    EQUIPMENT_ID        NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SEQUENCE_RUN_ID     NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SOURCE_REVISION     BIGINT         NOT NULL,
    ACCEPTED_AT         DATETIME2(7)   NOT NULL,
    APPLICATION_STATUS  NVARCHAR(20) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT 'Pending',
    ATTEMPT_COUNT       INT            NOT NULL DEFAULT 0,
    NEXT_ATTEMPT_AT     DATETIME2(7)   NULL,
    LEASE_OWNER         NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
    LEASE_FENCE         BIGINT         NOT NULL DEFAULT 0,
    LEASE_EXPIRES_AT    DATETIME2(7)   NULL,
    POLICY_ID           NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
    POLICY_REVISION     NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NULL,
    DECISION_HASH       CHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
    DECISION_JSON       NVARCHAR(MAX)  NULL,
    LAST_ERROR_CODE     NVARCHAR(100)  NULL,
    LAST_ERROR_MESSAGE  NVARCHAR(2000) NULL,
    COMPLETED_AT        DATETIME2(7)   NULL,
    CREATED_BY          NVARCHAR(50)   NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2(7)   NOT NULL DEFAULT SYSUTCDATETIME(),
    UPDATED_BY          NVARCHAR(50)   NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2(7)   NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_POM_WORK_SCOPE_PROJECTION_APPLICATION
        PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT FK_POM_WORK_SCOPE_PROJECTION_APPLICATION_INBOX
        FOREIGN KEY (SOURCE_CLIENT_ID, EVENT_ID)
        REFERENCES POM_WORK_SCOPE_PROJECTION_INBOX (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_REVISION
        CHECK (SOURCE_REVISION > 0),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_STATUS
        CHECK (APPLICATION_STATUS IN
            ('Pending', 'Processing', 'Retry', 'Applied', 'Observed', 'Superseded', 'Quarantined')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_COUNTERS
        CHECK (ATTEMPT_COUNT >= 0 AND LEASE_FENCE >= 0),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_LEASE
        CHECK ((APPLICATION_STATUS = 'Processing'
                    AND LEASE_OWNER IS NOT NULL
                    AND LEASE_FENCE > 0
                    AND LEASE_EXPIRES_AT IS NOT NULL)
               OR (APPLICATION_STATUS <> 'Processing'
                    AND LEASE_OWNER IS NULL
                    AND LEASE_EXPIRES_AT IS NULL)),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_RETRY
        CHECK ((APPLICATION_STATUS = 'Retry' AND NEXT_ATTEMPT_AT IS NOT NULL)
               OR (APPLICATION_STATUS <> 'Retry' AND NEXT_ATTEMPT_AT IS NULL)),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_COMPLETION
        CHECK ((APPLICATION_STATUS IN ('Applied', 'Observed', 'Superseded', 'Quarantined')
                    AND COMPLETED_AT IS NOT NULL)
               OR (APPLICATION_STATUS IN ('Pending', 'Processing', 'Retry')
                    AND COMPLETED_AT IS NULL)),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_POLICY
        CHECK ((POLICY_ID IS NULL AND POLICY_REVISION IS NULL)
               OR (POLICY_ID IS NOT NULL AND POLICY_REVISION IS NOT NULL)),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_DECISION
        CHECK ((DECISION_HASH IS NULL AND DECISION_JSON IS NULL)
               OR (DECISION_HASH IS NOT NULL AND DECISION_JSON IS NOT NULL))
);

-- The ready index supplies the durable work queue and deterministic acceptance order. The stream
-- index supports same-sequence supersession without scanning unrelated equipment or clients.
CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_READY
    ON POM_WORK_SCOPE_PROJECTION_APPLICATION
       (APPLICATION_STATUS, NEXT_ATTEMPT_AT, ACCEPTED_AT,
        SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, SOURCE_REVISION, EVENT_ID);

CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_STREAM
    ON POM_WORK_SCOPE_PROJECTION_APPLICATION
       (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
        SOURCE_REVISION, ACCEPTED_AT, EVENT_ID);

-- The V156 revision lookup does not cover its accepted-event tiebreaker. Keep the original index for
-- compatibility and add the complete ordering key used by projection admission and reconciliation.
CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_STREAM_ORDER
    ON POM_WORK_SCOPE_PROJECTION_INBOX
       (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
        SOURCE_REVISION, ACCEPTED_AT, EVENT_ID);

CREATE TABLE POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT (
    APPLICATION_EVENT_ID NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SOURCE_CLIENT_ID     NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_ID             NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_TYPE           NVARCHAR(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    FROM_STATUS          NVARCHAR(20) COLLATE Latin1_General_100_BIN2 NULL,
    TO_STATUS            NVARCHAR(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ATTEMPT_COUNT        INT            NOT NULL,
    LEASE_FENCE          BIGINT         NOT NULL,
    POLICY_ID            NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
    POLICY_REVISION      NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NULL,
    DECISION_HASH        CHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
    DECISION_JSON        NVARCHAR(MAX)  NULL,
    ERROR_CODE           NVARCHAR(100)  NULL,
    ERROR_MESSAGE        NVARCHAR(2000) NULL,
    OCCURRED_AT          DATETIME2(7)   NOT NULL,
    CREATED_BY           NVARCHAR(50)   NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT           DATETIME2(7)   NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
        PRIMARY KEY (APPLICATION_EVENT_ID),
    CONSTRAINT FK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT
        FOREIGN KEY (SOURCE_CLIENT_ID, EVENT_ID)
        REFERENCES POM_WORK_SCOPE_PROJECTION_APPLICATION (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_TYPE
        CHECK (EVENT_TYPE IN
            ('Pending', 'Processing', 'Retry', 'Applied', 'Observed', 'Superseded', 'Quarantined')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_FROM_STATUS
        CHECK (FROM_STATUS IS NULL OR FROM_STATUS IN
            ('Pending', 'Processing', 'Retry', 'Applied', 'Observed', 'Superseded', 'Quarantined')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_TO_STATUS
        CHECK (TO_STATUS IN
            ('Pending', 'Processing', 'Retry', 'Applied', 'Observed', 'Superseded', 'Quarantined')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_COUNTERS
        CHECK (ATTEMPT_COUNT >= 0 AND LEASE_FENCE >= 0),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_POLICY
        CHECK ((POLICY_ID IS NULL AND POLICY_REVISION IS NULL)
               OR (POLICY_ID IS NOT NULL AND POLICY_REVISION IS NOT NULL)),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_DECISION
        CHECK ((DECISION_HASH IS NULL AND DECISION_JSON IS NULL)
               OR (DECISION_HASH IS NOT NULL AND DECISION_JSON IS NOT NULL))
);

CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT
    ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
       (SOURCE_CLIENT_ID, EVENT_ID, OCCURRED_AT, APPLICATION_EVENT_ID);

-- Queryable carrier identity is immutable transport evidence, not a project-policy result. Keep all
-- V156 inbox history (including non-current events) so carrier and cleaning-run traces do not depend
-- on which event currently owns the sequence cursor.
CREATE TABLE POM_WORK_SCOPE_PROJECTION_CARRIER (
    SOURCE_CLIENT_ID NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_ID         NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CARRIER_ID       NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    LANE             NVARCHAR(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CLEANING_RUN_ID  NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ACCEPTED_AT      DATETIME2(7)   NOT NULL,
    CONSTRAINT PK_POM_WORK_SCOPE_PROJECTION_CARRIER
        PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID),
    CONSTRAINT UQ_POM_WORK_SCOPE_PROJECTION_CARRIER_LANE
        UNIQUE (SOURCE_CLIENT_ID, EVENT_ID, LANE),
    CONSTRAINT FK_POM_WORK_SCOPE_PROJECTION_CARRIER_INBOX
        FOREIGN KEY (SOURCE_CLIENT_ID, EVENT_ID)
        REFERENCES POM_WORK_SCOPE_PROJECTION_INBOX (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_CARRIER_IDENTITY
        CHECK (LEN(LANE) BETWEEN 1 AND 30
               AND LEN(CARRIER_ID) BETWEEN 1 AND 100
               AND LEN(CLEANING_RUN_ID) BETWEEN 1 AND 100)
);

CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID
    ON POM_WORK_SCOPE_PROJECTION_CARRIER
       (CARRIER_ID, ACCEPTED_AT DESC, SOURCE_CLIENT_ID, EVENT_ID);

CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN
    ON POM_WORK_SCOPE_PROJECTION_CARRIER
       (CLEANING_RUN_ID, ACCEPTED_AT DESC, SOURCE_CLIENT_ID, EVENT_ID);

-- Fail the SQL Server upgrade rather than silently normalizing partial or ambiguous legacy evidence.
-- V156 ingestion guarantees two distinct, normalized carriers; this preflight also protects databases
-- that received unsupported direct writes while the inbox trigger was temporarily unavailable.
-- SQLITE-OMIT-BEGIN
IF EXISTS (
    SELECT 1
      FROM POM_WORK_SCOPE_PROJECTION_INBOX E
     CROSS APPLY (
        SELECT COUNT_BIG(*) AS ITEM_COUNT,
               COUNT_BIG(DISTINCT JSON_VALUE(J.[value], '$.lane')
                   COLLATE Latin1_General_100_BIN2) AS LANE_COUNT,
               COUNT_BIG(DISTINCT JSON_VALUE(J.[value], '$.carrierId')
                   COLLATE Latin1_General_100_BIN2) AS CARRIER_COUNT,
               SUM(CASE
                   WHEN J.[type] <> 5
                     OR JSON_VALUE(J.[value], '$.lane') IS NULL
                     OR LEN(JSON_VALUE(J.[value], '$.lane')) NOT BETWEEN 1 AND 30
                     OR JSON_VALUE(J.[value], '$.carrierId') IS NULL
                     OR LEN(JSON_VALUE(J.[value], '$.carrierId')) NOT BETWEEN 1 AND 100
                     OR JSON_VALUE(J.[value], '$.cleaningRunId') IS NULL
                     OR LEN(JSON_VALUE(J.[value], '$.cleaningRunId')) NOT BETWEEN 1 AND 100
                   THEN 1 ELSE 0 END) AS INVALID_COUNT
          FROM OPENJSON(
              CASE WHEN ISJSON(E.CARRIERS_JSON) = 1 THEN E.CARRIERS_JSON ELSE N'[]' END) J
     ) P
     WHERE ISJSON(E.CARRIERS_JSON) <> 1
        OR P.ITEM_COUNT <> 2
        OR P.LANE_COUNT <> 2
        OR P.CARRIER_COUNT <> 2
        OR P.INVALID_COUNT <> 0)
    THROW 51538, 'POM projection carrier evidence must contain two distinct normalized carriers', 1;

INSERT INTO POM_WORK_SCOPE_PROJECTION_CARRIER
    (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID, LANE, CLEANING_RUN_ID, ACCEPTED_AT)
SELECT E.SOURCE_CLIENT_ID, E.EVENT_ID,
       J.CARRIER_ID, J.LANE, J.CLEANING_RUN_ID, E.ACCEPTED_AT
  FROM POM_WORK_SCOPE_PROJECTION_INBOX E
 CROSS APPLY OPENJSON(E.CARRIERS_JSON)
 WITH (
     LANE            NVARCHAR(30)  '$.lane',
     CARRIER_ID      NVARCHAR(100) '$.carrierId',
     CLEANING_RUN_ID NVARCHAR(100) '$.cleaningRunId'
 ) J;
-- SQLITE-OMIT-END

-- Deterministic upgrade policy: only the exact immutable inbox events referenced by the V156
-- current cursor become Pending. Older/non-current evidence remains queryable in the inbox but is
-- not unexpectedly replayed through a newly installed project policy. Future ingestion creates its
-- application row in the same transaction that advances the cursor. The SQLite contribution repeats
-- this INSERT idempotently because the generic incremental SQLite path intentionally skips DML.
INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION
    (SOURCE_CLIENT_ID, EVENT_ID, WORK_SCOPE_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
     SOURCE_REVISION, ACCEPTED_AT, APPLICATION_STATUS, ATTEMPT_COUNT,
     NEXT_ATTEMPT_AT, LEASE_OWNER, LEASE_FENCE, LEASE_EXPIRES_AT,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
SELECT E.SOURCE_CLIENT_ID, E.EVENT_ID, E.WORK_SCOPE_ID, E.EQUIPMENT_ID, E.SEQUENCE_RUN_ID,
       E.SOURCE_REVISION, E.ACCEPTED_AT, 'Pending', 0,
       NULL, NULL, 0, NULL,
       'SYSTEM', SYSUTCDATETIME(), 'SYSTEM', SYSUTCDATETIME()
  FROM POM_WORK_SCOPE_PROJECTION_CURRENT C
  JOIN POM_WORK_SCOPE_PROJECTION_INBOX E
    ON E.SOURCE_CLIENT_ID = C.SOURCE_CLIENT_ID
   AND E.EVENT_ID = C.EVENT_ID
   AND E.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
         = C.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
   AND E.EQUIPMENT_ID = C.EQUIPMENT_ID
   AND E.SEQUENCE_RUN_ID = C.SEQUENCE_RUN_ID
   AND E.SOURCE_REVISION = C.SOURCE_REVISION
   AND E.ACCEPTED_AT = C.ACCEPTED_AT;

-- Seed the same initial Pending audit produced by future ingestion. ProjectionIdentity.Audit uses
-- UTF-8 SHA-256 over length-prefixed source, event, type, fence, and attempt values, followed by
-- unpadded base64url. Source/event identities are normalized by the ingestion contract, so LEN has
-- the same UTF-16 code-unit count used by the .NET canonical builder.
-- SQLITE-OMIT-BEGIN
INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
    (APPLICATION_EVENT_ID, SOURCE_CLIENT_ID, EVENT_ID, EVENT_TYPE,
     FROM_STATUS, TO_STATUS, ATTEMPT_COUNT, LEASE_FENCE,
     POLICY_ID, POLICY_REVISION, DECISION_HASH, DECISION_JSON,
     ERROR_CODE, ERROR_MESSAGE, OCCURRED_AT, CREATED_BY, CREATED_AT)
SELECT CONCAT(
           'pae_',
           REPLACE(REPLACE(REPLACE(B.BASE64_VALUE, '+', '-'), '/', '_'), '=', '')),
       A.SOURCE_CLIENT_ID, A.EVENT_ID, 'Pending',
       NULL, 'Pending', 0, 0,
       NULL, NULL, NULL, NULL,
       NULL, NULL, A.CREATED_AT, 'SYSTEM', A.CREATED_AT
  FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
 CROSS APPLY (VALUES (
    HASHBYTES(
        'SHA2_256',
        CONVERT(VARCHAR(MAX),
            CONCAT(
                LEN(A.SOURCE_CLIENT_ID), N':', A.SOURCE_CLIENT_ID,
                LEN(A.EVENT_ID), N':', A.EVENT_ID,
                N'7:Pending1:01:0') COLLATE Latin1_General_100_BIN2_UTF8)))) D(DIGEST)
 CROSS APPLY (VALUES (
    CAST(N'' AS XML).value(
        'xs:base64Binary(sql:column("D.DIGEST"))', 'varchar(44)'))) B(BASE64_VALUE)
 WHERE A.APPLICATION_STATUS = 'Pending'
   AND A.ATTEMPT_COUNT = 0
   AND A.LEASE_FENCE = 0
   AND NOT EXISTS (
       SELECT 1
         FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT X
        WHERE X.APPLICATION_EVENT_ID = CONCAT(
            'pae_',
            REPLACE(REPLACE(REPLACE(B.BASE64_VALUE, '+', '-'), '/', '_'), '=', '')));
-- SQLITE-OMIT-END

-- SQL Server guards the mutable checkpoint and immutable audit ledger. SQLite installs equivalent
-- BEFORE triggers from PomWorkScopeProjectionSqliteSchemaContribution after every schema repair.
-- SQLITE-OMIT-BEGIN
EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_GUARD
ON POM_WORK_SCOPE_PROJECTION_APPLICATION
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- UPDATED_BY/UPDATED_AT are operational touch metadata and may be corrected without changing a
    -- terminal semantic outcome. Every status, counter, lease, policy, decision, error, and completion
    -- value is frozen once the row reaches a terminal state.
    IF EXISTS (
        SELECT 1
          FROM deleted D
          LEFT JOIN inserted I
            ON I.SOURCE_CLIENT_ID = D.SOURCE_CLIENT_ID
           AND I.EVENT_ID = D.EVENT_ID
         WHERE I.SOURCE_CLIENT_ID IS NULL)
        THROW 51533, ''POM work-scope projection application is not deletable or replaceable'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
         WHERE NOT EXISTS (
            SELECT 1
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E
             WHERE E.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
               AND E.EVENT_ID = I.EVENT_ID
               AND E.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                     = I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
               AND E.EQUIPMENT_ID = I.EQUIPMENT_ID
               AND E.SEQUENCE_RUN_ID = I.SEQUENCE_RUN_ID
               AND E.SOURCE_REVISION = I.SOURCE_REVISION
               AND E.ACCEPTED_AT = I.ACCEPTED_AT))
        THROW 51532, ''POM projection application must reference its exact inbox event'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EVENT_ID = I.EVENT_ID
         WHERE D.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                   <> I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
            OR D.EQUIPMENT_ID <> I.EQUIPMENT_ID
            OR D.SEQUENCE_RUN_ID <> I.SEQUENCE_RUN_ID
            OR D.SOURCE_REVISION <> I.SOURCE_REVISION
            OR D.ACCEPTED_AT <> I.ACCEPTED_AT
            OR D.CREATED_BY COLLATE Latin1_General_100_BIN2
                  <> I.CREATED_BY COLLATE Latin1_General_100_BIN2
            OR D.CREATED_AT <> I.CREATED_AT)
        THROW 51534, ''POM projection application identity is immutable'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EVENT_ID = I.EVENT_ID
         WHERE I.ATTEMPT_COUNT < D.ATTEMPT_COUNT
            OR I.LEASE_FENCE < D.LEASE_FENCE)
        THROW 51535, ''POM projection application attempts and lease fence are monotonic'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EVENT_ID = I.EVENT_ID
         WHERE D.APPLICATION_STATUS IN (''Applied'', ''Observed'', ''Superseded'', ''Quarantined'')
           AND (I.APPLICATION_STATUS <> D.APPLICATION_STATUS
                OR I.ATTEMPT_COUNT <> D.ATTEMPT_COUNT
                OR ISNULL(I.NEXT_ATTEMPT_AT, ''0001-01-01'')
                     <> ISNULL(D.NEXT_ATTEMPT_AT, ''0001-01-01'')
                OR ISNULL(I.LEASE_OWNER, '''') <> ISNULL(D.LEASE_OWNER, '''')
                OR I.LEASE_FENCE <> D.LEASE_FENCE
                OR ISNULL(I.LEASE_EXPIRES_AT, ''0001-01-01'')
                     <> ISNULL(D.LEASE_EXPIRES_AT, ''0001-01-01'')
                OR ISNULL(I.POLICY_ID, '''') <> ISNULL(D.POLICY_ID, '''')
                OR ISNULL(I.POLICY_REVISION, '''') <> ISNULL(D.POLICY_REVISION, '''')
                OR ISNULL(I.DECISION_HASH, '''') <> ISNULL(D.DECISION_HASH, '''')
                OR ISNULL(I.DECISION_JSON, '''') COLLATE Latin1_General_100_BIN2
                     <> ISNULL(D.DECISION_JSON, '''') COLLATE Latin1_General_100_BIN2
                OR ISNULL(I.LAST_ERROR_CODE, '''') <> ISNULL(D.LAST_ERROR_CODE, '''')
                OR ISNULL(I.LAST_ERROR_MESSAGE, '''') COLLATE Latin1_General_100_BIN2
                     <> ISNULL(D.LAST_ERROR_MESSAGE, '''') COLLATE Latin1_General_100_BIN2
                OR I.COMPLETED_AT <> D.COMPLETED_AT
                OR (I.COMPLETED_AT IS NULL AND D.COMPLETED_AT IS NOT NULL)
                OR (I.COMPLETED_AT IS NOT NULL AND D.COMPLETED_AT IS NULL)))
        THROW 51536, ''POM projection application terminal state cannot regress or mutate'', 1;
END');

EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_APPEND_ONLY
ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51537, ''POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT is append-only'', 1;
END');

EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_GUARD
ON POM_WORK_SCOPE_PROJECTION_CARRIER
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51539, ''POM_WORK_SCOPE_PROJECTION_CARRIER is append-only and not replaceable'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
         WHERE NOT EXISTS (
            SELECT 1
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E
             CROSS APPLY OPENJSON(E.CARRIERS_JSON)
             WITH (
                 LANE            NVARCHAR(30)  ''$.lane'',
                 CARRIER_ID      NVARCHAR(100) ''$.carrierId'',
                 CLEANING_RUN_ID NVARCHAR(100) ''$.cleaningRunId''
             ) J
             WHERE E.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
               AND E.EVENT_ID = I.EVENT_ID
               AND E.ACCEPTED_AT = I.ACCEPTED_AT
               AND J.CARRIER_ID COLLATE Latin1_General_100_BIN2 = I.CARRIER_ID
               AND J.LANE COLLATE Latin1_General_100_BIN2 = I.LANE
               AND J.CLEANING_RUN_ID COLLATE Latin1_General_100_BIN2 = I.CLEANING_RUN_ID))
        THROW 51540, ''POM projection carrier must reference its exact inbox evidence'', 1;
END');
-- SQLITE-OMIT-END
