using System.Globalization;
using NexaOne.FDC.Application.Fdc;
using NexaFramework.Resource;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>
/// NexaLogic <see cref="IPlcDriver"/>를 NexaFramework <see cref="IDeviceInterface"/>로
/// 노출하는 프로토콜 중립 어댑터. PlantController가 관리하는 Machine에 장착되어 PLC 연결의
/// lifecycle(Initialize → Start → Stop)을 담당한다.
/// </summary>
/// <remarks>
/// 프로토콜 본체(세션·재연결·구독·품질상태)는 NexaLogic 서브모듈이 소유한다. 본 어댑터는
/// IDeviceInterface 계약 변환과 에러 전파만 책임지며, 태그 구독·수집 데이터의 FDC_TB_COLLECT_DATA
/// 적재 같은 비즈니스 로직은 <see cref="Connection"/>(IPlcConnection)을 통해 상위 수집 서비스가 수행한다.
/// </remarks>
public sealed class PlcDeviceInterface : IDeviceInterface
{
    private readonly IPlcDriver _driver;
    private readonly PlcEndpoint _endpoint;
    private IPlcConnection? _connection;

    /// <summary>표준 PLC 엔드포인트와 이를 처리할 드라이버를 NexaFramework 장치 어댑터로 구성한다.</summary>
    public PlcDeviceInterface(string interfaceName, PlcEndpoint endpoint, IPlcDriver driver)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("Interface name is required.", nameof(interfaceName));

        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(driver);

        // 연결 시도 전에 프로토콜·드라이버 조합을 검증해 오류를 구성 경계에서 드러낸다.
        if (driver.Kind != endpoint.DriverKind)
            throw new ArgumentException(
                $"PLC driver '{driver.Name}' ({driver.Kind}) cannot handle endpoint "
                + $"'{endpoint.EndpointId}' ({endpoint.DriverKind}).",
                nameof(driver));

        InterfaceName = interfaceName;
        _endpoint = endpoint;
        _driver = driver;
    }

    /// <summary>PlantController에서 장치를 식별할 인터페이스 이름.</summary>
    public string InterfaceName { get; }

    /// <summary>현재 PLC 연결 수명 주기 상태.</summary>
    public ResourceState State { get; private set; } = ResourceState.Created;

    /// <summary>연결된 엔드포인트의 드라이버 종류.</summary>
    public PlcDriverKind DriverKind => _endpoint.DriverKind;

    /// <summary>초기화 이후 노출되는 NexaLogic 연결. 태그 읽기/쓰기/구독은 이 연결을 통해 수행한다.</summary>
    public IPlcConnection? Connection => _connection;

    /// <summary>장치 초기화·실행 중 오류가 발생했을 때 발행된다.</summary>
    public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred;

    /// <summary>드라이버 연결을 생성하고 통신 세션을 열어 장치를 준비 상태로 전환한다.</summary>
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

    /// <summary>이미 열린 연결의 응답을 확인하고 장치를 실행 상태로 전환한다.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        try
        {
            // Initialize에서 연 세션을 다시 열지 않고 Ping으로 실행 가능 상태만 확인한다.
            await _connection!.PingAsync(cancellationToken);
            State = ResourceState.Running;
        }
        catch (Exception ex)
        {
            Fail(ex, isAllStop: true);
            throw;
        }
    }

    /// <summary>열린 PLC 세션을 닫고 장치를 정지 상태로 전환한다.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            State = ResourceState.Stopping;
            await _connection.CloseAsync(cancellationToken);
        }
        State = ResourceState.Stopped;
    }

    /// <summary>
    /// Subscribes through the active PLC connection and normalizes protocol events before they
    /// cross into the FDC application layer.
    /// </summary>
    public Task SubscribeAsync(
        IEnumerable<PlcSubscription> subscriptions,
        Func<FdcTagSample, Task> onSample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(onSample);
        EnsureInitialized();

        return _connection!.SubscriptionProvider.StartAsync(
            _connection.Endpoint,
            subscriptions,
            plcEvent => onSample(NormalizeSample(plcEvent)),
            cancellationToken);
    }

    /// <summary>
    /// Converts a transport event into an FDC sample without allowing a null or non-numeric
    /// payload to retain <see cref="FdcSampleQuality.Good"/>. The fallback zero is persisted only
    /// as a bad observation and is therefore excluded from alarm and interlock evaluation.
    /// </summary>
    internal static FdcTagSample NormalizeSample(PlcTagChangeEvent plcEvent)
    {
        ArgumentNullException.ThrowIfNull(plcEvent);

        var quality = MapQuality(plcEvent.Quality);
        if (!TryConvertToDecimal(plcEvent.After, out var value))
            quality = FdcSampleQuality.Bad;

        return new FdcTagSample(plcEvent.TagName, value, quality);
    }

    internal static FdcSampleQuality MapQuality(PlcQuality quality) => quality switch
    {
        PlcQuality.Good => FdcSampleQuality.Good,
        PlcQuality.Uncertain => FdcSampleQuality.Uncertain,
        _ => FdcSampleQuality.Bad,
    };

    private static bool TryConvertToDecimal(object? value, out decimal result)
    {
        if (value is null)
        {
            result = 0m;
            return false;
        }

        try
        {
            result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            result = 0m;
            return false;
        }
    }

    /// <summary>드라이버 연결이 소유한 비동기 자원을 해제한다.</summary>
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
                $"PLC device interface '{InterfaceName}' is not initialized (state: {State}).");
    }

    private void Fail(Exception ex, bool isAllStop)
    {
        // 어댑터는 프로토콜 예외를 숨기지 않고 NexaFramework의 표준 장치 오류 신호로 변환한다.
        State = ResourceState.Error;
        ErrorOccurred?.Invoke(this, new DeviceErrorEventArgs
        {
            InterfaceName = InterfaceName,
            DeviceName = _endpoint.EndpointId,
            Message = ex.Message,
            IsAllStop = isAllStop
        });
    }
}
