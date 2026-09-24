using NexaOne.CRM.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.CRM;

/// <summary>CRM composition root. Storage and Framework adapters remain private to the module.</summary>
public sealed class Module
{
    private readonly ICrmBridge _bridge;
    private readonly ISqliteSchemaContribution _schema;

    public Module(EesDataSource dataSource, IBusinessMembershipBridge memberships)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(memberships);
        _bridge = new CrmBridge(dataSource, memberships);
        _schema = new CrmSqliteSchemaContribution();
    }

    public ICrmBridge GetCrmBridge() => _bridge;
    public ISqliteSchemaContribution GetSqliteSchemaContribution() => _schema;
}
