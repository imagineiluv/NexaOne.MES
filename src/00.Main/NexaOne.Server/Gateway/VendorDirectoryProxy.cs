using NexaFramework;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM 공급처 directory를 형제 Spring 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class VendorDirectoryProxy : IVendorDirectory
{
    public Task<bool> VendorExistsAsync(string vendorId, CancellationToken ct = default)
        => Resolve().VendorExistsAsync(vendorId, ct);

    private static IVendorDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Mdm", "vendorDirectory");
        return bean as IVendorDirectory
            ?? throw ModuleProxy.TypeMismatch<IVendorDirectory>("Mdm", "vendorDirectory", bean);
    }
}
