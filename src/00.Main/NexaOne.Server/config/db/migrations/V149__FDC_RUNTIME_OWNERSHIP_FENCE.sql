-- Owner: FDC. Elect exactly one durable runtime writer and preserve monotonic fencing across release/restart.

-- The GLOBAL row is never deleted. Release clears the lease tuple but intentionally retains
-- FENCE_TOKEN, so no process restart or owner reuse can make an old controller command current.
CREATE TABLE FDC_RUNTIME_OWNERSHIP (
    LEASE_SCOPE         NVARCHAR(20)    NOT NULL,
    OWNER_ID            NVARCHAR(100)   NULL,
    FENCE_TOKEN         BIGINT          NOT NULL,
    LEASE_EXPIRES_AT    DATETIME2(3)    NULL,
    HEARTBEAT_AT        DATETIME2(3)    NULL,
    CONFIG_REVISION     NVARCHAR(64)    NULL,
    LEASE_SECRET_HASH   NVARCHAR(64)    NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_FDC_RUNTIME_OWNERSHIP PRIMARY KEY (LEASE_SCOPE),
    CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_SCOPE CHECK (LEASE_SCOPE = 'GLOBAL'),
    CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_FENCE CHECK (FENCE_TOKEN >= 0),
    CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_TUPLE CHECK (
        (OWNER_ID IS NULL
         AND LEASE_EXPIRES_AT IS NULL
         AND HEARTBEAT_AT IS NULL
         AND CONFIG_REVISION IS NULL
         AND LEASE_SECRET_HASH IS NULL)
        OR
        (OWNER_ID IS NOT NULL
         AND LEN(LTRIM(RTRIM(OWNER_ID))) > 0
         AND LEASE_EXPIRES_AT IS NOT NULL
         AND HEARTBEAT_AT IS NOT NULL
         AND LEASE_EXPIRES_AT > HEARTBEAT_AT
         AND CONFIG_REVISION IS NOT NULL
         AND LEN(CONFIG_REVISION) = 64
         AND LEASE_SECRET_HASH IS NOT NULL
         AND LEN(LEASE_SECRET_HASH) = 64))
);

INSERT INTO FDC_RUNTIME_OWNERSHIP
    (LEASE_SCOPE, OWNER_ID, FENCE_TOKEN, LEASE_EXPIRES_AT, HEARTBEAT_AT,
     CONFIG_REVISION, LEASE_SECRET_HASH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    ('GLOBAL', NULL, 0, NULL, NULL, NULL, NULL,
     'SYSTEM', SYSUTCDATETIME(), 'SYSTEM', SYSUTCDATETIME());

-- SQL Server production guard. SQLite receives equivalent canonical UPDATE/DELETE triggers from
-- SqliteSchemaInitializer. Acquisition is the only operation allowed to increase the fence and
-- must increment it by one; renew/release retain the current fence. Direct deletion is forbidden.
-- SQLITE-OMIT-BEGIN
ALTER TABLE FDC_RUNTIME_OWNERSHIP ADD CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_DIGESTS CHECK (
    (CONFIG_REVISION IS NULL AND LEASE_SECRET_HASH IS NULL)
    OR
    (CONFIG_REVISION COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'
     AND LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'));

ALTER TABLE FDC_RUNTIME_OWNERSHIP ADD CONSTRAINT CK_FDC_RUNTIME_OWNERSHIP_LEASE_BOUND CHECK (
    LEASE_EXPIRES_AT IS NULL
    OR LEASE_EXPIRES_AT <= DATEADD(DAY, 1, HEARTBEAT_AT));

EXEC(N'CREATE TRIGGER TR_FDC_RUNTIME_OWNERSHIP_FENCE
ON FDC_RUNTIME_OWNERSHIP
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    IF EXISTS (
        SELECT 1
          FROM deleted D
          LEFT JOIN inserted I ON I.LEASE_SCOPE = D.LEASE_SCOPE
         WHERE I.LEASE_SCOPE IS NULL)
        THROW 51490, ''FDC runtime ownership row and fence counter are not deletable.'', 1;

    IF EXISTS (
        SELECT 1
          FROM inserted I
          JOIN deleted D ON D.LEASE_SCOPE = I.LEASE_SCOPE
         WHERE NOT (
            -- Active-owner renewal: same capability tuple, DB-time-valid old lease, fresh DB heartbeat.
            (I.FENCE_TOKEN = D.FENCE_TOKEN
             AND D.OWNER_ID IS NOT NULL
             AND I.OWNER_ID COLLATE Latin1_General_100_BIN2
                 = D.OWNER_ID COLLATE Latin1_General_100_BIN2
             AND I.CONFIG_REVISION COLLATE Latin1_General_100_BIN2
                 = D.CONFIG_REVISION COLLATE Latin1_General_100_BIN2
             AND I.LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2
                 = D.LEASE_SECRET_HASH COLLATE Latin1_General_100_BIN2
             AND I.HEARTBEAT_AT >= D.HEARTBEAT_AT
             AND D.LEASE_EXPIRES_AT > @Now
             AND I.HEARTBEAT_AT BETWEEN DATEADD(SECOND, -5, @Now) AND @Now
             AND I.LEASE_EXPIRES_AT >= D.LEASE_EXPIRES_AT
             AND I.LEASE_EXPIRES_AT > I.HEARTBEAT_AT
             AND I.LEASE_EXPIRES_AT <= DATEADD(DAY, 1, I.HEARTBEAT_AT))
            OR
            -- Voluntary release: clear the tuple but preserve the last issued fence.
            (I.FENCE_TOKEN = D.FENCE_TOKEN
             AND D.OWNER_ID IS NOT NULL
             AND I.OWNER_ID IS NULL)
            OR
            -- New acquisition: only an unowned/DB-time-expired row may issue exactly the next fence.
            (D.FENCE_TOKEN = I.FENCE_TOKEN - 1
             AND I.OWNER_ID IS NOT NULL
             AND (D.OWNER_ID IS NULL OR D.LEASE_EXPIRES_AT <= @Now)
             AND I.HEARTBEAT_AT BETWEEN DATEADD(SECOND, -5, @Now) AND @Now
             AND I.LEASE_EXPIRES_AT > I.HEARTBEAT_AT
             AND I.LEASE_EXPIRES_AT <= DATEADD(DAY, 1, I.HEARTBEAT_AT))))
        THROW 51491, ''FDC runtime ownership transition or fence token is invalid.'', 1;
END');
-- SQLITE-OMIT-END
