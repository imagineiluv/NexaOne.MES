using NexaOne.CRM.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.CRM;

/// <summary>CRM composition root. Storage and Framework adapters remain private to the module.</summary>
public sealed class Module
{
    private readonly ICrmBridge _bridge;
    private readonly IBusinessProjectDirectory _projectDirectory;
    private readonly ISqliteSchemaContribution _schema;

    public Module(EesDataSource dataSource, IBusinessMembershipBridge memberships,
        IBusinessMasterDirectory masterDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(masterDirectory);
        _bridge = new CrmBridge(dataSource, memberships, masterDirectory);
        _projectDirectory = new BusinessProjectDirectory();
        _schema = new CrmSqliteSchemaContribution();
    }

    public ICrmBridge GetCrmBridge() => _bridge;
    public IBusinessProjectDirectory GetBusinessProjectDirectory() => _projectDirectory;
    public ISqliteSchemaContribution GetSqliteSchemaContribution() => _schema;
}
