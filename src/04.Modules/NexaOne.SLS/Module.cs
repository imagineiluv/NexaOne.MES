using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sls;
using NexaOne.SLS.Application;
using NexaOne.SLS.Infrastructure;

namespace NexaOne.SLS;

/// <summary>SLS의 단일 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IMrpDemandDirectory _mrpDemandDirectory;
    private readonly ISalesRequestBridge _salesRequestBridge;

    public Module(EesDataSource dataSource, IBusinessMasterDirectory masterDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(masterDirectory);
        _mrpDemandDirectory = new MrpDemandDirectory(dataSource);
        _salesRequestBridge = new SalesRequestService(new SalesRequestRepository(dataSource, masterDirectory));
    }

    public IMrpDemandDirectory GetMrpDemandDirectory() => _mrpDemandDirectory;
    public ISalesRequestBridge GetSalesRequestBridge() => _salesRequestBridge;
}
