using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP;

/// <summary>
/// ERP의 단일 조립 진입점입니다. Spring XML에는 이 공개 모듈과 공개 bridge만 노출하고,
/// 저장소·업무 서비스의 구현 그래프는 이 클래스 안에 유지합니다.
/// </summary>
public sealed class Module
{
    private readonly IBillingBridge _billingBridge;
    private readonly IExpenseBridge _expenseBridge;

    public Module(
        EesDataSource dataSource,
        IBusinessMembershipBridge businessMemberships,
        IBusinessMasterDirectory businessMasters)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(businessMemberships);
        ArgumentNullException.ThrowIfNull(businessMasters);

        var bridge = new BillingBridge(dataSource, businessMemberships, businessMasters);
        _billingBridge = bridge;
        _expenseBridge = bridge;
    }

    /// <summary>Estimates, invoices and payments over the Framework billing service.</summary>
    public IBillingBridge GetBillingBridge() => _billingBridge;

    /// <summary>Expense directories, entries, reimbursements and invoice linkage.</summary>
    public IExpenseBridge GetExpenseBridge() => _expenseBridge;
}
