-- MRP v2 2단 — 정밀 페깅(2026-07-10). 스펙: 볼트 2026-07-09-mrp-v1-design.md v2 백로그 ②.
-- 계획오더 1건의 총소요(GROSS)가 어느 수요에서 얼마나 왔는지 기여 단위로 분해 보존한다
-- (기존 SOURCE_DEMAND는 "SO01 외 1건" 텍스트 요약 — 다수요 합산 시 추적 불가였다).
-- DEMAND_REF: 독립 수요=수주 ID(SO01), 종속 수요=부모 품목 전개("ITEM01 생산 전개").
-- append-only(런별 보존) — 신규 테이블이라 SQLite 증분 경로에서도 자동 생성된다(ALTER 아님).
CREATE TABLE MRP_PEGGING (
    PEGGING_ID          NVARCHAR(60)    NOT NULL,
    RUN_ID              NVARCHAR(50)    NOT NULL,
    PLANNED_ORDER_ID    NVARCHAR(50)    NOT NULL,
    ITEM_ID             NVARCHAR(50)    NOT NULL,
    DEMAND_REF          NVARCHAR(200)   NOT NULL,
    QTY                 DECIMAL(18,4)   NOT NULL,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_MRP_PEGGING PRIMARY KEY (PEGGING_ID)
);

CREATE INDEX IX_MRP_PEGGING_RUN   ON MRP_PEGGING (RUN_ID);
CREATE INDEX IX_MRP_PEGGING_ORDER ON MRP_PEGGING (PLANNED_ORDER_ID);
