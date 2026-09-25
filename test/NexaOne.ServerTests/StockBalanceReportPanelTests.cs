using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class StockBalanceReportPanelTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly List<string> _reads = [];
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public StockBalanceReportPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
    }

    [Fact]
    public void Initial_state_does_not_fetch_and_requires_current_selections_for_optional_filters()
    {
        var cut = Panel(Scope(1));

        cut.Find("[data-initial]").TextContent.Should().Contain("필터를 확인");
        cut.Find("#inventory-balance-report-download").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#inventory-balance-report-warehouse-filter").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#inventory-balance-report-variant-filter").HasAttribute("disabled").Should().BeTrue();
        _api.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void Whole_scope_report_renders_fifty_rows_per_page_without_adding_quantities_across_units()
    {
        var scope = Scope(1);
        var rows = Enumerable.Range(1, 51).Select(index => Row(scope, variantId: Guid.NewGuid(),
            productId: "P-" + index, unit: index % 2 == 0 ? "EA" : "KG")).ToArray();
        Reads(_ => Ok(Report(scope, rows)));
        var cut = Panel(scope);

        cut.Find("#inventory-balance-report-run").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-report-row]").Should().HaveCount(50));
        _reads.Should().Equal(Root(scope) + "/reports/balances");
        cut.Find(".ibr-summary").TextContent.Should().Contain("51").And.Contain("범위 전체");
        cut.FindAll(".ibr-summary").Should().ContainSingle("mixed units must not be presented as an aggregate quantity KPI");
        cut.Find("#inventory-balance-report-next").Click();
        cut.FindAll("[data-report-row]").Should().ContainSingle();
        cut.Find("#inventory-balance-report-previous").HasAttribute("disabled").Should().BeFalse();
        _reads.Should().ContainSingle("paging the loaded report is client-side");
    }

    [Fact]
    public void Selected_filters_are_applied_in_stable_order_and_csv_uses_the_loaded_filters()
    {
        var scope = Scope(1);
        var warehouse = Warehouse(scope);
        var variant = Variant(scope);
        var reportPath = Root(scope) + $"/reports/balances?warehouseId={warehouse.Id:D}&variantId={variant.Id:D}";
        var exportPath = Root(scope) + $"/reports/balances/export.csv?warehouseId={warehouse.Id:D}&variantId={variant.Id:D}";
        Reads(path => path == reportPath ? Ok(Report(scope, [Row(scope, warehouse.Id, variant.Id)])) : Failure(404));
        _api.Setup(api => api.DownloadInventoryFileAsync(exportPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Encoding.UTF8.GetBytes("warehouse_code,variant_id\r\nWH-1,V-1\r\n"),
                "stock-balance.csv", "text/csv", 200, null, null));
        var cut = Panel(scope, warehouse, variant);

        cut.Find("#inventory-balance-report-warehouse-filter").Change(true);
        cut.Find("#inventory-balance-report-variant-filter").Change(true);
        cut.Find("#inventory-balance-report-run").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-report-row]").Should().ContainSingle());
        _reads.Should().Equal(reportPath);

        // Draft selections can change later; export remains tied to the successfully loaded report.
        cut.Find("#inventory-balance-report-warehouse-filter").Change(false);
        cut.Find("#inventory-balance-report-variant-filter").Change(false);
        cut.Find("#inventory-balance-report-download").Click();

        cut.WaitForAssertion(() => JSInterop.Invocations.Should().ContainSingle(call => call.Identifier == "nxDownloadStream"));
        _api.Verify(api => api.DownloadInventoryFileAsync(exportPath, It.IsAny<CancellationToken>()), Times.Once);
        cut.Find(".ibr-status").TextContent.Should().Contain("다운로드를 시작");
    }

    [Fact]
    public void Mismatched_scope_or_filter_response_is_rejected_without_enabling_download()
    {
        var scope = Scope(1);
        var warehouse = Warehouse(scope);
        var other = Scope(2);
        Reads(_ => Ok(Report(other, [Row(scope, Guid.NewGuid(), Guid.NewGuid())])));
        var cut = Panel(scope, warehouse);
        cut.Find("#inventory-balance-report-warehouse-filter").Change(true);

        cut.Find("#inventory-balance-report-run").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("범위 또는 필터"));
        cut.Find("[role=alert]").TextContent.Should().Contain("STOCK_REPORT_RESPONSE_MISMATCH");
        cut.Find("#inventory-balance-report-download").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-report-row]").Should().BeEmpty();
    }

    [Fact]
    public void Oversized_report_names_the_filter_recovery_action()
    {
        Reads(_ => Failure(413, "STOCK_REPORT_TOO_LARGE", "too large"));
        var cut = Panel(Scope(1));

        cut.Find("#inventory-balance-report-run").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("10,000행"));
        cut.Find("[role=alert]").TextContent.Should().Contain("창고 또는 품목 필터").And.Contain("STOCK_REPORT_TOO_LARGE");
    }

    [Fact]
    public async Task Scope_change_cancels_the_old_read_and_clears_its_late_result()
    {
        var first = Scope(1);
        var second = Scope(2);
        var pending = new TaskCompletionSource<(StockBalanceReport?, int, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        _api.Setup(api => api.ReadInventoryAsync<StockBalanceReport>(Root(first) + "/reports/balances", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) => { firstToken = token; return pending.Task; });
        var cut = Panel(first);
        var load = cut.Find("#inventory-balance-report-run").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#inventory-balance-report").GetAttribute("aria-busy").Should().Be("true"));

        cut.Render(parameters => parameters.Add(component => component.Scope, second));

        firstToken.IsCancellationRequested.Should().BeTrue();
        cut.Find("[data-initial]").Should().NotBeNull();
        pending.SetResult(Ok(Report(first, [Row(first)])));
        await load;
        cut.FindAll("[data-report-row]").Should().BeEmpty();
        cut.Find("#inventory-balance-report-download").HasAttribute("disabled").Should().BeTrue();
    }

    private IRenderedComponent<StockBalanceReportPanel> Panel(InventoryAccessScope scope,
        Warehouse? warehouse = null, ProductVariant? variant = null)
        => Render<StockBalanceReportPanel>(parameters => parameters.Add(component => component.Scope, scope)
            .Add(component => component.Warehouse, warehouse).Add(component => component.Variant, variant));

    private void Reads(Func<string, (StockBalanceReport?, int, string?, string?)> respond)
        => _api.Setup(api => api.ReadInventoryAsync<StockBalanceReport>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => { _reads.Add(path); return Task.FromResult(respond(path)); });

    private static (StockBalanceReport?, int, string?, string?) Ok(StockBalanceReport report) => (report, 200, null, null);
    private static (StockBalanceReport?, int, string?, string?) Failure(int status, string? code = null, string? error = null)
        => (null, status, code, error);
    private static StockBalanceReport Report(InventoryAccessScope scope, IReadOnlyList<StockBalanceReportRow> rows)
        => new(Business(scope), new DateTimeOffset(2026, 9, 25, 12, 34, 56, TimeSpan.Zero), rows);
    private static StockBalanceReportRow Row(InventoryAccessScope scope, Guid? warehouseId = null, Guid? variantId = null,
        string productId = "P-1", string unit = "EA")
        => new(warehouseId ?? Guid.NewGuid(), "WH-1", "Main warehouse", true, variantId ?? Guid.NewGuid(),
            productId, "Product " + productId, true, unit, 12.5m, 2.5m, 10m);
    private static InventoryAccessScope Scope(int index)
    {
        var organization = Guid.Parse($"20000000-0000-0000-0000-{index:000000000000}");
        return new(new BusinessMembership(Tenant, organization, "operator", Guid.NewGuid(), true, 1, ["stock.read"]),
            new(Tenant, organization, "P" + index, Guid.NewGuid(), true), new("P" + index, "Plant " + index, "", "KR", "Asia/Seoul"));
    }
    private static BusinessScope Business(InventoryAccessScope scope)
        => new("NexaOne.MES", scope.Membership.TenantId.ToString("D"), scope.Membership.OrganizationId.ToString("D"));
    private static Warehouse Warehouse(InventoryAccessScope scope)
        => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), "WH-1", "Main warehouse");
    private static ProductVariant Variant(InventoryAccessScope scope)
        => new(Guid.NewGuid(), Business(scope), Guid.NewGuid(), Guid.NewGuid(), "SKU-1", "EA", []);
    private static string Root(InventoryAccessScope scope)
        => $"api/v1/ivt/stock/{scope.Membership.TenantId:D}/{scope.Membership.OrganizationId:D}";
}
