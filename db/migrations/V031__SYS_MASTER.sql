-- SYS Module: Role / Menu / MultiLanguageResource 마스터 (누락 테이블 보강)
-- 기존 갭 — V001(SYS_USER)·V010(조건)·V013(사용자 메뉴)·V024(화면 정의)는 있었으나
-- 역할(SYS_ROLE)·메뉴(SYS_MENU)·다국어 리소스(SYS_MULTI_LANGUAGE_RESOURCE)가 누락되어
-- roles/menu/languages 엔드포인트가 신규 DB(MSSQL/SQLite)에서 SELECT 시 깨졌다.
-- 컬럼/타입/널/PK는 각 리포지토리 SQL + Row 클래스 프로퍼티와 1:1 정합:
--   RoleRepository / MenuRepository / MultiLanguageResourceRepository.
-- 형식 컨벤션은 V001__SYS_USER.sql 참조(NVARCHAR 크기, DATETIME2, 명명 PK, GETUTCDATE() DEFAULT).

-- 역할 — RoleRepository는 ServiceObjectProcessor.InsertAsync/UpdateAsync(감사 주입)를 쓰고,
-- INSERT SQL이 CREATED_BY/AT·UPDATED_BY/AT를 명시 컬럼으로 채우므로 감사 컬럼 4개를 둔다.
-- PERMISSIONS는 '|'로 조인된 권한 문자열(빈 문자열 가능)이라 NVARCHAR(MAX). IS_DELETED는 소프트 삭제 플래그.
CREATE TABLE SYS_ROLE (
    ROLE_ID         NVARCHAR(50)    NOT NULL,
    ROLE_NAME       NVARCHAR(100)   NOT NULL,
    DESCRIPTION     NVARCHAR(500)   NOT NULL DEFAULT '',
    PERMISSIONS     NVARCHAR(MAX)   NOT NULL DEFAULT '',
    IS_DELETED      BIT             NOT NULL DEFAULT 0,
    CREATED_BY      NVARCHAR(50)    NOT NULL,
    CREATED_AT      DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY      NVARCHAR(50)    NOT NULL,
    UPDATED_AT      DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_SYS_ROLE PRIMARY KEY (ROLE_ID)
);

-- 기본 ADMIN 역할 — SYS_USER('admin')이 ROLE_ID='ADMIN'을 참조한다(V001 시드 정합).
INSERT INTO SYS_ROLE
    (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    ('ADMIN', 'Administrator', 'System administrator role', '*', 0,
     'SYSTEM', GETUTCDATE(), 'SYSTEM', GETUTCDATE());

-- 메뉴 — MenuRepository는 InsertAsync/UpdateAsync(감사 주입)를 쓰지만 INSERT/UPDATE SQL이
-- 감사 컬럼을 참조하지 않으므로(주입 파라미터는 Dapper가 미사용 시 무시) 감사 컬럼을 두지 않는다.
-- 컬럼은 SELECT/INSERT 컬럼 목록 + MenuRow 프로퍼티 타입에서 도출. PARENT_MENU_ID/IMAGE_ID/OPTIONS는 NULL 허용.
-- VALID_STATE는 'Valid'/'Invalid' 문자열로 저장하며 SELECT가 'Valid'만 필터한다.
CREATE TABLE SYS_MENU (
    MENU_ID         NVARCHAR(50)    NOT NULL,
    MENU_NAME       NVARCHAR(100)   NOT NULL,
    PARENT_MENU_ID  NVARCHAR(50)    NULL,
    DISPLAY_SEQUENCE INT            NOT NULL DEFAULT 0,
    MENU_TYPE       NVARCHAR(20)    NOT NULL DEFAULT 'Screen',
    PROGRAM_ID      NVARCHAR(100)   NOT NULL DEFAULT '',
    IMAGE_ID        NVARCHAR(100)   NULL,
    OPTIONS         NVARCHAR(MAX)   NULL,
    UI_ID           NVARCHAR(50)    NOT NULL DEFAULT '',
    VALID_STATE     NVARCHAR(20)    NOT NULL DEFAULT 'Valid',
    CONSTRAINT PK_SYS_MENU PRIMARY KEY (MENU_ID)
);

CREATE INDEX IX_SYS_MENU_PARENT ON SYS_MENU (PARENT_MENU_ID, DISPLAY_SEQUENCE);

-- 다국어 리소스 — MultiLanguageResourceRepository는 InsertAsync/UpdateAsync(감사 주입)를 쓰지만
-- INSERT/UPDATE SQL이 감사 컬럼을 참조하지 않으므로 감사 컬럼을 두지 않는다.
-- 키는 (RESOURCE_KEY, LANGUAGE) 복합 PK — ExistsAsync가 언어별로 존재를 확인하고 업서트가
-- 다른 언어 행을 덮어쓰지 않도록 한다. LANGUAGE는 LanguageType enum 이름(KoKr/EnUs/...) 문자열.
CREATE TABLE SYS_MULTI_LANGUAGE_RESOURCE (
    RESOURCE_KEY    NVARCHAR(200)   NOT NULL,
    MENU_ID         NVARCHAR(50)    NOT NULL,
    LANGUAGE        NVARCHAR(10)    NOT NULL,
    VALUE           NVARCHAR(MAX)   NOT NULL DEFAULT '',
    CONSTRAINT PK_SYS_MULTI_LANGUAGE_RESOURCE PRIMARY KEY (RESOURCE_KEY, LANGUAGE)
);

CREATE INDEX IX_SYS_MLR_MENU ON SYS_MULTI_LANGUAGE_RESOURCE (MENU_ID);
CREATE INDEX IX_SYS_MLR_LANGUAGE ON SYS_MULTI_LANGUAGE_RESOURCE (LANGUAGE);

-- 메뉴-역할 매핑 — MenuRepository.GetAuthorizedMenusAsync가 SYS_MENU ⋈ SYS_MENU_ROLE ⋈ SYS_USER로
-- 사용자 역할 기준 인가 메뉴를 조회한다(SysController GET /api/v1/sys/menu). 이 테이블이 없으면 인가 메뉴
-- SELECT가 'no such table'로 깨지고, 컨트롤러의 try/catch가 이를 흡수해 메뉴가 항상 빈 결과로 조용히 망가진다.
-- 리포에 쓰기 경로가 없어 감사 컬럼은 두지 않는다. 키 = (MENU_ID, ROLE_ID). FK는 상위에서 생성된 SYS_MENU/SYS_ROLE.
CREATE TABLE SYS_MENU_ROLE (
    MENU_ID         NVARCHAR(50)    NOT NULL,
    ROLE_ID         NVARCHAR(50)    NOT NULL,
    VALID_STATE     NVARCHAR(20)    NOT NULL DEFAULT 'Valid',
    CONSTRAINT PK_SYS_MENU_ROLE PRIMARY KEY (MENU_ID, ROLE_ID),
    CONSTRAINT FK_SYS_MENU_ROLE_MENU FOREIGN KEY (MENU_ID) REFERENCES SYS_MENU (MENU_ID),
    CONSTRAINT FK_SYS_MENU_ROLE_ROLE FOREIGN KEY (ROLE_ID) REFERENCES SYS_ROLE (ROLE_ID)
);

CREATE INDEX IX_SYS_MENU_ROLE_ROLE ON SYS_MENU_ROLE (ROLE_ID, VALID_STATE);
