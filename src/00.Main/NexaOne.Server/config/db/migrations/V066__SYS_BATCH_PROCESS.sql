-- SYS Module: 배치 작업 정의(설계 SmartUX 시스템 관리>배치 작업 관리). 레거시 SYS_TB_BATCH_PROCESS를
-- 현행 관례로 포팅(감사 DEFAULT, CREATOR/MODIFIER→CREATED_BY/UPDATED_BY). SYSTEM_2_BATCH_PROC_MANAGEMENT 점등용.
-- 1차 범위 = 정의 관리(CRUD)까지 — 실행 엔진(BATCH_RULE 스케줄 실행, 레거시 Quartz 대응)은 후속 슬라이스.
-- 현행 BackgroundService 워커(정적 등록)와의 통합 방식은 실행 엔진 설계 시 결정한다.
CREATE TABLE SYS_BATCH_PROCESS (
    BATCH_ID            NVARCHAR(50)    NOT NULL,
    BATCH_NAME          NVARCHAR(200)   NOT NULL,
    BATCH_TYPE          NVARCHAR(50)    NULL,           -- 레거시: 실행 유형(스케줄/수동 등)
    BATCH_RULE          NVARCHAR(200)   NULL,           -- 실행 대상 룰/작업 식별자
    START_DATETIME      DATETIME2       NULL,           -- 유효 시작(스케줄 창)
    END_DATETIME        DATETIME2       NULL,           -- 유효 종료
    BATCH_OPTIONS       NVARCHAR(1000)  NULL,           -- 스케줄/실행 옵션(레거시 형식 보존, 불투명 문자열)
    BATCH_INPUTDATA     NVARCHAR(2000)  NULL,           -- 실행 입력 파라미터(불투명 문자열)
    AUTO_TRANSACTION    BIT             NOT NULL DEFAULT 1,
    SAVE_HISTORY        BIT             NOT NULL DEFAULT 1,
    DESCRIPTION         NVARCHAR(500)   NULL,
    VALID_STATE         NVARCHAR(20)    NOT NULL DEFAULT 'Valid',
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_SYS_BATCH_PROCESS PRIMARY KEY (BATCH_ID)
);

CREATE INDEX IX_SYS_BATCH_PROCESS_STATE ON SYS_BATCH_PROCESS (VALID_STATE, BATCH_NAME);
