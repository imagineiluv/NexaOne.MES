-- MRP v2 1단 — 계획오더→실오더 전환(2026-07-10). 스펙: 볼트 2026-07-09-mrp-v1-design.md v2 백로그 ③.
-- PLANT_ID: 실오더(PRC/POM)가 PLANT_ID NOT NULL이라 제안 단계에서 수요(SLS.PLANT_ID)로부터 전파해 둔다.
-- CONVERTED_ORDER_ID: 전환 산출 실오더 역링크(감사/중복 방지 — STATUS='Converted'와 세트).
-- ⚠ SQLite 증분 경로는 ALTER를 적용하지 않는다 — 기존 dev SQLite는 재생성 필요(V080 선례).
ALTER TABLE MRP_PLANNED_ORDER ADD PLANT_ID NVARCHAR(50) NULL;
ALTER TABLE MRP_PLANNED_ORDER ADD CONVERTED_ORDER_ID NVARCHAR(50) NULL;
