-- SYS Module: 사용자 등록 신청/승인 (설계서 19.3)
-- 설계 표준 SYS_TB_USER.STATE/VALID_STATE 컬럼은 추가하지 않는다 —
-- 신청 생명주기(Request/Approved/Rejected)는 SYS_USER_REQUEST가 단독 소유하고,
-- SYS_USER 행은 승인 시점에만 생성된다(미승인자는 로그인 자체가 불가).
-- 재신청(ReapplyMode)은 동일 행 전환 + REQUEST_VERSION 증가로 고정 적응.
CREATE TABLE SYS_USER_REQUEST (
    REQUEST_ID          NVARCHAR(50)    NOT NULL,
    USER_ID             NVARCHAR(50)    NOT NULL,
    USER_NAME           NVARCHAR(100)   NOT NULL,
    EMAIL               NVARCHAR(200)   NOT NULL,
    DEPARTMENT          NVARCHAR(100)   NOT NULL,
    POSITION            NVARCHAR(100)   NOT NULL,       -- 직급
    DUTY                NVARCHAR(100)   NULL,           -- 직책 (선택)
    PLANT_ID            NVARCHAR(50)    NOT NULL,
    LANGUAGE            NVARCHAR(10)    NOT NULL DEFAULT 'KoKr',
    CELL_PHONE          NVARCHAR(50)    NULL,
    ADDRESS             NVARCHAR(300)   NULL,
    DESCRIPTION         NVARCHAR(500)   NULL,
    NICKNAME            NVARCHAR(100)   NULL,
    STATUS              NVARCHAR(20)    NOT NULL DEFAULT 'Request',  -- Request/Approved/Rejected
    REQUEST_VERSION     INT             NOT NULL DEFAULT 1,          -- 재신청 시 증가 (설계 19.3.6)
    REQUESTED_AT        DATETIME2       NOT NULL,       -- 신청 시각 — 재신청 시 갱신 (검색 조건 '신청일')
    TERMS_ACCEPTED_AT   DATETIME2       NOT NULL,       -- 약관 동의 시각 (설계 19.3.2)
    TERMS_ACCEPTED_IP   NVARCHAR(45)    NOT NULL,       -- 약관 동의 IP
    APPROVED_BY         NVARCHAR(50)    NULL,
    APPROVED_AT         DATETIME2       NULL,
    REJECT_REASON       NVARCHAR(500)   NULL,
    REJECTED_BY         NVARCHAR(50)    NULL,
    REJECTED_AT         DATETIME2       NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL,
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL,
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_SYS_USER_REQUEST PRIMARY KEY (REQUEST_ID),
    -- 사용자당 1행 — 반려 후 재신청은 같은 행을 Request로 전환한다
    CONSTRAINT UQ_SYS_USER_REQUEST_USER UNIQUE (USER_ID)
);

-- 승인 화면 검색 조건: Plant/상태/신청일 (설계 19.3.4)
CREATE INDEX IX_SYS_USER_REQUEST_SEARCH ON SYS_USER_REQUEST (PLANT_ID, STATUS, REQUESTED_AT DESC);
