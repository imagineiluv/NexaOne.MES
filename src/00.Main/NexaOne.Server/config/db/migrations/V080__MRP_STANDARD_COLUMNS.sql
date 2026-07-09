-- MES/MRP 표준화 ALTER(2026-07-09 감사 갭 3·추가발견) — ⚠ SqliteSchemaInitializer 증분 경로는
-- 테이블/인덱스 생성문만 적용하므로 이 ALTER는 '빈 DB 전체 적용'에서만 자동 반영된다.
-- 기존 dev SQLite는 재생성, MSSQL 운영은 수동 적용(런북 §4 관례).

-- BOM 표준화 — 단위·스크랩률. MRP 전개 소요량 = 부모수량 × QUANTITY × (1 + SCRAP_RATE).
ALTER TABLE MDM_BOM ADD UOM_ID NVARCHAR(20) NULL;
ALTER TABLE MDM_BOM ADD SCRAP_RATE DECIMAL(9,4) NOT NULL DEFAULT 0;

-- 구매오더 품목 링크 — 감사 중 추가 발견 갭: PO에 PRODUCT_ID가 없어 예정입고(Scheduled Receipts)를
-- 품목별로 집계할 수 없었다(표준 이탈). NULL 허용(기존 행 무해), MRP는 품목 지정 행만 집계한다.
ALTER TABLE PRC_PURCHASE_ORDER ADD PRODUCT_ID NVARCHAR(50) NULL;
