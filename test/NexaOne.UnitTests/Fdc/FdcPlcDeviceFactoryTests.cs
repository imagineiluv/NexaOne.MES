using System.Text.Json;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexaFramework;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;
using NexaLogic.Plc.Hosting;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcPlcDeviceFactoryTests
{
    [Fact]
    public async Task Collection_worker_treats_STOP_as_opaque_and_does_not_stop_the_machine()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-STOP", "EQ-001", "OpcUa", "opc.tcp://plc.local:4840", 500).Value;
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;

        var endpointRepository = new Mock<IFdcEquipmentEndpointRepository>();
        endpointRepository.Setup(repository => repository.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { endpoint });

        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { parameter });

        var subscriptionProvider = new Mock<IPlcSubscriptionProvider>();
        subscriptionProvider.Setup(provider => provider.StartAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<IEnumerable<PlcSubscription>>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(candidate => candidate.Endpoint)
            .Returns(new PlcEndpoint("EP-STOP", PlcDriverKind.OpcUa, "plc.local", 4840));
        connection.SetupGet(candidate => candidate.SubscriptionProvider)
            .Returns(subscriptionProvider.Object);
        connection.Setup(candidate => candidate.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.PingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(candidate => candidate.CloseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated stop failure"));

        var driver = Driver(PlcDriverKind.OpcUa, "opcua");
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
        interlockRuleRepository.Setup(repository => repository.GetActiveRulesAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { interlockRule });
        var interlockHistoryRepository = new Mock<IFdcInterlockHistoryRepository>();
        interlockHistoryRepository.Setup(repository => repository.GetUnresolvedAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        interlockHistoryRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var plant = new PlantController();
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, collectDataRepository.Object),
            new FdcInterlockService(interlockRuleRepository.Object, interlockHistoryRepository.Object));
        var worker = new FdcCollectionWorker(
            collector,
            endpointRepository.Object,
            parameterRepository.Object,
            plant,
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
            "EP-MODBUS", "EQ-001", "ModbusTcp", "tcp://plc.local:1502", 500).Value;

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
            "EP-RTU", "EQ-001", "ModbusRtu", "serial://com3", 500).Value;

        var act = () => sut.Create(endpoint);

        var error = act.Should().Throw<FdcPlcDriverNotRegisteredException>()
            .WithMessage("*EP-RTU*ModbusRtu*Registered driver kinds: OpcUa*")
            .Which;
        error.EndpointId.Should().Be("EP-RTU");
        error.Protocol.Should().Be("ModbusRtu");
        error.DriverKind.Should().Be(PlcDriverKind.ModbusRtu);
        error.RegisteredKinds.Should().Equal(PlcDriverKind.OpcUa);
    }

    [Fact]
    public async Task Collection_worker_propagates_driver_configuration_error_instead_of_skipping_endpoint()
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-RTU", "EQ-001", "ModbusRtu", "serial://com3", 500).Value;
        var endpointRepository = new Mock<IFdcEquipmentEndpointRepository>();
        endpointRepository.Setup(repository => repository.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { endpoint });

        var opcUa = Driver(PlcDriverKind.OpcUa, "opcua");
        var deviceFactory = new FdcPlcDeviceFactory(new PlcDriverFactory(new[] { opcUa.Object }));
        var parameterRepository = Mock.Of<IFdcParameterRepository>();
        var collector = new FdcCollectorService(new FdcDataService(
            parameterRepository,
            Mock.Of<IFdcCollectDataRepository>()));
        var worker = new FdcCollectionWorker(
            collector,
            endpointRepository.Object,
            parameterRepository,
            new PlantController(),
            deviceFactory,
            Mock.Of<IMessageBus>(),
            enabled: true,
            topic: "nexaone.events");

        var act = () => worker.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<FdcPlcDriverNotRegisteredException>()
            .WithMessage("*EP-RTU*ModbusRtu*");
    }

    private static Mock<IPlcDriver> Driver(PlcDriverKind kind, string name)
    {
        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(candidate => candidate.Kind).Returns(kind);
        driver.SetupGet(candidate => candidate.Name).Returns(name);
        return driver;
    }
}
