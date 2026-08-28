using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;
using NexaLogic.Plc.Hosting;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcPlcDeviceFactoryTests
{
    private static string ExistingTagMapPath => typeof(FdcPlcDeviceFactoryTests).Assembly.Location;
    private static FdcLeaseOptions RuntimeLeaseOptions { get; } = new(
        "test-fdc-node",
        new string('a', 64),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10));
    private static FdcLeaseOptions FastRuntimeLeaseOptions { get; } = new(
        "test-fdc-node",
        new string('b', 64),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Collection_worker_treats_STOP_as_opaque_and_does_not_stop_the_machine()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-STOP", "EQ-001", "ModbusTcp", "tcp://plc.local:502", 500, ExistingTagMapPath).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-STOP").Value;

        var endpointRepository = new Mock<IFdcEquipmentEndpointRepository>();
        endpointRepository.Setup(repository => repository.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { endpoint });

        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { parameter });

        var subscriptionProvider = new Mock<IPlcSubscriptionProvider>();
        AttachRuntimeHealth(subscriptionProvider);
        subscriptionProvider.As<IPlcAtomicSubscriptionSnapshotProvider>()
            .Setup(provider => provider.StartWithSnapshotAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<PlcSubscription>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlcTagValue(
                "TEMP01", 20m, PlcQuality.Good, DateTimeOffset.UtcNow, "ns=2;s=TEMP01")]);

        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(candidate => candidate.Endpoint)
            .Returns(new PlcEndpoint("EP-STOP", PlcDriverKind.ModbusTcp, "plc.local", 502));
        connection.SetupGet(candidate => candidate.SubscriptionProvider)
            .Returns(subscriptionProvider.Object);
        connection.Setup(candidate => candidate.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.PingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.CloseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated stop failure"));

        var driver = Driver(PlcDriverKind.ModbusTcp, "modbus");
        driver.Setup(candidate => candidate.ConnectAsync(
                It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);

        var messages = new List<DomainEventMessage>();
        var bus = new Mock<IMessageBus>();
        bus.Setup(candidate => candidate.PublishAsync(
                "nexaone.events", It.IsAny<DomainEventMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, DomainEventMessage, CancellationToken>((_, message, _) => messages.Add(message))
            .Returns(Task.CompletedTask);

        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var collectDataRepository = new Mock<IFdcCollectDataRepository>();
        collectDataRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var interlockRule = FdcInterlockRule.Create(
            "RULE-1", "Over temperature", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var interlockRuleRepository = new Mock<IFdcInterlockRuleRepository>();
        interlockRuleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { interlockRule });
        var interlockHistoryRepository = new Mock<IFdcInterlockHistoryRepository>();
        interlockHistoryRepository.Setup(repository => repository.GetAllUnresolvedAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        interlockHistoryRepository.Setup(repository => repository.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        interlockHistoryRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, collectDataRepository.Object),
            new FdcInterlockService(interlockRuleRepository.Object, interlockHistoryRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        var worker = new FdcCollectionWorker(
            collector,
            endpointRepository.Object,
            parameterRepository.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory(new[] { driver.Object })),
            bus.Object,
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events");

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await collector.OnTagChangeAsync(
                "EQ-001",
                new FdcTagSample("TEMP01", 90m, FdcSampleQuality.Good));

            connection.Verify(candidate => candidate.CloseAsync(It.IsAny<CancellationToken>()), Times.Never,
                "a common FDC worker must leave project-specific STOP policy to a consumer");
            var message = messages.Should().ContainSingle().Subject;
            message.EventType.Should().Be("InterlockTriggered");
            message.Module.Should().Be("FDC");
            message.AggregateId.Should().Be("EQ-001");
            using var payload = JsonDocument.Parse(message.Payload);
            payload.RootElement.GetProperty("EffectId").GetString().Should().NotBeNullOrWhiteSpace();
            payload.RootElement.GetProperty("RuleId").GetString().Should().Be("RULE-1");
            payload.RootElement.GetProperty("ParameterId").GetString().Should().Be("TEMP01");
            payload.RootElement.GetProperty("Action").GetString().Should().Be("STOP");
            payload.RootElement.GetProperty("Value").GetDecimal().Should().Be(90m);
        }
        finally
        {
            connection.Setup(candidate => candidate.CloseAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Create_selects_the_driver_matching_endpoint_protocol()
    {
        var connection = new Mock<IPlcConnection>();
        connection.Setup(candidate => candidate.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var opcUa = Driver(PlcDriverKind.OpcUa, "opcua");
        var modbus = Driver(PlcDriverKind.ModbusTcp, "modbus");
        modbus.Setup(candidate => candidate.ConnectAsync(
                It.Is<PlcEndpoint>(endpoint => endpoint.DriverKind == PlcDriverKind.ModbusTcp),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);

        var sut = new FdcPlcDeviceFactory(new PlcDriverFactory(new[] { opcUa.Object, modbus.Object }));
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-MODBUS", "EQ-001", "ModbusTcp", "tcp://plc.local:1502", 500, ExistingTagMapPath).Value;

        var device = sut.Create(endpoint);
        await device.InitializeAsync();

        device.DriverKind.Should().Be(PlcDriverKind.ModbusTcp);
        modbus.Verify(candidate => candidate.ConnectAsync(
            It.Is<PlcEndpoint>(plc => plc.Host == "plc.local" && plc.Port == 1502),
            It.IsAny<CancellationToken>()), Times.Once);
        opcUa.Verify(candidate => candidate.ConnectAsync(
            It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Create_reports_endpoint_protocol_and_registered_kinds_when_driver_is_missing()
    {
        var opcUa = Driver(PlcDriverKind.OpcUa, "opcua");
        var sut = new FdcPlcDeviceFactory(new PlcDriverFactory(new[] { opcUa.Object }));
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-MC", "EQ-001", "MitsubishiMc", "tcp://plc.local:5007", 500, ExistingTagMapPath).Value;

        var act = () => sut.Create(endpoint);

        var error = act.Should().Throw<FdcPlcDriverNotRegisteredException>()
            .WithMessage("*EP-MC*MitsubishiMc*Registered driver kinds: OpcUa*")
            .Which;
        error.EndpointId.Should().Be("EP-MC");
        error.Protocol.Should().Be("MitsubishiMc");
        error.DriverKind.Should().Be(PlcDriverKind.MitsubishiMc);
        error.RegisteredKinds.Should().Equal(PlcDriverKind.OpcUa);
    }

    [Fact]
    public async Task Collection_worker_propagates_driver_configuration_error_instead_of_skipping_endpoint()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-MC", "EQ-001", "MitsubishiMc", "tcp://plc.local:5007", 500, ExistingTagMapPath).Value;
        var endpointRepository = new Mock<IFdcEquipmentEndpointRepository>();
        endpointRepository.Setup(repository => repository.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { endpoint });

        var opcUa = Driver(PlcDriverKind.OpcUa, "opcua");
        var deviceFactory = new FdcPlcDeviceFactory(new PlcDriverFactory(new[] { opcUa.Object }));
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                FdcParameter.Create("TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-MC").Value
            });
        var collector = new FdcCollectorService(new FdcDataService(
            parameterRepository.Object,
            Mock.Of<IFdcCollectDataRepository>()));
        var worker = new FdcCollectionWorker(
            collector,
            endpointRepository.Object,
            parameterRepository.Object,
            deviceFactory,
            Mock.Of<IMessageBus>(),
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events");

        var act = () => StartAndAwaitExecutionAsync(worker);

        await act.Should().ThrowAsync<FdcPlcDriverNotRegisteredException>()
            .WithMessage("*EP-MC*MitsubishiMc*");
    }

    [Fact]
    public async Task Collection_worker_denies_device_start_before_driver_connect_when_action_adapter_is_unavailable()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-1", "EQ-001", "ModbusTcp", "tcp://plc.local:502", 500, ExistingTagMapPath).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-1").Value;
        var endpointRepository = new Mock<IFdcEquipmentEndpointRepository>();
        endpointRepository.Setup(x => x.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([endpoint]);
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([parameter]);
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                FdcInterlockRule.Create(
                    "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value
            ]);
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.Unavailable("project action adapter offline"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(rules.Object, history.Object),
            actionPort: action.Object);
        var driver = Driver(PlcDriverKind.ModbusTcp, "modbus");
        var worker = new FdcCollectionWorker(
            collector,
            endpointRepository.Object,
            parameterRepository.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory([driver.Object])),
            Mock.Of<IMessageBus>(),
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events");

        var act = () => StartAndAwaitExecutionAsync(worker);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*project action adapter offline*");
        driver.Verify(x => x.ConnectAsync(
            It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Never,
            "action readiness and open-effect reconciliation precede driver initialization/start");
    }

    [Fact]
    public async Task Collection_worker_rejects_an_active_rule_for_an_inactive_parameter_before_driver_connect()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-1", "EQ-001", "ModbusTcp", "tcp://plc.local:502", 500, ExistingTagMapPath).Value;
        var active = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-1").Value;
        var inactive = FdcParameter.Create(
            "OLD01", "Retired temperature", "EQ-001", "C", 0m, 100m, "EP-1").Value;
        inactive.Deactivate();

        var endpoints = new Mock<IFdcEquipmentEndpointRepository>();
        endpoints.Setup(x => x.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([endpoint]);
        var parameters = new Mock<IFdcParameterRepository>();
        parameters.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([active, inactive]);
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                FdcInterlockRule.Create(
                    "R-OLD", "Retired input", "EQ-001", "OLD01", "GT", 80m, "STOP", 1).Value
            ]);
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        var action = new ConfirmedInterlockActionPort();
        var collector = new FdcCollectorService(
            new FdcDataService(parameters.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(rules.Object, history.Object),
            actionPort: action);
        var driver = Driver(PlcDriverKind.ModbusTcp, "modbus");
        var worker = new FdcCollectionWorker(
            collector,
            endpoints.Object,
            parameters.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory([driver.Object])),
            Mock.Of<IMessageBus>(),
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events");

        var act = () => StartAndAwaitExecutionAsync(worker);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*R-OLD*OLD01*outside the loaded topology*");
        driver.Verify(x => x.ConnectAsync(
            It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Collection_worker_denies_start_before_driver_connect_when_runtime_lease_acquisition_is_rejected()
    {
        var lease = new ScriptedRuntimeLease(acquire: false);
        var fixture = CreateRuntimeLeaseWorkerFixture(lease, FastRuntimeLeaseOptions);

        var act = () => StartAndAwaitExecutionAsync(fixture.Worker);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*runtime writer lease*already held*collection remains disabled*");
        fixture.Driver.Verify(x => x.ConnectAsync(
            It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Never,
            "the writer lease must be acquired before any driver session is created");
        fixture.Collector.IsRunPermitted.Should().BeFalse();
        lease.ReleaseCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collection_worker_revokes_permit_closes_owned_driver_and_preserves_lease_renewal_fault(
        bool renewalThrows)
    {
        var allowRenewal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseCause = new InvalidOperationException("sentinel lease heartbeat failure");
        var lease = new ScriptedRuntimeLease(
            renewalBehavior: renewalThrows
                ? LeaseRenewalBehavior.Throw
                : LeaseRenewalBehavior.ReturnNull,
            renewalGate: allowRenewal.Task,
            renewalFailure: leaseCause);
        var fixture = CreateRuntimeLeaseWorkerFixture(lease, FastRuntimeLeaseOptions);

        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.Running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Collector.IsRunPermitted.Should().BeTrue();

        allowRenewal.TrySetResult(true);
        var act = async () =>
        {
            if (fixture.Worker.ExecuteTask is not null)
                await fixture.Worker.ExecuteTask;
        };

        var error = await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>();
        if (renewalThrows)
        {
            error.WithMessage("*lease heartbeat failed*collection is fenced immediately*");
            error.Which.InnerException.Should().BeSameAs(leaseCause,
                "the lease failure must remain the causal exception after driver cleanup");
        }
        else
        {
            error.WithMessage("*lease renewal lost its opaque grant CAS or expired*");
            error.Which.InnerException.Should().BeNull();
        }

        await fixture.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Collector.IsRunPermitted.Should().BeFalse();
        fixture.Connection.Verify(
            x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once,
            "lease loss must close every driver session owned by this worker");
        error.Which.Data.Contains(FdcCollectionWorker.DriverCleanupFailureDataKey).Should().BeFalse(
            "successful cleanup must not replace or decorate the causal lease fault");
    }

    [Fact]
    public async Task Collection_worker_fences_at_grant_expiry_when_lease_renewal_ignores_cancellation_forever()
    {
        var leaseOptions = new FdcLeaseOptions(
            "test-fdc-node",
            new string('c', 64),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(1));
        var lease = new ScriptedRuntimeLease(
            renewalBehavior: LeaseRenewalBehavior.IgnoreCancellationAndNeverComplete);
        var fixture = CreateRuntimeLeaseWorkerFixture(lease, leaseOptions);

        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.Running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.FirstRenewalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Collector.IsRunPermitted.Should().BeTrue();

        var act = async () =>
        {
            if (fixture.Worker.ExecuteTask is not null)
                await fixture.Worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(8));
        };

        var error = await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>();
        error.WithMessage("*lease*expir*action publication is fenced*",
            "either the renewal waiter or a synchronous collector boundary may observe the same deadline first");
        lease.AcquiredLeaseExpiresAt.Should().NotBeNull();
        DateTime.UtcNow.Should().BeOnOrAfter(lease.AcquiredLeaseExpiresAt!.Value,
            "an uncooperative renewal cannot revoke authority before the issued grant deadline");
        await fixture.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Collector.IsRunPermitted.Should().BeFalse();
        fixture.Connection.Verify(
            connection => connection.CloseAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "grant expiry must close the worker-owned PLC session");
    }

    [Fact]
    public async Task Collection_worker_closes_owned_driver_before_releasing_latest_opaque_runtime_lease_grant()
    {
        var lifecycle = new ConcurrentQueue<string>();
        var lease = new ScriptedRuntimeLease(
            renewalBehavior: LeaseRenewalBehavior.SucceedOnceThenBlock,
            recordLifecycle: lifecycle.Enqueue);
        var fixture = CreateRuntimeLeaseWorkerFixture(
            lease,
            FastRuntimeLeaseOptions,
            lifecycle.Enqueue);

        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.Running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.SecondRenewalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.StopAsync(CancellationToken.None);

        lease.FirstSuccessfulGrant.Should().NotBeNull();
        lease.SecondRenewalInput.Should().BeSameAs(lease.FirstSuccessfulGrant,
            "the heartbeat loop must CAS from the latest opaque grant");
        lease.ReleasedGrant.Should().BeSameAs(lease.FirstSuccessfulGrant,
            "shutdown must release the latest successfully renewed opaque grant");
        lease.ReleaseCallCount.Should().Be(1);
        fixture.Connection.Verify(x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
        lifecycle.Should().ContainInOrder("driver-close", "lease-release");
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("listener")]
    [InlineData("stale")]
    [InlineData("freeze")]
    public async Task Collection_worker_replays_the_baseline_then_closes_owned_device_on_runtime_health_failure(
        string failureMode)
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-1", "EQ-001", "ModbusTcp", "tcp://plc.local:502", 1,
            ExistingTagMapPath,
            new FdcPlcEndpointSettings(
                ConnectionTimeoutMs: 1,
                ReadWriteTimeoutMs: 1,
                HeartbeatTimeoutMs: 1,
                PollingDisconnectBackoffMs: 1,
                PollingMaxDisconnectBackoffMs: 1)).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-1").Value;
        var endpoints = new Mock<IFdcEquipmentEndpointRepository>();
        endpoints.Setup(x => x.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([endpoint]);
        var parameters = new Mock<IFdcParameterRepository>();
        parameters.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([parameter]);
        parameters.Setup(x => x.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var collected = new Mock<IFdcCollectDataRepository>();
        collected.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Func<PlcTagChangeEvent, Task>? callback = null;
        var subscribedAt = DateTimeOffset.UtcNow;
        var subscription = new Mock<IPlcSubscriptionProvider>();
        var listenerCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AttachRuntimeHealth(
            subscription,
            listenerCompletion.Task,
            failureMode == "stale" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero);
        subscription.As<IPlcAtomicSubscriptionSnapshotProvider>()
            .Setup(x => x.StartWithSnapshotAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<PlcSubscription>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (PlcEndpoint _, PlcSubscription _, Func<PlcTagChangeEvent, Task> onEvent, CancellationToken _) =>
            {
                callback = onEvent;
                await callback(new PlcTagChangeEvent(
                    "evt-newer", "EP-1", "TEMP01", "ns=2;s=TEMP01",
                    20m, 90m, PlcQuality.Good, subscribedAt.AddMilliseconds(1),
                    "test", IsChanged: true));
                return [new PlcTagValue(
                    "TEMP01", 20m, PlcQuality.Good, subscribedAt, "ns=2;s=TEMP01")];
            });

        var connection = new Mock<IPlcConnection>();
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SetupGet(x => x.Endpoint)
            .Returns(new PlcEndpoint("EP-1", PlcDriverKind.ModbusTcp, "plc.local", 502));
        connection.SetupGet(x => x.SubscriptionProvider).Returns(subscription.Object);
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connection.Setup(x => x.PingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connection.Setup(x => x.CloseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => closed.TrySetResult(true))
            .Returns(Task.CompletedTask);
        var driver = Driver(PlcDriverKind.ModbusTcp, "modbus");
        driver.Setup(x => x.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);

        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                FdcInterlockRule.Create(
                    "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value
            ]);
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockHistory?)null);
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        FdcCollectorService? collector = null;
        var actionBeforeDeviceStart = false;
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                connection.Verify(x => x.PingAsync(It.IsAny<CancellationToken>()), Times.Never);
                collector!.IsRunPermitted.Should().BeFalse();
                actionBeforeDeviceStart = true;
            })
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("ack-readback"));
        collector = new FdcCollectorService(
            new FdcDataService(parameters.Object, collected.Object),
            new FdcInterlockService(rules.Object, history.Object),
            actionPort: action.Object);
        var worker = new FdcCollectionWorker(
            collector,
            endpoints.Object,
            parameters.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory([driver.Object])),
            Mock.Of<IMessageBus>(),
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events",
            streamFreshnessTimeout: failureMode is "stale" or "freeze"
                ? TimeSpan.FromMilliseconds(20)
                : TimeSpan.FromSeconds(30));

        if (failureMode == "stale")
        {
            var staleStart = () => StartAndAwaitExecutionAsync(worker);
            await staleStart.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
                .WithMessage("*subscription stream is stale*");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.IsRunPermitted.Should().BeFalse();
            connection.Verify(x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
            return;
        }

        await worker.StartAsync(CancellationToken.None);

        actionBeforeDeviceStart.Should().BeTrue();
        action.Verify(x => x.ApplyAsync(
            It.Is<FdcInterlockActionRequest>(request => request.TriggerValue == 90m),
            It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.PingAsync(It.IsAny<CancellationToken>()), Times.Once);
        collector.IsRunPermitted.Should().BeFalse(
            "the violating startup snapshot keeps automatic-run admission closed without stopping supervision");

        if (failureMode == "callback")
        {
            collected.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("telemetry database unavailable"));
            var callbackFailure = () => callback!(new PlcTagChangeEvent(
                "evt-live-failure", "EP-1", "TEMP01", "40001",
                90m, 91m, PlcQuality.Good, DateTimeOffset.UtcNow, "test", true));
            await callbackFailure.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*telemetry database unavailable*");
        }
        else if (failureMode == "listener")
        {
            listenerCompletion.TrySetException(new InvalidOperationException("poll listener failed"));
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        collector.IsRunPermitted.Should().BeFalse();
        connection.Verify(x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once,
            "permit revocation must wake the worker supervisor and close its directly owned driver");
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Collection_worker_preserves_startup_failure_and_attaches_cleanup_failure_when_buffer_overflows()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-1", "EQ-001", "ModbusTcp", "tcp://plc.local:502", 500, ExistingTagMapPath).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m, "EP-1").Value;
        var endpoints = new Mock<IFdcEquipmentEndpointRepository>();
        endpoints.Setup(x => x.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([endpoint]);
        var parameters = new Mock<IFdcParameterRepository>();
        parameters.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([parameter]);

        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        AttachRuntimeHealth(subscriptions);
        subscriptions.As<IPlcAtomicSubscriptionSnapshotProvider>()
            .Setup(x => x.StartWithSnapshotAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<PlcSubscription>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (PlcEndpoint _, PlcSubscription _, Func<PlcTagChangeEvent, Task> onEvent, CancellationToken _) =>
            {
                for (var index = 0; index < 4097; index++)
                {
                    await onEvent(new PlcTagChangeEvent(
                        $"event-{index}", "EP-1", "TEMP01", "40001",
                        20m, 20m, PlcQuality.Good, DateTimeOffset.UtcNow, "test", true));
                }

                return [new PlcTagValue("TEMP01", 20m, PlcQuality.Good, DateTimeOffset.UtcNow, "40001")];
            });
        subscriptions.Setup(x => x.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("subscription cleanup failed"));
        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(x => x.Endpoint)
            .Returns(new PlcEndpoint("EP-1", PlcDriverKind.ModbusTcp, "plc.local", 502));
        connection.SetupGet(x => x.SubscriptionProvider).Returns(subscriptions.Object);
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connection.Setup(x => x.CloseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport cleanup failed"));
        connection.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var driver = Driver(PlcDriverKind.ModbusTcp, "modbus");
        driver.Setup(x => x.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);
        var collector = new FdcCollectorService(
            new FdcDataService(parameters.Object, Mock.Of<IFdcCollectDataRepository>()));
        var worker = new FdcCollectionWorker(
            collector,
            endpoints.Object,
            parameters.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory([driver.Object])),
            Mock.Of<IMessageBus>(),
            new ConfirmedRuntimeLease(),
            RuntimeLeaseOptions,
            enabled: true,
            topic: "nexaone.events");

        var act = () => StartAndAwaitExecutionAsync(worker);

        var error = await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>();
        error.WithMessage("*bounded capacity of 4096*");
        error.Which.Data[FdcCollectionWorker.DriverCleanupFailureDataKey]
            .Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().ContainSingle()
            .Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(2);
        connection.Verify(x => x.PingAsync(It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Driver_cleanup_is_reverse_order_bounded_and_continues_after_hung_dispose()
    {
        var lifecycle = new List<string>();
        var first = await CreateCleanupDeviceAsync("first", lifecycle, hangDispose: false);
        var second = await CreateCleanupDeviceAsync("second", lifecycle, hangDispose: true);

        var cleanup = FdcCollectionWorker.StopAndDisposeReverseAsync(
            [first, second],
            TimeSpan.FromMilliseconds(20));
        var errors = await cleanup.WaitAsync(TimeSpan.FromSeconds(1));

        lifecycle.Should().Equal(
            "second:subscription",
            "second:transport",
            "second:dispose",
            "first:subscription",
            "first:transport",
            "first:dispose");
        errors.Should().ContainSingle()
            .Which.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("second").And.Contain("dispose");
    }

    private static async Task<PlcDeviceInterface> CreateCleanupDeviceAsync(
        string interfaceName,
        ICollection<string> lifecycle,
        bool hangDispose)
    {
        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        subscriptions.Setup(provider => provider.StopAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => lifecycle.Add($"{interfaceName}:subscription"))
            .Returns(Task.CompletedTask);
        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(candidate => candidate.SubscriptionProvider).Returns(subscriptions.Object);
        connection.Setup(candidate => candidate.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.CloseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => lifecycle.Add($"{interfaceName}:transport"))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.DisposeAsync())
            .Callback(() => lifecycle.Add($"{interfaceName}:dispose"))
            .Returns(hangDispose
                ? new ValueTask(new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task)
                : ValueTask.CompletedTask);
        var driver = Driver(PlcDriverKind.ModbusTcp, $"{interfaceName}-driver");
        driver.Setup(candidate => candidate.ConnectAsync(
                It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);
        var device = new PlcDeviceInterface(
            interfaceName,
            new PlcEndpoint(interfaceName, PlcDriverKind.ModbusTcp, "plc.local", 502),
            driver.Object);
        await device.InitializeAsync();
        return device;
    }

    private static Mock<IPlcDriver> Driver(PlcDriverKind kind, string name)
    {
        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(candidate => candidate.Kind).Returns(kind);
        driver.SetupGet(candidate => candidate.Name).Returns(name);
        return driver;
    }

    private static RuntimeLeaseWorkerFixture CreateRuntimeLeaseWorkerFixture(
        IFdcRuntimeLease lease,
        FdcLeaseOptions leaseOptions,
        Action<string>? recordLifecycle = null)
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-LEASE", "EQ-LEASE", "ModbusTcp", "tcp://plc.local:502", 500,
            ExistingTagMapPath).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-LEASE", "C", 0m, 100m, "EP-LEASE").Value;

        var endpoints = new Mock<IFdcEquipmentEndpointRepository>();
        endpoints.Setup(x => x.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([endpoint]);
        var parameters = new Mock<IFdcParameterRepository>();
        parameters.Setup(x => x.GetByEquipmentAsync("EQ-LEASE", It.IsAny<CancellationToken>()))
            .ReturnsAsync([parameter]);
        parameters.Setup(x => x.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var collected = new Mock<IFdcCollectDataRepository>();
        collected.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-LEASE", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                FdcInterlockRule.Create(
                    "RULE-LEASE", "Lease guard", "EQ-LEASE", "TEMP01", "GT", 80m, "STOP", 1).Value
            ]);
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-LEASE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());

        var subscriptions = new Mock<IPlcSubscriptionProvider>();
        AttachRuntimeHealth(subscriptions);
        subscriptions.As<IPlcAtomicSubscriptionSnapshotProvider>()
            .Setup(x => x.StartWithSnapshotAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<PlcSubscription>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlcTagValue(
                    "TEMP01", 20m, PlcQuality.Good, DateTimeOffset.UtcNow, "ns=2;s=TEMP01")
            ]);
        subscriptions.Setup(x => x.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(x => x.Endpoint)
            .Returns(new PlcEndpoint("EP-LEASE", PlcDriverKind.ModbusTcp, "plc.local", 502));
        connection.SetupGet(x => x.SubscriptionProvider).Returns(subscriptions.Object);
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connection.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .Callback(() => running.TrySetResult(true))
            .Returns(Task.CompletedTask);
        connection.Setup(x => x.CloseAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                recordLifecycle?.Invoke("driver-close");
                closed.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);
        connection.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var driver = Driver(PlcDriverKind.ModbusTcp, "lease-test-driver");
        driver.Setup(x => x.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);

        var collector = new FdcCollectorService(
            new FdcDataService(parameters.Object, collected.Object),
            new FdcInterlockService(rules.Object, history.Object),
            actionPort: new ConfirmedInterlockActionPort(),
            requireRuntimeAuthority: true);
        var worker = new FdcCollectionWorker(
            collector,
            endpoints.Object,
            parameters.Object,
            new FdcPlcDeviceFactory(new PlcDriverFactory([driver.Object])),
            Mock.Of<IMessageBus>(),
            lease,
            leaseOptions,
            enabled: true,
            topic: "nexaone.events");
        return new RuntimeLeaseWorkerFixture(
            worker, collector, driver, connection, running, closed);
    }

    private static Mock<IPlcCompletedPollSnapshotRuntimeHealth> AttachRuntimeHealth(
        Mock<IPlcSubscriptionProvider> provider,
        Task? completion = null,
        TimeSpan? initialPollAge = null)
    {
        var elapsed = Stopwatch.StartNew();
        var initialAge = initialPollAge ?? TimeSpan.Zero;
        var health = provider.As<IPlcCompletedPollSnapshotRuntimeHealth>();
        health.SetupGet(candidate => candidate.Completion).Returns(
            completion ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        health.SetupGet(candidate => candidate.SubscriptionGeneration).Returns(1);
        health.SetupGet(candidate => candidate.IsRunning).Returns(true);
        health.SetupGet(candidate => candidate.StartedPollCount).Returns(1);
        health.SetupGet(candidate => candidate.CompletedPollCount).Returns(1);
        health.SetupGet(candidate => candidate.TimeSinceLastCompletedPoll)
            .Returns(() => initialAge + elapsed.Elapsed);
        health.SetupGet(candidate => candidate.LastCompletedPollAt)
            .Returns(() => DateTimeOffset.UtcNow - initialAge - elapsed.Elapsed);
        health.SetupGet(candidate => candidate.LatestCompletedPollSnapshot)
            .Returns(() => new PlcCompletedPollSnapshot(
                subscriptionGeneration: 1,
                startedPollCount: 1,
                completedPollCount: 1,
                completedAt: DateTimeOffset.UtcNow - initialAge - elapsed.Elapsed,
                values:
                [
                    new PlcTagValue(
                        "TEMP01", 20m, PlcQuality.Good,
                        DateTimeOffset.UtcNow - initialAge - elapsed.Elapsed,
                        "test")
                ]));
        return health;
    }

    private sealed class ConfirmedRuntimeLease : IFdcRuntimeLease
    {
        private FdcRuntimeLeaseGrant? _grant;

        public Task<FdcRuntimeLeaseAcquireResult> TryAcquireAsync(
            string ownerId,
            string configRevisionSha256,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var authority = new FdcRuntimeAuthority(
                ownerId, 1, configRevisionSha256, DateTime.UtcNow.Add(leaseDuration));
            _grant = new TestGrant(authority);
            return Task.FromResult(new FdcRuntimeLeaseAcquireResult(
                true,
                State(authority),
                _grant));
        }

        public Task<FdcRuntimeLeaseGrant?> TryRenewAsync(
            FdcRuntimeLeaseGrant grant,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var renewed = new TestGrant(
                grant.Authority with { LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration) });
            _grant = renewed;
            return Task.FromResult<FdcRuntimeLeaseGrant?>(renewed);
        }

        public Task<bool> TryReleaseAsync(
            FdcRuntimeLeaseGrant grant,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var released = ReferenceEquals(_grant, grant);
            if (released) _grant = null;
            return Task.FromResult(released);
        }

        public Task<FdcRuntimeLeaseState> GetStateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_grant is null
                ? new FdcRuntimeLeaseState(null, 1, null, null, null)
                : State(_grant.Authority));
        }

        private static FdcRuntimeLeaseState State(FdcRuntimeAuthority authority) => new(
            authority.OwnerId,
            authority.FenceToken,
            authority.LeaseExpiresAt,
            DateTime.UtcNow,
            authority.ConfigRevision);

        private sealed class TestGrant(FdcRuntimeAuthority authority)
            : FdcRuntimeLeaseGrant(authority);
    }

    private enum LeaseRenewalBehavior
    {
        Succeed,
        ReturnNull,
        Throw,
        SucceedOnceThenBlock,
        IgnoreCancellationAndNeverComplete
    }

    private sealed class ScriptedRuntimeLease : IFdcRuntimeLease
    {
        private readonly bool _acquire;
        private readonly LeaseRenewalBehavior _renewalBehavior;
        private readonly Task _renewalGate;
        private readonly Exception _renewalFailure;
        private readonly Action<string>? _recordLifecycle;
        private FdcRuntimeLeaseGrant? _currentGrant;
        private int _renewalCallCount;
        private int _releaseCallCount;

        public ScriptedRuntimeLease(
            bool acquire = true,
            LeaseRenewalBehavior renewalBehavior = LeaseRenewalBehavior.Succeed,
            Task? renewalGate = null,
            Exception? renewalFailure = null,
            Action<string>? recordLifecycle = null)
        {
            _acquire = acquire;
            _renewalBehavior = renewalBehavior;
            _renewalGate = renewalGate ?? Task.CompletedTask;
            _renewalFailure = renewalFailure ?? new InvalidOperationException("scripted lease renewal failure");
            _recordLifecycle = recordLifecycle;
        }

        public TaskCompletionSource<bool> SecondRenewalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstRenewalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> NeverCompletingRenewal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FdcRuntimeLeaseGrant? FirstSuccessfulGrant { get; private set; }
        public FdcRuntimeLeaseGrant? SecondRenewalInput { get; private set; }
        public FdcRuntimeLeaseGrant? ReleasedGrant { get; private set; }
        public DateTime? AcquiredLeaseExpiresAt { get; private set; }
        public int ReleaseCallCount => Volatile.Read(ref _releaseCallCount);

        public Task<FdcRuntimeLeaseAcquireResult> TryAcquireAsync(
            string ownerId,
            string configRevisionSha256,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_acquire)
            {
                return Task.FromResult(new FdcRuntimeLeaseAcquireResult(
                    false,
                    new FdcRuntimeLeaseState(null, 1, null, DateTime.UtcNow, null),
                    null));
            }

            var authority = new FdcRuntimeAuthority(
                ownerId, 1, configRevisionSha256, DateTime.UtcNow.Add(leaseDuration));
            AcquiredLeaseExpiresAt = authority.LeaseExpiresAt;
            _currentGrant = new TestGrant(authority);
            return Task.FromResult(new FdcRuntimeLeaseAcquireResult(
                true,
                State(authority),
                _currentGrant));
        }

        public async Task<FdcRuntimeLeaseGrant?> TryRenewAsync(
            FdcRuntimeLeaseGrant grant,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _renewalCallCount);
            if (call == 1)
                FirstRenewalEntered.TrySetResult(true);
            if (_renewalBehavior == LeaseRenewalBehavior.IgnoreCancellationAndNeverComplete)
                await NeverCompletingRenewal.Task;
            if (_renewalBehavior == LeaseRenewalBehavior.SucceedOnceThenBlock && call == 2)
            {
                SecondRenewalInput = grant;
                SecondRenewalEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            await _renewalGate.WaitAsync(ct);
            if (_renewalBehavior == LeaseRenewalBehavior.ReturnNull)
                return null;
            if (_renewalBehavior == LeaseRenewalBehavior.Throw)
                throw _renewalFailure;

            var renewed = new TestGrant(
                grant.Authority with { LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration) });
            _currentGrant = renewed;
            if (call == 1)
                FirstSuccessfulGrant = renewed;
            return renewed;
        }

        public Task<bool> TryReleaseAsync(
            FdcRuntimeLeaseGrant grant,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _releaseCallCount);
            ReleasedGrant = grant;
            _recordLifecycle?.Invoke("lease-release");
            var released = ReferenceEquals(_currentGrant, grant);
            if (released)
                _currentGrant = null;
            return Task.FromResult(released);
        }

        public Task<FdcRuntimeLeaseState> GetStateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_currentGrant is null
                ? new FdcRuntimeLeaseState(null, 1, null, DateTime.UtcNow, null)
                : State(_currentGrant.Authority));
        }

        private static FdcRuntimeLeaseState State(FdcRuntimeAuthority authority) => new(
            authority.OwnerId,
            authority.FenceToken,
            authority.LeaseExpiresAt,
            DateTime.UtcNow,
            authority.ConfigRevision);

        private sealed class TestGrant(FdcRuntimeAuthority authority)
            : FdcRuntimeLeaseGrant(authority);
    }

    private sealed record RuntimeLeaseWorkerFixture(
        FdcCollectionWorker Worker,
        FdcCollectorService Collector,
        Mock<IPlcDriver> Driver,
        Mock<IPlcConnection> Connection,
        TaskCompletionSource<bool> Running,
        TaskCompletionSource<bool> Closed);

    private static async Task StartAndAwaitExecutionAsync(FdcCollectionWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        if (worker.ExecuteTask is not null)
            await worker.ExecuteTask;
    }

    private sealed class ConfirmedInterlockActionPort : IFdcInterlockActionPort
    {
        public Task<FdcInterlockActionReadiness> CheckReadyAsync(
            IReadOnlyCollection<string> requiredActions,
            CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));

        public Task<FdcInterlockActionResult> ApplyAsync(
            FdcInterlockActionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockActionResult.Confirmed($"test:{request.EffectId}"));

        public Task<FdcInterlockActionResult> ReconcileAsync(FdcInterlockActionRequest request, CancellationToken ct = default) =>
            ApplyAsync(request, ct);

        public Task<FdcInterlockReleaseResult> ReleaseAsync(FdcInterlockReleaseRequest request, CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockReleaseResult.Confirmed($"release:{request.EffectId}"));
    }
}
