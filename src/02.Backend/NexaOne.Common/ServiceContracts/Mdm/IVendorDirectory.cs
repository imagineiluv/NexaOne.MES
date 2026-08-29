using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// 공급처 마스터의 존재 여부만 노출하는 교차 모듈 directory 계약입니다.
/// MDM 물리 스키마는 MDM adapter가 소유하며 EMS는 이 계약만 사용합니다.
/// </summary>
public interface IVendorDirectory : INexaModuleBridge
{
    Task<bool> VendorExistsAsync(string vendorId, CancellationToken ct = default);
}
