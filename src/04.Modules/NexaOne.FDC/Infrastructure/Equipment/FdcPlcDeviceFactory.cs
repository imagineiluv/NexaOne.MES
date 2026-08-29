using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaLogic.Plc.Abstractions.Models;
using NexaLogic.Plc.Hosting;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>FDC 엔드포인트의 프로토콜에 맞는 NexaLogic 드라이버를 선택해 장치 어댑터를 만든다.</summary>
public sealed class FdcPlcDeviceFactory
{
    private readonly IPlcDriverFactory _drivers;

    /// <summary>호스트에 등록된 PLC 드라이버 카탈로그를 사용하는 FDC 장치 팩토리를 생성한다.</summary>
    public FdcPlcDeviceFactory(IPlcDriverFactory drivers)
    {
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
    }

    /// <summary>FDC 설비 엔드포인트를 표준 PLC 엔드포인트로 변환하고 일치하는 드라이버 어댑터를 만든다.</summary>
    /// <param name="endpoint">장치 연결 정보와 프로토콜이 확정된 FDC 엔드포인트.</param>
    /// <returns>NexaFramework 설비 수명 주기에 연결할 PLC 장치 어댑터.</returns>
    public PlcDeviceInterface Create(FdcEquipmentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // 프로토콜 문자열 해석은 매퍼 한 곳에서 수행하고, 팩토리는 결정된 DriverKind만으로 드라이버를 선택한다.
        var plcEndpoint = FdcEndpointMapper.ToPlcEndpoint(endpoint);
        var driver = _drivers.GetDriver(plcEndpoint.DriverKind);
        if (driver is null)
        {
            // 실패 시 현재 호스트가 제공하는 종류를 함께 남겨 누락된 드라이버 등록을 즉시 진단할 수 있게 한다.
            var registeredKinds = _drivers.GetAllDrivers()
                .Select(static candidate => candidate.Kind)
                .Distinct()
                .OrderBy(static kind => kind)
                .ToArray();

            throw new FdcPlcDriverNotRegisteredException(
                endpoint.Id,
                endpoint.Protocol,
                plcEndpoint.DriverKind,
                registeredKinds);
        }

        return new PlcDeviceInterface(endpoint.Id, plcEndpoint, driver);
    }
}

/// <summary>엔드포인트 프로토콜은 유효하지만 해당 NexaLogic 드라이버가 호스트에 등록되지 않은 구성 오류.</summary>
public sealed class FdcPlcDriverNotRegisteredException : InvalidOperationException
{
    /// <summary>엔드포인트가 요구하는 드라이버와 호스트에 등록된 드라이버 목록을 포함한 구성 예외를 생성한다.</summary>
    public FdcPlcDriverNotRegisteredException(
        string endpointId,
        string protocol,
        PlcDriverKind driverKind,
        IReadOnlyList<PlcDriverKind> registeredKinds)
        : base(BuildMessage(endpointId, protocol, driverKind, registeredKinds))
    {
        EndpointId = endpointId;
        Protocol = protocol;
        DriverKind = driverKind;
        RegisteredKinds = Array.AsReadOnly(registeredKinds.ToArray());
    }

    /// <summary>연결에 실패한 FDC 엔드포인트 식별자.</summary>
    public string EndpointId { get; }

    /// <summary>FDC 설정에 입력된 프로토콜.</summary>
    public string Protocol { get; }

    /// <summary>엔드포인트가 요구한 표준 PLC 드라이버 종류.</summary>
    public PlcDriverKind DriverKind { get; }

    /// <summary>예외 발생 시점에 호스트에 등록되어 있던 드라이버 종류.</summary>
    public IReadOnlyList<PlcDriverKind> RegisteredKinds { get; }

    private static string BuildMessage(
        string endpointId,
        string protocol,
        PlcDriverKind driverKind,
        IReadOnlyList<PlcDriverKind> registeredKinds)
    {
        var registered = registeredKinds.Count == 0
            ? "none"
            : string.Join(", ", registeredKinds);

        return $"No PLC driver is registered for FDC endpoint '{endpointId}' "
            + $"(protocol='{protocol}', driverKind='{driverKind}'). "
            + $"Registered driver kinds: {registered}.";
    }
}
