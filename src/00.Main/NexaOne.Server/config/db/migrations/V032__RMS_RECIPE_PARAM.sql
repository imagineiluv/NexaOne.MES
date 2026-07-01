-- RMS Module: Recipe Parameter (레시피 파라미터)
-- 누락 마이그레이션 보강 — V005는 RMS_RECIPE만 생성했고, RecipeParamRepository가 사용하는
-- RMS_RECIPE_PARAM 테이블이 db/migrations에 없어 레시피 파라미터 CRUD(recipes/{id}/params)가
-- 모든 환경에서 'no such table'로 실패했다(통합테스트는 레시피 본체만, 단위테스트는 리포 Mock이라 미포착).
-- 컬럼/타입/널 허용은 RecipeParamRepository SQL과 1:1 정합:
--   SELECT * WHERE RECIPE_ID(ORDER BY SORT_ORDER) / WHERE PARAM_ID,
--   INSERT(PARAM_ID, RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER),
--   UPDATE(PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER) WHERE PARAM_ID, DELETE WHERE PARAM_ID.
-- INSERT/UPDATE는 감사 컬럼을 명시하지 않으므로(ServiceObjectProcessor는 파라미터만 주입, SQL 미재작성)
-- 감사 컬럼은 다른 마스터 테이블과 동일하게 DEFAULT로 채운다(V002/V026 컨벤션).
CREATE TABLE RMS_RECIPE_PARAM (
    PARAM_ID            NVARCHAR(50)    NOT NULL,
    RECIPE_ID           NVARCHAR(50)    NOT NULL,
    PARAM_NAME          NVARCHAR(200)   NOT NULL,
    PARAM_VALUE         NVARCHAR(500)   NOT NULL,
    UNIT                NVARCHAR(50)    NULL,
    SORT_ORDER          INT             NOT NULL DEFAULT 0,
    CREATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    CREATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    UPDATED_BY          NVARCHAR(50)    NOT NULL DEFAULT 'SYSTEM',
    UPDATED_AT          DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT PK_RMS_RECIPE_PARAM PRIMARY KEY (PARAM_ID),
    CONSTRAINT FK_RMS_RECIPE_PARAM_RECIPE FOREIGN KEY (RECIPE_ID)
        REFERENCES RMS_RECIPE (RECIPE_ID)
);

CREATE INDEX IX_RMS_RECIPE_PARAM_RECIPE ON RMS_RECIPE_PARAM (RECIPE_ID, SORT_ORDER);
