using Microsoft.Extensions.Configuration;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaFramework.Scheduling;
using NexaOne.ERP;
using NexaOne.ERP.Application.Delivery;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.UnitTests.Erp;

public sealed class DeliveryDispatchWorkerTests
{
    [Fact]
    public void Enabled_configuration_requires_principal_and_bounds_dispatch_settings()
    {
        var missing = Configuration(new Dictionary<string, string?>
        {
            ["Worker:Collaboration:Delivery:Enabled"] = "true",
        });
        Action invalid = () => ErpModuleOptions.FromConfiguration(missing);
        invalid.Should().Throw<InvalidOperationException>().WithMessage("*PrincipalId*");

        var configured = Configuration(new Dictionary<string, string?>
        {
            ["Worker:Collaboration:Delivery:Enabled"] = "true",
            ["Worker:Collaboration:Delivery:PrincipalId"] = " mail-dispatch ",
            ["Worker:Collaboration:Delivery:IntervalSeconds"] = "1",
            ["Worker:Collaboration:Delivery:LeaseSeconds"] = "90",
            ["Worker:Collaboration:Delivery:BatchSize"] = "40",
        });

        var options = ErpModuleOptions.FromConfiguration(configured);

        options.DeliveryEnabled.Should().BeTrue();
        options.DeliveryPrincipalId.Should().Be("mail-dispatch");
        options.DeliveryIntervalSeconds.Should().Be(10);
        options.DeliveryLeaseSeconds.Should().Be(90);
        options.DeliveryBatchSize.Should().Be(40);
    }

    [Fact]
    public async Task Disabled_worker_registers_no_job()
    {
        var scheduler = new Mock<IRecurringScheduler>();
        var worker = new DeliveryDispatchWorker(
            scheduler.Object, Mock.Of<IDeliveryAutomationBridge>(), Mock.Of<IDeliveryProviderRegistry>(),
            false, string.Empty, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 25);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        scheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Worker_completes_acceptance_and_persists_normalized_provider_failure()
    {
        var scope = Scope();
        var accepted = Request(scope, "smtp");
        var rejected = Request(scope, "smtp");
        var automation = Automation(scope, [accepted, rejected]);
        var provider = new Mock<IDeliveryProvider>();
        provider.SetupGet(x => x.Key).Returns("smtp");
        provider.SetupSequence(x => x.SendAsync(It.IsAny<DeliveryPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("smtp:accepted")
            .ThrowsAsync(new DeliveryProviderException(DeliveryProviderError.SendFailed));
        var registry = new Mock<IDeliveryProviderRegistry>();
        registry.Setup(x => x.GetRequired("smtp")).Returns(provider.Object);
        var worker = Worker(automation.Object, registry.Object);

        await worker.RunAsync(CancellationToken.None);

        automation.Verify(x => x.CompleteAsync(
            "mail-dispatch", scope.TenantId, scope.OrganizationId, scope.Version,
            accepted.Id, accepted.Version, accepted.LeaseId!.Value, "smtp:accepted",
            It.IsAny<CancellationToken>()), Times.Once);
        automation.Verify(x => x.FailAsync(
            "mail-dispatch", scope.TenantId, scope.OrganizationId, scope.Version,
            rejected.Id, rejected.Version, rejected.LeaseId!.Value, "DELIVERY_PROVIDER_SEND_FAILED",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Provider_acceptance_with_uncertain_settlement_is_not_recorded_as_provider_failure()
    {
        var scope = Scope();
        var request = Request(scope, "smtp");
        var automation = Automation(scope, [request]);
        automation.Setup(x => x.CompleteAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(),
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var provider = new Mock<IDeliveryProvider>();
        provider.Setup(x => x.SendAsync(request.Payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync("smtp:accepted");
        var registry = new Mock<IDeliveryProviderRegistry>();
        registry.Setup(x => x.GetRequired("smtp")).Returns(provider.Object);

        await Worker(automation.Object, registry.Object).RunAsync(CancellationToken.None);

        automation.Verify(x => x.FailAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(),
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "a provider acceptance must remain an expiring lease when durable settlement is uncertain");
    }

    [Fact]
    public async Task Host_cancellation_leaves_the_lease_for_recovery()
    {
        using var cancellation = new CancellationTokenSource();
        var scope = Scope();
        var request = Request(scope, "smtp");
        var automation = Automation(scope, [request]);
        var provider = new Mock<IDeliveryProvider>();
        provider.Setup(x => x.SendAsync(request.Payload, It.IsAny<CancellationToken>()))
            .Returns<DeliveryPayload, CancellationToken>((_, ct) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<string>(ct);
            });
        var registry = new Mock<IDeliveryProviderRegistry>();
        registry.Setup(x => x.GetRequired("smtp")).Returns(provider.Object);

        var action = () => Worker(automation.Object, registry.Object).RunAsync(cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        automation.Verify(x => x.CompleteAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(),
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        automation.Verify(x => x.FailAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(),
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static DeliveryDispatchWorker Worker(
        IDeliveryAutomationBridge automation, IDeliveryProviderRegistry providers)
        => new(Mock.Of<IRecurringScheduler>(), automation, providers, true, "mail-dispatch",
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 25);

    private static Mock<IDeliveryAutomationBridge> Automation(
        DeliveryServicePrincipalScope scope, IReadOnlyList<DeliveryRequest> requests)
    {
        var automation = new Mock<IDeliveryAutomationBridge>();
        automation.Setup(x => x.ListActiveScopesAsync(
                "mail-dispatch", 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<DeliveryServicePrincipalScope>([scope], 1));
        automation.Setup(x => x.ClaimDueAsync(
                "mail-dispatch", scope.TenantId, scope.OrganizationId, scope.Version,
                25, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(requests);
        return automation;
    }

    private static DeliveryServicePrincipalScope Scope()
        => new("mail-dispatch", Guid.NewGuid(), Guid.NewGuid(), true, 1);

    private static DeliveryRequest Request(DeliveryServicePrincipalScope scope, string providerKey)
    {
        var requestScope = new BusinessScope(
            "NexaOne.MES", scope.TenantId.ToString("D"), scope.OrganizationId.ToString("D"));
        var lease = Guid.NewGuid();
        var template = Guid.NewGuid();
        var profile = Guid.NewGuid();
        return new(Guid.NewGuid(), requestScope, Guid.NewGuid(), Guid.NewGuid(),
            new(template, profile, "buyer@example.com"), Guid.NewGuid(), Guid.NewGuid(),
            new(DeliveryChannel.Email, "buyer@example.com", "Subject", "Body", providerKey, "mail-primary"),
            new(3, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)), DeliveryState.Leased, 1,
            DateTimeOffset.UtcNow, "creator", DateTimeOffset.UtcNow, lease, DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
