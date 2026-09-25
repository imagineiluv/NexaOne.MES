using Microsoft.Extensions.Configuration;
using NexaFramework.Service;
using NexaFramework.Scheduling;
using NexaOne.ERP;
using NexaOne.ERP.Application.Expense;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.UnitTests.Erp;

public sealed class ExpensePayoutWorkerTests
{
    [Fact]
    public void Enabled_configuration_requires_user_and_bounds_settings()
    {
        var missing = Configuration(new() { ["Worker:Erp:ExpensePayout:Enabled"] = "true" });
        Action invalid = () => ErpModuleOptions.FromConfiguration(missing);
        invalid.Should().Throw<InvalidOperationException>().WithMessage("*UserId*");

        var configured = Configuration(new()
        {
            ["Worker:Erp:ExpensePayout:Enabled"] = "true",
            ["Worker:Erp:ExpensePayout:UserId"] = " payout-service ",
            ["Worker:Erp:ExpensePayout:IntervalSeconds"] = "1",
            ["Worker:Erp:ExpensePayout:LeaseSeconds"] = "90",
            ["Worker:Erp:ExpensePayout:BatchSize"] = "40"
        });
        var options = ErpModuleOptions.FromConfiguration(configured);
        options.ExpensePayoutEnabled.Should().BeTrue();
        options.ExpensePayoutUserId.Should().Be("payout-service");
        options.ExpensePayoutIntervalSeconds.Should().Be(10);
        options.ExpensePayoutLeaseSeconds.Should().Be(90);
        options.ExpensePayoutBatchSize.Should().Be(40);
    }

    [Fact]
    public async Task Worker_completes_provider_acceptance_and_normalizes_failure()
    {
        var scope = Membership();
        var accepted = Request(scope);
        var rejected = Request(scope);
        var automation = Automation(scope, [accepted, rejected]);
        var provider = new Mock<IExpensePayoutProvider>();
        provider.SetupSequence(x => x.PayAsync(It.IsAny<ExpensePayoutInstruction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExpensePayoutReceipt("BANK-1", new(2026, 9, 25, 1, 0, 0, TimeSpan.Zero)))
            .ThrowsAsync(new ExpensePayoutProviderException(ExpensePayoutProviderError.Rejected));
        var providers = new Mock<IExpensePayoutProviderRegistry>();
        providers.Setup(x => x.GetRequired("test-bank")).Returns(provider.Object);

        await Worker(automation.Object, providers.Object).RunAsync(CancellationToken.None);

        automation.Verify(x => x.CompleteAsync("payout-service", scope.TenantId, scope.OrganizationId,
            accepted.Id, accepted.Version, accepted.LeaseId!.Value,
            It.Is<ExpensePayoutReceipt>(r => r.Reference == "BANK-1"), It.IsAny<CancellationToken>()), Times.Once);
        automation.Verify(x => x.FailAsync("payout-service", scope.TenantId, scope.OrganizationId,
            rejected.Id, rejected.Version, rejected.LeaseId!.Value, "EXPENSE_PAYOUT_REJECTED",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Accepted_provider_result_with_uncertain_settlement_is_not_failed()
    {
        var scope = Membership();
        var request = Request(scope);
        var automation = Automation(scope, [request]);
        automation.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ExpensePayoutReceipt>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var provider = new Mock<IExpensePayoutProvider>();
        provider.Setup(x => x.PayAsync(It.IsAny<ExpensePayoutInstruction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExpensePayoutReceipt("BANK-1", new(2026, 9, 25, 1, 0, 0, TimeSpan.Zero)));
        var providers = new Mock<IExpensePayoutProviderRegistry>();
        providers.Setup(x => x.GetRequired("test-bank")).Returns(provider.Object);

        await Worker(automation.Object, providers.Object).RunAsync(CancellationToken.None);

        automation.Verify(x => x.FailAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ExpensePayoutWorker Worker(IExpensePayoutAutomationBridge automation,
        IExpensePayoutProviderRegistry providers)
        => new(Mock.Of<IRecurringScheduler>(), automation, providers, true, "payout-service",
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 25);

    private static Mock<IExpensePayoutAutomationBridge> Automation(
        BusinessMembership scope, IReadOnlyList<ExpensePayoutRequest> requests)
    {
        var automation = new Mock<IExpensePayoutAutomationBridge>();
        automation.Setup(x => x.ListScopesAsync("payout-service", 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<BusinessMembership>([scope], 1));
        automation.Setup(x => x.ClaimDueAsync("payout-service", scope.TenantId, scope.OrganizationId,
                25, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(requests);
        return automation;
    }

    private static BusinessMembership Membership()
        => new(Guid.NewGuid(), Guid.NewGuid(), "payout-service", Guid.NewGuid(), true, 1, ["expense.reimburse"]);

    private static ExpensePayoutRequest Request(BusinessMembership membership)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new BusinessScope("NexaOne.MES",
                membership.TenantId.ToString("D"), membership.OrganizationId.ToString("D")),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 110m, "KRW", "test-bank",
            ExpensePayoutState.Processing, 1, DateTimeOffset.UtcNow, "creator",
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
}
