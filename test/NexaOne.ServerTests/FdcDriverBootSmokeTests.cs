using System.Net;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>NexaLogic PLC DriverFactory의 Spring 부모 조립과 FDC plugin ALC 주입만 빠르게 검증한다.</summary>
[Collection(ChildProcessSmokeCollection.Name)]
public sealed class FdcDriverBootSmokeTests
{
    private readonly ITestOutputHelper _output;

    public FdcDriverBootSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Modules_host_boots_with_plc_driver_factory_and_fdc_worker()
    {
        using var host = await HostProcess.StartAsync(_output, springConfig: null, expectListening: true);

        host.Listening.Should().BeTrue(
            $"PLC DriverFactory와 FDC 자식 컨텍스트가 조립된 호스트가 기동해야 한다. 로그:\n{host.Log}");
        host.Log.Should().Contain("Service 'Fdc' registered",
            "plcDriverFactory → FdcPlcDeviceFactory → FdcCollectionWorker의 cross-context 생성이 완료돼야 한다");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        (await http.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
