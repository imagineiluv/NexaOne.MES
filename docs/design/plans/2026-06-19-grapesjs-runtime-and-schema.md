# GrapesJS 화면 디자이너 — 스키마 + Blazor 런타임 구현 계획 (Phase 0 + 1a)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** GrapesJS WYSIWYG 디자이너의 산출물(렌더러 중립적 레이아웃 트리)을 저장·복원하는 계약과 그것을 렌더하는 Blazor `/meta` 런타임을, 기존 평면 정의·런타임과 완전 하위호환으로 추가한다.

**Architecture:** `ScreenDefinition`에 선택적 `Layout`(init-only 다형 노드 트리)을 맨 끝 매개변수로 확장한다. `ScreenDefinitionJson`은 layout을 2단계로 분리 파싱해 layout이 깨져도 평면 경로로 폴백한다(화면 전체 백지화 방지). 신규 재귀 `LayoutRenderer`가 트리를 렌더하되 그리드/폼은 기존 `MetaGridRenderer`/`MetaFormRenderer`를 재사용하고, `MetaScreen`이 단일 오케스트레이터로서 멀티 read 쿼리를 수집·실행해 위젯별 결과맵을 내려보낸다. 디자이너 드롭다운용 쿼리 카탈로그 엔드포인트를 인터페이스 변경 없이 추가한다.

**Tech Stack:** C# / .NET 8, ASP.NET Core 8, Blazor Server, System.Text.Json(다형 직렬화), xUnit + FluentAssertions + Moq + bUnit(단위), WebApplicationFactory(통합).

**Scope:** 이 계획은 Phase 0(백엔드 카탈로그 + 스키마 + 직렬화/파싱격리) + Phase 1a(Blazor `LayoutRenderer` + `MetaScreen` 오케스트레이션)를 다룬다. Phase 1b(React SPA GrapesJS 에디터)는 이 계약이 입증된 뒤 별도 계획으로 작성한다. 승인 스펙: [specs/2026-06-19-grapesjs-screen-designer-design.md](../specs/2026-06-19-grapesjs-screen-designer-design.md).

---

## 파일 구조

생성/수정 대상과 책임:

- **수정** `src/01.Web/NexaOne.Web/Services/Meta/ScreenMetadata.cs` — `ScreenDefinition`에 `Layout` 추가 + `LayoutNode` 계층(컨테이너/위젯 record) 정의. 단일 책임: 메타데이터 화면 모델.
- **수정** `src/01.Web/NexaOne.Web/Services/Meta/ScreenDefinitionJson.cs` — layout 분리 파싱·폴백·MaxDepth. 단일 책임: 정의 ↔ JSON.
- **생성** `src/01.Web/NexaOne.Web/Components/Meta/LayoutRenderer.razor` — 재귀 레이아웃 렌더러. 단일 책임: 레이아웃 트리 → DOM(기존 렌더러 위임).
- **수정** `src/01.Web/NexaOne.Web/Pages/Meta/MetaScreen.razor` — Layout 분기 + 멀티 read 오케스트레이션 + 레이아웃 기반 검증 + 명령 처리.
- **생성** `src/02.Backend/NexaOne.API/Controllers/QueryCatalogController.cs` — `GET /api/v1/sys/queries`(디자이너 드롭다운/UX 권한 출처). 단일 책임: 쿼리 카탈로그 조회.
- **생성** `test/NexaOne.UnitTests/Web/LayoutSchemaJsonTests.cs` — 레이아웃 직렬화/파싱격리 단위 테스트.
- **생성** `test/NexaOne.UnitTests/Web/LayoutRendererTests.cs` — `LayoutRenderer` bUnit 테스트.
- **수정** `test/NexaOne.UnitTests/Web/MetaScreenTests.cs` — 멀티 read·레이아웃 검증·명령 테스트 추가.
- **생성** `test/NexaOne.IntegrationTests/SYS/QueryCatalogIntegrationTests.cs` — 카탈로그 엔드포인트 통합 테스트.

---

## Phase 0 — 백엔드 카탈로그 + 스키마 + 직렬화

### Task 1: 쿼리 카탈로그 엔드포인트 (GET /api/v1/sys/queries)

디자이너가 queryId/command 드롭다운을 채우고 UX 권한 비활성을 유도할 출처. **인터페이스 변경 없이** 기존 `IQueryRegistry.Ids` + `TryGet`로 구현한다(스펙의 "IQueryRegistry 확장"보다 위험이 낮은 정련).

**Files:**
- Create: `src/02.Backend/NexaOne.API/Controllers/QueryCatalogController.cs`
- Test: `test/NexaOne.IntegrationTests/SYS/QueryCatalogIntegrationTests.cs`

- [ ] **Step 1: 실패 통합 테스트 작성**

```csharp
using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>쿼리 카탈로그 엔드포인트 — 디자이너 드롭다운/UX 권한의 출처.
/// 등록된 쿼리를 {id, isWrite, requiredPermission}로 노출하되 SQL은 절대 노출하지 않는다.
/// 관리 권한(perm:sys:manage)으로만 접근 가능.</summary>
public sealed class QueryCatalogIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public QueryCatalogIntegrationTests(TestApiFactory factory) => _factory = factory;

    private sealed record QueryDescriptorDto(string Id, bool IsWrite, string? RequiredPermission);

    [Fact]
    public async Task Lists_registered_queries_with_kind_and_permission_but_no_sql()
    {
        var client = _factory.CreateAuthenticatedClient("sys:manage");

        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("SELECT", "SQL 본문은 카탈로그에 노출되면 안 된다");
        body.Should().NotContain("INSERT");

        var items = await res.Content.ReadFromJsonAsync<List<QueryDescriptorDto>>();
        items.Should().NotBeNull();
        items!.Should().Contain(d => d.Id == "MDM.PlantList" && d.IsWrite == false);
        items.Should().Contain(d => d.Id == "MDM.CreatePlant" && d.IsWrite == true && d.RequiredPermission == "mdm:manage");
    }

    [Fact]
    public async Task Forbids_without_sys_manage_permission()
    {
        var client = _factory.CreateAuthenticatedClient("fdc:read");   // sys:manage 없음
        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test test/NexaOne.IntegrationTests --filter QueryCatalogIntegrationTests`
Expected: FAIL — 404 NotFound(엔드포인트 미존재)로 첫 단언 실패.

- [ ] **Step 3: 컨트롤러 구현**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Application.Query;
using NexaOne.Common;

namespace NexaOne.API.Controllers;

/// <summary>
/// Low-Code 디자이너용 쿼리 카탈로그. 파일 기반 레지스트리의 등록 쿼리를 {id, isWrite, requiredPermission}로
/// 노출한다(SQL 본문은 노출하지 않음 — 주입/정보유출 방지). 디자이너가 그리드/명령 바인딩 드롭다운을 채우고,
/// 위젯의 UX 권한 비활성을 쿼리의 실제 requiredPermission에서 유도하는 단일 출처. 관리 권한 전용(ADR-003).
/// </summary>
[ApiController]
[Route("api/v1/sys/queries")]
[Authorize(Policy = "perm:sys:manage")]
[ProducesErrorResponseType(typeof(Error))]
public class QueryCatalogController(IQueryRegistry registry) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QueryDescriptor>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult List()
    {
        var items = new List<QueryDescriptor>();
        foreach (var id in registry.Ids)
            if (registry.TryGet(id, out var def) && def is not null)
                items.Add(new QueryDescriptor(def.Id, def.IsWrite, def.RequiredPermission));
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Ok(items);
    }
}

/// <summary>디자이너에 노출하는 안전한 쿼리 서술자(SQL 제외).</summary>
public sealed record QueryDescriptor(string Id, bool IsWrite, string? RequiredPermission);
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test test/NexaOne.IntegrationTests --filter QueryCatalogIntegrationTests`
Expected: PASS (2 tests).

- [ ] **Step 5: 커밋**

```bash
git add src/02.Backend/NexaOne.API/Controllers/QueryCatalogController.cs test/NexaOne.IntegrationTests/SYS/QueryCatalogIntegrationTests.cs
git commit -m "feat(api): Low-Code 디자이너용 쿼리 카탈로그 엔드포인트(GET /sys/queries, SQL 비노출)"
```

---

### Task 2: 레이아웃 스키마 (init-only 다형 노드)

**Files:**
- Modify: `src/01.Web/NexaOne.Web/Services/Meta/ScreenMetadata.cs`
- Test: `test/NexaOne.UnitTests/Web/LayoutSchemaJsonTests.cs` (Task 3에서 작성)

- [ ] **Step 1: `ScreenMetadata.cs` 상단에 using 추가**

파일 맨 위(`namespace` 선언 위)에 추가:

```csharp
using System.Text.Json.Serialization;
```

- [ ] **Step 2: `ScreenDefinition`에 `Layout` 매개변수 추가**

`src/01.Web/NexaOne.Web/Services/Meta/ScreenMetadata.cs:30-36`의 record를 아래로 교체(맨 끝에 `Layout` 추가):

```csharp
public sealed record ScreenDefinition(
    string UiId,
    string Title,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<GridColumnDefinition>? Columns = null,
    string? QueryId = null,
    string? SaveQueryId = null,
    LayoutNode? Layout = null);   // null => 기존 평면 렌더(하위호환). 비null => LayoutRenderer가 렌더.
```

- [ ] **Step 3: 레이아웃 노드 계층 추가**

같은 파일 끝에 추가. **모든 노드는 init-only 프로퍼티 record**(매개변수 없는 생성자) — STJ 다형 역직렬화가 `"kind"` 위치와 무관하게 동작하게 한다(외부 직렬화기의 키 순서 비의존).

```csharp
/// <summary>
/// 레이아웃 트리 노드(Low-Code WYSIWYG). 컨테이너(Section/Row/Column)는 Children을, 위젯은 바인딩을 가진다.
/// discriminator는 "kind"(camelCase 안전). init-only 프로퍼티라 역직렬화가 속성 순서에 의존하지 않는다.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SectionNode), "section")]
[JsonDerivedType(typeof(RowNode), "row")]
[JsonDerivedType(typeof(ColumnNode), "column")]
[JsonDerivedType(typeof(GridWidget), "grid")]
[JsonDerivedType(typeof(FormWidget), "form")]
[JsonDerivedType(typeof(FieldWidget), "field")]
[JsonDerivedType(typeof(ButtonWidget), "commandButton")]
[JsonDerivedType(typeof(TextWidget), "text")]
public abstract record LayoutNode
{
    /// <summary>GrapesJS 컴포넌트 id == 노드 id(편집 라운드트립 정체성).</summary>
    public string? Id { get; init; }
    /// <summary>UX 힌트 전용 권한(ADR-003 module:action). 서버가 실제 게이트 — 런타임은 표시/비활성만.</summary>
    public string? RequiredPermission { get; init; }
}

// 컨테이너
public sealed record SectionNode : LayoutNode { public string? Title { get; init; } public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record RowNode : LayoutNode { public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record ColumnNode : LayoutNode { public int Span { get; init; } = 12; public IReadOnlyList<LayoutNode>? Children { get; init; } }

// 위젯 — 바인딩을 위젯별로 분리(잘못된 조합을 표현 불가능하게)
public sealed record GridWidget : LayoutNode { public string? QueryId { get; init; } public IReadOnlyList<GridColumnDefinition>? Columns { get; init; } }
public sealed record FormWidget : LayoutNode { public string? SaveQueryId { get; init; } public IReadOnlyList<FieldWidget>? Fields { get; init; } }
public sealed record FieldWidget : LayoutNode { public string? FieldKey { get; init; } public FieldDefinition? Field { get; init; } }
public sealed record ButtonWidget : LayoutNode { public string Label { get; init; } = ""; public string? Command { get; init; } }
public sealed record TextWidget : LayoutNode { public string Text { get; init; } = ""; public bool IsLabel { get; init; } }
```

- [ ] **Step 4: 컴파일 확인**

Run: `dotnet build src/01.Web/NexaOne.Web`
Expected: 성공(기존 호출부는 `Layout` 기본값 null로 그대로 컴파일).

- [ ] **Step 5: 커밋**

```bash
git add src/01.Web/NexaOne.Web/Services/Meta/ScreenMetadata.cs
git commit -m "feat(web): ScreenDefinition에 선택적 Layout 트리(init-only 다형 노드) 확장 — 하위호환"
```

---

### Task 3: 직렬화 라운드트립 + 레이아웃 분리 파싱/폴백

layout 파싱 실패(미지 kind·과대 깊이)가 화면 전체를 null로 만들지 않도록 평면 정의와 분리 파싱한다.

**Files:**
- Modify: `src/01.Web/NexaOne.Web/Services/Meta/ScreenDefinitionJson.cs`
- Test: `test/NexaOne.UnitTests/Web/LayoutSchemaJsonTests.cs`

- [ ] **Step 1: 실패 단위 테스트 작성**

```csharp
using System.Text.Json;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>레이아웃 트리 직렬화 라운드트립 + 분리 파싱/폴백.
/// 핵심 불변식: layout이 깨져도(미지 kind·과대 깊이) 화면 전체가 null이 되지 않고 평면 경로로 폴백한다.</summary>
public sealed class LayoutSchemaJsonTests
{
    private static ScreenDefinition Sample() => new(
        "PLANT_MGMT", "공장 관리",
        new FieldDefinition[] { new("plantId", "공장 ID", FieldType.Text, Required: true) },
        Layout: new SectionNode
        {
            Id = "sec-root", Title = "공장 마스터",
            Children = new LayoutNode[]
            {
                new RowNode { Id = "row-1", Children = new LayoutNode[]
                {
                    new ColumnNode { Span = 7, Children = new LayoutNode[]
                    {
                        new GridWidget { Id = "grid-plants", QueryId = "MDM.PlantList",
                            Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장 ID") } },
                    } },
                    new ColumnNode { Span = 5, Children = new LayoutNode[]
                    {
                        new FormWidget { Id = "form-plant", SaveQueryId = "MDM.CreatePlant",
                            Fields = new FieldWidget[] { new() { FieldKey = "plantId",
                                Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) } } },
                        new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant", RequiredPermission = "mdm:manage" },
                    } },
                } },
            },
        });

    [Fact]
    public void Roundtrips_full_layout_tree_losslessly()
    {
        var json = ScreenDefinitionJson.Serialize(Sample());
        var back = ScreenDefinitionJson.Deserialize(json);

        back.Should().NotBeNull();
        back!.Layout.Should().BeOfType<SectionNode>();
        var section = (SectionNode)back.Layout!;
        section.Title.Should().Be("공장 마스터");
        var row = (RowNode)section.Children![0];
        var col0 = (ColumnNode)row.Children![0];
        col0.Span.Should().Be(7);
        var grid = (GridWidget)col0.Children![0];
        grid.QueryId.Should().Be("MDM.PlantList");
        grid.Id.Should().Be("grid-plants", "노드 Id가 라운드트립돼야 한다");
        var col1 = (ColumnNode)row.Children![1];
        var btn = (ButtonWidget)col1.Children![1];
        btn.Command.Should().Be("MDM.CreatePlant");
        btn.RequiredPermission.Should().Be("mdm:manage");
    }

    [Fact]
    public void Null_layout_roundtrips_to_null()
    {
        var def = new ScreenDefinition("S", "T",
            new FieldDefinition[] { new("a", "A") });
        var back = ScreenDefinitionJson.Deserialize(ScreenDefinitionJson.Serialize(def));
        back.Should().NotBeNull();
        back!.Layout.Should().BeNull();
    }

    [Fact]
    public void Deserializes_layout_when_kind_is_not_first_property()
    {
        // 외부(SPA/포매터)가 kind를 첫 속성이 아닌 곳에 둔 경우에도 init-only라 복원돼야 한다.
        const string json = """
        {
          "uiId": "S", "title": "T",
          "fields": [],
          "layout": { "title": "섹션", "id": "s1", "kind": "section", "children": [
            { "children": [], "kind": "row" }
          ] }
        }
        """;
        var back = ScreenDefinitionJson.Deserialize(json);
        back.Should().NotBeNull();
        back!.Layout.Should().BeOfType<SectionNode>();
        ((SectionNode)back.Layout!).Title.Should().Be("섹션");
    }

    [Fact]
    public void Unknown_kind_falls_back_to_flat_definition_not_whole_null()
    {
        // 미래 디자이너가 추가한 미지 위젯(carousel) — 전체 화면이 null이 되면 안 되고 평면 정의는 살아야 한다.
        const string json = """
        {
          "uiId": "S", "title": "T",
          "fields": [ { "key": "a", "label": "A", "type": "Text" } ],
          "layout": { "kind": "carousel", "id": "x" }
        }
        """;
        var back = ScreenDefinitionJson.Deserialize(json);
        back.Should().NotBeNull("미지 kind는 layout만 폴백시키고 평면 정의는 보존돼야 한다");
        back!.UiId.Should().Be("S");
        back.Fields.Should().ContainSingle();
        back.Layout.Should().BeNull("미지 kind layout은 null로 폴백된다");
    }

    [Fact]
    public void Over_max_depth_layout_falls_back_to_flat_definition()
    {
        // MaxDepth를 넘는 깊은 중첩 — JsonException을 layout 범위로 격리해 평면 정의로 폴백.
        var sb = new System.Text.StringBuilder();
        sb.Append("""{ "uiId":"S","title":"T","fields":[],"layout": """);
        const int depth = 80;
        for (var i = 0; i < depth; i++) sb.Append("""{ "kind":"section","children":[""");
        for (var i = 0; i < depth; i++) sb.Append(i == 0 ? "]}" : "]}");
        sb.Append(" }");
        var back = ScreenDefinitionJson.Deserialize(sb.ToString());
        back.Should().NotBeNull();
        back!.Layout.Should().BeNull("과대 깊이 layout은 폴백, 평면 정의는 보존");
    }

    [Fact]
    public void Invalid_json_still_returns_null()
        => ScreenDefinitionJson.Deserialize("{ not valid").Should().BeNull();
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter LayoutSchemaJsonTests`
Expected: FAIL — 미지 kind/과대 깊이 케이스가 전체 null을 반환(현재 `Deserialize`가 JsonException을 통째로 삼킴).

- [ ] **Step 3: `ScreenDefinitionJson.cs` 분리 파싱 구현**

파일 전체를 아래로 교체:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NexaOne.Web.Services.Meta;

/// <summary>ScreenDefinition ↔ JSON 직렬화. DB 저장소(SYS_SCREEN_DEFINITION)·내보내기·디자이너에서 사용.
/// FieldType은 문자열로 직렬화한다. Layout(레이아웃 트리)은 평면 정의와 분리 파싱해, layout이 깨져도
/// (미지 kind·과대 깊이) 화면 전체가 null이 되지 않고 평면 경로로 폴백한다.</summary>
public static class ScreenDefinitionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // layout 서브트리 전용 옵션 — 깊이를 명시 제한해 비정상 중첩을 layout 범위 예외로 격리한다.
    private static readonly JsonSerializerOptions LayoutOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
    };

    public static string Serialize(ScreenDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static ScreenDefinition? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // 외부 파싱은 깊이를 넉넉히 허용하고(전체 문서가 layout 격리 단계까지 도달하게), 깊이 제한은
        // layout 하위 역직렬화(LayoutOptions.MaxDepth)에서만 강제한다 — 그래야 과대 깊이 layout이
        // 전체 파싱을 죽이지 않고 layout 범위로만 격리돼 평면 정의가 보존된다.
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;

        // 1) layout 서브트리를 떼어 별도 파싱 — 실패하면 layout만 폴백(null), 평면 정의는 보존.
        LayoutNode? layout = null;
        if (obj.TryGetPropertyValue("layout", out var layoutNode) && layoutNode is not null)
        {
            try { layout = layoutNode.Deserialize<LayoutNode>(LayoutOptions); }
            catch (JsonException) { layout = null; }            // 미지 kind·과대 깊이 등 격리
            catch (NotSupportedException) { layout = null; }    // 다형 생성 불가 등 격리
        }

        // 2) 평면 정의는 layout 키를 제거한 본문으로 복원(전체 역직렬화가 layout 오류로 죽지 않게).
        obj.Remove("layout");
        ScreenDefinition? flat;
        try { flat = obj.Deserialize<ScreenDefinition>(Options); }
        catch (JsonException) { return null; }
        if (flat is null) return null;

        return flat with { Layout = layout };
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter LayoutSchemaJsonTests`
Expected: PASS (6 tests).

- [ ] **Step 5: 기존 직렬화 테스트 회귀 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter ScreenDefinitionJsonTests`
Expected: PASS — 기존 라운드트립/무효 JSON 테스트 무변경 통과(layout 없는 정의는 평면 경로로 동일 동작).

- [ ] **Step 6: 커밋**

```bash
git add src/01.Web/NexaOne.Web/Services/Meta/ScreenDefinitionJson.cs test/NexaOne.UnitTests/Web/LayoutSchemaJsonTests.cs
git commit -m "feat(web): 레이아웃 분리 파싱/폴백(미지 kind·과대 깊이 격리, MaxDepth) + 라운드트립 테스트"
```

---

## Phase 1a — Blazor 런타임

### Task 4: `LayoutRenderer` 재귀 컴포넌트

레이아웃 트리를 렌더한다. Section/Row/Column/Text는 직접, Grid/Form/Field/CommandButton은 위임/콜백. 데이터·콜백은 부모(MetaScreen)가 주입한다(렌더러는 dumb).

**Files:**
- Create: `src/01.Web/NexaOne.Web/Components/Meta/LayoutRenderer.razor`
- Test: `test/NexaOne.UnitTests/Web/LayoutRendererTests.cs`

- [ ] **Step 1: 실패 bUnit 테스트 작성**

```csharp
using Bunit;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>재귀 레이아웃 렌더러 — 컨테이너 구조를 그리고, 그리드는 위젯별 결과맵의 행을,
/// 폼/필드는 공유 Model을, 명령 버튼은 콜백을 연결하는지 검증한다.</summary>
public sealed class LayoutRendererTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>> NoResults
        = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>();

    private static IRenderedComponent<LayoutRenderer> Render(
        TestContext ctx, LayoutNode layout, Dictionary<string, object?>? model = null,
        IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>>? results = null,
        Action<string>? onCommand = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.RenderComponent<LayoutRenderer>(p => p
            .Add(c => c.Node, layout)
            .Add(c => c.Model, model ?? new Dictionary<string, object?>())
            .Add(c => c.QueryResults, results ?? NoResults)
            .Add(c => c.OnCommand, onCommand is null
                ? default
                : Microsoft.AspNetCore.Components.EventCallback.Factory.Create(new object(), onCommand)));
    }

    [Fact]
    public void Renders_section_row_column_structure_with_text()
    {
        using var ctx = new TestContext();
        var layout = new SectionNode
        {
            Title = "마스터",
            Children = new LayoutNode[]
            {
                new RowNode { Children = new LayoutNode[]
                {
                    new ColumnNode { Span = 6, Children = new LayoutNode[] { new TextWidget { Text = "왼쪽" } } },
                    new ColumnNode { Span = 6, Children = new LayoutNode[] { new TextWidget { Text = "오른쪽", IsLabel = true } } },
                } },
            },
        };

        var cut = Render(ctx, layout);

        cut.Markup.Should().Contain("마스터").And.Contain("왼쪽").And.Contain("오른쪽");
        cut.FindAll(".layout-column").Count.Should().Be(2, "Row 아래 Column 2개가 렌더돼야 한다");
    }

    [Fact]
    public void Grid_widget_renders_rows_from_query_result_map()
    {
        using var ctx = new TestContext();
        var layout = new GridWidget
        {
            QueryId = "MDM.PlantList",
            Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명") },
        };
        var results = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>
        {
            ["MDM.PlantList"] = new List<Dictionary<string, object?>>
            {
                new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One" },
            },
        };

        var cut = Render(ctx, layout, results: results);

        cut.Markup.Should().Contain("공장 ID").And.Contain("Plant One");
        cut.FindAll("tbody tr").Count.Should().Be(1);
    }

    [Fact]
    public void Command_button_invokes_OnCommand_with_command_id()
    {
        using var ctx = new TestContext();
        string? invoked = null;
        var layout = new ButtonWidget { Label = "승인", Command = "SYS.Approve" };

        var cut = Render(ctx, layout, onCommand: c => invoked = c);
        cut.Find("button").Click();

        invoked.Should().Be("SYS.Approve", "명령 버튼은 OnCommand에 command id를 전달해야 한다");
    }

    [Fact]
    public void Field_widget_two_way_binds_shared_model()
    {
        using var ctx = new TestContext();
        var model = new Dictionary<string, object?>();
        var layout = new FormWidget
        {
            SaveQueryId = "MDM.CreatePlant",
            Fields = new FieldWidget[]
            {
                new() { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text) },
            },
        };

        var cut = Render(ctx, layout, model: model);
        cut.Find("input").Change("플랜트1");

        model.Should().ContainKey("plantName");
        model["plantName"]!.ToString().Should().Be("플랜트1");
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter LayoutRendererTests`
Expected: FAIL — `LayoutRenderer` 컴포넌트 미존재(컴파일 실패).

- [ ] **Step 3: `LayoutRenderer.razor` 구현**

```razor
@* Low-Code 레이아웃 트리 렌더러(재귀). Section/Row/Column/Text는 직접, Grid/Form/Field는 기존 렌더러 위임,
   CommandButton은 콜백. 데이터(QueryResults)·콜백(OnCommand)·공유 Model은 부모(MetaScreen)가 주입한다. *@
@using NexaOne.Web.Services.Meta

@switch (Node)
{
    case SectionNode s:
        <section class="layout-section">
            @if (!string.IsNullOrWhiteSpace(s.Title)) { <h3 class="layout-section-title">@s.Title</h3> }
            @RenderChildren(s.Children)
        </section>
        break;

    case RowNode r:
        <div class="layout-row" style="display:flex; flex-wrap:wrap; gap:1rem;">
            @RenderChildren(r.Children)
        </div>
        break;

    case ColumnNode c:
        <div class="layout-column" style="@ColumnStyle(c.Span)">
            @RenderChildren(c.Children)
        </div>
        break;

    case GridWidget g:
        <MetaGridRenderer Columns="g.Columns ?? Array.Empty<GridColumnDefinition>()" Rows="RowsFor(g.QueryId)" />
        break;

    case FormWidget f:
        <MetaFormRenderer Definition="FormDefinition(f)" Model="Model" ModelChanged="OnModelChanged" />
        break;

    case FieldWidget fw:
        <MetaFormRenderer Definition="SingleFieldDefinition(fw)" Model="Model" ModelChanged="OnModelChanged" />
        break;

    case ButtonWidget b:
        <button class="layout-command" @onclick="() => OnCommand.InvokeAsync(b.Command)" disabled="@string.IsNullOrWhiteSpace(b.Command)">@b.Label</button>
        break;

    case TextWidget t:
        @if (t.IsLabel) { <label class="layout-text">@t.Text</label> }
        else { <span class="layout-text">@t.Text</span> }
        break;
}

@code {
    [Parameter, EditorRequired] public LayoutNode Node { get; set; } = default!;
    [Parameter] public Dictionary<string, object?> Model { get; set; } = new();
    [Parameter] public EventCallback<Dictionary<string, object?>> ModelChanged { get; set; }
    [Parameter] public IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>> QueryResults { get; set; }
        = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>();
    [Parameter] public EventCallback<string> OnCommand { get; set; }

    private RenderFragment RenderChildren(IReadOnlyList<LayoutNode>? children) => builder =>
    {
        if (children is null) return;
        var seq = 0;
        foreach (var child in children)
        {
            builder.OpenComponent<LayoutRenderer>(seq++);
            builder.AddAttribute(seq++, nameof(Node), child);
            builder.AddAttribute(seq++, nameof(Model), Model);
            builder.AddAttribute(seq++, nameof(ModelChanged), ModelChanged);
            builder.AddAttribute(seq++, nameof(QueryResults), QueryResults);
            builder.AddAttribute(seq++, nameof(OnCommand), OnCommand);
            builder.CloseComponent();
        }
    };

    // null = 미실행(MetaGridRenderer가 안내 문구), 그 외 = 행. queryId 미바인딩/결과맵 부재는 null.
    private IReadOnlyList<Dictionary<string, object?>>? RowsFor(string? queryId)
        => queryId is not null && QueryResults.TryGetValue(queryId, out var rows) ? rows : null;

    private static string ColumnStyle(int span)
    {
        var pct = Math.Clamp(span, 1, 12) / 12d * 100d;
        return $"flex:0 0 calc({pct:0.##}% - 1rem); min-width:0;";
    }

    // 폼 위젯의 FieldWidget들을 인메모리 ScreenDefinition으로 합성해 기존 MetaFormRenderer를 재사용한다.
    private static ScreenDefinition FormDefinition(FormWidget f)
        => new(string.Empty, string.Empty, (f.Fields ?? Array.Empty<FieldWidget>()).Select(ToField).ToList());

    private static ScreenDefinition SingleFieldDefinition(FieldWidget fw)
        => new(string.Empty, string.Empty, new[] { ToField(fw) });

    private static FieldDefinition ToField(FieldWidget fw)
        => fw.Field ?? new FieldDefinition(fw.FieldKey ?? string.Empty, fw.FieldKey ?? string.Empty);

    private Task OnModelChanged(Dictionary<string, object?> m) => ModelChanged.InvokeAsync(m);
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter LayoutRendererTests`
Expected: PASS (4 tests).

- [ ] **Step 5: 커밋**

```bash
git add src/01.Web/NexaOne.Web/Components/Meta/LayoutRenderer.razor test/NexaOne.UnitTests/Web/LayoutRendererTests.cs
git commit -m "feat(web): 재귀 LayoutRenderer(컨테이너 직접·그리드/폼 위임·명령 콜백) + bUnit"
```

---

### Task 5: `MetaScreen` 레이아웃 분기 + 멀티 read 오케스트레이션

`MetaScreen`이 Layout이 있으면 트리에서 distinct queryId를 수집·각 1회 실행해 결과맵을 만들고 `LayoutRenderer`에 위임한다. 검증은 트리를 걸어 수행(평면 Fields 미러링 금지). 명령은 `ExecuteCommandAsync`로 처리.

**Files:**
- Modify: `src/01.Web/NexaOne.Web/Pages/Meta/MetaScreen.razor`
- Test: `test/NexaOne.UnitTests/Web/MetaScreenTests.cs`

- [ ] **Step 1: 실패 bUnit 테스트 추가**

`test/NexaOne.UnitTests/Web/MetaScreenTests.cs`의 마지막 `}` 직전에 추가:

```csharp
    [Fact]
    public void Layout_executes_each_distinct_read_query_once_and_renders_grids()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // 레이아웃에 그리드 2개(서로 다른 queryId) — 각 1회 실행, 각자 결과로 렌더.
        var def = new ScreenDefinition("LAY1", "대시보드",
            Array.Empty<FieldDefinition>(),
            Layout: new RowNode
            {
                Children = new LayoutNode[]
                {
                    new GridWidget { QueryId = "Q.Plants", Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장") } },
                    new GridWidget { QueryId = "Q.Lines", Columns = new GridColumnDefinition[] { new("LINE_ID", "라인") } },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["PLANT_ID"] = "P-1" } });
        api.Setup(a => a.ExecuteQueryAsync("Q.Lines", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["LINE_ID"] = "L-1" } });

        ctx.Services.AddSingleton(Provider("LAY1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("P-1").And.Contain("L-1");
            cut.FindAll("tbody tr").Count.Should().Be(2, "그리드 2개가 각자 1행씩 렌더");
        }, TimeSpan.FromSeconds(2));

        api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.ExecuteQueryAsync("Q.Lines", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Layout_command_button_posts_shared_model_to_command_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("LAY2", "등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FieldWidget { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                    new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant" },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("LAY2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY2"));

        cut.Find("input").Change("플랜트1");
        cut.Find("button.layout-command").Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Layout_validation_blocks_command_when_required_field_empty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("LAY3", "등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FieldWidget { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                    new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant" },
                },
            });

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("LAY3", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY3"));

        cut.Find("button.layout-command").Click();   // 필수 필드 비움

        cut.Markup.Should().Contain("필수");
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "레이아웃 검증 실패 시 명령 게이트웨이를 호출하지 않아야 한다");
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter MetaScreenTests`
Expected: FAIL — Layout 분기/오케스트레이션 미존재로 신규 3 테스트 실패(기존 4 테스트는 통과).

- [ ] **Step 3: `MetaScreen.razor` 수정**

마크업의 닫는 `else { ... }` 블록(현재 16-40행)을 아래로 교체 — Layout 우선 분기 추가:

```razor
else if (_definition.Layout is not null)
{
    @* 레이아웃 트리 화면 — 멀티 read 결과맵을 내려보내고, 명령/검증은 MetaScreen이 오케스트레이션한다. *@
    <LayoutRenderer Node="_definition.Layout" @bind-Model="_model"
                    QueryResults="_queryResults" OnCommand="RunCommand" />

    @if (_errors.Count > 0)
    {
        <ul class="validation-errors">
            @foreach (var err in _errors) { <li>@err</li> }
        </ul>
    }
    @if (_saved) { <span class="save-ok" style="color:#15803d;">저장됨</span> }
}
else
{
    @* 폼(입력 필드)이 정의된 화면만 폼·저장 영역을 렌더한다 — 그리드 전용(조회) 화면은 폼이 비어 있을 수 있다. *@
    @if (_definition.Fields.Count > 0)
    {
        <MetaFormRenderer Definition="_definition" @bind-Model="_model" />

        <div class="meta-actions">
            <button @onclick="Save" disabled="@_saving">@(_saving ? "저장 중…" : "저장")</button>
            @if (_saved) { <span class="save-ok" style="color:#15803d; margin-left:0.5rem;">저장됨</span> }
        </div>

        @if (_errors.Count > 0)
        {
            <ul class="validation-errors">
                @foreach (var err in _errors) { <li>@err</li> }
            </ul>
        }
    }

    @* 그리드(컬럼) 메타가 있으면 명명 쿼리 결과를 런타임 렌더한다 — 손코딩 없이 메타+파일쿼리로 조회 화면 구성. *@
    @if (_definition.Columns is { Count: > 0 })
    {
        <MetaGridRenderer Columns="_definition.Columns" Rows="_rows" />
    }
}
```

`@code` 블록의 필드 선언부(현재 45-50행)에 결과맵 필드를 추가(`_rows` 선언 아래):

```csharp
    // 레이아웃 화면의 위젯별 read 결과(queryId→행). 평면 경로는 _rows를 쓴다.
    private IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>> _queryResults
        = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>();
```

`OnParametersSetAsync`의 그리드 조회 블록(현재 68-72행) 바로 다음(메서드 닫기 `}` 전)에 레이아웃 오케스트레이션을 추가:

```csharp
        // 레이아웃 화면: 트리에서 distinct read 쿼리를 수집해 각 1회 실행 → 위젯별 결과맵.
        if (_definition?.Layout is not null)
        {
            var map = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>(StringComparer.Ordinal);
            foreach (var queryId in CollectQueryIds(_definition.Layout))
                if (!map.ContainsKey(queryId))
                    map[queryId] = await Api.ExecuteQueryAsync(queryId);
            _queryResults = map;
        }
```

`Validate()` 메서드를 레이아웃 인지로 교체(현재 101-111행) — 평면 Fields 또는 레이아웃 트리에서 필드 집합을 단일 출처로 도출:

```csharp
    private List<string> Validate()
    {
        var errors = new List<string>();
        if (_definition is null) return errors;
        var fields = _definition.Layout is not null
            ? CollectFields(_definition.Layout)
            : _definition.Fields;
        foreach (var field in fields.Where(f => f.Required))
        {
            if (!_model.TryGetValue(field.Key, out var v) || string.IsNullOrWhiteSpace(v?.ToString()))
                errors.Add($"{field.Label}은(는) 필수입니다.");
        }
        return errors;
    }
```

`@code` 블록 끝(닫는 `}` 전)에 명령 처리와 트리 순회 헬퍼를 추가:

```csharp
    // 레이아웃 명령 버튼 클릭 — 공유 Model을 명명 쓰기쿼리로 전송한다(검증 통과 시).
    private async Task RunCommand(string? command)
    {
        _saved = false;
        if (string.IsNullOrWhiteSpace(command)) return;
        _errors = Validate();
        if (_errors.Count > 0) return;
        _saved = await Api.ExecuteCommandAsync(command, _model);
        if (!_saved) _errors.Add("저장에 실패했습니다(권한/입력 확인).");
    }

    // 트리에서 그리드 위젯의 distinct read 쿼리 id를 수집(빈/공백 제외).
    private static IEnumerable<string> CollectQueryIds(LayoutNode node)
    {
        switch (node)
        {
            case GridWidget g when !string.IsNullOrWhiteSpace(g.QueryId):
                yield return g.QueryId!;
                break;
            case SectionNode s: foreach (var id in Children(s.Children)) yield return id; break;
            case RowNode r: foreach (var id in Children(r.Children)) yield return id; break;
            case ColumnNode c: foreach (var id in Children(c.Children)) yield return id; break;
        }
        static IEnumerable<string> Children(IReadOnlyList<LayoutNode>? children)
            => (children ?? Array.Empty<LayoutNode>()).SelectMany(CollectQueryIds);
    }

    // 트리에서 검증 대상 필드(FieldWidget)를 수집 — 검증의 단일 출처(평면 Fields 미러링 금지).
    private static IEnumerable<FieldDefinition> CollectFields(LayoutNode node)
    {
        switch (node)
        {
            case FieldWidget fw:
                yield return fw.Field ?? new FieldDefinition(fw.FieldKey ?? string.Empty, fw.FieldKey ?? string.Empty);
                break;
            case FormWidget f:
                foreach (var child in f.Fields ?? Array.Empty<FieldWidget>())
                    foreach (var fd in CollectFields(child)) yield return fd;
                break;
            case SectionNode s: foreach (var fd in Children(s.Children)) yield return fd; break;
            case RowNode r: foreach (var fd in Children(r.Children)) yield return fd; break;
            case ColumnNode c: foreach (var fd in Children(c.Children)) yield return fd; break;
        }
        static IEnumerable<FieldDefinition> Children(IReadOnlyList<LayoutNode>? children)
            => (children ?? Array.Empty<LayoutNode>()).SelectMany(CollectFields);
    }
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test test/NexaOne.UnitTests --filter MetaScreenTests`
Expected: PASS (기존 4 + 신규 3 = 7 tests).

- [ ] **Step 5: 커밋**

```bash
git add src/01.Web/NexaOne.Web/Pages/Meta/MetaScreen.razor test/NexaOne.UnitTests/Web/MetaScreenTests.cs
git commit -m "feat(web): MetaScreen 레이아웃 분기 + 멀티 read 오케스트레이션 + 레이아웃 기반 검증/명령"
```

---

### Task 6: 전체 회귀 + 데모 시드(엔드투엔드 시연)

레이아웃 화면이 런타임에서 실제로 렌더됨을 데모 시드로 입증하고 전체 스위트를 녹색으로 확인한다.

**Files:**
- Modify: `src/01.Web/NexaOne.Web/Services/Meta/InMemoryScreenDefinitionProvider.cs`

- [ ] **Step 1: 레이아웃 데모 시드 추가**

`InMemoryScreenDefinitionProvider` 생성자의 마지막 `Register(...)` 다음(닫는 `}` 전)에 추가 — 기존 그리드/폼을 한 화면에 조합한 레이아웃 데모:

```csharp
        // 데모 시드: 레이아웃(WYSIWYG) 화면 — 좌측 공장 그리드(MDM.PlantList) + 우측 등록 폼/저장 버튼(MDM.CreatePlant)을
        // 한 화면에 조합한다. /meta/DEMO_LAYOUT 이 LayoutRenderer로 렌더되는 레이아웃 런타임 end-to-end 시연.
        Register(new ScreenDefinition("DEMO_LAYOUT", "데모 — 레이아웃(그리드+폼)",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec", Title = "공장 마스터",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g", QueryId = "MDM.PlantList", Columns = new GridColumnDefinition[]
                            {
                                new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f", SaveQueryId = "MDM.CreatePlant", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                            } },
                            new ButtonWidget { Id = "b", Label = "저장", Command = "MDM.CreatePlant", RequiredPermission = "mdm:manage" },
                        } },
                    } },
                },
            }));
```

- [ ] **Step 2: 전체 단위 테스트 회귀 확인**

Run: `dotnet test test/NexaOne.UnitTests`
Expected: PASS — 신규 포함 전체 녹색(레이아웃 시드는 모델 생성만이라 기존 테스트 영향 없음).

- [ ] **Step 3: 전체 통합 테스트 회귀 확인**

Run: `dotnet test test/NexaOne.IntegrationTests`
Expected: PASS — 카탈로그 엔드포인트 포함 녹색(OPC-UA 1건 skip 가능).

- [ ] **Step 4: 커밋**

```bash
git add src/01.Web/NexaOne.Web/Services/Meta/InMemoryScreenDefinitionProvider.cs
git commit -m "feat(web): 레이아웃 런타임 end-to-end 데모 시드(/meta/DEMO_LAYOUT, 그리드+폼+저장)"
```

---

## Self-Review (작성자 점검 결과)

**1. 스펙 커버리지:** §5 스키마=Task 2; §2/§4 init-only·파싱격리·MaxDepth=Task 3; §6 런타임·멀티 read·레이아웃 검증·명령=Task 4·5; §9 카탈로그 엔드포인트=Task 1; §10 하위호환=Task 2(맨끝 선택 매개변수)·Task 3(평면 폴백)·Task 5(평면 분기 보존)·Task 6 회귀. §7 보안: CommandButton은 서버 등록 쿼리만 호출(런타임은 command id만 전달, 서버가 게이트)·XSS는 LayoutRenderer가 `@`-보간만 사용(MarkupString 없음). **유예 명시(무자르기 아님):** §7의 "UX 권한 비활성을 쿼리 실제 requiredPermission에서 유도"는 카탈로그 엔드포인트를 소비하는 Phase 1b(디자이너)에서 구현 — Phase 1a 런타임은 RequiredPermission을 스키마로 보존만 하고 비활성 UX는 적용하지 않는다(서버가 진짜 게이트이므로 보안 영향 없음). §8/§11 Phase 1b(SPA GrapesJS)와 §12의 SPA 매핑 테스트는 본 계획 범위 밖(별도 계획).

**2. 플레이스홀더 스캔:** TBD/TODO 없음. 모든 코드 단계에 실제 코드 포함.

**3. 타입 일관성:** `LayoutNode`/파생 노드 시그니처가 Task 2 정의와 Task 3·4·5 사용에서 일치(`SectionNode.Title/Children`, `ColumnNode.Span`, `GridWidget.QueryId/Columns`, `FormWidget.SaveQueryId/Fields`, `FieldWidget.FieldKey/Field`, `ButtonWidget.Label/Command`, `TextWidget.Text/IsLabel`). `LayoutRenderer` 파라미터(`Node`/`Model`/`ModelChanged`/`QueryResults`/`OnCommand`)가 Task 4 정의와 Task 5 사용에서 일치. `_queryResults` 타입이 `LayoutRenderer.QueryResults`와 동일(`IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string,object?>>>`). `QueryDescriptor(Id, IsWrite, RequiredPermission)`가 Task 1에서 일관.

---

## 실행 핸드오프

이 계획은 Phase 0 + 1a(백엔드 + Blazor 런타임)를 다룬다. 완료되면 **계약이 입증된 동작 런타임**(임의 생산자가 저장한 레이아웃을 렌더)을 얻으며, 이후 **Phase 1b(React SPA GrapesJS 에디터)**를 별도 계획으로 작성한다(이유: TS/React 서브시스템은 독립적이고 현재 SPA에 테스트 러너 부재 — vitest 도입 + 순수 매핑 모듈 TDD를 별도 사이클로).
