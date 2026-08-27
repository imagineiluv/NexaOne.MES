using NexaFramework;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 보전 identity directory를 EMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class MaintenanceIdentityDirectoryProxy : IMaintenanceIdentityDirectory
{
    public Task<MaintenanceIdentityEntry?> GetActiveIdentityAsync(
        string userId,
        DateTime at,
        CancellationToken ct = default)
        => Resolve().GetActiveIdentityAsync(userId, at, ct);

    private static IMaintenanceIdentityDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Sys", "maintenanceIdentityDirectory");
        return bean as IMaintenanceIdentityDirectory
            ?? throw ModuleProxy.TypeMismatch<IMaintenanceIdentityDirectory>(
                "Sys", "maintenanceIdentityDirectory", bean);
    }
}
