-- 메뉴 사용 통계 누적(2026-07-10) — 트리 심층 재배열(운영 메뉴 중심)의 데이터 전제.
-- SYS_RECENT_MENU는 사용자당 10행 트림이라 장기 빈도가 소실된다 — 전역 누적 테이블을 분리한다
-- (신규 테이블 = SQLite 증분 자동 생성, ALTER 아님). 기록은 최근메뉴 기록과 동일 지점(개인화 컨트롤러).
CREATE TABLE SYS_MENU_USAGE (
    MENU_ID         NVARCHAR(50)    NOT NULL,
    USE_COUNT       BIGINT          NOT NULL DEFAULT 0,
    LAST_USED_AT    DATETIME2       NOT NULL,
    CONSTRAINT PK_SYS_MENU_USAGE PRIMARY KEY (MENU_ID)
);
