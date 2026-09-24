using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaOne.Server.Components.Pages;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class FinancialReportHistoryPageTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly Action<string> _changeUser;
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public FinancialReportHistoryPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = this.AddAuthorization();
        authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator"));
        authorization.SetAuthorized("operator");
        _changeUser = name => authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, name));
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new UiTextService());
        Read<BusinessMembership>(_ => new BusinessPage<BusinessMembership>([], 0));
        Read<FinancialReportSnapshotSummary>(_ => new BusinessPage<FinancialReportSnapshotSummary>([], 0));
        Read<FinancialReportSnapshotAuditEntry>(_ => new BusinessPage<FinancialReportSnapshotAuditEntry>([], 0));
    }

    [Fact]
    public void Empty_scope_list_is_a_successful_state()
    {
        var cut = Render<HostFinancialReportHistory>();

        cut.WaitForAssertion(() => Paths<BusinessMembership>().Should()
            .Equal("api/v1/erp/financial-reports/scopes/me?offset=0&limit=50"));
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Find("[data-empty]").TextContent.Should().Contain("접근 가능한 재무 보고 범위가 없습니다");
    }

    [Fact]
    public void Selected_scope_loads_only_its_snapshot_history()
    {
        ShowScope();
        var summary = Summary(Guid.NewGuid());
        Read<FinancialReportSnapshotSummary>(_ => new([summary], 1));
        var cut = Render<HostFinancialReportHistory>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());

        cut.Find("[data-scope]").Click();

        cut.WaitForAssertion(() => cut.Find($"[data-select-report='{summary.Id}']").Should().NotBeNull());
        Paths<FinancialReportSnapshotSummary>().Should().Equal(
            $"api/v1/erp/report-snapshots/{Tenant:D}/{Organization:D}?offset=0&limit=50");
    }

    [Fact]
    public void Uncertain_create_reuses_operation_and_recovers_from_history()
    {
        ShowScope();
        CreateFinancialReportSnapshotRequest? first = null;
        var writes = 0;
        _api.Setup(api => api.WriteInventoryAsync<FinancialReportSnapshot>(HttpMethod.Post,
                $"api/v1/erp/report-snapshots/{Tenant:D}/{Organization:D}", It.IsAny<object>(), "operator",
                It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                var request = (CreateFinancialReportSnapshotRequest)body;
                first ??= request;
                request.OperationId.Should().Be(first.OperationId);
                writes++;
                return Task.FromResult<(FinancialReportSnapshot?, int, string?, string?)>(
                    (null, 503, "INVENTORY_RESPONSE_UNAVAILABLE", "unknown outcome"));
            });
        Read<FinancialReportSnapshotSummary>(_ => writes < 2 || first is null
            ? new([], 0)
            : new([Summary(first.OperationId, first.Kind, first.Start, first.End)], 1));
        var cut = Render<HostFinancialReportHistory>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        cut.Find("#report-create").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("unknown outcome"));
        cut.Find("#report-kind").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#report-create").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=alert]").Should().BeEmpty());
        cut.Find("#report-kind").HasAttribute("disabled").Should().BeFalse();
        writes.Should().Be(2);
    }

    [Fact]
    public void Definite_client_error_allows_a_corrected_request_with_a_new_operation_id()
    {
        ShowScope();
        var operations = new List<Guid>();
        _api.Setup(api => api.WriteInventoryAsync<FinancialReportSnapshot>(HttpMethod.Post,
                $"api/v1/erp/report-snapshots/{Tenant:D}/{Organization:D}", It.IsAny<object>(), "operator",
                It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, object body, string _, CancellationToken _) =>
            {
                operations.Add(((CreateFinancialReportSnapshotRequest)body).OperationId);
                return Task.FromResult<(FinancialReportSnapshot?, int, string?, string?)>(
                    (null, 400, "INVALID_PERIOD", "invalid period"));
            });
        var cut = Render<HostFinancialReportHistory>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();

        cut.Find("#report-create").Click();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("invalid period"));
        cut.Find("#report-kind").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#report-create").Click();

        cut.WaitForAssertion(() => operations.Should().HaveCount(2));
        operations.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Authentication_change_rejects_a_late_scope_response()
    {
        var oldScope = new BusinessMembership(Tenant, Organization, "operator", Guid.NewGuid(), true, 1,
            ["financial-report.read"]);
        var newScope = new BusinessMembership(Tenant, Guid.NewGuid(), "replacement", Guid.NewGuid(), true, 1,
            ["financial-report.read"]);
        var pending = new TaskCompletionSource<(BusinessPage<BusinessMembership>?, int, string?, string?)>();
        CancellationToken oldToken = default;
        var calls = 0;
        _api.Setup(api => api.ReadInventoryAsync<BusinessPage<BusinessMembership>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                if (calls++ == 0) { oldToken = token; return pending.Task; }
                return Task.FromResult<(BusinessPage<BusinessMembership>?, int, string?, string?)>(
                    (new([newScope], 1), 200, null, null));
            });
        var cut = Render<HostFinancialReportHistory>();
        cut.WaitForAssertion(() => oldToken.CanBeCanceled.Should().BeTrue());

        await cut.InvokeAsync(() => _changeUser("replacement"));
        cut.WaitForAssertion(() => oldToken.IsCancellationRequested.Should().BeTrue());
        pending.SetResult((new([oldScope], 1), 200, null, null));

        cut.WaitForAssertion(() => cut.Find("[data-scope]").GetAttribute("data-scope").Should()
            .Be($"{newScope.TenantId:D}/{newScope.OrganizationId:D}"));
    }

    private void ShowScope() => Read<BusinessMembership>(_ => new([new BusinessMembership(
        Tenant, Organization, "operator", Guid.NewGuid(), true, 1, ["financial-report.read"])], 1));

    private void Read<T>(Func<string, BusinessPage<T>> response)
        => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult<(BusinessPage<T>?, int, string?, string?)>((response(path), 200, null, null)));

    private string[] Paths<T>() => _api.Invocations.Where(call => call.Method.Name == nameof(IApiClient.ReadInventoryAsync)
        && call.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(call => call.Arguments[0]).OfType<string>().ToArray();

    private static FinancialReportSnapshotSummary Summary(Guid id) => new(id,
        FinancialReportSnapshotKind.Financial, new(2026, 9, 1), new(2026, 9, 30),
        new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), "operator", new string('a', 64));
    private static FinancialReportSnapshotSummary Summary(Guid id, FinancialReportSnapshotKind kind,
        DateOnly start, DateOnly end) => new(id, kind, start, end,
        new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), "operator", new string('a', 64));
}
