using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM 공급처 directory를 형제 Spring 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class VendorDirectoryProxy : IVendorDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public VendorDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> VendorExistsAsync(string vendorId, CancellationToken ct = default)
        => Resolve().VendorExistsAsync(vendorId, ct);

    private IVendorDirectory Resolve() =>
        _resolver.Resolve<IVendorDirectory>("Mdm", "vendorDirectory");
}
