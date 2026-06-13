using NexusFramework.Resource;
using NexusLogic.Plc.Abstractions.Interfaces;
using NexusLogic.Plc.Abstractions.Models;
using NexusLogic.Plc.OpcUa;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>
/// NexusLogic.Plc.OpcUa(<see cref="IPlcDriver"/>)를 NexusFramework <see cref="IDeviceInterface"/>로
/// 노출하는 어댑터 글루. PlantController가 관리하는 Machine에 장착되어 OPC-UA 설비 연결의
/// lifecycle(Initialize → Start → Stop)을 담당한다.
/// </summary>
/// <remarks>
/// 프로토콜 본체(세션·재연결·구독·품질상태)는 NexusLogic 서브모듈이 소유한다. 본 어댑터는
/// IDeviceInterface 계약 변환과 에러 전파만 책임지며, 태그 구독·수집 데이터의 FDC_TB_COLLECT_DATA
/// 적재 같은 비즈니스 로직은 <see cref="Connection"/>(IPlcConnection)을 통해 상위 수집 서비스가 수행한다.
/// </remarks>
public sealed class OpcUaDeviceInterface : IDeviceInterface
{
    private readonly IPlcDriver _driver;
    private readonly PlcEndpoint _endpoint;
    private IPlcConnection? _connection;

    public OpcUaDeviceInterface(string interfaceName, PlcEndpoint endpoint, IPlcDriver? driver = null)
    {
        if (endpoint.DriverKind != PlcDriverKind.OpcUa)
            throw new ArgumentException(
                $"OpcUaDeviceInterface requires an OPC-UA endpoint, but got {endpoint.DriverKind}.",
                nameof(endpoint));

        InterfaceName = interfaceName;
        _endpoint = endpoint;
        _driver = driver ?? new OpcUaDriver();   // NexusLogic.Plc.OpcUa
    }

    public string InterfaceName { get; }
    public ResourceState State { get; private set; } = ResourceState.Created;

    /// <summary>초기화 이후 노출되는 NexusLogic 연결. 태그 읽기/쓰기/구독은 이 연결을 통해 수행한다.</summary>
    public IPlcConnection? Connection => _connection;

    public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            State = ResourceState.Initializing;
            _connection = await _driver.ConnectAsync(_endpoint, cancellationToken);
            await _connection.OpenAsync(cancellationToken);
            State = ResourceState.Ready;
        }
        catch (Exception ex)
        {
            Fail(ex, isAllStop: false);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        try
        {
            await _connection!.PingAsync(cancellationToken);   // 연결 활성 확인
            State = ResourceState.Running;
        }
        catch (Exception ex)
        {
            Fail(ex, isAllStop: true);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            State = ResourceState.Stopping;
            await _connection.CloseAsync(cancellationToken);
        }
        State = ResourceState.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private void EnsureInitialized()
    {
        if (_connection is null || (State != ResourceState.Ready && State != ResourceState.Running))
            throw new InvalidOperationException(
                $"OpcUaDeviceInterface '{InterfaceName}' is not initialized (state: {State}).");
    }

    private void Fail(Exception ex, bool isAllStop)
    {
        State = ResourceState.Error;
        ErrorOccurred?.Invoke(this, new DeviceErrorEventArgs
        {
            InterfaceName = InterfaceName,
            DeviceName    = _endpoint.EndpointId,
            Message       = ex.Message,
            IsAllStop     = isAllStop
        });
    }
}
