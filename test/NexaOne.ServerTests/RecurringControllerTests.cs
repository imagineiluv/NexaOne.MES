using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class RecurringControllerTests
{
    [Fact]
    public async Task Create_and_execute_forward_route_scope_and_json_safe_template()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var ruleId = Guid.NewGuid();
        var contact = Guid.NewGuid(); var operation = Guid.NewGuid(); var month = new DateOnly(2026, 9, 1);
        var schedule = new RecurringSchedule(month, null, 15);
        var template = new RecurringIncomeTemplate(10m, contact, null, "KRW");
        var command = new RecurringController.RuleCreate(operation, "Monthly", schedule,
            RecurringTarget.Income, Income: template);
        var input = new RecurringRuleInput(command.Name, schedule, template);
        var scope = new BusinessScope("NexaOne.MES", tenant.ToString("D"), organization.ToString("D"));
        var rule = new RecurringRule(ruleId, scope, Guid.NewGuid(), operation, input,
            RecurringTarget.Income, "actor");
        var occurrence = new RecurringOccurrence(Guid.NewGuid(), scope, Guid.NewGuid(), ruleId,
            month, RecurringTarget.Income, Guid.NewGuid(), Guid.NewGuid(), "actor");
        var execution = new RecurringExecution(occurrence, Income: new(occurrence.ResourceId, scope,
            Guid.NewGuid(), occurrence.ResourceOperationId,
            new(10m, contact, null, "KRW", new(2026, 9, 15)), "actor", IncomeState.Active));
        var bridge = new Mock<IRecurringBridge>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        bridge.Setup(x => x.CreateRuleAsync("recurring-user", tenant, organization, operation,
            It.Is<RecurringRuleInput>(value => value.Name == "Monthly" && ReferenceEquals(value.Template, template)),
            cancellation.Token)).ReturnsAsync(rule);
        bridge.Setup(x => x.ExecuteOccurrenceAsync("recurring-user", tenant, organization,
            ruleId, month, cancellation.Token)).ReturnsAsync(execution);
        var controller = Controller(bridge.Object);

        (await controller.CreateRule(tenant, organization, command, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(rule);
        (await controller.ExecuteOccurrence(tenant, organization, ruleId,
            new(month), cancellation.Token)).Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(execution);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Ambiguous_template_is_bad_request_without_calling_bridge()
    {
        var bridge = new Mock<IRecurringBridge>(MockBehavior.Strict);
        var command = new RecurringController.RuleCreate(Guid.NewGuid(), "Ambiguous",
            new(new(2026, 9, 1), null, 1), RecurringTarget.Income,
            Billing: new(BillingKind.Invoice, Guid.NewGuid(), 0, "KRW", [new("Line", 1m, 1m)]),
            Income: new(1m, Guid.NewGuid(), null, "KRW"));

        var result = (await Controller(bridge.Object).CreateRule(Guid.NewGuid(), Guid.NewGuid(),
            command, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString()
            .Should().Be("INVALID_BUSINESS_INPUT");
        bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Occurrence_history_forwards_route_scope_query_and_user()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var query = new RecurringOccurrenceQuery(Guid.NewGuid(), RecurringTarget.Expense,
            new(2026, 9, 1), new(2026, 11, 1), 50, 25);
        var expected = new BusinessPage<RecurringOccurrenceHistoryItem>([], 0);
        var bridge = new Mock<IRecurringBridge>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        bridge.Setup(x => x.ListOccurrencesAsync("recurring-user", tenant, organization,
            query, cancellation.Token)).ReturnsAsync(expected);

        (await Controller(bridge.Object).ListOccurrences(
            tenant, organization, query, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(expected);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    private static RecurringController Controller(IRecurringBridge bridge, string? userId = "recurring-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] :
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        return new(bridge, Mock.Of<ILogger<RecurringController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
