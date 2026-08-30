using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Common.Security;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>메타 JSON에 권한이 빠져 있어도 서버 실행 카탈로그와 claims가 조회·명령 UX를 제어하는지 검증합니다.</summary>
public sealed class MetaScreenPermissionTests
{
    [Fact]
    public void Catalog_read_permission_blocks_flat_query_without_explicit_metadata()
    {
        using var ctx = CreateContext(Permissions.QmsRead);
        var definition = FlatGrid("DENIED_READ", "QMS.SecretList");
        var api = new Mock<IApiClient>();
        var catalog = Catalog(reads: new() { ["QMS.SecretList"] = Permissions.MdmRead });
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(Permissions.MdmRead));
        api.Verify(item => item.ExecuteQueryAsync(
            "QMS.SecretList", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryPagedAsync(
            "QMS.SecretList", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "권한 없는 read는 403 응답을 받기 전에 호출 자체를 만들지 않아야 한다");
    }

    [Fact]
    public void Unknown_binding_without_explicit_permission_fails_closed()
    {
        using var ctx = CreateContext(Permissions.QmsManage);
        var definition = FlatGrid("UNKNOWN_READ", "QMS.RemovedQuery");
        var api = new Mock<IApiClient>();
        var catalog = Catalog();
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("권한 카탈로그에 등록되지 않은 바인딩"));
        cut.Markup.Should().Contain("QMS.RemovedQuery");
        api.Verify(item => item.ExecuteQueryAsync(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryPagedAsync(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "삭제되거나 오타 난 binding은 높은 권한 claim이 있어도 실행하면 안 된다");
    }

    [Fact]
    public void Module_manage_claim_satisfies_catalog_read_permission()
    {
        using var ctx = CreateContext(Permissions.MdmManage);
        var definition = FlatGrid("MANAGE_READ", "MDM.PlantList");
        var rows = new List<Dictionary<string, object?>> { new() { ["ID"] = "P1" } };
        var api = new Mock<IApiClient>();
        api.Setup(item => item.ExecuteQueryPagedAsync(
                "MDM.PlantList", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedQueryResult(1, rows));
        var catalog = Catalog(reads: new() { ["MDM.PlantList"] = Permissions.MdmRead });
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("P1"));
        api.Verify(item => item.ExecuteQueryPagedAsync(
            "MDM.PlantList", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void Denied_parent_permission_suppresses_child_query_and_subtree()
    {
        using var ctx = CreateContext(Permissions.QmsRead);
        var definition = new ScreenDefinition(
            "DENIED_PARENT",
            "보호 화면",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                RequiredPermission = Permissions.QmsManage,
                Children =
                [
                    new TextWidget { Text = "비밀 본문" },
                    new GridWidget
                    {
                        QueryId = "QMS.SecretList",
                        Columns = [new GridColumnDefinition("ID", "ID")],
                    },
                ],
            });
        var api = new Mock<IApiClient>();
        var catalog = Catalog(reads: new() { ["QMS.SecretList"] = Permissions.QmsRead });
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(Permissions.QmsManage));
        cut.Markup.Should().NotContain("비밀 본문");
        api.Verify(item => item.ExecuteQueryAsync(
            "QMS.SecretList", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryPagedAsync(
            "QMS.SecretList", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Denied_parent_permission_blocks_collection_option_query_and_editor_subtree()
    {
        using var ctx = CreateContext(Permissions.QmsRead);
        var definition = new ScreenDefinition(
            "DENIED_COLLECTION_PARENT",
            "보호 반복 입력",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                RequiredPermission = Permissions.QmsManage,
                Children =
                [
                    new CollectionWidget
                    {
                        CollectionKey = "items",
                        Label = "비밀 검사 항목",
                        MinItems = 1,
                        Fields =
                        [
                            new FieldWidget
                            {
                                Field = new FieldDefinition(
                                    "specId",
                                    "비밀 규격",
                                    FieldType.Select,
                                    OptionsQueryId: "QMS.SecretSpecCombo"),
                            },
                        ],
                    },
                ],
            });
        var api = new Mock<IApiClient>();
        var catalog = Catalog(reads: new() { ["QMS.SecretSpecCombo"] = Permissions.QmsRead });
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(Permissions.QmsManage));
        cut.Markup.Should().NotContain("비밀 검사 항목").And.NotContain("비밀 규격");
        cut.FindAll(".meta-collection-editor").Should().BeEmpty();
        api.Verify(client => client.ExecuteQueryAsync(
            "QMS.SecretSpecCombo", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never,
            "부모 노드가 차단되면 collection 하위 옵션 조회도 실행하지 않아야 한다");
    }

    [Fact]
    public void Catalog_write_permissions_hide_save_and_disable_bulk_with_reason()
    {
        using var ctx = CreateContext(Permissions.QmsRead);
        var definition = new ScreenDefinition(
            "DENIED_WRITES",
            "검사 관리",
            [new FieldDefinition("name", "이름")],
            [new GridColumnDefinition("ID", "ID")],
            QueryId: "QMS.InspectionList",
            SaveQueryId: "QMS.SaveInspection",
            DeleteQueryId: "QMS.DeleteInspection",
            BulkCommands: [new BulkCommandDefinition("승인", "QMS.ApproveInspection")]);
        var rows = new List<Dictionary<string, object?>> { new() { ["ID"] = "I1" } };
        var api = new Mock<IApiClient>();
        api.Setup(item => item.ExecuteQueryPagedAsync(
                "QMS.InspectionList", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedQueryResult(1, rows));
        var catalog = Catalog(
            reads: new() { ["QMS.InspectionList"] = Permissions.QmsRead },
            writes: new()
            {
                ["QMS.SaveInspection"] = Permissions.QmsManage,
                ["QMS.DeleteInspection"] = Permissions.QmsManage,
                ["QMS.ApproveInspection"] = Permissions.QmsManage,
            });
        Register(ctx, definition, api, catalog);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("I1"));
        cut.FindAll("button.layout-save").Should().BeEmpty("권한 없는 평면 저장 폼은 렌더하지 않는다");
        var delete = cut.FindAll(".meta-grid-toolbar button").Single(button => button.TextContent.Contains("삭제"));
        delete.HasAttribute("disabled").Should().BeTrue();
        delete.GetAttribute("title").Should().Contain(Permissions.QmsManage)
            .And.NotContain("DeleteDisabledReason",
                "string 컴포넌트 매개변수는 속성 이름 리터럴이 아니라 실제 거부 사유로 바인딩되어야 한다");
        var bulk = cut.FindAll(".meta-grid-toolbar button").Single(button => button.TextContent.Contains("승인"));
        bulk.HasAttribute("disabled").Should().BeTrue();
        bulk.GetAttribute("title").Should().Contain(Permissions.QmsManage);
        bulk.GetAttribute("aria-label").Should().Contain(Permissions.QmsManage);
        api.Verify(item => item.ExecuteCommandAsync(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BunitContext CreateContext(string permission)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var authorization = ctx.AddAuthorization();
        authorization.SetAuthorized("operator");
        authorization.SetClaims(new Claim(Permissions.ClaimType, permission));
        return ctx;
    }

    private static ScreenDefinition FlatGrid(string uiId, string queryId)
        => new(
            uiId,
            "권한 목록",
            Array.Empty<FieldDefinition>(),
            [new GridColumnDefinition("ID", "ID")],
            QueryId: queryId);

    private static Mock<IMetaPermissionCatalog> Catalog(
        Dictionary<string, string?>? reads = null,
        Dictionary<string, string?>? writes = null)
    {
        var catalog = new Mock<IMetaPermissionCatalog>();
        catalog.Setup(item => item.ResolveRead(It.IsAny<string>()))
            .Returns((string id) => reads?.TryGetValue(id, out var permission) == true
                ? MetaBindingPermission.Known(permission)
                : MetaBindingPermission.Unknown);
        catalog.Setup(item => item.ResolveWrite(It.IsAny<string>()))
            .Returns((string id) => writes?.TryGetValue(id, out var permission) == true
                ? MetaBindingPermission.Known(permission)
                : MetaBindingPermission.Unknown);
        return catalog;
    }

    private static void Register(
        BunitContext ctx,
        ScreenDefinition definition,
        Mock<IApiClient> api,
        Mock<IMetaPermissionCatalog> catalog)
    {
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.GetAsync(definition.UiId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);
    }
}
