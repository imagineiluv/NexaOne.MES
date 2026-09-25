using System.Data.Common;
using NexaOne.ServiceContracts.Crm;

namespace NexaOne.Server.Gateway;

/// <summary>Connects ERP's plugin context to the CRM-owned project directory.</summary>
public sealed class BusinessProjectDirectoryProxy(ModuleBeanResolver resolver) : IBusinessProjectDirectory
{
    private readonly ModuleBeanResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> ProjectExistsInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid projectId,
        CancellationToken ct = default)
        => Resolve().ProjectExistsInTransactionAsync(transaction, tenantId, organizationId, projectId, ct);

    private IBusinessProjectDirectory Resolve()
        => _resolver.Resolve<IBusinessProjectDirectory>("Crm", "businessProjectDirectory");
}
