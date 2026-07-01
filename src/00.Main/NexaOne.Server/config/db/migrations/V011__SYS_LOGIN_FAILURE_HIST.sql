-- SYS Module: Login security (design 20.10 / 19.2.1)
-- SYS_USER: password lifecycle state + consecutive-failure lockout (5 tries / 30 min)
-- DEFAULT constraints are named: auto-generated names (DF__SYS_USER__...) differ
-- per environment and force dynamic SQL in any future migration that drops/alters them.
ALTER TABLE SYS_USER ADD
    PASSWORD_STATE      NVARCHAR(20)    NOT NULL CONSTRAINT DF_SYS_USER_PASSWORD_STATE DEFAULT 'Normal',  -- Normal/Create/Forgot/Expired
    FAIL_COUNT          INT             NOT NULL CONSTRAINT DF_SYS_USER_FAIL_COUNT DEFAULT 0,
    LOCKED_UNTIL        DATETIME2       NULL;

-- Login failure history. No FK to SYS_USER: attempts against
-- non-existent user IDs must also be recorded (design 20.10).
CREATE TABLE SYS_LOGIN_FAILURE_HIST (
    FAILURE_ID          NVARCHAR(50)    NOT NULL,
    USER_ID             NVARCHAR(50)    NOT NULL,
    IP_ADDRESS          NVARCHAR(45)    NOT NULL,
    USER_AGENT          NVARCHAR(500)   NOT NULL,
    FAILURE_REASON      NVARCHAR(50)    NOT NULL,
    OCCURRED_AT         DATETIME2       NOT NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL,
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL,
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_SYS_LOGIN_FAILURE_HIST PRIMARY KEY (FAILURE_ID)
);

CREATE INDEX IX_SYS_LOGIN_FAIL_USER ON SYS_LOGIN_FAILURE_HIST (USER_ID, OCCURRED_AT DESC);
