using FluentAssertions;
using NexaOne.Server;
using NexaOne.ServiceContracts;
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

        first.Descriptors.Should().HaveCount(49);
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
        catalog.TryGet(typeof(IOeeProductionDirectory), out var production).Should().BeTrue();
        production.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IOeeProductionDirectory), "Pom", "oeeProductionDirectory"));
        catalog.TryGet(typeof(ITraceMaterialBridge), out var traceMaterial).Should().BeTrue();
        traceMaterial.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(ITraceMaterialBridge), "Ivt", "traceMaterialBridge"));
        catalog.TryGet(typeof(IWorkScopeBridge), out var workScope).Should().BeTrue();
        workScope.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IWorkScopeBridge), "Pom", "workScopeBridge"));
        catalog.TryGet(typeof(IWorkScopeProjectionBridge), out var workScopeProjection)
            .Should().BeTrue();
        workScopeProjection.Should().Be(new NexaModuleBridgeDescriptor(
            typeof(IWorkScopeProjectionBridge), "Pom", "workScopeProjectionBridge"));
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
