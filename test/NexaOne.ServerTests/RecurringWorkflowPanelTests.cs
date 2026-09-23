using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Components.Pages;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class RecurringWorkflowPanelTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly InventorySessionStorageJs _browser = new();
    private readonly ProtectedSessionStorage _storage;
    private readonly List<(string Path, object Body)> _writes = [];
    private int _saved;
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BusinessUser = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly BusinessScope Scope = new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
    private static string Root => $"api/v1/erp/recurring/{Tenant:D}/{Organization:D}";

    public RecurringWorkflowPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; _storage = new(_browser, new EphemeralDataProtectionProvider());
        var ui = new UiTextService(); ui.Load("EnUs", new()); Services.AddSingleton(_api.Object); Services.AddSingleton(ui); Services.AddSingleton(_storage);
        Writes<RecurringRule>((_, _) => Failure<RecurringRule>(503)); Writes<RecurringExecution>((_, _) => Failure<RecurringExecution>(503));
    }

    [Fact]
    public void Grants_control_create_deactivate_and_execute_actions()
    {
        var cut = Panel(Membership("recurring.read")); cut.FindAll("#recurring-new-rule").Should().BeEmpty();
        cut = Panel(Membership("recurring.write")); cut.Find("#recurring-new-rule").Should().NotBeNull(); cut.FindAll("#recurring-execute").Should().BeEmpty();
    }

    [Fact]
    public async Task Income_rule_is_written_after_a_recoverable_intent()
    {
        RecurringController.RuleCreate? sent = null;
        Writes<RecurringRule>(async (path, body) =>
        {
            (await Stored()).Kind.Should().Be("create"); path.Should().Be(Root + "/rules"); sent = (RecurringController.RuleCreate)body;
            return Ok(new RecurringRule(Guid.NewGuid(), Scope, Guid.NewGuid(), sent.OperationId, new(sent.Name, sent.Schedule, sent.Income!), RecurringTarget.Income, "operator"));
        });
        var cut = Panel(Membership("recurring.write")); cut.Find("#recurring-new-rule").Click(); cut.Find("#recurring-create-target").Change("Income");
        cut.Find("#recurring-name").Change("Monthly income"); cut.Find("#recurring-start").Change("2026-09"); cut.Find("#recurring-day").Change("10");
        cut.Find("#rc-amount").Change("125.5"); cut.Find("#rc-contact").Change(Guid.NewGuid().ToString("D")); cut.Find("#rc-currency").Change("KRW");
        await cut.Find("#recurring-rule-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find("#recurring-selected-rule").TextContent.Should().Contain("Monthly income"));
        sent.Should().NotBeNull(); sent!.Income!.Amount.Should().Be(125.5m); sent.Schedule.Should().Be(new RecurringSchedule(new(2026, 9, 1), null, 10)); _saved.Should().Be(1); _browser.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Billing_rule_sends_document_template_lines()
    {
        RecurringController.RuleCreate? sent = null;
        Writes<RecurringRule>((_, body) => { sent = (RecurringController.RuleCreate)body; return Ok(Created(sent)); });
        var cut = Panel(Membership("recurring.write")); StartRule(cut, "Monthly invoice", "Billing");
        cut.Find("#rc-billing-kind").Change("Invoice"); cut.Find("#rc-contact").Change(Guid.NewGuid().ToString("D")); cut.Find("#rc-due-days").Change("14");
        cut.FindAll(".rc-line-description")[0].Change("Support"); cut.FindAll(".rc-line-price")[0].Change("50"); cut.FindAll(".rc-line-quantity")[0].Change("2");
        await cut.Find("#recurring-rule-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => sent.Should().NotBeNull()); sent!.Billing.Should().NotBeNull(); sent.Billing!.Kind.Should().Be(BillingKind.Invoice); sent.Billing.DueDays.Should().Be(14); sent.Billing.Lines.Should().Equal(new BillingLine("Support", 50m, 2m));
    }

    [Fact]
    public async Task Expense_rule_sends_required_master_references_without_project_or_tags()
    {
        RecurringController.RuleCreate? sent = null;
        Writes<RecurringRule>((_, body) => { sent = (RecurringController.RuleCreate)body; return Ok(Created(sent)); });
        var cut = Panel(Membership("recurring.write")); StartRule(cut, "Monthly rent", "Expense");
        cut.Find("#rc-amount").Change("500"); cut.Find("#rc-category").Change(Guid.NewGuid().ToString("D")); cut.Find("#rc-vendor").Change(Guid.NewGuid().ToString("D")); cut.Find("#rc-purpose").Change("Office rent");
        await cut.Find("#recurring-rule-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => sent.Should().NotBeNull()); sent!.Expense.Should().NotBeNull(); sent.Expense!.Amount.Should().Be(500m); sent.Expense.ProjectId.Should().BeNull(); sent.Expense.TagIds.Should().BeNull();
    }

    [Fact]
    public async Task Execution_retries_the_same_rule_and_month_after_unknown_outcome()
    {
        var attempts = 0; var rule = Rule();
        Writes<RecurringExecution>((path, body) =>
        {
            ++attempts; path.Should().Be(Root + $"/rules/{rule.Id:D}/occurrences"); var command = (RecurringController.OccurrenceCommand)body;
            if (attempts == 1) return Failure<RecurringExecution>(503);
            var occurrence = new RecurringOccurrence(Guid.NewGuid(), Scope, Guid.NewGuid(), rule.Id, command.Month, RecurringTarget.Income, Guid.NewGuid(), Guid.NewGuid(), "operator");
            return Ok(new RecurringExecution(occurrence, Income: null));
        });
        var cut = Panel(Membership("recurring.execute")); await cut.InvokeAsync(() => cut.Instance.SelectRuleAsync(rule)); cut.Find("#recurring-month").Change("2026-10"); cut.Find("#recurring-execute").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-pending").Should().NotBeNull()); var first = (RecurringController.OccurrenceCommand)_writes[0].Body;
        cut.Find("#recurring-retry").Click(); cut.WaitForAssertion(() => cut.Find("#recurring-last-execution").TextContent.Should().Contain("2026-10"));
        ((RecurringController.OccurrenceCommand)_writes[1].Body).Should().Be(first); attempts.Should().Be(2); _browser.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Deactivation_sends_the_selected_version()
    {
        var rule = Rule();
        Writes<RecurringRule>((path, body) => { path.Should().Be(Root + $"/rules/{rule.Id:D}/deactivate"); ((RecurringController.VersionedCommand)body).Version.Should().Be(rule.Version); return Ok(rule with { Active = false, Version = Guid.NewGuid() }); });
        var cut = Panel(Membership("recurring.write")); await cut.InvokeAsync(() => cut.Instance.SelectRuleAsync(rule)); cut.Find("#recurring-deactivate").Click();
        cut.WaitForAssertion(() => cut.Find("#recurring-selected-rule").TextContent.Should().Contain("Inactive")); _saved.Should().Be(1);
    }

    [Fact]
    public async Task Inactive_rule_keeps_execution_available_for_existing_month_replay()
    {
        var cut = Panel(Membership("recurring.execute"));
        await cut.InvokeAsync(() => cut.Instance.SelectRuleAsync(Rule() with { Active = false }));
        cut.Find("#recurring-execute").HasAttribute("disabled").Should().BeFalse();
    }

    private IRenderedComponent<RecurringWorkflowPanel> Panel(BusinessMembership membership)
    {
        var cut = Render<RecurringWorkflowPanel>(p => p.Add(x => x.Membership, membership).Add(x => x.UserId, "operator").Add(x => x.Saved, () => { ++_saved; return Task.CompletedTask; }));
        cut.WaitForAssertion(() => cut.Find("#recurring-workflows").GetAttribute("aria-busy").Should().Be("false")); return cut;
    }
    private static void StartRule(IRenderedComponent<RecurringWorkflowPanel> cut, string name, string target)
    {
        cut.Find("#recurring-new-rule").Click(); cut.Find("#recurring-create-target").Change(target); cut.Find("#recurring-name").Change(name);
        cut.Find("#recurring-start").Change("2026-09"); cut.Find("#recurring-day").Change("10"); cut.Find("#rc-currency").Change("KRW");
    }
    private static RecurringRule Created(RecurringController.RuleCreate value)
    {
        RecurringTemplate template = value.Target switch { RecurringTarget.Billing => value.Billing!, RecurringTarget.Income => value.Income!, _ => value.Expense! };
        return new(Guid.NewGuid(), Scope, Guid.NewGuid(), value.OperationId, new(value.Name, value.Schedule, template), value.Target, "operator");
    }
    private void Writes<T>(Func<string, object, (T?, int, string?, string?)> response) where T : class => _api.Setup(x => x.WriteInventoryAsync<T>(HttpMethod.Post, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>())).Returns((HttpMethod _, string path, object body, string _, CancellationToken _) => { _writes.Add((path, body)); return Task.FromResult(response(path, body)); });
    private void Writes<T>(Func<string, object, Task<(T?, int, string?, string?)>> response) where T : class => _api.Setup(x => x.WriteInventoryAsync<T>(HttpMethod.Post, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>())).Returns((HttpMethod _, string path, object body, string _, CancellationToken _) => { _writes.Add((path, body)); return response(path, body); });
    private async Task<RecurringWorkflowPanel.PendingWrite> Stored() { var value = await _storage.GetAsync<RecurringWorkflowPanel.PendingWrite>(_browser.Values.Keys.Single()); value.Success.Should().BeTrue(); return value.Value!; }
    private static (T?, int, string?, string?) Ok<T>(T value) where T : class => (value, 200, null, null); private static (T?, int, string?, string?) Failure<T>(int status) where T : class => (null, status, "FAILED", "failed");
    private static BusinessMembership Membership(params string[] grants) => new(Tenant, Organization, "operator", BusinessUser, true, 1, grants);
    private static RecurringRule Rule() { var operation = Guid.NewGuid(); return new(operation, Scope, Guid.NewGuid(), operation, new("Monthly income", new(new(2026, 9, 1), null, 10), new RecurringIncomeTemplate(10m, Guid.NewGuid(), null, "KRW")), RecurringTarget.Income, "operator"); }
}
