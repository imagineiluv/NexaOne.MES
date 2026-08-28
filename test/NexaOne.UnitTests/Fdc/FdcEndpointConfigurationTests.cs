using Microsoft.Data.Sqlite;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Persistence;
using NexaOne.UnitTests.TestInfrastructure;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>V145의 비밀 없는 구조화 endpoint 설정을 도메인→DB→NexaLogic 계약까지 고정한다.</summary>
public sealed class FdcEndpointConfigurationTests
{
    private static string ExistingTagMapPath => typeof(FdcEndpointConfigurationTests).Assembly.Location;

    [Fact]
    public void Mapper_maps_modbus_unit_timeouts_and_polling_recovery_without_arbitrary_options()
    {
        var settings = new FdcPlcEndpointSettings(
            ModbusUnitId: 7,
            ConnectionTimeoutMs: 1_500,
            ReadWriteTimeoutMs: 2_500,
            HeartbeatTimeoutMs: 3_500,
            PollingDisconnectBackoffMs: 125,
            PollingMaxDisconnectBackoffMs: 2_000);
        var endpoint = Create("ModbusTcp", settings);

        var mapped = FdcEndpointMapper.ToPlcEndpoint(endpoint);

        mapped.DriverKind.Should().Be(PlcDriverKind.ModbusTcp);
        mapped.UnitId.Should().Be("7");
        mapped.Rack.Should().BeNull();
        mapped.Station.Should().BeNull();
        mapped.Timeouts.ConnectionTimeout.Should().Be(TimeSpan.FromMilliseconds(1_500));
        mapped.Timeouts.ReadWriteTimeout.Should().Be(TimeSpan.FromMilliseconds(2_500));
        mapped.Timeouts.HeartbeatTimeout.Should().Be(TimeSpan.FromMilliseconds(3_500));
        mapped.Options.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["tagMapPath"] = Path.GetFullPath(ExistingTagMapPath),
            ["polling.disconnectBackoffMs"] = "125",
            ["polling.maxDisconnectBackoffMs"] = "2000",
        });
        mapped.Options.Keys.Should().NotContain(key =>
            key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("username", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Domain_and_mapper_accept_configured_protocol_case_insensitively()
    {
        var endpoint = Create("modbustcp", new FdcPlcEndpointSettings(ModbusUnitId: 9));

        var mapped = FdcEndpointMapper.ToPlcEndpoint(endpoint);

        mapped.DriverKind.Should().Be(PlcDriverKind.ModbusTcp);
        mapped.UnitId.Should().Be("9");
    }

    [Fact]
    public void Mapper_maps_siemens_rack_and_slot_as_structured_endpoint_fields()
    {
        var mapped = FdcEndpointMapper.ToPlcEndpoint(Create(
            "SiemensS7",
            new FdcPlcEndpointSettings(S7Rack: 2, S7Slot: 5)));

        mapped.DriverKind.Should().Be(PlcDriverKind.SiemensS7);
        mapped.Rack.Should().Be("2");
        mapped.Slot.Should().Be("5");
        mapped.UnitId.Should().BeNull();
    }

    [Fact]
    public void Mapper_maps_mitsubishi_routing_and_canonical_hex_io_number()
    {
        var mapped = FdcEndpointMapper.ToPlcEndpoint(Create(
            "MitsubishiMc",
            new FdcPlcEndpointSettings(
                MitsubishiStationNo: 3,
                MitsubishiNetworkNo: 1,
                MitsubishiPcNo: 255,
                MitsubishiIoNo: 1023,
                MitsubishiFrameFormat: "Ascii")));

        mapped.DriverKind.Should().Be(PlcDriverKind.MitsubishiMc);
        mapped.Station.Should().Be("3");
        mapped.Options["networkNo"].Should().Be("1");
        mapped.Options["pcNo"].Should().Be("255");
        mapped.Options["ioNo"].Should().Be("03FF",
            "NexaLogic parses Mitsubishi ioNo as hexadecimal before decimal");
        mapped.Options["frameFormat"].Should().Be("ascii");
    }

    [Fact]
    public void Mapper_keeps_ethernet_ip_options_to_the_common_allowlist()
    {
        var mapped = FdcEndpointMapper.ToPlcEndpoint(Create("EtherNetIp", new FdcPlcEndpointSettings()));

        mapped.Options.Keys.Should().BeEquivalentTo(
            "tagMapPath",
            "polling.disconnectBackoffMs",
            "polling.maxDisconnectBackoffMs");
        mapped.UnitId.Should().BeNull();
        mapped.Rack.Should().BeNull();
        mapped.Slot.Should().BeNull();
        mapped.Station.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 5000, 5000)]
    [InlineData(5000, 0, 5000)]
    [InlineData(5000, 5000, 0)]
    public void Domain_rejects_non_positive_required_timeouts(
        int connectionTimeoutMs,
        int readWriteTimeoutMs,
        int heartbeatTimeoutMs)
    {
        var result = FdcEquipmentEndpoint.Create(
            "EP-BAD", "EQ-1", "ModbusTcp", "tcp://plc:502", tagMapPath: ExistingTagMapPath,
            plcSettings: new FdcPlcEndpointSettings(
                ConnectionTimeoutMs: connectionTimeoutMs,
                ReadWriteTimeoutMs: readWriteTimeoutMs,
                HeartbeatTimeoutMs: heartbeatTimeoutMs));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("timeout", Exactly.Once());
    }

    [Fact]
    public void Restored_drifted_settings_fail_closed_at_mapper_startup()
    {
        var endpoint = FdcEquipmentEndpoint.Restore(
            "EP-DRIFT", "EQ-1", "EtherNetIp", "tcp://plc:44818", 500, true,
            tagMapPath: ExistingTagMapPath,
            plcSettings: new FdcPlcEndpointSettings(S7Rack: 0, S7Slot: 1));

        var map = () => FdcEndpointMapper.ToPlcEndpoint(endpoint);

        map.Should().Throw<InvalidOperationException>()
            .WithMessage("*EP-DRIFT*invalid PLC settings*S7 rack/slot*SiemensS7*");
    }

    [Theory]
    [InlineData("tcp://plc:502", 502)]
    [InlineData("plc:502", 502)]
    [InlineData("tcp://plc:80", 80)]
    public void Mapper_preserves_every_explicit_tcp_port(string endpointUrl, int expectedPort)
    {
        var endpoint = FdcEquipmentEndpoint.Create(
            "EP-PORT", "EQ-1", "ModbusTcp", endpointUrl,
            tagMapPath: ExistingTagMapPath).Value;

        var mapped = FdcEndpointMapper.ToPlcEndpoint(endpoint);

        mapped.Host.Should().Be("plc");
        mapped.Port.Should().Be(expectedPort,
            "an explicitly configured port must not collapse into a driver default");
    }

    [Theory]
    [InlineData("http://plc:80")]
    [InlineData("https://plc:443")]
    [InlineData("opc.tcp://plc:4840")]
    [InlineData("ftp://plc:21")]
    public void Domain_rejects_non_tcp_endpoint_schemes(string endpointUrl)
    {
        var result = FdcEquipmentEndpoint.Create(
            "EP-SCHEME", "EQ-1", "ModbusTcp", endpointUrl,
            tagMapPath: ExistingTagMapPath);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("tcp://");
    }

    [Fact]
    public void Restored_non_tcp_scheme_fails_closed_at_mapper_startup()
    {
        var endpoint = FdcEquipmentEndpoint.Restore(
            "EP-HTTP", "EQ-1", "ModbusTcp", "http://plc:80", 500, true,
            tagMapPath: ExistingTagMapPath);

        var map = () => FdcEndpointMapper.ToPlcEndpoint(endpoint);

        map.Should().Throw<InvalidOperationException>()
            .WithMessage("*EP-HTTP*must use tcp://*");
    }

    [Theory]
    [InlineData("tcp://operator:password@plc:502")]
    [InlineData("tcp://plc:502?password=plain")]
    [InlineData("tcp://plc:502/config/secret")]
    [InlineData("tcp://plc:502/")]
    [InlineData("plc:502/config/secret")]
    [InlineData("tcp://plc:502\\config\\secret")]
    public void Domain_rejects_inline_connection_secrets_and_unused_url_payloads(string endpointUrl)
    {
        var result = FdcEquipmentEndpoint.Create(
            "EP-SECRET", "EQ-1", "ModbusTcp", endpointUrl,
            tagMapPath: ExistingTagMapPath);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("only the PLC host and port");
    }

    [Fact]
    public void Update_url_does_not_replace_a_safe_endpoint_with_inline_credentials()
    {
        var endpoint = Create("ModbusTcp", new FdcPlcEndpointSettings());

        endpoint.UpdateUrl("tcp://operator:plain-password@plc:502");

        endpoint.EndpointUrl.Should().Be("tcp://plc:1234");
    }

    [Fact]
    public async Task Repository_round_trip_preserves_structured_endpoint_configuration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexa-fdc-endpoint-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=False";
        try
        {
            CreateEndpointTable(connectionString);
            var repository = new FdcEquipmentEndpointRepository(new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            });
            var expectedSettings = new FdcPlcEndpointSettings(
                MitsubishiStationNo: 4,
                MitsubishiNetworkNo: 2,
                MitsubishiPcNo: 254,
                MitsubishiIoNo: 1023,
                MitsubishiFrameFormat: "Binary",
                ConnectionTimeoutMs: 1_100,
                ReadWriteTimeoutMs: 2_200,
                HeartbeatTimeoutMs: 3_300,
                PollingDisconnectBackoffMs: 150,
                PollingMaxDisconnectBackoffMs: 1_500);
            var endpoint = Create("MitsubishiMc", expectedSettings);

            await repository.AddAsync(endpoint);
            var restored = await repository.GetByIdAsync(endpoint.Id);

            restored.Should().NotBeNull();
            restored!.PlcSettings.Should().Be(expectedSettings);
            FdcEndpointMapper.ToPlcEndpoint(restored).Options["ioNo"].Should().Be("03FF");
        }
        finally
        {
            try { File.Delete(databasePath); } catch { /* best-effort temporary database cleanup */ }
        }
    }

    private static FdcEquipmentEndpoint Create(string protocol, FdcPlcEndpointSettings settings) =>
        FdcEquipmentEndpoint.Create(
            "EP-1", "EQ-1", protocol, "tcp://plc:1234", 500,
            ExistingTagMapPath, settings).Value;

    private static void CreateEndpointTable(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE FDC_EQUIPMENT_ENDPOINT (
                ENDPOINT_ID TEXT NOT NULL PRIMARY KEY,
                EQUIPMENT_ID TEXT NOT NULL,
                PROTOCOL TEXT NOT NULL,
                ENDPOINT_URL TEXT NOT NULL,
                TAG_MAP_PATH TEXT NULL,
                MODBUS_UNIT_ID INTEGER NULL,
                S7_RACK INTEGER NULL,
                S7_SLOT INTEGER NULL,
                MITSUBISHI_STATION_NO INTEGER NULL,
                MITSUBISHI_NETWORK_NO INTEGER NULL,
                MITSUBISHI_PC_NO INTEGER NULL,
                MITSUBISHI_IO_NO INTEGER NULL,
                MITSUBISHI_FRAME_FORMAT TEXT NULL,
                CONNECTION_TIMEOUT_MS INTEGER NOT NULL,
                READ_WRITE_TIMEOUT_MS INTEGER NOT NULL,
                HEARTBEAT_TIMEOUT_MS INTEGER NOT NULL,
                POLLING_DISCONNECT_BACKOFF_MS INTEGER NOT NULL,
                POLLING_MAX_DISCONNECT_BACKOFF_MS INTEGER NOT NULL,
                SAMPLING_INTERVAL_MS INTEGER NOT NULL,
                IS_ACTIVE INTEGER NOT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL,
                UPDATED_BY TEXT NULL,
                UPDATED_AT TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
