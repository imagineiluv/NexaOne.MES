using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.ServiceContracts.Crm;

namespace NexaOne.CRM.Infrastructure;

/// <summary>Small cross-module read seam over CRM_PROJECT; it never starts or completes a transaction.</summary>
internal sealed class BusinessProjectDirectory : IBusinessProjectDirectory
{
    public async Task<bool> ProjectExistsInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (tenantId == Guid.Empty || organizationId == Guid.Empty || projectId == Guid.Empty)
            throw new ArgumentException("Nonempty tenant, organization and project identifiers are required.");
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The project lookup transaction is not connected.");
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The project lookup connection must be open.");
        ct.ThrowIfCancellationRequested();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM CRM_PROJECT
             WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@ProjectId
            """, new
            {
                TenantId = tenantId.ToString("D"),
                OrganizationId = organizationId.ToString("D"),
                ProjectId = projectId.ToString("D")
            }, transaction, cancellationToken: ct));
        ct.ThrowIfCancellationRequested();
        return count == 1;
    }
}
