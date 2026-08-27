using NexaOne.Infrastructure.Diagnostics;
using NexaLogic.Plc.Hosting;

namespace NexaOne.Server;

/// <summary>
/// Spring 루트의 NexaLogic PLC factory 등록 상태를 제품 readiness catalog에 연결한다.
/// 이 probe는 외부 PLC에 접속하지 않고 등록된 프로토콜 구현과 구성 상태만 진단한다.
/// </summary>
internal sealed class FdcPlcProtocolCatalogProbe : IExternalDependencyProbe
{
    public const string DependencyId = "nexaone.fdc.plc";

    private readonly Func<bool> _modulesEnabled;
    private readonly Func<bool> _runtimeInitialized;
    private readonly Func<IPlcDriverFactory> _resolveFactory;

    public FdcPlcProtocolCatalogProbe(NexaOneMesRuntimeState runtime)
        : this(
            modulesEnabled: CreateModulesEnabledProbe(runtime),
            runtimeInitialized: () => runtime.IsInitialized,
            resolveFactory: () => runtime.GetInitializedServerBean<IPlcDriverFactory>("plcDriverFactory"))
    {
    }

    /// <summary>Deterministic contract seam used to verify a real protocol catalog without starting Spring.</summary>
    internal FdcPlcProtocolCatalogProbe(IPlcDriverFactory driverFactory)
        : this(
            modulesEnabled: static () => true,
            runtimeInitialized: static () => true,
            resolveFactory: CreateFactoryProbe(driverFactory))
    {
    }

    private FdcPlcProtocolCatalogProbe(
        Func<bool> modulesEnabled,
        Func<bool> runtimeInitialized,
        Func<IPlcDriverFactory> resolveFactory)
    {
        _modulesEnabled = modulesEnabled ?? throw new ArgumentNullException(nameof(modulesEnabled));
        _runtimeInitialized = runtimeInitialized ?? throw new ArgumentNullException(nameof(runtimeInitialized));
        _resolveFactory = resolveFactory ?? throw new ArgumentNullException(nameof(resolveFactory));
        Descriptor = new ExternalDependencyDescriptor(
            DependencyId,
            "NexaOne FDC PLC protocol catalog",
            "plc",
            typeof(IPlcDriverFactory).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            [
                "endpoint-mapping",
                "multi-protocol-selection",
                "plc-quality-state",
                "plc-read",
                "plc-subscription",
                "plc-write",
            ]);
    }

    public ExternalDependencyDescriptor Descriptor { get; }

    private static Func<bool> CreateModulesEnabledProbe(NexaOneMesRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return () => runtime.ModulesEnabled;
    }

    private static Func<IPlcDriverFactory> CreateFactoryProbe(IPlcDriverFactory driverFactory)
    {
        ArgumentNullException.ThrowIfNull(driverFactory);
        return () => driverFactory;
    }

    public ValueTask<ExternalDependencyHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        if (!_modulesEnabled())
        {
            return ValueTask.FromResult(new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Disabled,
                "MES modules are disabled; the PLC factory was not started.",
                now,
                new Dictionary<string, string> { ["driverCount"] = "0" }));
        }

        if (!_runtimeInitialized())
        {
            return ValueTask.FromResult(new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Unhealthy,
                "MES module runtime is not initialized and has not started, so the PLC factory cannot be resolved.",
                now));
        }

        try
        {
            var factory = _resolveFactory();
            var drivers = factory.GetAllDrivers()
                .OrderBy(static driver => driver.Kind)
                .ToArray();
            if (drivers.Length == 0)
            {
                return ValueTask.FromResult(new ExternalDependencyHealth(
                    ExternalDependencyHealthStatus.Unhealthy,
                    "The PLC driver factory contains no registered protocol drivers.",
                    now,
                    new Dictionary<string, string> { ["driverCount"] = "0" }));
            }

            var protocols = string.Join(",", drivers.Select(static driver => driver.Kind));
            return ValueTask.FromResult(new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Healthy,
                $"PLC factory is ready with {drivers.Length} protocol driver(s); endpoint connectivity is checked separately.",
                now,
                new Dictionary<string, string>
                {
                    ["driverCount"] = drivers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["registeredProtocols"] = protocols,
                }));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Spring 예외 메시지에는 XML 값이 섞일 수 있으므로 공개 진단에는 bean 이름과 예외 형식만 남긴다.
            return ValueTask.FromResult(new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Unhealthy,
                $"Spring bean 'plcDriverFactory' is unavailable ({error.GetType().Name}).",
                now,
                new Dictionary<string, string>
                {
                    ["exceptionType"] = error.GetType().FullName ?? error.GetType().Name,
                }));
        }
    }
}
