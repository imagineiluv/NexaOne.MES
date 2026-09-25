using NexaOne.HR.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Hr;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.HR;

public sealed class Module
{
    private readonly TimeTrackingBridge _bridge;

    public Module(EesDataSource dataSource, IBusinessMembershipBridge businessMemberships,
        IBusinessProjectDirectory businessProjects)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(businessMemberships);
        ArgumentNullException.ThrowIfNull(businessProjects);
        _bridge = new(dataSource, businessMemberships, businessProjects);
    }

    public IHumanResourcesBridge GetHumanResourcesBridge() => _bridge;
    public ITimeBillingDirectory GetTimeBillingDirectory() => _bridge;
}
