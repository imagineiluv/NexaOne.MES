using System.Data.Common;

namespace NexaOne.ServiceContracts.Crm;

/// <summary>CRM-owned project existence checks for sibling modules that already own a
/// Serializable transaction. The caller retains connection, transaction and commit ownership.</summary>
public interface IBusinessProjectDirectory : INexaModuleBridge
{
    Task<bool> ProjectExistsInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid projectId,
        CancellationToken ct = default);
}
