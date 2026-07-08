using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// Phase 3/4 — 메타데이터 화면 런타임(/meta/{UiId}). 그리드(컬럼) + 명명 쿼리가 바인딩된 정의면
/// 파일 기반 쿼리 게이트웨이(ExecuteQueryAsync)로 행을 조회해 MetaGridRenderer로 렌더하고,
/// 폼(필드) 전용 정의면 쿼리를 치지 않고 폼만 렌더하는지(데이터 소스 분기)를 검증한다.
/// </summary>
public sealed class MetaScreenTests
{
    private static Mock<IScreenDefinitionProvider> Provider(string uiId, ScreenDefinition def)
    {
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(p => p.Get(uiId)).Returns(def);
        // MetaScreen 로드는 GetAsync 경로를 사용한다 — 동기 Get 셋업과 동일 결과로 미러링(거동 불변).
        provider.Setup(p => p.GetAsync(uiId, It.IsAny<CancellationToken>())).ReturnsAsync(def);
        return provider;
    }

    // 저장 버튼은 클래스로 특정한다(RadzenButton이 아이콘 리게이처 텍스트 'save'를 TextContent에 더해
    // 라벨 매칭이 깨지므로 — 폼/암시적 저장 버튼 모두 .layout-save 마커를 갖는다).
    private static AngleSharp.Dom.IElement SaveButton(IRenderedFragment cut)
        => cut.FindAll("button.layout-save").First();

    [Fact]
    public void Grid_definition_loads_rows_from_query_gateway_and_renders()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 그리드 전용 정의: 폼 필드 없음 + 컬럼 메타 + 데이터 소스 쿼리(Q.Plants) 바인딩.
        var def = new ScreenDefinition("GRID1", "공장 목록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명") },
            QueryId: "Q.Plants");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>
           {
               new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One" },
           });

        ctx.Services.AddSingleton(Provider("GRID1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "GRID1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("공장 목록").And.Contain("공장 ID").And.Contain("Plant One");
            cut.FindAll("tbody tr").Count.Should().Be(1, "쿼리 결과 1행이 그리드로 렌더돼야 한다");
        }, TimeSpan.FromSeconds(2));

        // 그리드+쿼리 바인딩 화면은 명명 쿼리로 게이트웨이를 호출한다(저코드 조회 경로 end-to-end UI측).
        api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Form_only_definition_renders_form_and_does_not_call_query_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 폼 전용 정의: 컬럼/쿼리 없음 — 쿼리 게이트웨이를 치면 안 된다.
        var def = new ScreenDefinition("FORM1", "파라미터 입력",
            new FieldDefinition[] { new("name", "이름", FieldType.Text, Required: true) });

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("FORM1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "FORM1"));

        cut.Markup.Should().Contain("파라미터 입력").And.Contain("이름");
        cut.FindAll("button").Should().NotBeEmpty("폼 화면은 저장 버튼을 렌더해야 한다");
        cut.FindAll("tbody tr").Should().BeEmpty("폼 전용 화면은 그리드가 없어야 한다");

        api.Verify(a => a.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "데이터 소스 쿼리가 없는 폼 전용 화면은 쿼리 게이트웨이를 호출하지 않아야 한다");
    }

    [Fact]
    public void Save_with_SaveQueryId_posts_form_values_to_command_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 저장(쓰기) 쿼리가 바인딩된 폼 — 필수 필드 1개.
        var def = new ScreenDefinition("SAVE1", "공장 등록",
            new FieldDefinition[] { new("plantName", "공장명", FieldType.Text, Required: true) },
            SaveQueryId: "MDM.CreatePlant");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("SAVE1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SAVE1"));

        // 필수 필드를 채운 뒤 저장 → command 게이트웨이로 전송.
        cut.Find("input").Change("플랜트1");
        SaveButton(cut).Click();

        cut.WaitForAssertion(() =>
        {
            api.Verify(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                Times.Once, "저장은 바인딩된 명명 쓰기쿼리로 폼 값을 전송해야 한다");
            cut.Markup.Should().Contain("저장됨");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Save_success_reloads_grid_so_new_row_shows_without_page_reload()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 그리드+폼 화면: 저장 성공 시 그리드를 재조회해 새 행이 즉시 보여야 한다(실브라우저 스모크에서 발견된 공백).
        var def = new ScreenDefinition("SAVE3", "가상 이벤트",
            new FieldDefinition[] { new("eventId", "이벤트 ID", FieldType.Text, Required: true) },
            new GridColumnDefinition[] { new("EVENT_ID", "이벤트 ID") },
            QueryId: "Q.Events",
            SaveQueryId: "FDC.SaveEvent");

        var rows = new List<Dictionary<string, object?>>();
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Events", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(() => rows.ToList());
        api.Setup(a => a.ExecuteCommandAsync("FDC.SaveEvent", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true)
           .Callback(() => rows.Add(new Dictionary<string, object?> { ["EVENT_ID"] = "VE-NEW" }));
        ctx.Services.AddSingleton(Provider("SAVE3", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SAVE3"));
        cut.Markup.Should().NotContain("VE-NEW", "저장 전에는 새 행이 없어야 한다");

        cut.Find("input").Change("VE-NEW");
        SaveButton(cut).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("저장됨");
            cut.Markup.Should().Contain("VE-NEW", "저장 성공 후 그리드가 재조회돼 새 행이 즉시 보여야 한다");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Manual_refresh_button_reloads_grid_rows()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var def = new ScreenDefinition("GRID-R", "공장 목록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PLANT_ID", "공장 ID") },
            QueryId: "Q.Plants");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>());
        ctx.Services.AddSingleton(Provider("GRID-R", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "GRID-R"));
        api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);

        // 헤더 새로고침 버튼 → 데이터 재조회(폼 상태는 건드리지 않는 ReloadDataAsync 경로).
        cut.FindAll("button").First(b => b.TextContent.Contains("새로고침")).Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2), "수동 새로고침은 그리드 쿼리를 재실행해야 한다"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Select_field_loads_dynamic_options_from_options_query()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 동적 Select — OptionsQueryId 결과를 MetaScreen이 조회해 RadzenDropDown 옵션으로 내려보낸다.
        // (Radzen 드롭다운 옵션은 팝업 렌더라 정적 마크업에 없으므로, 옵션 쿼리 호출+드롭다운 렌더로 검증.)
        var def = new ScreenDefinition("SEL1", "매핑 등록",
            new FieldDefinition[] { new("roleId", "역할", FieldType.Select, Required: true, OptionsQueryId: "Q.Roles") },
            SaveQueryId: "SYS.UpsertMenuRole");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Roles", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>
           {
               new() { ["ROLE_ID"] = "ADMIN", ["ROLE_NAME"] = "Administrator" },
               new() { ["ROLE_ID"] = "VIEWER", ["ROLE_NAME"] = "뷰어" },
           });
        ctx.Services.AddSingleton(Provider("SEL1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SEL1"));

        cut.WaitForAssertion(() =>
        {
            api.Verify(a => a.ExecuteQueryAsync("Q.Roles", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce, "OptionsQueryId 화면은 동적 옵션 쿼리를 실행해야 한다");
            cut.Markup.Should().Contain("rz-dropdown", "Select 필드는 RadzenDropDown으로 렌더돼야 한다");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Search_fields_restore_latest_condition_and_bind_query_parameters()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // §20.8 — 재진입 시 '$latest'(마지막 조회 조건)를 복원해 초기 조회 파라미터로 바인딩해야 한다.
        var def = new ScreenDefinition("GRID-S", "로그", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("LOG_LEVEL", "레벨") },
            QueryId: "Q.Logs",
            SearchFields: new FieldDefinition[]
            {
                new("logLevel", "레벨", FieldType.Select, Options: new[] { "Warning", "Error" }),
            });

        object? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Logs", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, CancellationToken>((_, p, _) => captured = p)
           .ReturnsAsync(new List<Dictionary<string, object?>>());
        api.Setup(a => a.GetConditionSettingsAsync("GRID-S", It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ConditionSettingDto(
               new ConditionItemDto("$latest", DateTime.UtcNow, new() { ["logLevel"] = "Error" }),
               new List<ConditionItemDto>()));
        ctx.Services.AddSingleton(Provider("GRID-S", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "GRID-S"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("조회", "검색 조건 영역은 조회 버튼을 렌더해야 한다");
            captured.Should().BeOfType<Dictionary<string, object?>>()
                .Which.Should().Contain(kv => kv.Key == "logLevel" && kv.Value!.ToString() == "Error",
                    "$latest 조건이 초기 조회 파라미터로 복원돼야 한다");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Search_button_saves_latest_condition_and_requeries_with_values()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var def = new ScreenDefinition("GRID-S2", "로그", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("LOG_LEVEL", "레벨") },
            QueryId: "Q.Logs",
            SearchFields: new FieldDefinition[]
            {
                new("logLevel", "레벨", FieldType.Select, Options: new[] { "Warning", "Error" }),
            });

        var capturedParams = new List<object?>();
        Dictionary<string, string?>? savedLatest = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Logs", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, CancellationToken>((_, p, _) => capturedParams.Add(p))
           .ReturnsAsync(new List<Dictionary<string, object?>>());
        api.Setup(a => a.SaveLatestConditionAsync("GRID-S2", It.IsAny<Dictionary<string, string?>>(), It.IsAny<CancellationToken>()))
           .Callback<string, Dictionary<string, string?>, CancellationToken>((_, v, _) => savedLatest = v)
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("GRID-S2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "GRID-S2"));

        // 검색 필드(RadzenDropDown)의 Change를 직접 발화해 조건값 설정(Radzen 입력은 DOM Change 불가) → 조회.
        var search = cut.FindComponent<Radzen.Blazor.RadzenDropDown<string>>();
        cut.InvokeAsync(() => search.Instance.Change.InvokeAsync("Warning"));
        cut.Find("button.layout-command").Click();   // 조회(RadzenButton, search 아이콘) — 클래스로 특정

        cut.WaitForAssertion(() =>
        {
            savedLatest.Should().NotBeNull("조회는 '$latest' 조건을 자동 저장해야 한다(§20.8)");
            savedLatest!.Should().Contain(kv => kv.Key == "logLevel" && kv.Value == "Warning");
            capturedParams.Count.Should().BeGreaterThan(1, "조회 클릭은 재조회를 트리거해야 한다");
            capturedParams[^1].Should().BeOfType<Dictionary<string, object?>>()
                .Which.Should().Contain(kv => kv.Key == "logLevel" && kv.Value!.ToString() == "Warning");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Row_click_loads_row_values_into_form_fields()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();   // 그리드는 Radzen DataGrid — 렌더에 Radzen 서비스 필요

        // P1-1 — 행 클릭=폼 로드(레거시 표준 편집 흐름). 컬럼 UPPER_SNAKE ↔ 필드 camel 정규화 매칭,
        // 표시되지 않는 컬럼(BATCH_NAME)도 행 딕셔너리에 있으면 폼에 채워져야 한다.
        var def = new ScreenDefinition("ROWSEL", "배치",
            new FieldDefinition[] { new("batchId", "배치 ID"), new("batchName", "배치명") },
            new GridColumnDefinition[] { new("BATCH_ID", "배치 ID") },
            QueryId: "Q.B", SaveQueryId: "SYS.X");

        Dictionary<string, object?>? savedModel = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.B", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>
           {
               new() { ["BATCH_ID"] = "B-1", ["BATCH_NAME"] = "야간 집계" },
           });
        api.Setup(a => a.ExecuteCommandAsync("SYS.X", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, CancellationToken>((_, m, _) => savedModel = m as Dictionary<string, object?>)
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("ROWSEL", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "ROWSEL"));
        cut.WaitForAssertion(() => cut.FindAll(".rz-data-row").Should().NotBeEmpty());

        // Radzen 행 클릭은 라이브러리 내부라 bUnit이 직접 트리거하기 어렵고(브라우저 스모크로 검증), Radzen 입력
        // 값은 정적 마크업에 안 드러난다. 그래서 그리드의 OnRowSelect 콜백을 직접 호출해 MetaScreen의 정규화
        // (UPPER_SNAKE↔camel)·미표시 컬럼 로드를 채운 뒤, 저장으로 모델을 되받아 매핑 결과를 검증한다.
        var grid = cut.FindComponent<MetaGridRenderer>();
        cut.InvokeAsync(() => grid.Instance.OnRowSelect.InvokeAsync(
            new Dictionary<string, object?> { ["BATCH_ID"] = "B-1", ["BATCH_NAME"] = "야간 집계" }));
        SaveButton(cut).Click();   // 저장(RadzenButton, save 아이콘) — .layout-save 클래스로 특정

        cut.WaitForAssertion(() =>
        {
            savedModel.Should().NotBeNull("행 선택 후 저장 시 모델이 커맨드 게이트웨이로 전송돼야 한다");
            savedModel!["batchId"].Should().Be("B-1", "BATCH_ID → batchId 정규화 매칭");
            savedModel!["batchName"].Should().Be("야간 집계", "미표시 컬럼(BATCH_NAME)도 행 값이 폼 모델에 로드돼야 한다");
        }, TimeSpan.FromSeconds(2));
    }

    // 파괴적 확인은 RadzenDialog(DialogService.Confirm) — 브라우저 confirm 대신 비블로킹 모달. 테스트는
    // 결과(수락/취소)를 미리 정한 FakeDialogService를 주입해 커맨드 실행 여부를 검증한다(모달 렌더는 브라우저).
    private sealed class FakeDialogService : Radzen.DialogService
    {
        private readonly bool? _result;
        public FakeDialogService(bool? result) : base(null!, null!) => _result = result;
        public override Task<bool?> Confirm(string message = "Confirm", string title = "Confirm",
            Radzen.ConfirmOptions? options = null, CancellationToken? cancellationToken = null)
            => Task.FromResult(_result);
    }

    [Fact]
    public void Confirm_message_blocks_command_when_cancelled_and_runs_when_accepted()
    {
        // P1-2 — ConfirmMessage가 있는 버튼은 확인 다이얼로그 취소(false)면 커맨드 미실행, 수락(true)면 실행.
        ScreenDefinition Def() => new("CONF", "확인", Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "s", Children = new LayoutNode[]
                {
                    new ButtonWidget { Id = "b", Label = "삭제", Command = "SYS.Del", ConfirmMessage = "정말 삭제하시겠습니까?" },
                },
            });

        using (var ctx = new TestContext())
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddRadzenComponents();
            ctx.Services.AddSingleton<Radzen.DialogService>(new FakeDialogService(false));   // 취소
            var api = new Mock<IApiClient>();
            ctx.Services.AddSingleton(Provider("CONF", Def()).Object);
            ctx.Services.AddSingleton(api.Object);

            var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "CONF"));
            cut.FindAll("button").First(b => b.TextContent.Trim() == "삭제").Click();

            api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                Times.Never, "확인 취소 시 커맨드를 실행하지 않아야 한다");
        }

        using (var ctx = new TestContext())
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddRadzenComponents();
            ctx.Services.AddSingleton<Radzen.DialogService>(new FakeDialogService(true));   // 수락
            var api = new Mock<IApiClient>();
            api.Setup(a => a.ExecuteCommandAsync("SYS.Del", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
            ctx.Services.AddSingleton(Provider("CONF", Def()).Object);
            ctx.Services.AddSingleton(api.Object);

            var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "CONF"));
            cut.FindAll("button").First(b => b.TextContent.Trim() == "삭제").Click();

            cut.WaitForAssertion(() =>
                api.Verify(a => a.ExecuteCommandAsync("SYS.Del", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                    Times.Once, "확인 수락 시 커맨드가 실행돼야 한다"),
                TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void Save_blocks_and_does_not_call_gateway_when_required_field_empty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var def = new ScreenDefinition("SAVE2", "공장 등록",
            new FieldDefinition[] { new("plantName", "공장명", FieldType.Text, Required: true) },
            SaveQueryId: "MDM.CreatePlant");

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("SAVE2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SAVE2"));

        // 필수 필드를 비운 채 저장 → 검증 실패 메시지 + 게이트웨이 미호출.
        SaveButton(cut).Click();

        cut.Markup.Should().Contain("필수");
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "필수 검증 실패 시 쓰기 게이트웨이를 호출하지 않아야 한다");
    }

    [Fact]
    public void Layout_executes_each_distinct_read_query_once_and_renders_grids()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

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
        ctx.Services.AddRadzenComponents();

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
        ctx.Services.AddRadzenComponents();

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

    [Fact]
    public void Layout_form_with_save_query_but_no_button_renders_implicit_save_and_posts()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 명령 버튼 없는 손코딩 레이아웃: FormWidget이 SaveQueryId만 가진다 → 암시적 저장 버튼이 생겨야 한다.
        var def = new ScreenDefinition("LAY4", "공장 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new RowNode
                    {
                        Children = new LayoutNode[]
                        {
                            new ColumnNode
                            {
                                Children = new LayoutNode[]
                                {
                                    new FormWidget
                                    {
                                        SaveQueryId = "SYS.Save",
                                        Fields = new[]
                                        {
                                            new FieldWidget { FieldKey = "name", Field = new FieldDefinition("name", "이름", FieldType.Text) },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("SYS.Save", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("LAY4", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY4"));

        cut.FindAll("button.layout-save").Count.Should().Be(1, "버튼 없는 폼 저장쿼리는 암시적 저장 버튼 1개를 렌더해야 한다");

        cut.Find("button.layout-save").Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ExecuteCommandAsync("SYS.Save", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once,
                "암시적 저장 버튼은 폼의 SaveQueryId로 공유 Model을 전송해야 한다"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Layout_form_save_query_covered_by_button_renders_no_implicit_save()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 동일 저장쿼리를 가리키는 명령 버튼이 이미 있다 → 암시적 저장 버튼은 생기지 않아야 한다(중복 방지).
        var def = new ScreenDefinition("LAY5", "공장 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FormWidget
                    {
                        SaveQueryId = "SYS.Save",
                        Fields = new[]
                        {
                            new FieldWidget { FieldKey = "name", Field = new FieldDefinition("name", "이름", FieldType.Text) },
                        },
                    },
                    new ButtonWidget { Label = "저장", Command = "SYS.Save" },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("SYS.Save", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("LAY5", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY5"));

        cut.FindAll("button.layout-save").Should().BeEmpty("명령 버튼이 커버하는 저장쿼리는 암시적 저장 버튼을 만들지 않아야 한다");

        // 기존 명령 버튼 경로는 그대로 동작한다.
        cut.Find("button.layout-command").Click();
        cut.WaitForAssertion(() =>
            api.Verify(a => a.ExecuteCommandAsync("SYS.Save", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Layout_implicit_save_blocks_and_does_not_call_gateway_when_required_field_empty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 암시적 저장 버튼도 RunCommand 의미론을 따라 검증 실패 시 게이트웨이를 치면 안 된다.
        var def = new ScreenDefinition("LAY6", "공장 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FormWidget
                    {
                        SaveQueryId = "SYS.Save",
                        Fields = new[]
                        {
                            new FieldWidget { FieldKey = "name", Field = new FieldDefinition("name", "이름", FieldType.Text, Required: true) },
                        },
                    },
                },
            });

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("LAY6", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY6"));

        cut.Find("button.layout-save").Click();   // 필수 필드 비움

        cut.Markup.Should().Contain("필수");
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "암시적 저장 버튼도 검증 실패 시 쓰기 게이트웨이를 호출하지 않아야 한다");
    }

    [Fact]
    public void RefreshIntervalSeconds_re_executes_queries_periodically()
    {
        // Phase-2 실시간 v2 — 자동 새로고침 정의는 데이터 쿼리를 주기 재실행해야 한다(폼 상태는 본 검증 범위 외).
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var layout = new SectionNode
        {
            Id = "sec",
            Children = new LayoutNode[]
            {
                new KpiWidget { Id = "k", Label = "카운트", QueryId = "Q.Count", ValueColumn = "N" },
            },
        };
        var def = new ScreenDefinition("LIVE1", "실시간", Array.Empty<FieldDefinition>(),
            Layout: layout, RefreshIntervalSeconds: 1);   // 렌더러가 최소 2초로 클램프

        var calls = 0;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Count", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback(() => Interlocked.Increment(ref calls))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["N"] = 1L } });

        ctx.Services.AddSingleton(Provider("LIVE1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LIVE1"));

        cut.WaitForAssertion(() => calls.Should().BeGreaterThanOrEqualTo(2,
            "초기 조회 후 주기 새로고침이 최소 1회는 더 실행돼야 한다"), TimeSpan.FromSeconds(6));

        // 컴포넌트 폐기 후 루프가 멈춰야 한다(취소 토큰) — 잔여 타이머로 카운트가 계속 늘면 누수.
        var atDispose = calls;
        cut.Instance.Dispose();
        Thread.Sleep(2500);
        calls.Should().BeInRange(atDispose, atDispose + 1, "Dispose 후 새로고침 루프가 중단돼야 한다(경계 1회 허용)");
    }

    [Fact]
    public void Push_notification_triggers_immediate_reload_with_throttle_and_unsubscribes_on_dispose()
    {
        // 실시간 v3 — 라이브 화면은 이벤트 푸시로 즉시 재조회(1초 스로틀), 폐기 시 구독 해지.
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var layout = new SectionNode
        {
            Id = "sec",
            Children = new LayoutNode[] { new KpiWidget { Id = "k", Label = "N", QueryId = "Q.Push", ValueColumn = "N" } },
        };
        // 폴링 주기를 크게(300s) 두어 푸시 경로만 관찰한다.
        var def = new ScreenDefinition("PUSH1", "푸시", Array.Empty<FieldDefinition>(),
            Layout: layout, RefreshIntervalSeconds: 300);

        var calls = 0;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Push", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback(() => Interlocked.Increment(ref calls))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["N"] = 1L } });

        var notifier = new FakeNotifier();
        ctx.Services.AddSingleton(Provider("PUSH1", def).Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton<IScreenRefreshNotifier>(notifier);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "PUSH1"));
        cut.WaitForAssertion(() => notifier.Callback.Should().NotBeNull("라이브 화면은 푸시를 구독해야 한다"));
        var initial = calls;

        // 이벤트 푸시 → 폴링 주기와 무관하게 즉시 재조회.
        notifier.Callback!().GetAwaiter().GetResult();
        cut.WaitForAssertion(() => calls.Should().Be(initial + 1, "푸시는 즉시 재조회를 유발해야 한다"));

        // 1초 내 연속 푸시는 스로틀(이벤트 폭주 방어).
        notifier.Callback!().GetAwaiter().GetResult();
        calls.Should().Be(initial + 1, "1초 스로틀로 연속 푸시는 무시돼야 한다");

        cut.Instance.Dispose();
        notifier.Disposed.Should().BeTrue("폐기 시 구독이 해지돼야 한다(회로 누수 방지)");
    }

    private sealed class FakeNotifier : IScreenRefreshNotifier
    {
        public Func<Task>? Callback;
        public bool Disposed;

        public IDisposable Subscribe(Func<Task> onChanged)
        {
            Callback = onChanged;
            return new Unsubscriber(this);
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly FakeNotifier _owner;
            public Unsubscriber(FakeNotifier owner) => _owner = owner;
            public void Dispose() => _owner.Disposed = true;
        }
    }

    [Fact]
    public void Isolated_forms_keep_separate_models_and_post_only_their_own_values()
    {
        // Phase-2 멀티폼 — Isolated 폼 2개: 입력·검증·저장이 폼별로 격리돼야 한다(공유 모델이면 값이 섞인다).
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var layout = new SectionNode
        {
            Id = "sec",
            Children = new LayoutNode[]
            {
                new FormWidget { Id = "form-a", SaveQueryId = "MDM.CreatePlant", Isolated = true, Fields = new FieldWidget[]
                {
                    new() { Id = "fa", FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) },
                } },
                new FormWidget { Id = "form-b", SaveQueryId = "MDM.CreateArea", Isolated = true, Fields = new FieldWidget[]
                {
                    new() { Id = "fb", FieldKey = "areaId", Field = new FieldDefinition("areaId", "AREA ID", FieldType.Text, Required: true) },
                } },
            },
        };
        var def = new ScreenDefinition("MULTI1", "멀티폼", Array.Empty<FieldDefinition>(), Layout: layout);

        var posted = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, CancellationToken>((cmd, p, _) =>
               posted[cmd] = new Dictionary<string, object?>((Dictionary<string, object?>)p!))
           .ReturnsAsync(true);

        ctx.Services.AddSingleton(Provider("MULTI1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "MULTI1"));

        // 폼별 암시적 저장 버튼 2개(각자 SaveQueryId) — 라벨로 구분 렌더.
        cut.WaitForAssertion(() => cut.FindAll("button.layout-save").Count.Should().Be(2));

        // 폼 A에만 입력 — 격리라면 폼 B 모델은 여전히 비어 있어야 한다.
        cut.FindAll("input")[0].Change("P-9");

        // 폼 B 저장 → B 자신의 필수(areaId) 미입력으로 검증 실패 + 커맨드 미호출(격리 증명 — 공유 모델이면 통과해버림).
        cut.FindAll("button.layout-save")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("AREA ID"));
        posted.Should().NotContainKey("MDM.CreateArea", "격리 폼 B는 자기 모델(비어 있음)만으로 검증돼야 한다");

        // 폼 A 저장 → 자기 값(plantId)만 전송되고 타 폼 필드는 섞이지 않는다.
        cut.FindAll("button.layout-save")[0].Click();
        cut.WaitForAssertion(() => posted.Should().ContainKey("MDM.CreatePlant"));
        posted["MDM.CreatePlant"].Should().ContainKey("plantId");
        posted["MDM.CreatePlant"]["plantId"]!.ToString().Should().Be("P-9");
        posted["MDM.CreatePlant"].Keys.Should().NotContain("areaId", "폼 간 모델이 격리돼야 한다");
    }

    // ── 하이브리드 서버 페이징(DB-레벨 LIMIT) — total≤상한=클라 모드, 초과=서버 모드, 미지원=전량 폴백 ──

    [Fact]
    public void Hybrid_small_total_stays_client_mode_with_full_rows()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var def = new ScreenDefinition("HY1", "소형 목록", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ID", "ID") }, QueryId: "Q.Small");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryPagedAsync("Q.Small", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new PagedQueryResult(2, new List<Dictionary<string, object?>>
           {
               new() { ["ID"] = "A" }, new() { ["ID"] = "B" },
           }));
        ctx.Services.AddSingleton(Provider("HY1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "HY1"));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".rz-data-row").Count.Should().Be(2, "total≤상한이면 전량(클라 모드)");
            cut.FindAll("input.meta-grid-filter").Should().NotBeEmpty("클라 모드는 빠른 필터 유지");
        });
        // 전량 경로(ExecuteQueryAsync)는 호출되지 않는다 — paged가 데이터 소스.
        api.Verify(a => a.ExecuteQueryAsync("Q.Small", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Hybrid_large_total_switches_to_server_paging_and_pages_by_offset()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var def = new ScreenDefinition("HY2", "대형 목록", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ID", "ID") }, QueryId: "Q.Big");

        var offsets = new List<(int Limit, int Offset)>();
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryPagedAsync("Q.Big", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, int, int, CancellationToken>((_, _, l, o, _) => offsets.Add((l, o)))
           .ReturnsAsync((string _, object? _, int limit, int offset, CancellationToken _) =>
               new PagedQueryResult(1200, Enumerable.Range(offset, Math.Min(limit, 20))
                   .Select(i => new Dictionary<string, object?> { ["ID"] = $"R{i:D4}" }).ToList()));
        ctx.Services.AddSingleton(Provider("HY2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "HY2"));
        cut.WaitForAssertion(() =>
        {
            // total>상한 → 서버 모드: 총건수 페이저 + 현재 페이지(20행)만.
            cut.Markup.Should().Contain("1200 행").And.Contain("1 / 60");
            cut.FindAll(".rz-data-row").Count.Should().Be(20);
            cut.FindAll("input.meta-grid-filter").Should().BeEmpty("서버 모드는 빠른 필터 숨김(현재 페이지만 걸려 오해)");
        });

        // 다음 페이지 → offset=20으로 페이지 단위 재조회.
        cut.FindAll(".meta-grid-pager button").Last(b => !b.HasAttribute("disabled")).Click();
        cut.WaitForAssertion(() =>
        {
            offsets.Should().Contain((MetaGridRenderer.PageSize, 20), "서버 모드 페이지 전환은 PageSize 창으로 재조회");
            cut.Markup.Should().Contain("2 / 60");
        });
    }

    // ── 그리드 '추가' 팝업(사용자 요청) — 툴바 추가 → 모달 폼 → 저장=커맨드+재조회, 취소=닫기 ──────

    [Fact]
    public void Add_button_opens_modal_and_save_posts_command()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        // 그리드+폼(SaveQueryId) 화면 — 추가 버튼이 켜지는 조건.
        var def = new ScreenDefinition("ADD1", "공장 관리",
            new FieldDefinition[] { new("plantId", "공장 ID", Required: true), new("plantName", "공장명") },
            new GridColumnDefinition[] { new("PLANT_ID", "공장 ID") },
            QueryId: "Q.Plants", SaveQueryId: "MDM.CreatePlant");

        Dictionary<string, object?>? posted = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["PLANT_ID"] = "P-1" } });
        api.Setup(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .Callback<string, object?, CancellationToken>((_, body, _) => posted = body as Dictionary<string, object?>)
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("ADD1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "ADD1"));
        cut.WaitForAssertion(() => cut.FindAll(".rz-data-row").Count.Should().Be(1));

        // 초기엔 모달 없음 → 추가 클릭 → 모달 열림(팝업 등록 폼).
        cut.FindAll(".nx-modal").Should().BeEmpty();
        cut.FindAll(".meta-grid-toolbar button").First(b => b.TextContent.Contains("추가")).Click();
        cut.WaitForAssertion(() => cut.FindAll(".nx-modal").Should().NotBeEmpty("추가는 팝업으로 진행"));

        // 필수(plantId) 미입력 저장 → 인라인 오류, 커맨드 미호출.
        cut.Find(".nx-modal .nx-modal-save").Click();
        cut.WaitForAssertion(() => cut.FindAll(".nx-modal .meta-field-error").Should().NotBeEmpty());
        posted.Should().BeNull();

        // 입력 후 저장 → 커맨드 호출 + 모달 닫힘.
        cut.FindAll(".nx-modal .meta-field input")[0].Change("P-NEW");
        cut.Find(".nx-modal .nx-modal-save").Click();
        cut.WaitForAssertion(() =>
        {
            posted.Should().NotBeNull("저장은 SaveQueryId 커맨드를 호출해야 한다");
            cut.FindAll(".nx-modal").Should().BeEmpty("저장 성공 시 모달이 닫힌다");
        });
        posted!["plantId"]!.ToString().Should().Be("P-NEW");
    }

    [Fact]
    public void Add_modal_cancel_closes_without_command()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var def = new ScreenDefinition("ADD2", "작업조",
            new FieldDefinition[] { new("shiftId", "작업조 ID") },
            new GridColumnDefinition[] { new("SHIFT_ID", "ID") },
            QueryId: "Q.S", SaveQueryId: "MDM.CreateShift");
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.S", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>());
        ctx.Services.AddSingleton(Provider("ADD2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "ADD2"));
        cut.WaitForAssertion(() => cut.FindAll(".meta-grid-toolbar").Should().NotBeEmpty("빈 결과에서도 툴바(추가) 유지"));

        cut.FindAll(".meta-grid-toolbar button").First(b => b.TextContent.Contains("추가")).Click();
        cut.WaitForAssertion(() => cut.FindAll(".nx-modal").Should().NotBeEmpty());
        cut.FindAll(".nx-modal + * , .nx-modal").Count.Should().BeGreaterThan(0);
        cut.FindAll(".nx-modal .rz-button").First(b => b.TextContent.Contains("취소")).Click();
        cut.WaitForAssertion(() => cut.FindAll(".nx-modal").Should().BeEmpty("취소는 커맨드 없이 닫힌다"));
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
