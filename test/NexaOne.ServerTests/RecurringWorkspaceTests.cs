using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Components.Pages;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class RecurringWorkspaceTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public RecurringWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = this.AddAuthorization(); auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "operator")); auth.SetAuthorized("operator");
        Services.AddSingleton(_api.Object); Services.AddSingleton(new UiTextService());
        Services.AddSingleton(new ProtectedSessionStorage(new InventorySessionStorageJs(), new EphemeralDataProtectionProvider()));
        Reads<BusinessMembership>(_ => new([], 0)); Reads<RecurringRule>(_ => new([], 0));
        Reads<RecurringOccurrenceHistoryItem>(_ => new([], 0));
    }

    [Fact]
    public void Empty_recurring_scope_list_is_a_success_state()
    {
        var cut = Render<HostRecurringWorkspace>();
        cut.WaitForAssertion(() => cut.Find("#recurring-scopes [data-empty]").TextContent.Should().Contain("접근 가능한"));
        Paths<BusinessMembership>().Should().Equal("api/v1/erp/recurring/scopes/me?offset=0&limit=50");
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public void Recurring_only_membership_can_select_scope_and_filter_rules()
    {
        var scope = Membership("recurring.read", "recurring.write", "recurring.execute");
        Reads<BusinessMembership>(_ => new([scope], 1));
        var rule = Rule(RecurringTarget.Income);
        Reads<RecurringRule>(path => path.Contains("target=Income") && path.Contains("active=true") ? new([rule], 1) : new([], 0));
        var cut = Render<HostRecurringWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull()); cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-rules").Should().NotBeNull());
        cut.Find("#recurring-target").Change("Income"); cut.Find("#recurring-active").Change("true"); cut.Find("#recurring-rules-search").Click();
        cut.WaitForAssertion(() => cut.Find("[data-select-rule]").TextContent.Should().Contain("규칙 선택"));
        Paths<RecurringRule>().Last().Should().Contain("target=Income").And.Contain("active=true");
        cut.Find("[data-select-rule]").Click(); cut.WaitForAssertion(() => cut.Find("#recurring-selected-rule").TextContent.Should().Contain("Monthly income"));
    }

    [Fact]
    public void Write_only_membership_keeps_actions_without_reading_rules()
    {
        var scope = Membership("recurring.write"); Reads<BusinessMembership>(_ => new([scope], 1));
        var cut = Render<HostRecurringWorkspace>(); cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull()); cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-new-rule").Should().NotBeNull());
        cut.Find("#recurring-no-read-access").Should().NotBeNull(); Paths<RecurringRule>().Should().BeEmpty();
        Paths<RecurringOccurrenceHistoryItem>().Should().BeEmpty();
    }

    [Fact]
    public void Occurrence_history_supports_filters_and_rule_drill_down()
    {
        var scope = Membership("recurring.read");
        var rule = Rule(RecurringTarget.Income);
        var occurrence = new RecurringOccurrence(Guid.NewGuid(), rule.Scope, Guid.NewGuid(), rule.Id,
            new(2026, 11, 1), RecurringTarget.Income, Guid.NewGuid(), Guid.NewGuid(), "operator");
        Reads<BusinessMembership>(_ => new([scope], 1));
        Reads<RecurringRule>(_ => new([rule], 1));
        Reads<RecurringOccurrenceHistoryItem>(path => path.Contains("target=Income")
            && path.Contains("startMonth=2026-10-01") && path.Contains("endMonth=2026-11-01")
                ? new([new(occurrence, rule.Input.Name)], 1) : new([], 0));

        var cut = Render<HostRecurringWorkspace>();
        cut.WaitForAssertion(() => cut.Find("[data-scope]").Should().NotBeNull());
        cut.Find("[data-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-history").Should().NotBeNull());
        cut.Find("#recurring-history-target").Change("Income");
        cut.Find("#recurring-history-start").Change("2026-10");
        cut.Find("#recurring-history-end").Change("2026-11");
        cut.Find("#recurring-history-search").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-history tbody").TextContent
            .Should().Contain("Monthly income").And.Contain("2026-11"));
        Paths<RecurringOccurrenceHistoryItem>().Last().Should().Contain("target=Income")
            .And.Contain("startMonth=2026-10-01").And.Contain("endMonth=2026-11-01");

        cut.Find("[data-rule-history]").Click();
        cut.WaitForAssertion(() => Paths<RecurringOccurrenceHistoryItem>().Last()
            .Should().Contain($"ruleId={rule.Id:D}"));
        cut.Find("#recurring-history-all-rules").Click();
        cut.WaitForAssertion(() => Paths<RecurringOccurrenceHistoryItem>().Last()
            .Should().NotContain("ruleId="));
    }

    private void Reads<T>(Func<string, BusinessPage<T>> response) => _api.Setup(api => api.ReadInventoryAsync<BusinessPage<T>>(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string path, CancellationToken _) => Task.FromResult<(BusinessPage<T>?, int, string?, string?)>((response(path), 200, null, null)));
    private string[] Paths<T>() => _api.Invocations.Where(x => x.Method.Name == nameof(IApiClient.ReadInventoryAsync) && x.Method.GetGenericArguments()[0] == typeof(BusinessPage<T>)).Select(x => (string)x.Arguments[0]).ToArray();
    private static BusinessMembership Membership(params string[] grants) => new(Tenant, Organization, "operator", Guid.NewGuid(), true, 1, grants);
    private static RecurringRule Rule(RecurringTarget target) => new(Guid.NewGuid(), new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D")), Guid.NewGuid(), Guid.NewGuid(), new("Monthly income", new(new(2026, 9, 1), null, 10), new RecurringIncomeTemplate(10m, Guid.NewGuid(), null, "KRW")), target, "operator");
}
