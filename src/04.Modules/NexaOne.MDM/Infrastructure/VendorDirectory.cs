using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>MDM 공급처 마스터의 존재 여부를 제공하는 adapter입니다.</summary>
public sealed class VendorDirectory : QueryRepository, IVendorDirectory
{
    public VendorDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<bool> VendorExistsAsync(
        string vendorId,
        CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM MDM_VENDOR WHERE VENDOR_ID=@vendorId",
            new { vendorId },
            ct) > 0;
}
