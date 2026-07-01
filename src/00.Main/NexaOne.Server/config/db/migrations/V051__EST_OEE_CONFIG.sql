-- EST Module: OEE 집계 워커 설정 마스터 — 상태 카테고리 분류(가용성 계산) + 설비별 목표(이상 사이클타임/계획시간).
-- OEE 집계 워커(OeeAggregationService)가 원자료(EST_EQUIPMENT_STATE_HISTORY 상태구간 · POM_LOT 생산/불량 수량)를
-- 이 설정과 결합해 EST_OEE_SUMMARY/EST_OEE_LOSS 마트를 계산·적재한다. 감사/PK 관례는 V050과 정합.

-- 설비 상태 코드 분류 — 상태 문자열(RUN/DOWN/IDLE/SETUP...)을 OEE 관점 카테고리로 매핑한다.
-- IS_PRODUCTIVE=가동(가용성 분자), IS_DOWNTIME=비가동 손실, IS_SCHEDULED=계획 생산시간 포함(비계획 IDLE은 제외).
CREATE TABLE EST_STATE_CATEGORY (
    STATE_ID         NVARCHAR(50)    NOT NULL,
    STATE_NAME       NVARCHAR(200)   NULL,
    CATEGORY         NVARCHAR(30)    NOT NULL,   -- Productive/Breakdown/Setup/MinorStop/SpeedLoss/Idle 등
    IS_PRODUCTIVE    BIT             NOT NULL DEFAULT 0,
    IS_DOWNTIME      BIT             NOT NULL DEFAULT 0,
    IS_SCHEDULED     BIT             NOT NULL DEFAULT 1,
    CREATED_BY       NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT       DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY       NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT       DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_EST_STATE_CATEGORY PRIMARY KEY (STATE_ID)
);

-- 설비별 OEE 목표 — 이상 사이클타임(성능 계산 기준)과 표준 계획시간(분). 상태이력이 없을 때의 계획시간 폴백.
CREATE TABLE EST_OEE_TARGET (
    EQUIPMENT_ID          NVARCHAR(50)    NOT NULL,
    IDEAL_CYCLE_TIME_SEC  DECIMAL(18,4)   NOT NULL DEFAULT 0,   -- 이상 사이클타임(초/개)
    PLANNED_MINUTES       DECIMAL(18,4)   NOT NULL DEFAULT 0,   -- 표준 계획 생산시간(분) — 상태이력 부재 시 폴백
    DESCRIPTION           NVARCHAR(500)   NULL,
    IS_ACTIVE             BIT             NOT NULL DEFAULT 1,
    CREATED_BY            NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT            DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY            NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT            DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_EST_OEE_TARGET PRIMARY KEY (EQUIPMENT_ID),
    CONSTRAINT FK_EST_OEE_TARGET_EQUIPMENT FOREIGN KEY (EQUIPMENT_ID)
        REFERENCES MDM_EQUIPMENT (EQUIPMENT_ID)
);
