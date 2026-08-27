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
