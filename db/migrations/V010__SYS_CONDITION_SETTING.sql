-- SYS Module: 조건 저장/불러오기 (설계서 20.8)
-- 현행 ConditionSetting_{userId}_{menuId}.json 파일 저장소의 DB 마이그레이션.
-- NAME = '$latest' 행은 마지막 조회 조건(자동 저장)이며 저장 한도에서 제외된다.
-- NAME 콜레이션: DB 기본(CI, 대소문자 무시)을 의도적으로 유지한다.
--   서비스 계층(ConditionSettingService)이 OrdinalIgnoreCase로 비교하므로 CI와 일관되며,
--   BIN2(대소문자 구분)로 바꾸면 '$LATEST'/'$latest'가 PK상 별개 행이 되어
--   예약어 보호('$latest' 자동 저장 행) 가정이 깨진다. (§20.10 리뷰 이월 검토 종결)
CREATE TABLE SYS_CONDITION_SETTING (
    USER_ID         NVARCHAR(50)    NOT NULL,
    MENU_ID         NVARCHAR(100)   NOT NULL,   -- 웹 클라이언트 라우트 경로 (예: /fdc/monitor)
    NAME            NVARCHAR(100)   NOT NULL,   -- 사용자 지정 조건명, '$latest'는 예약어
    SAVED_AT        DATETIME2       NOT NULL,
    VALUES_JSON     NVARCHAR(MAX)   NOT NULL,   -- 조건 키/값 Dictionary 직렬화
    CONSTRAINT PK_SYS_CONDITION_SETTING PRIMARY KEY (USER_ID, MENU_ID, NAME)
);

CREATE INDEX IX_SYS_CONDITION_SETTING_USER_MENU
    ON SYS_CONDITION_SETTING (USER_ID, MENU_ID, SAVED_AT);
