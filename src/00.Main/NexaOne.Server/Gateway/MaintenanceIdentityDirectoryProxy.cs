using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 보전 identity directory를 EMS 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class MaintenanceIdentityDirectoryProxy : IMaintenanceIdentityDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public MaintenanceIdentityDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<MaintenanceIdentityEntry?> GetActiveIdentityAsync(
        string userId,
        DateTime at,
        CancellationToken ct = default)
        => Resolve().GetActiveIdentityAsync(userId, at, ct);

    private IMaintenanceIdentityDirectory Resolve() =>
        _resolver.Resolve<IMaintenanceIdentityDirectory>("Sys", "maintenanceIdentityDirectory");
}
