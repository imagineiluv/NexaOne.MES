using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ExpenseControllerTests
{
    [Fact]
    public async Task Create_reimburse_and_invoice_link_forward_authoritative_route_scope_and_operations()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var expenseId = Guid.NewGuid();
        var category = Guid.NewGuid(); var vendor = Guid.NewGuid(); var expenseVersion = Guid.NewGuid();
        var invoiceId = Guid.NewGuid(); var invoiceVersion = Guid.NewGuid();
        var input = new ExpenseInput(12.345678m, ExpenseType.TaxDeductible, category, vendor,
            null, null, null, "KRW", new(2026, 9, 23));
        var expense = Record(expenseId, tenant, organization, expenseVersion, input);
        var employees = new BusinessPage<ExpenseEmployee>([new(Guid.NewGuid(), "expense-user")], 1);
        var bridge = new Mock<IExpenseBridge>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        var create = new ExpenseController.ExpenseCreate(Guid.NewGuid(), input);
        bridge.Setup(x => x.CreateExpenseAsync("expense-user", tenant, organization,
            create.OperationId, input, cancellation.Token)).ReturnsAsync(expense);
        bridge.Setup(x => x.ListEmployeesAsync("expense-user", tenant, organization, 50, 25,
            cancellation.Token)).ReturnsAsync(employees);
        var reimbursement = new ExpenseController.ReimbursementCommand(Guid.NewGuid(), expenseVersion,
            new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero), "BANK");
        bridge.Setup(x => x.ReimburseExpenseAsync("expense-user", tenant, organization, reimbursement.OperationId,
            expenseId, expenseVersion, reimbursement.PaidAt, reimbursement.Reference, cancellation.Token)).ReturnsAsync(expense);
        var link = new ExpenseController.InvoiceLinkCommand(Guid.NewGuid(), expenseVersion,
            invoiceId, invoiceVersion, "Travel");
        var invoice = new BillingDocument(invoiceId, expense.Scope, invoiceVersion, Guid.NewGuid(), BillingKind.Invoice,
            1, new(Guid.NewGuid(), new(2026, 9, 23), new(2026, 10, 23), "KRW", [new("Base", 1m, 1m)]),
            new(1m, 0m, 0m, 1m), BillingStatus.Draft, "actor");
        var linked = new ExpenseInvoiceLink(expense, invoice);
        bridge.Setup(x => x.LinkInvoiceAsync("expense-user", tenant, organization, link.OperationId,
            expenseId, expenseVersion, invoiceId, invoiceVersion, link.Description, cancellation.Token)).ReturnsAsync(linked);
        var controller = Controller(bridge.Object);

        (await controller.CreateExpense(tenant, organization, create, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(expense);
        (await controller.ListEmployees(tenant, organization, cancellation.Token, 50, 25))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(employees);
        (await controller.Reimburse(tenant, organization, expenseId, reimbursement, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(expense);
        (await controller.LinkInvoice(tenant, organization, expenseId, link, cancellation.Token))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(linked);
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_principal_and_business_failures_follow_gateway_conventions()
    {
        var bridge = new Mock<IExpenseBridge>(MockBehavior.Strict);
        var anonymous = Controller(bridge.Object, userId: null);
        (await anonymous.GetExpense(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<UnauthorizedResult>();
        bridge.VerifyNoOtherCalls();

        foreach (var (code, status) in new[]
        {
            ("EXPENSE_NOT_FOUND", 404), ("INVALID_BUSINESS_INPUT", 400),
            ("EXPENSE_OPERATION_CONFLICT", 409)
        })
        {
            var failing = Failing(new BusinessException(code));
            var result = (await failing.GetExpense(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
                .Should().BeOfType<ObjectResult>().Which;
            result.StatusCode.Should().Be(status);
            JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString().Should().Be(code);
        }
        (await Failing(new BusinessException("BUSINESS_ACCESS_DENIED"))
            .GetExpense(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        var conflict = (await Failing(new DBConcurrencyException("private"))
            .GetExpense(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>().Which;
        JsonSerializer.SerializeToElement(conflict.Value).GetProperty("code").GetString()
            .Should().Be("BUSINESS_VERSION_CONFLICT");
    }

    [Fact]
    public async Task Tag_routes_forward_scope_query_and_versions()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var id = Guid.NewGuid();
        var version = Guid.NewGuid(); var nextVersion = Guid.NewGuid();
        var scope = new BusinessScope("NexaOne.MES", tenant.ToString("D"), organization.ToString("D"));
        var tag = new ExpenseTag(id, scope, version, new("Travel"));
        var updated = tag with { Version = nextVersion, Input = new("Business travel") };
        var query = new ExpenseTagQuery("trav", true, 25, 10);
        var bridge = new Mock<IExpenseBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.ListTagsAsync("expense-user", tenant, organization, query, CancellationToken.None))
            .ReturnsAsync(new BusinessPage<ExpenseTag>([tag], 1));
        bridge.Setup(x => x.CreateTagAsync("expense-user", tenant, organization,
            tag.Input, CancellationToken.None)).ReturnsAsync(tag);
        bridge.Setup(x => x.GetTagAsync("expense-user", tenant, organization, id,
            CancellationToken.None)).ReturnsAsync(tag);
        bridge.Setup(x => x.UpdateTagAsync("expense-user", tenant, organization, id, version,
            updated.Input, CancellationToken.None)).ReturnsAsync(updated);
        bridge.Setup(x => x.SetTagActiveAsync("expense-user", tenant, organization, id, nextVersion,
            false, CancellationToken.None)).ReturnsAsync(updated with { Active = false });
        var controller = Controller(bridge.Object);

        (await controller.ListTags(tenant, organization, query, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await controller.CreateTag(tenant, organization, tag.Input, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await controller.GetTag(tenant, organization, id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await controller.UpdateTag(tenant, organization, id,
            new(version, updated.Input), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await controller.SetTagActive(tenant, organization, id,
            new(nextVersion, false), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll(); bridge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Database_failures_are_sanitized_and_logged_as_unknown_outcome()
    {
        var failure = new AggregateException("private", new StorageFailure("private database"));
        var logger = new Mock<ILogger<ExpenseController>>();
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var result = (await Failing(failure, logger.Object, services)
            .GetExpense(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Title.Should().Be("Expense storage is unavailable.");
        problem.Detail.Should().Contain("same operation ID and original payload");
        JsonSerializer.Serialize(problem).Should().NotContain("private");
        logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.Is<Exception?>(error => ReferenceEquals(error, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private static ExpenseController Failing(Exception failure, ILogger<ExpenseController>? logger = null,
        IServiceProvider? services = null)
    {
        var bridge = new Mock<IExpenseBridge>(MockBehavior.Strict);
        bridge.Setup(x => x.GetExpenseAsync("expense-user", It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), CancellationToken.None)).ThrowsAsync(failure);
        return Controller(bridge.Object, logger, services);
    }

    private static ExpenseController Controller(IExpenseBridge bridge, ILogger<ExpenseController>? logger = null,
        IServiceProvider? services = null, string? userId = "expense-user")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(userId is null ? [] :
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
        if (services is not null) context.RequestServices = services;
        return new(bridge, logger ?? Mock.Of<ILogger<ExpenseController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static ExpenseRecord Record(Guid id, Guid tenant, Guid organization, Guid version, ExpenseInput input)
        => new(id, new("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")), version,
            Guid.NewGuid(), input, "actor", ExpenseStatus.NotBillable, ExpenseState.Active);

    private sealed class StorageFailure(string message) : DbException(message);
}
