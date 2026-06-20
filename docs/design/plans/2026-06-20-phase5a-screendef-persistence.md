# Phase 5a — 호스트 화면정의 영속 + 쿼리 카탈로그 (GrapesJS 디자이너 선결) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. 체크박스(`- [ ]`).

**Goal:** 통합 호스트가 화면정의(ScreenDefinition)를 SYS_SCREEN_DEFINITION에 저장·DB로드하고 쿼리 카탈로그를 노출해, 디자이너(Phase 5b)가 SAVE한 정의를 단일 프로세스 /meta가 DB에서 LOAD·렌더하도록 한다. 비파괴·자동검증.

**설계(임베드, ADR-001 게이트웨이-최대):** 호스트는 현재 `InMemoryScreenDefinitionProvider`(시드만)뿐 — 영속·카탈로그가 NexaOne.API에만 있다. Phase 5a는 게이트웨이 명명쿼리로 영속을 도착시킨다: `SYS.GetScreenDefinition`/`SYS.ListScreenDefinitions`(read)·`SYS.UpsertScreenDefinition`(write, requiredPermission=sys:manage)를 공개 db/queries에 둔다(화면정의는 UI 메타 — read 무해, write는 sys:manage 게이트). 호스트 `GatewayScreenDefinitionProvider`가 `IRuleDispatcher`로 DB를 읽어(없으면 InMemory 시드 폴백) /meta에 제공. 디자이너 SAVE는 기존 command 게이트웨이(`POST /api/v1/command/SYS.UpsertScreenDefinition`). 호스트 `QueryCatalogController`(GET /api/v1/sys/queries)는 호스트 `IQueryRegistry.Ids`를 노출(SQL 비노출). 동기 `IScreenDefinitionProvider.Get`의 sync-over-async를 피하려 `GetAsync`를 인터페이스에 가산(MetaScreen이 await). 격리: ADR-006 무변경(RCL Default-ALC, 모듈 게시 deps-제외 불변).

**Tech Stack:** ASP.NET Core 8, 게이트웨이(IRuleDispatcher/IQueryRegistry), 명명쿼리(mssql/sqlite), Blazor RCL, xunit + SQLite. 빌드/테스트 `dotnet ... NexaOne.sln`. 커밋 PowerShell BOM-free, `git add -A` 금지, push/merge 금지.

---

## Task 1: 명명쿼리 — 화면정의 영속(mssql + sqlite)

**Files:** Modify `db/queries/mssql/SYS.xml`(없으면 생성), `db/queries/sqlite/SYS.xml`(없으면 생성). (주의: db/queries 공개 폴더 — 인증 SYS 쿼리는 db/queries-auth에 별도 존재하므로 혼동 금지. 여기 SYS.xml은 화면정의용 공개 쿼리.)

- [ ] **Step 1: 공개 SYS.xml 존재 확인** — `db/queries/mssql/`·`db/queries/sqlite/`에 SYS.xml이 이미 있는지 확인(Glob). 없으면 신규 생성(루트 `<queries module="SYS">`). 있으면 아래 3쿼리를 추가(중복 id 금지 — FileQueryRegistry는 방언 전역 고유 id를 fail-fast 검증).

- [ ] **Step 2: mssql `db/queries/mssql/SYS.xml`에 3쿼리 추가**
```xml
  <!-- 화면정의 영속(Phase 5a) — SYS_SCREEN_DEFINITION. 화면정의는 UI 메타(불투명 JSON), read 무해/ write는 sys:manage. -->
  <query id="SYS.GetScreenDefinition">
    <statement><![CDATA[
SELECT UI_ID, TITLE, DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WITH (NOLOCK) WHERE UI_ID = @uiId
]]></statement>
  </query>
  <query id="SYS.ListScreenDefinitions">
    <statement><![CDATA[
SELECT UI_ID, TITLE FROM SYS_SCREEN_DEFINITION WITH (NOLOCK) ORDER BY UI_ID
]]></statement>
  </query>
  <query id="SYS.UpsertScreenDefinition" kind="write" requiredPermission="sys:manage">
    <statement><![CDATA[
MERGE SYS_SCREEN_DEFINITION WITH (HOLDLOCK) AS t
USING (SELECT @uiId AS UI_ID) AS s ON t.UI_ID = s.UI_ID
WHEN MATCHED THEN UPDATE SET
    TITLE = @title, DEFINITION_JSON = @definitionJson, UPDATED_BY = @currentUser, UPDATED_AT = @utcNow
WHEN NOT MATCHED THEN INSERT (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
    VALUES (@uiId, @title, @definitionJson, @currentUser, @utcNow, @currentUser, @utcNow);
]]></statement>
  </query>
```

- [ ] **Step 3: sqlite `db/queries/sqlite/SYS.xml`에 3쿼리 추가**(NOLOCK 제거, upsert는 ON CONFLICT)
```xml
  <query id="SYS.GetScreenDefinition">
    <statement><![CDATA[
SELECT UI_ID, TITLE, DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId
]]></statement>
  </query>
  <query id="SYS.ListScreenDefinitions">
    <statement><![CDATA[
SELECT UI_ID, TITLE FROM SYS_SCREEN_DEFINITION ORDER BY UI_ID
]]></statement>
  </query>
  <query id="SYS.UpsertScreenDefinition" kind="write" requiredPermission="sys:manage">
    <statement><![CDATA[
INSERT INTO SYS_SCREEN_DEFINITION (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES (@uiId, @title, @definitionJson, @currentUser, @utcNow, @currentUser, @utcNow)
ON CONFLICT(UI_ID) DO UPDATE SET
    TITLE = excluded.TITLE, DEFINITION_JSON = excluded.DEFINITION_JSON,
    UPDATED_BY = excluded.UPDATED_BY, UPDATED_AT = excluded.UPDATED_AT
]]></statement>
  </query>
```
(command 게이트웨이가 write에 @currentUser·@utcNow 주입. @uiId/@title/@definitionJson은 요청 본문. CREATED_BY/AT는 conflict 시 보존.)

- [ ] **Step 4: well-formed 검증 + 빌드** — `[xml](Get-Content -Raw db/queries/mssql/SYS.xml)` OK, sqlite OK. `dotnet build NexaOne.sln -c Debug`(0 error). 기존 ServerTests/IntegrationTests 그린(FileQueryRegistry가 새 쿼리 로드, 중복 id 없음).

- [ ] **Step 5: Commit** — `git add db/queries/mssql/SYS.xml db/queries/sqlite/SYS.xml`; 메시지 `feat(queries): 화면정의 영속 명명쿼리(SYS.Get/List/UpsertScreenDefinition, sys:manage)(Phase 5a)`.

---

## Task 2: RCL — IScreenDefinitionProvider.GetAsync 가산(sync-over-async 회피)

**Files:** Modify `src/01.Web/NexaOne.Web.Components/Services/Meta/IScreenDefinitionProvider.cs`, `InMemoryScreenDefinitionProvider.cs`, `Pages/Meta/MetaScreen.razor`.

- [ ] **Step 1: 인터페이스에 GetAsync 가산**(기존 동기 멤버 유지 — 가산, 비파괴)
```csharp
namespace NexaOne.Web.Services.Meta;

public interface IScreenDefinitionProvider
{
    void Register(ScreenDefinition definition);
    bool TryGet(string uiId, out ScreenDefinition? definition);
    ScreenDefinition? Get(string uiId);
    /// <summary>비동기 조회(DB-backed 구현용). 인메모리는 동기 결과를 래핑한다.</summary>
    Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default);
}
```

- [ ] **Step 2: InMemory에 GetAsync 구현**(동기 래핑) — `InMemoryScreenDefinitionProvider`에 추가:
```csharp
    public Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
        => Task.FromResult(Get(uiId));
```

- [ ] **Step 3: MetaScreen 로드를 GetAsync로** — `MetaScreen.razor`의 정의 로드(현재 `Definitions.Get(uiId)` 호출, OnInitializedAsync 내)를 `await Definitions.GetAsync(uiId)`로 교체. API 폴백·Register 로직은 유지(GetAsync가 null이면 기존 Api 폴백 경로 그대로). 동기 `Get` 다른 호출부(있으면)는 무변경.

- [ ] **Step 4: 빌드 + 비회귀(중요)** — `dotnet build NexaOne.sln -c Debug`(0 error). `dotnet test test/NexaOne.UnitTests`(1090 — MetaScreen bUnit이 InMemory.GetAsync로 동일 동작), `dotnet test test/NexaOne.ServerTests`(39). **NexaOne.Web 빌드 + bUnit 비회귀 필수**(인터페이스 가산이 깨뜨리지 않음 확인). 다른 IScreenDefinitionProvider 구현이 있으면 GetAsync 추가.

- [ ] **Step 5: Commit** — `git add src/01.Web/NexaOne.Web.Components/Services/Meta/IScreenDefinitionProvider.cs src/01.Web/NexaOne.Web.Components/Services/Meta/InMemoryScreenDefinitionProvider.cs src/01.Web/NexaOne.Web.Components/Pages/Meta/MetaScreen.razor`; 메시지 `feat(web): IScreenDefinitionProvider.GetAsync 가산 + MetaScreen 비동기 로드(DB provider 대비, Phase 5a)`.

---

## Task 3: 호스트 — GatewayScreenDefinitionProvider + QueryCatalogController

**Files:** Create `src/00.Main/NexaOne.Server/Gateway/GatewayScreenDefinitionProvider.cs`, `src/00.Main/NexaOne.Server/Gateway/QueryCatalogController.cs`. Modify `Program.cs`.

- [ ] **Step 1: GatewayScreenDefinitionProvider** — `src/00.Main/NexaOne.Server/Gateway/GatewayScreenDefinitionProvider.cs`:
```csharp
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>DB-backed 화면정의 제공자(Phase 5a) — SYS_SCREEN_DEFINITION을 게이트웨이(IRuleDispatcher+명명쿼리)로
/// 읽어 /meta에 제공한다. DB에 없으면 InMemory 시드(DEMO_*)로 폴백. 디자이너 SAVE는 command 게이트웨이
/// (SYS.UpsertScreenDefinition)로 쓰고, 다음 로드 시 이 provider가 DB에서 읽는다.</summary>
public sealed class GatewayScreenDefinitionProvider : IScreenDefinitionProvider
{
    private readonly InMemoryScreenDefinitionProvider _seed = new();   // 시드(DEMO_*) + Register 캐시
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _queries;

    public GatewayScreenDefinitionProvider(IRuleDispatcher dispatcher, IQueryRegistry queries)
    {
        _dispatcher = dispatcher;
        _queries = queries;
    }

    public void Register(ScreenDefinition definition) => _seed.Register(definition);

    public bool TryGet(string uiId, out ScreenDefinition? definition)
    {
        definition = GetAsync(uiId).GetAwaiter().GetResult();
        return definition is not null;
    }

    public ScreenDefinition? Get(string uiId) => GetAsync(uiId).GetAwaiter().GetResult();

    public async Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
    {
        var fromDb = await LoadFromDbAsync(uiId, ct);   // DB 우선(사용자 편집 정의)
        return fromDb ?? _seed.Get(uiId);               // 없으면 시드/캐시 폴백
    }

    private async Task<ScreenDefinition?> LoadFromDbAsync(string uiId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uiId)) return null;
        if (!_queries.TryGet("SYS.GetScreenDefinition", out var def) || def is null) return null;
        var rows = await _dispatcher.QueryAsync(def.Sql, new Dictionary<string, object> { ["uiId"] = uiId }, ct);
        if (rows.Count == 0) return null;
        var json = rows[0].TryGetValue("DEFINITION_JSON", out var v) ? v?.ToString() : null;
        return string.IsNullOrWhiteSpace(json) ? null : ScreenDefinitionJson.Deserialize(json);
    }
}
```
(동기 `Get`/`TryGet`은 호환용 sync-over-async — ASP.NET엔 SyncContext 없어 데드락 없음; /meta 로드는 `GetAsync`(await)를 쓴다. DB read는 게이트웨이 per-call 연결.)

- [ ] **Step 2: Program.cs — provider 교체** — Phase 4에서 등록한 `builder.Services.AddSingleton<IScreenDefinitionProvider, InMemoryScreenDefinitionProvider>();`를 다음으로 교체:
```csharp
// Phase 5a — DB-backed 화면정의 제공자(게이트웨이 SYS.GetScreenDefinition, InMemory 시드 폴백).
builder.Services.AddSingleton<IScreenDefinitionProvider>(sp => new GatewayScreenDefinitionProvider(
    sp.GetRequiredService<IRuleDispatcher>(), sp.GetRequiredService<IQueryRegistry>()));
```
(`IRuleDispatcher`/`IQueryRegistry`는 AddNexaOneGateway가 등록 — singleton. `using NexaOne.Server.Gateway;`는 이미 있음.)

- [ ] **Step 3: 호스트 QueryCatalogController** — `src/00.Main/NexaOne.Server/Gateway/QueryCatalogController.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>Low-Code 디자이너용 쿼리 카탈로그(Phase 5a) — 호스트 IQueryRegistry의 등록 쿼리를
/// {id, isWrite, requiredPermission}로 노출(SQL 비노출). 디자이너 드롭다운 단일 출처. sys:manage 수동 검사.</summary>
[ApiController]
[Route("api/v1/sys/queries")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class QueryCatalogController : ControllerBase
{
    private readonly IQueryRegistry _registry;

    public QueryCatalogController(IQueryRegistry registry) => _registry = registry;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QueryDescriptor>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult List()
    {
        if (!HasPermission(Permissions.SysManage)) return Forbid();
        var items = new List<QueryDescriptor>();
        foreach (var id in _registry.Ids)
            if (_registry.TryGet(id, out var def) && def is not null)
                items.Add(new QueryDescriptor(def.Id, def.IsWrite, def.RequiredPermission));
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Ok(items);
    }

    private bool HasPermission(string permission) =>
        User.FindAll(Permissions.ClaimType)
            .Any(c => c.Value == Permissions.All || string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record QueryDescriptor(string Id, bool IsWrite, string? RequiredPermission);
```
(`Permissions.SysManage`="sys:manage" 존재 확인 — Permissions.cs에 있음.)

- [ ] **Step 4: 빌드 + ServerTests** — `dotnet build NexaOne.sln -c Debug`(0 error/no-new-warn). `dotnet test test/NexaOne.ServerTests`(39 + 신규는 Task 4). 호스트 기동 비회귀(provider 교체가 /meta·기존 테스트 안 깸 — modules OFF 테스트에서 GatewayProvider가 DB 빈테이블이면 시드 폴백).

- [ ] **Step 5: Commit** — `git add src/00.Main/NexaOne.Server/Gateway/GatewayScreenDefinitionProvider.cs src/00.Main/NexaOne.Server/Gateway/QueryCatalogController.cs src/00.Main/NexaOne.Server/Program.cs`; 메시지 `feat(server): DB-backed 화면정의 provider(게이트웨이) + 호스트 QueryCatalog(Phase 5a)`.

---

## Task 4: 테스트 — 영속 E2E + 카탈로그 (SQLite, 자동)

**Files:** Create `test/NexaOne.ServerTests/ScreenDefinitionPersistenceTests.cs`.

- [ ] **Step 1: 테스트**(modules OFF + SQLite + JWT, GatewayMdmE2ETests 팩토리 패턴). Fact:
  - `Upsert_then_get_roundtrips_layout_definition`: sys:manage JWT로 `POST /api/v1/command/SYS.UpsertScreenDefinition` body `{uiId:"T_LAYOUT_<guid>", title:"t", definitionJson: <Layout 포함 ScreenDefinition JSON>}` → 200. 그 후 같은 호스트의 `GatewayScreenDefinitionProvider`(또는 /meta 로드)로 정의를 되읽어 Layout이 보존됐는지 검증. **provider를 직접 검증**하려면 `factory.Services.GetRequiredService<IScreenDefinitionProvider>().GetAsync(uiId)` → ScreenDefinition.Layout is not null + 기대 구조. (definitionJson은 `ScreenDefinitionJson.Serialize(new ScreenDefinition(uiId,"t",[],Layout: new SectionNode{...}))`로 생성.)
  - `Upsert_without_sys_manage_is_forbidden`: 권한 `fdc:read` JWT로 `POST /command/SYS.UpsertScreenDefinition` → 403(write 쿼리 requiredPermission 게이트).
  - `Query_catalog_lists_registered_queries`: sys:manage JWT로 `GET /api/v1/sys/queries` → 200, 응답에 `SYS.UpsertScreenDefinition`(IsWrite true, RequiredPermission "sys:manage")·`MDM.PlantList` 포함. 권한 없는 JWT → 403.
  - `Db_definition_overrides_seed`: 시드 UiId(DEMO_GRID)와 다른 새 uiId를 upsert 후 provider.GetAsync가 DB 정의 반환; 미존재 uiId는 시드/ null.
- [ ] **Step 2: 전체** — `dotnet build NexaOne.sln -c Debug`; `dotnet test test/NexaOne.ServerTests`(39+신규); `dotnet test test/NexaOne.IntegrationTests`(289+1skip); `dotnet test test/NexaOne.UnitTests`(1090). 전부 그린. 실 제품 버그 발견 시 BLOCKED 보고(테스트 약화 금지).
- [ ] **Step 3: Commit** — `git add test/NexaOne.ServerTests/ScreenDefinitionPersistenceTests.cs`; 메시지 `test(server): 화면정의 영속 E2E(upsert→provider 로드·403·카탈로그)(Phase 5a)`.

---

## Self-Review
- 명명쿼리(영속)→T1; RCL GetAsync 가산(비파괴)→T2; 호스트 DB provider+카탈로그→T3; E2E→T4. ✅
- 게이트웨이-최대(ADR-001) 유지 — 영속을 명명쿼리로, 카탈로그는 IQueryRegistry. write는 sys:manage 게이트. ADR-006 무변경(RCL Default-ALC). 
- 타입 일관성: `IScreenDefinitionProvider`(+GetAsync), `ScreenDefinitionJson.Serialize/Deserialize`, `ScreenDefinition`/LayoutNode, `IRuleDispatcher.QueryAsync→IReadOnlyList<Dictionary<string,object?>>`, `IQueryRegistry.TryGet/Ids`, `Permissions.SysManage/ClaimType/All`, SYS_SCREEN_DEFINITION 컬럼(UI_ID/TITLE/DEFINITION_JSON/audit) — 실제 코드 확인값.
- 미해결: 동기 Get sync-over-async(ASP.NET 안전, /meta는 GetAsync); 저장 후 호스트 캐시 즉시성(다음 GetAsync가 DB read라 일관 — InMemory Register 캐시는 보조). Phase 5b(GrapesJS UI)가 이 영속/카탈로그를 소비.
