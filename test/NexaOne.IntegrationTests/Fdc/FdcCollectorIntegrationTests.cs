using NexaOne.FDC.Infrastructure.Equipment;
using NexusLogic.Plc.Abstractions.Models;
using NexusLogic.Plc.Simulator;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FDC 실연결 통합 테스트 골격 (design 10.4.2). NexusLogic OPC-UA 시뮬레이터 서버를 인프로세스로 기동해
/// OpcUaDeviceInterface → NexusLogic.Plc.OpcUa 실연결 라운드트립을 검증한다.
/// </summary>
/// <remarks>
/// 실제 OPC-UA 서버 기동(인증서 PKI 생성 + TCP 포트 바인딩 + 세션 핸드셰이크)과, 적재 검증 시 실 MSSQL이
/// 필요하므로 기본 실행에서는 <c>Skip</c>한다. testcontainers(MSSQL) + 포트/인증서가 갖춰진 CI에서 Skip을 제거해 실행한다.
/// </remarks>
public sealed class FdcCollectorIntegrationTests
{
    [Fact(Skip = "실 OPC-UA 서버 기동(인증서/포트)·실 MSSQL 필요 — 환경 구축된 CI에서 Skip 제거 후 실행")]
    public async Task OpcUaDevice_connects_and_reads_from_simulator()
    {
        // 1) OPC-UA 시뮬레이터 서버 인프로세스 기동 (기본 노드: D0-D15 UInt16, M0-M3 Bool 등)
        await using var server = new OpcUaSimulatorServer(port: 48400);
        await server.StartAsync();

        // 2) OpcUaDeviceInterface(NexusLogic.Plc.OpcUa 어댑터)로 연결
        var endpoint = new PlcEndpoint("EP-SIM", PlcDriverKind.OpcUa, $"opc.tcp://localhost:{server.Port}");
        await using var device = new OpcUaDeviceInterface("EP-SIM", endpoint);
        await device.InitializeAsync();
        await device.StartAsync();

        // 3) 태그 읽기 라운드트립 — 시뮬레이터 기본 노드 D0
        device.Connection.Should().NotBeNull();
        var value = await device.Connection!.Reader.ReadAsync(
            new PlcTagDefinition("D0", "D0", PlcDataType.UInt16));

        value.Quality.Should().Be(PlcQuality.Good, "시뮬레이터 노드는 정상 품질로 읽혀야 한다");

        // 4) (확장) FdcCollectorService.StartCollectingAsync 구독 → FDC_COLLECT_DATA 적재 검증은
        //    testcontainers MSSQL 연결 후 추가한다.
    }
}
