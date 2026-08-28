using System.Diagnostics;
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
            enabled: true,
            topic: "nexaone.events");

        var act = () => worker.StartAsync(CancellationToken.None);

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
            enabled: true,
            topic: "nexaone.events");

        var act = () => worker.StartAsync(CancellationToken.None);

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
            enabled: true,
            topic: "nexaone.events");

        var act = () => worker.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*R-OLD*OLD01*outside the loaded topology*");
        driver.Verify(x => x.ConnectAsync(
            It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .ReturnsAsync(FdcInterlockActionReadiness.Ready());
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
            enabled: true,
            topic: "nexaone.events",
            streamFreshnessTimeout: failureMode is "stale" or "freeze"
                ? TimeSpan.FromMilliseconds(20)
                : TimeSpan.FromSeconds(30));

        if (failureMode == "stale")
        {
            var staleStart = () => worker.StartAsync(CancellationToken.None);
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
        collector.IsRunPermitted.Should().BeTrue();

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
            enabled: true,
            topic: "nexaone.events");

        var act = () => worker.StartAsync(CancellationToken.None);

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

    private static Mock<IPlcSubscriptionRuntimeHealth> AttachRuntimeHealth(
        Mock<IPlcSubscriptionProvider> provider,
        Task? completion = null,
        TimeSpan? initialPollAge = null)
    {
        var elapsed = Stopwatch.StartNew();
        var initialAge = initialPollAge ?? TimeSpan.Zero;
        var health = provider.As<IPlcSubscriptionRuntimeHealth>();
        health.SetupGet(candidate => candidate.Completion).Returns(
            completion ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        health.SetupGet(candidate => candidate.SubscriptionGeneration).Returns(1);
        health.SetupGet(candidate => candidate.IsRunning).Returns(true);
        health.SetupGet(candidate => candidate.CompletedPollCount).Returns(1);
        health.SetupGet(candidate => candidate.TimeSinceLastCompletedPoll)
            .Returns(() => initialAge + elapsed.Elapsed);
        health.SetupGet(candidate => candidate.LastCompletedPollAt)
            .Returns(() => DateTimeOffset.UtcNow - initialAge - elapsed.Elapsed);
        return health;
    }

    private sealed class ConfirmedInterlockActionPort : IFdcInterlockActionPort
    {
        public Task<FdcInterlockActionReadiness> CheckReadyAsync(
            IReadOnlyCollection<string> requiredActions,
            CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockActionReadiness.Ready());

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
