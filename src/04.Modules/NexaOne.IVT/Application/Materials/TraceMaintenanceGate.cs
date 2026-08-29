using Microsoft.Extensions.Configuration;

namespace NexaOne.IVT.Application.Materials;

/// <summary>
/// V150 보존 경계와 binding mutation을 동시에 실행하지 않도록 설정 시점에 닫히는 fail-closed gate입니다.
/// </summary>
internal sealed record TraceMaintenanceGate(bool IsOpen, string Reason)
{
    public static TraceMaintenanceGate Open() => new(true, string.Empty);

    public static TraceMaintenanceGate Closed(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason) ? "trace configuration is not quiesced" : reason.Trim());

    public static TraceMaintenanceGate From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue("Ivt:TraceConfiguration:MaintenanceMode", false))
            return Closed("Ivt:TraceConfiguration:MaintenanceMode=true is required.");
        if (configuration.GetValue("Worker:Fdc:Enabled", false))
            return Closed("FDC collection must be disabled for trace configuration maintenance.");
        if (configuration.GetValue("Worker:Fdc:Retention:Enabled", false))
            return Closed("FDC retention must be disabled for trace configuration maintenance.");
        if (configuration.GetValue("Worker:Ivt:TraceMaterialConsumption:Enabled", false)
            || configuration.GetValue("Ivt:TraceProjection:Enabled", false))
        {
            return Closed("TRACE material projection must be disabled for trace configuration maintenance.");
        }

        return Open();
    }
}
