-- MRP v1(표준 소요량 전개) + 계획 파라미터 + UOM 마스터. MES/MRP 표준 갭 감사(2026-07-09) 해소분:
-- 수요(SLS)·예정입고(PRC/POM)·BOM·재고(IVT)·조달 파라미터(MDM_VENDOR_ITEM)는 기존 스키마가 보유 —
-- 여기서는 계산 결과(계획오더 제안)와 부족하던 마스터(단위·품목 계획 파라미터)만 신설한다.
-- 스펙: 볼트 docs/design/specs/2026-07-09-mrp-v1-design.md

-- 단위(UOM) 마스터 — 기존 제품/자재 UNIT 자유 텍스트의 표준화 근거(참조 강제는 후속, v1은 마스터+화면).
CREATE TABLE MDM_UOM (
    UOM_ID              NVARCHAR(20)    NOT NULL,
    UOM_NAME            NVARCHAR(100)   NOT NULL,
    UOM_TYPE            NVARCHAR(20)    NOT NULL DEFAULT 'Count',   -- Count/Weight/Volume/Length/Time
    IS_ACTIVE           CHAR(1)         NOT NULL DEFAULT 'Y',
    DESCRIPTION         NVARCHAR(500)   NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_MDM_UOM PRIMARY KEY (UOM_ID)
);

-- 표준 단위 시드 — 자주 쓰는 11종(빈 DB 경로에서만 적용, 기존 DB는 화면 CRUD로 보강).
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('EA',  '개(Each)',       'Count');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('PCS', '피스(Pieces)',   'Count');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('KG',  '킬로그램',        'Weight');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('G',   '그램',            'Weight');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('T',   '톤',              'Weight');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('L',   '리터',            'Volume');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('ML',  '밀리리터',        'Volume');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('M',   '미터',            'Length');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('MM',  '밀리미터',        'Length');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('HR',  '시간',            'Time');
INSERT INTO MDM_UOM (UOM_ID, UOM_NAME, UOM_TYPE) VALUES ('MIN', '분',              'Time');

-- 품목 계획 파라미터 — 안전재고/리드타임/로트크기/조달구분. MRP 넷팅·로트사이징·역산의 품목별 입력.
-- LEAD_TIME_DAYS NULL=벤더품목(MDM_VENDOR_ITEM.LEAD_TIME_DAYS MIN) 폴백. MAKE_OR_BUY NULL=자동 판정
-- (BOM 부모=Make, 벤더품목 보유=Buy, 기본 Buy).
CREATE TABLE MDM_ITEM_PLANNING (
    ITEM_ID             NVARCHAR(50)    NOT NULL,
    SAFETY_STOCK        DECIMAL(18,4)   NOT NULL DEFAULT 0,
    LEAD_TIME_DAYS      INT             NULL,
    LOT_SIZE            DECIMAL(18,4)   NOT NULL DEFAULT 1,          -- 제안 수량 올림 배수(1=미적용)
    MAKE_OR_BUY         NVARCHAR(10)    NULL,                        -- Make/Buy/NULL(자동)
    IS_ACTIVE           CHAR(1)         NOT NULL DEFAULT 'Y',
    DESCRIPTION         NVARCHAR(500)   NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_MDM_ITEM_PLANNING PRIMARY KEY (ITEM_ID)
);

-- MRP 실행 이력 — append-only(런별 제안 보존), 조회 기본은 최신 런.
CREATE TABLE MRP_RUN (
    RUN_ID              NVARCHAR(50)    NOT NULL,
    STARTED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    FINISHED_AT         DATETIME2       NULL,
    STATUS              NVARCHAR(20)    NOT NULL DEFAULT 'Running',  -- Running/Success/Failed
    DEMAND_COUNT        INT             NOT NULL DEFAULT 0,
    PLANNED_ORDER_COUNT INT             NOT NULL DEFAULT 0,
    MESSAGE             NVARCHAR(1000)  NULL,                        -- 실패 사유(BOM 순환 경로 등)
    EXECUTED_BY         NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_MRP_RUN PRIMARY KEY (RUN_ID)
);

-- 계획오더 제안 — 넷팅 근거(GROSS/ON_HAND/ON_ORDER/SAFETY/NET)를 함께 노출해 화면에서 산식 추적 가능.
CREATE TABLE MRP_PLANNED_ORDER (
    PLANNED_ORDER_ID    NVARCHAR(50)    NOT NULL,
    RUN_ID              NVARCHAR(50)    NOT NULL,
    ITEM_ID             NVARCHAR(50)    NOT NULL,
    ORDER_TYPE          NVARCHAR(20)    NOT NULL,                    -- Purchase/Production
    GROSS_QTY           DECIMAL(18,4)   NOT NULL DEFAULT 0,
    ON_HAND_QTY         DECIMAL(18,4)   NOT NULL DEFAULT 0,
    ON_ORDER_QTY        DECIMAL(18,4)   NOT NULL DEFAULT 0,
    SAFETY_STOCK_QTY    DECIMAL(18,4)   NOT NULL DEFAULT 0,
    NET_QTY             DECIMAL(18,4)   NOT NULL DEFAULT 0,
    SUGGESTED_QTY       DECIMAL(18,4)   NOT NULL DEFAULT 0,
    DUE_DATE            DATETIME2       NULL,
    RELEASE_DATE        DATETIME2       NULL,                        -- DUE − 리드타임(과거면 지연 가시화 그대로)
    SOURCE_DEMAND       NVARCHAR(500)   NULL,                        -- 대표 수요(페깅 v1 — 텍스트 요약)
    STATUS              NVARCHAR(20)    NOT NULL DEFAULT 'Proposed',
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_MRP_PLANNED_ORDER PRIMARY KEY (PLANNED_ORDER_ID)
);
CREATE INDEX IX_MRP_PLANNED_ORDER_RUN ON MRP_PLANNED_ORDER (RUN_ID, ITEM_ID);
