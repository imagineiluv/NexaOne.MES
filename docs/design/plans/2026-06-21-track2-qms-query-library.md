# 쿼리 라이브러리 확장 — QMS read/combo 슬라이스 (트랙 ②) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. 체크박스 단계.

**Goal:** 게이트웨이-최대(ADR-001) 파일 기반 명명 쿼리 라이브러리를 MDM/SYS 2개에서 **QMS 1개 모듈을 추가**해 확장한다. 메타 런타임·디자이너 카탈로그·게이트웨이가 즉시 소비할 QMS read·combo(+write 1개) 쿼리를 양 방언(mssql/sqlite)으로 추가하고, SQLite 게이트웨이 라운드트립 통합테스트로 검증한다.

**Architecture:** 신규 `db/queries/{mssql,sqlite}/QMS.xml`. csproj의 `db/queries/**/*.xml` 재귀 글롭이 자동 복사(csproj 무수정). `QueryCatalogController`가 레지스트리 순회로 QMS 쿼리를 자동 노출(디자이너 드롭다운, 추가 코드 0). read는 인증만, write(`QMS.CreateDefectClass`)는 `requiredPermission="qms:manage"`(부팅 fail-fast 충족).

**Tech Stack:** 파일 쿼리 XML(mssql NOLOCK / sqlite), xUnit 통합테스트(SQLite, QMS REST 시드→명명쿼리 되읽기 E2E).

---

## 검증된 사실 (배경 워크플로 실측)

- 게이트웨이 [QueryGatewayController.cs](../../../src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs): `/query/{id}`=read(write면 400), `/command/{id}`=write. `requiredPermission`→`HasPermission`(L90-93), write는 `@currentUser`/`@utcNow` 서버 주입(L76-80), 본문 없는 `@param`은 `DBNull.Value`(L81-82) → `(@p IS NULL OR COL=@p)` 선택필터 동작.
- 레지스트리 `FileQueryRegistry`: 방언 폴더 `*.xml` 로드, 중복 ID 부팅 fail-fast, `kind="write"` + requiredPermission 미선언 부팅 fail-fast. ID는 방언 전역 고유.
- csproj([NexaOne.Server.csproj](../../../src/00.Main/NexaOne.Server/NexaOne.Server.csproj) L50-54) `db/queries/**/*.xml` 재귀 글롭 → 새 QMS.xml 자동 복사(csproj 무수정).
- QMS 테이블 실재(db/migrations): `QMS_DEFECT_CLASS`(V030: DEFECT_CLASS_ID/DEFECT_CLASS_NAME/DESCRIPTION/SEVERITY/IS_ACTIVE/IS_DELETED/...), `QMS_INSPECTION_SPEC`(V030: SPEC_ID/SPEC_NAME/PROCESS_ID/ITEM_NAME/MEASURE_TYPE/NOMINAL_VALUE/TOLERANCE_PLUS/TOLERANCE_MINUS/IS_ACTIVE), `QMS_SPC_PARAM`(V030: PARAM_ID/PARAM_NAME/EQUIPMENT_ID/PROCESS_ID/MEAN/UCL/LCL/USL/LSL/SAMPLE_SIZE/IS_ACTIVE), `QMS_DEFECT`(V006: DEFECT_ID/LOT_ID/EQUIPMENT_ID/DEFECT_CLASS_ID/DEFECT_COUNT/DEFECT_RATE/INSPECTED_AT/INSPECTOR_ID/IS_CONFIRMED).
- QMS REST 시드 경로: [QmsController.cs](../../../src/02.Backend/NexaOne.API/Controllers/QmsController.cs) `POST /api/v1/qms/defect-classes`·`/inspection-specs`(perm:qms:manage). 통합테스트 하니스 `TestApiFactory`가 SQLite에 db/migrations 전체 적용 + `CreateAuthenticatedClient(perms)`(미지정=`*`).
- `Permissions.cs`에 `qms:manage` 실재. SQLite는 BIT를 정수 0/1로 저장 → `IS_ACTIVE = 1` 양 방언 공통.
- 레거시 SQL은 그대로 이식 불가(다국어 컬럼·Velocity 보간·STD_TB_*/COM_TB_* 미존재) → NexaMes 실존 스키마 + `@param` 바인딩으로 재작성.

## File Structure
- 생성: `db/queries/mssql/QMS.xml`, `db/queries/sqlite/QMS.xml`, `test/NexaOne.IntegrationTests/Query/QmsQueryGatewayIntegrationTests.cs`.
- 선택(채택): `src/01.Web/NexaOne.Web.Components/Services/Meta/InMemoryScreenDefinitionProvider.cs`(DEMO_QMS_DEFECT_CLASS 시드 1개).
- 무수정: csproj(글롭 자동), QueryCatalogController(레지스트리 순회 자동).

---

## Task 1: db/queries/mssql/QMS.xml (MSSQL 방언, WITH (NOLOCK))

BOM 없는 UTF-8로 생성:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!-- 파일 기반 쿼리 레지스트리(MSSQL 방언) — QMS 품질관리 read/combo 슬라이스.
     SQLite판과 동일 ID·동일 의미이되 NOLOCK 힌트만 추가. 레거시 다국어 CASE·Velocity 보간·STD_TB_*/COM_TB_*를
     NexaMes 실존 스키마(QMS_*)·@param 선택필터로 재작성. 선택필터 (@p IS NULL OR COL=@p) — 본문 없는 @param은 게이트웨이가 null로 채움. -->
<queries module="QMS">
    <query id="QMS.DefectClassCombo">
        <statement><![CDATA[
            SELECT DEFECT_CLASS_ID AS VALUE, DEFECT_CLASS_NAME AS TEXT
            FROM QMS_DEFECT_CLASS WITH (NOLOCK)
            WHERE IS_DELETED = 0 AND IS_ACTIVE = 1
              AND (@severity IS NULL OR SEVERITY = @severity)
            ORDER BY DEFECT_CLASS_NAME
        ]]></statement>
    </query>
    <query id="QMS.DefectClassList">
        <statement><![CDATA[
            SELECT DEFECT_CLASS_ID, DEFECT_CLASS_NAME, DESCRIPTION, SEVERITY, IS_ACTIVE
            FROM QMS_DEFECT_CLASS WITH (NOLOCK)
            WHERE IS_DELETED = 0 AND (@severity IS NULL OR SEVERITY = @severity)
            ORDER BY DEFECT_CLASS_NAME
        ]]></statement>
    </query>
    <query id="QMS.InspectionSpecCombo">
        <statement><![CDATA[
            SELECT SPEC_ID AS VALUE, SPEC_NAME AS TEXT
            FROM QMS_INSPECTION_SPEC WITH (NOLOCK)
            WHERE IS_ACTIVE = 1 AND (@processId IS NULL OR PROCESS_ID = @processId)
            ORDER BY SPEC_NAME
        ]]></statement>
    </query>
    <query id="QMS.InspectionSpecList">
        <statement><![CDATA[
            SELECT SPEC_ID, SPEC_NAME, PROCESS_ID, ITEM_NAME, MEASURE_TYPE,
                   NOMINAL_VALUE, TOLERANCE_PLUS, TOLERANCE_MINUS, IS_ACTIVE
            FROM QMS_INSPECTION_SPEC WITH (NOLOCK)
            WHERE (@processId IS NULL OR PROCESS_ID = @processId)
              AND (@measureType IS NULL OR MEASURE_TYPE = @measureType)
            ORDER BY SPEC_NAME
        ]]></statement>
    </query>
    <query id="QMS.SpcParamList">
        <statement><![CDATA[
            SELECT PARAM_ID, PARAM_NAME, EQUIPMENT_ID, PROCESS_ID,
                   MEAN, UCL, LCL, USL, LSL, SAMPLE_SIZE, IS_ACTIVE
            FROM QMS_SPC_PARAM WITH (NOLOCK)
            WHERE (@equipmentId IS NULL OR EQUIPMENT_ID = @equipmentId)
              AND (@processId IS NULL OR PROCESS_ID = @processId)
            ORDER BY PARAM_NAME
        ]]></statement>
    </query>
    <query id="QMS.DefectList">
        <statement><![CDATA[
            SELECT DEFECT_ID, LOT_ID, EQUIPMENT_ID, DEFECT_CLASS_ID,
                   DEFECT_COUNT, DEFECT_RATE, INSPECTED_AT, INSPECTOR_ID, IS_CONFIRMED
            FROM QMS_DEFECT WITH (NOLOCK)
            WHERE (@lotId IS NULL OR LOT_ID = @lotId)
              AND (@equipmentId IS NULL OR EQUIPMENT_ID = @equipmentId)
            ORDER BY INSPECTED_AT DESC
        ]]></statement>
    </query>
    <query id="QMS.CreateDefectClass" kind="write" requiredPermission="qms:manage">
        <statement><![CDATA[
            INSERT INTO QMS_DEFECT_CLASS (DEFECT_CLASS_ID, DEFECT_CLASS_NAME, DESCRIPTION, SEVERITY,
                                          IS_ACTIVE, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@defectClassId, @defectClassName, @description, @severity,
                    1, 0, @currentUser, @utcNow, @currentUser, @utcNow)
        ]]></statement>
    </query>
</queries>
```

## Task 2: db/queries/sqlite/QMS.xml (SQLite 방언 — NOLOCK 제거, 그 외 동일)

위 MSSQL판과 **동일 ID·@param·의미**, `WITH (NOLOCK)` 6곳만 제거. BOM 없는 UTF-8. (전체 내용은 위에서 NOLOCK만 뺀 형태 — 구현자는 MSSQL판을 복사 후 `WITH (NOLOCK)` 제거.)

## Task 3: 통합테스트 test/NexaOne.IntegrationTests/Query/QmsQueryGatewayIntegrationTests.cs

`TestApiFactory`(SQLite, db/migrations 적용, `CreateAuthenticatedClient`)를 쓴다. 시드는 QMS REST(`POST /api/v1/qms/defect-classes`·`/inspection-specs`), 되읽기는 명명 쿼리 게이트웨이. 검증:
1. **미인증 401**: anon으로 `POST /query/QMS.DefectClassList` → 401.
2. **DefectClassCombo**: REST로 결함분류 2건(Major/Minor) 시드 → 콤보 전체 2건(VALUE/TEXT) + `@severity=Major` 필터 1건.
3. **InspectionSpec combo+list**: REST로 규격 1건 시드(processId) → combo·list가 processId 필터로 1건, ITEM_NAME/MEASURE_TYPE 확인.
4. **CreateDefectClass command**: 권한 없는 토큰(`fdc:read`) → 403; `qms:manage` 토큰 → 200(affected=1), 본문 위변조 `currentUser:"HACKER"`는 무시; 콤보 되읽기로 영속 확인.
5. **write를 /query로** → 400(종류 가드).

(전체 테스트 코드는 배경 워크플로 산출물의 `QmsQueryGatewayIntegrationTests.cs`를 그대로 사용 — 구현자에게 별도 제공.)

## Task 4 (선택): InMemoryScreenDefinitionProvider에 DEMO_QMS_DEFECT_CLASS 그리드 시드 1개(QMS.DefectClassList 바인딩). 채택 시 ScreenDefinitionProviderTests 정합 유지.

## 검증 명령
```
dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj -c Debug --nologo
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug --filter "FullyQualifiedName~QmsQueryGatewayIntegrationTests"
dotnet test test/NexaOne.IntegrationTests/NexaOne.IntegrationTests.csproj -c Debug --filter "FullyQualifiedName~Query|FullyQualifiedName~QueryCatalog"
```

## 커밋/병합
- BOM-free UTF-8(Write 도구). `git add -A` 금지(submodules/NexusLogic 더티) — 명시 경로만. main ff-merge, push 안 함. Co-Authored-By 트레일러.

## 주의/리스크
- 실존 테이블만 대상(QMS_DEFECT_CLASS/INSPECTION_SPEC/SPC_PARAM/DEFECT). write fail-fast(requiredPermission 양 방언 선언 필수). ID 고유(QMS. 접두, MDM./SYS.와 비충돌). SQLite BIT=정수(`IS_ACTIVE=1`). nullable decimal(NOMINAL_VALUE 등)은 테스트가 문자열 컬럼만 단언해 회피.
