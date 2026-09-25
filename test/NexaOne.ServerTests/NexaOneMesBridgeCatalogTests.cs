using FluentAssertions;
using NexaOne.Server;
using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Collaboration;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class NexaOneMesBridgeCatalogTests
{
    [Fact]
    public void Product_catalog_is_explicit_complete_and_deterministic()
    {
        var first = NexaOneMesBridgeCatalog.Create();
        var second = NexaOneMesBridgeCatalog.Create();

        first.Descriptors.Should().HaveCount(66);
        first.Descriptors.Should().Equal(second.Descriptors);
        first.Descriptors.Should().OnlyContain(descriptor =>
            descriptor.ContractType.IsInterface
            && typeof(INexaModuleBridge).IsAssignableFrom(descriptor.ContractType));
        var declaredContracts = typeof(INexaModuleBridge).Assembly
            .GetTypes()
            .Where(type => type.IsInterface
                           && type != typeof(INexaModuleBridge)
                           && typeof(INexaModuleBridge).IsAssignableFrom(type))
            .ToArray();
        first.Descriptors.Select(static descriptor => descriptor.ContractType)
            .Should().BeEquivalentTo(declaredContracts,
                "the explicit product catalog must be updated whenever a marker contract is added or removed");
        var orderingKeys = first.Descriptors
            .Select(descriptor =>
                $"{descriptor.Module}\0{descriptor.BeanName}\0{descriptor.ContractType.FullName}")
            .ToArray();
        orderingKeys.Should().Equal(orderingKeys.OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void TryGet_returns_the_host_owned_binding()
    {
        INexaModuleBridgeCatalog catalog = NexaOneMesBridgeCatalog.Create();

        catalog.TryGet(typeof(IOeePlanDirectory), out var plan).Should().BeTrue();
        plan.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IOeePlanDirectory), "Mdm", "oeePlanDirectory"));
        catalog.TryGet(typeof(IBusinessMasterDirectory), out var businessMaster).Should().BeTrue();
        businessMaster.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IBusinessMasterDirectory), "Mdm", "businessMasterDirectory"));
        catalog.TryGet(typeof(IOeeProductionDirectory), out var production).Should().BeTrue();
        production.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IOeeProductionDirectory), "Pom", "oeeProductionDirectory"));
        catalog.TryGet(typeof(ITraceMaterialBridge), out var traceMaterial).Should().BeTrue();
        traceMaterial.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(ITraceMaterialBridge), "Ivt", "traceMaterialBridge"));
        catalog.TryGet(typeof(IStockBridge), out var stock).Should().BeTrue();
        stock.Should().Be(new NexaModuleBridgeDescriptor(typeof(IStockBridge), "Ivt", "stockBridge"));
        catalog.TryGet(typeof(IStockBillingDirectory), out var stockBilling).Should().BeTrue();
        stockBilling.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IStockBillingDirectory), "Ivt", "stockBillingDirectory"));
        catalog.TryGet(typeof(IBillingBridge), out var billing).Should().BeTrue();
        billing.Should().Be(new NexaModuleBridgeDescriptor(typeof(IBillingBridge), "Erp", "billingBridge"));
        catalog.TryGet(typeof(ICrmBridge), out var crm).Should().BeTrue();
        crm.Should().Be(new NexaModuleBridgeDescriptor(typeof(ICrmBridge), "Crm", "crmBridge"));
        catalog.TryGet(typeof(IBusinessProjectDirectory), out var projects).Should().BeTrue();
        projects.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IBusinessProjectDirectory), "Crm", "businessProjectDirectory"));
        catalog.TryGet(typeof(IDeliveryBridge), out var delivery).Should().BeTrue();
        delivery.Should().Be(new NexaModuleBridgeDescriptor(typeof(IDeliveryBridge), "Erp", "deliveryBridge"));
        catalog.TryGet(typeof(IDeliveryAutomationBridge), out var deliveryAutomation).Should().BeTrue();
        deliveryAutomation.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IDeliveryAutomationBridge), "Erp", "deliveryAutomationBridge"));
        catalog.TryGet(typeof(IExpenseBridge), out var expense).Should().BeTrue();
        expense.Should().Be(new NexaModuleBridgeDescriptor(typeof(IExpenseBridge), "Erp", "expenseBridge"));
        catalog.TryGet(typeof(IExpensePayoutAutomationBridge), out var expensePayoutAutomation).Should().BeTrue();
        expensePayoutAutomation.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IExpensePayoutAutomationBridge), "Erp", "expensePayoutAutomationBridge"));
        catalog.TryGet(typeof(IFinancialReportBridge), out var financialReport).Should().BeTrue();
        financialReport.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IFinancialReportBridge), "Erp", "financialReportBridge"));
        catalog.TryGet(typeof(IBusinessReportBridge), out var businessReport).Should().BeTrue();
        businessReport.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IBusinessReportBridge), "Erp", "businessReportBridge"));
        catalog.TryGet(typeof(IRecurringBridge), out var recurring).Should().BeTrue();
        recurring.Should().Be(new NexaModuleBridgeDescriptor(typeof(IRecurringBridge), "Erp", "recurringBridge"));
        catalog.TryGet(typeof(IRecurringAutomationBridge), out var recurringAutomation).Should().BeTrue();
        recurringAutomation.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IRecurringAutomationBridge), "Erp", "recurringAutomationBridge"));
        catalog.TryGet(typeof(IWorkScopeBridge), out var workScope).Should().BeTrue();
        workScope.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IWorkScopeBridge), "Pom", "workScopeBridge"));
        catalog.TryGet(typeof(IWorkScopeProjectionBridge), out var workScopeProjection)
            .Should().BeTrue();
        workScopeProjection.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IWorkScopeProjectionBridge), "Pom", "workScopeProjectionBridge"));
        catalog.TryGet(typeof(IWorkScopeProjectionAuthorityBridge), out var projectionAuthority)
            .Should().BeTrue();
        projectionAuthority.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IWorkScopeProjectionAuthorityBridge), "Pom", "workScopeProjectionAuthorityBridge"));
        catalog.TryGet(typeof(IDisposable), out _).Should().BeFalse();
    }

    [Fact]
    public void Create_rejects_non_bridge_contracts()
    {
        var act = () => NexaOneMesBridgeCatalog.Create(
            new NexaModuleBridgeDescriptor(typeof(IDisposable), "Sys", "invalid"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*IDisposable*");
    }

    [Theory]
    [InlineData(" ", "bean")]
    [InlineData("Mdm", "\t")]
    public void Create_rejects_blank_bindings(string module, string beanName)
    {
        var act = () => NexaOneMesBridgeCatalog.Create(
            new NexaModuleBridgeDescriptor(typeof(IEquipmentDirectory), module, beanName));

        act.Should().Throw<InvalidOperationException>().WithMessage("*blank*");
    }

    [Fact]
    public void Create_rejects_duplicate_contracts_and_bindings()
    {
        var duplicateContract = () => NexaOneMesBridgeCatalog.Create(
            new NexaModuleBridgeDescriptor(typeof(IEquipmentDirectory), "Mdm", "first"),
            new NexaModuleBridgeDescriptor(typeof(IEquipmentDirectory), "Mdm", "second"));
        duplicateContract.Should().Throw<InvalidOperationException>().WithMessage("*duplicated*");

        var duplicateBinding = () => NexaOneMesBridgeCatalog.Create(
            new NexaModuleBridgeDescriptor(typeof(IEquipmentDirectory), "Mdm", "shared"),
            new NexaModuleBridgeDescriptor(typeof(IOeePlanDirectory), "Mdm", "shared"));
        duplicateBinding.Should().Throw<InvalidOperationException>().WithMessage("*duplicated*");
    }
}
