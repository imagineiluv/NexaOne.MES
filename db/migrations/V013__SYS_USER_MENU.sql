-- SYS Module: 즐겨찾기 / 최근 메뉴 (설계서 20.12)
-- 현행: 즐겨찾기는 DB(드래그로 순서 변경), 최근 메뉴는 %AppData% JSON 파일(최대 10개).
-- 웹 적응: 둘 다 서버 DB로 영속 — 어느 브라우저/PC에서 접속해도 동일하게 유지된다.
-- SYS_MENU DDL이 본 마이그레이션 셋에 없어 FK는 두지 않는다 (SYS_LOGIN_FAILURE_HIST 선례).
-- 권한이 제거된 메뉴의 행도 삭제하지 않는다 — 조회 시 권한 메뉴와 교차해 숨기고,
-- 권한이 복원되면 별도 조치 없이 다시 보인다 (설계 20.12).

CREATE TABLE SYS_FAVORITE_MENU (
    USER_ID             NVARCHAR(50)    NOT NULL,
    MENU_ID             NVARCHAR(50)    NOT NULL,
    DISPLAY_SEQUENCE    INT             NOT NULL,   -- 사용자 지정 표시 순서
    CREATED_AT          DATETIME2       NOT NULL,
    CONSTRAINT PK_SYS_FAVORITE_MENU PRIMARY KEY (USER_ID, MENU_ID)
);

CREATE TABLE SYS_RECENT_MENU (
    USER_ID             NVARCHAR(50)    NOT NULL,
    MENU_ID             NVARCHAR(50)    NOT NULL,
    LAST_USED_AT        DATETIME2       NOT NULL,   -- 동일 메뉴 재사용 시 갱신 → 목록 맨 앞으로
    CONSTRAINT PK_SYS_RECENT_MENU PRIMARY KEY (USER_ID, MENU_ID)
);

CREATE INDEX IX_SYS_RECENT_MENU_USER ON SYS_RECENT_MENU (USER_ID, LAST_USED_AT DESC);
