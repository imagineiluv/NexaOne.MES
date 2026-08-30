-- Owner: POM. Durable equipment projection inbox and monotonic current cursor.
-- The inbox is immutable evidence. A later mapper/plugin may translate it into WorkScope
-- business transitions; ingestion itself deliberately does not change POM_WORK_SCOPE.

CREATE TABLE POM_WORK_SCOPE_PROJECTION_INBOX (
    SOURCE_CLIENT_ID           NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_ID                   NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    REQUEST_HASH               CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    WORK_SCOPE_ID              NVARCHAR(50)   NOT NULL,
    EQUIPMENT_ID               NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OPERATION_KEY              NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PAIR_RUN_ID                NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SEQUENCE_RUN_ID            NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SOURCE_REVISION            BIGINT         NOT NULL,
    PROJECTION_STATUS          NVARCHAR(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TERMINAL_CLEANUP_COMPLETED BIT            NOT NULL,
    RECIPE_ID                  NVARCHAR(100)  NOT NULL,
    RECIPE_SNAPSHOT_HASH       CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROGRAM_HASH               CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CARRIERS_JSON              NVARCHAR(MAX)  NOT NULL,
    RESULT_CODE                NVARCHAR(100)  NOT NULL,
    RESULT_METADATA_JSON       NVARCHAR(MAX)  NULL,
    OCCURRED_AT                DATETIME2      NOT NULL,
    PAYLOAD_JSON               NVARCHAR(MAX)  NOT NULL,
    ACCEPTED_AT                DATETIME2      NOT NULL,
    CREATED_BY                 NVARCHAR(50)   NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT                 DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_POM_WORK_SCOPE_PROJECTION_INBOX
        PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT FK_POM_WORK_SCOPE_PROJECTION_SCOPE
        FOREIGN KEY (WORK_SCOPE_ID) REFERENCES POM_WORK_SCOPE (WORK_SCOPE_ID),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_REVISION
        CHECK (SOURCE_REVISION > 0),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_STATUS
        CHECK (PROJECTION_STATUS IN ('Running', 'Completed', 'Abandoned', 'RecoveryRequired')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_CLEANUP
        CHECK (TERMINAL_CLEANUP_COMPLETED = 0
               OR PROJECTION_STATUS IN ('Completed', 'Abandoned'))
);

-- Recovery may emit more than one event at the same revision (for example Completed/cleanup=false
-- followed by RecoveryRequired). Keep this lookup non-unique; EventId + request hash is the only
-- transport idempotency identity. Cleaner outbox delivery is ordered, so current projection uses
-- source revision first and durable ACCEPTED_AT order within one revision. OCCURRED_AT remains
-- immutable evidence and is not allowed to suppress a later accepted recovery event.
CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_REVISION
    ON POM_WORK_SCOPE_PROJECTION_INBOX
       (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, SOURCE_REVISION);

CREATE INDEX IX_POM_WORK_SCOPE_PROJECTION_SCOPE_TIME
    ON POM_WORK_SCOPE_PROJECTION_INBOX (WORK_SCOPE_ID, OCCURRED_AT DESC, EVENT_ID);

CREATE TABLE POM_WORK_SCOPE_PROJECTION_CURRENT (
    SOURCE_CLIENT_ID     NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EQUIPMENT_ID         NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SEQUENCE_RUN_ID      NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EVENT_ID             NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    WORK_SCOPE_ID        NVARCHAR(50)  NOT NULL,
    OPERATION_KEY        NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PAIR_RUN_ID          NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RECIPE_ID            NVARCHAR(100)  NOT NULL,
    RECIPE_SNAPSHOT_HASH CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PROGRAM_HASH         CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CARRIERS_JSON        NVARCHAR(MAX)  NOT NULL,
    SOURCE_REVISION      BIGINT        NOT NULL,
    PROJECTION_STATUS    NVARCHAR(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TERMINAL_CLEANUP_COMPLETED BIT     NOT NULL,
    OCCURRED_AT          DATETIME2     NOT NULL,
    ACCEPTED_AT          DATETIME2     NOT NULL,
    UPDATED_AT           DATETIME2     NOT NULL,
    CONSTRAINT PK_POM_WORK_SCOPE_PROJECTION_CURRENT
        PRIMARY KEY (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID),
    CONSTRAINT FK_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT
        FOREIGN KEY (SOURCE_CLIENT_ID, EVENT_ID)
        REFERENCES POM_WORK_SCOPE_PROJECTION_INBOX (SOURCE_CLIENT_ID, EVENT_ID),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_CURRENT_REVISION
        CHECK (SOURCE_REVISION > 0),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_CURRENT_STATUS
        CHECK (PROJECTION_STATUS IN ('Running', 'Completed', 'Abandoned', 'RecoveryRequired')),
    CONSTRAINT CK_POM_WORK_SCOPE_PROJECTION_CURRENT_CLEANUP
        CHECK (TERMINAL_CLEANUP_COMPLETED = 0
               OR PROJECTION_STATUS IN ('Completed', 'Abandoned'))
);

-- SQL Server keeps inbox evidence append-only. SQLite installs the equivalent triggers in
-- SqliteSchemaInitializer because the shared migration translator omits T-SQL triggers.
-- SQLITE-OMIT-BEGIN
EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_APPEND_ONLY
ON POM_WORK_SCOPE_PROJECTION_INBOX
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51526, ''POM_WORK_SCOPE_PROJECTION_INBOX is append-only'', 1;
END');

EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_SCOPE
ON POM_WORK_SCOPE_PROJECTION_INBOX
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
          FROM inserted I
          LEFT JOIN POM_WORK_SCOPE S
            ON S.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                 = I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
           AND S.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2
                 = I.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2
         WHERE S.WORK_SCOPE_ID IS NULL)
        THROW 51531, ''POM work-scope projection requires exact equipment ownership'', 1;
END');

EXEC(N'CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_IDENTITY
ON POM_WORK_SCOPE_PROJECTION_CURRENT
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
          FROM deleted D
          LEFT JOIN inserted I
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EQUIPMENT_ID = I.EQUIPMENT_ID
           AND D.SEQUENCE_RUN_ID = I.SEQUENCE_RUN_ID
         WHERE I.SOURCE_CLIENT_ID IS NULL)
        THROW 51528, ''POM work-scope projection current cursor is not deletable'', 1;
    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EQUIPMENT_ID = I.EQUIPMENT_ID
           AND D.SEQUENCE_RUN_ID = I.SEQUENCE_RUN_ID
         WHERE D.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                   <> I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
            OR D.OPERATION_KEY <> I.OPERATION_KEY
            OR D.PAIR_RUN_ID <> I.PAIR_RUN_ID
            OR D.RECIPE_ID COLLATE Latin1_General_100_BIN2
                 <> I.RECIPE_ID COLLATE Latin1_General_100_BIN2
            OR D.RECIPE_SNAPSHOT_HASH <> I.RECIPE_SNAPSHOT_HASH
            OR D.PROGRAM_HASH <> I.PROGRAM_HASH
            OR D.CARRIERS_JSON COLLATE Latin1_General_100_BIN2
                 <> I.CARRIERS_JSON COLLATE Latin1_General_100_BIN2)
        THROW 51527, ''POM work-scope projection sequence identity is immutable'', 1;
    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D
            ON D.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
           AND D.EQUIPMENT_ID = I.EQUIPMENT_ID
           AND D.SEQUENCE_RUN_ID = I.SEQUENCE_RUN_ID
         WHERE I.SOURCE_REVISION < D.SOURCE_REVISION
            OR I.ACCEPTED_AT <= D.ACCEPTED_AT)
        THROW 51529, ''POM work-scope projection current cursor must advance monotonically'', 1;
    IF EXISTS (
        SELECT 1
          FROM inserted I
         WHERE NOT EXISTS (
            SELECT 1
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E
             WHERE E.SOURCE_CLIENT_ID = I.SOURCE_CLIENT_ID
               AND E.EVENT_ID = I.EVENT_ID
               AND E.EQUIPMENT_ID = I.EQUIPMENT_ID
               AND E.SEQUENCE_RUN_ID = I.SEQUENCE_RUN_ID
               AND E.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                     = I.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
               AND E.OPERATION_KEY = I.OPERATION_KEY
               AND E.PAIR_RUN_ID = I.PAIR_RUN_ID
               AND E.RECIPE_ID COLLATE Latin1_General_100_BIN2
                     = I.RECIPE_ID COLLATE Latin1_General_100_BIN2
               AND E.RECIPE_SNAPSHOT_HASH = I.RECIPE_SNAPSHOT_HASH
               AND E.PROGRAM_HASH = I.PROGRAM_HASH
               AND E.CARRIERS_JSON COLLATE Latin1_General_100_BIN2
                     = I.CARRIERS_JSON COLLATE Latin1_General_100_BIN2
               AND E.SOURCE_REVISION = I.SOURCE_REVISION
               AND E.PROJECTION_STATUS = I.PROJECTION_STATUS
               AND E.TERMINAL_CLEANUP_COMPLETED = I.TERMINAL_CLEANUP_COMPLETED
               AND E.OCCURRED_AT = I.OCCURRED_AT
               AND E.ACCEPTED_AT = I.ACCEPTED_AT
               AND E.ACCEPTED_AT = I.UPDATED_AT))
        THROW 51530, ''POM work-scope projection current cursor must reference its exact inbox event'', 1;
END');
-- SQLITE-OMIT-END
