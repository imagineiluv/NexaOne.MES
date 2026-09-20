using System.Data.Common;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>Forwards the caller's transaction to the current MDM master directory.</summary>
public sealed class BusinessMasterDirectoryProxy : IBusinessMasterDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public BusinessMasterDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<string?> FindPlantAsync(
        DbTransaction transaction, string plantId, CancellationToken ct = default)
        => Resolve().FindPlantAsync(transaction, plantId, ct);

    public Task<string?> FindActiveWorkerAsync(
        DbTransaction transaction, string workerId, string plantId, CancellationToken ct = default)
        => Resolve().FindActiveWorkerAsync(transaction, workerId, plantId, ct);

    public Task<ProductDto?> FindProductAsync(
        DbTransaction transaction, string productId, CancellationToken ct = default)
        => Resolve().FindProductAsync(transaction, productId, ct);

    public Task<IReadOnlyList<ProductDto>> FindProductsAsync(
        DbTransaction transaction, IReadOnlyList<string> productIds, CancellationToken ct = default)
        => Resolve().FindProductsAsync(transaction, productIds, ct);

    private IBusinessMasterDirectory Resolve() =>
        _resolver.Resolve<IBusinessMasterDirectory>("Mdm", "businessMasterDirectory");
}
