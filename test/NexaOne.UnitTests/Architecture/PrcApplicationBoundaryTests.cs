namespace NexaOne.UnitTests.Architecture;

public sealed class PrcApplicationBoundaryTests
{
    [Fact]
    public void Purchase_order_planning_invariants_are_owned_by_the_application_module()
    {
        var service = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.PRC", "Application", "PurchaseOrders",
            "PurchaseOrderPlanningService.cs"));
        var port = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.PRC", "Application", "PurchaseOrders",
            "IPurchaseOrderPlanningStore.cs"));
        var bridge = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.PRC", "Application", "PurchaseOrders",
            "PurchaseOrderPlanningBridge.cs"));
        var repository = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.PRC", "Infrastructure",
            "PurchaseOrderPlanningRepository.cs"));

        service.Should().Contain("Validate(");
        service.Should().Contain("EnsureSameCommand(");
        service.Should().Contain("PurchaseOrderInsertOutcome.IdentityConflict");

        port.Should().Contain("interface IPurchaseOrderPlanningStore");
        port.Should().Contain("enum PurchaseOrderInsertOutcome");

        bridge.Should().Contain("PurchaseOrderPlanningService");
        bridge.Should().NotContain("SELECT ");
        bridge.Should().NotContain("INSERT INTO");
        bridge.Should().NotContain("Validate(");
        bridge.Should().NotContain("EnsureSameCommand(");

        repository.Should().NotContain("MrpPurchaseOrderRequest");
        repository.Should().NotContain("Validate(");
        repository.Should().NotContain("EnsureSameCommand(");
        repository.Should().NotContain("CancellationToken.None");
        repository.Should().Contain("IsExpectedPurchaseOrderIdentityRace");

        var formerInfrastructureBridge = Path.Combine(
            RepositorySource.Root,
            "src", "04.Modules", "NexaOne.PRC", "Infrastructure",
            "PurchaseOrderPlanningBridge.cs");
        File.Exists(formerInfrastructureBridge).Should().BeFalse(
            "the former Infrastructure bridge mixed business invariants with persistence");
    }
}
