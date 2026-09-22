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

    public Task<PlantDto?> FindPlantDetailsAsync(
        DbTransaction transaction, string plantId, CancellationToken ct = default)
        => Resolve().FindPlantDetailsAsync(transaction, plantId, ct);

    public Task<string?> FindActiveWorkerAsync(
        DbTransaction transaction, string workerId, string plantId, CancellationToken ct = default)
        => Resolve().FindActiveWorkerAsync(transaction, workerId, plantId, ct);

    public Task<(IReadOnlyList<WorkerDto> Items, long Total)> QueryActiveWorkersAsync(
        DbTransaction transaction, string plantId, string? text = null, int offset = 0, int limit = 50,
        CancellationToken ct = default)
        => Resolve().QueryActiveWorkersAsync(transaction, plantId, text, offset, limit, ct);

    public Task<ProductDto?> FindProductAsync(
        DbTransaction transaction, string productId, CancellationToken ct = default)
        => Resolve().FindProductAsync(transaction, productId, ct);

    public Task<IReadOnlyList<ProductDto>> FindProductsAsync(
        DbTransaction transaction, IReadOnlyList<string> productIds, CancellationToken ct = default)
        => Resolve().FindProductsAsync(transaction, productIds, ct);

    public Task<CustomerDto?> FindCustomerAsync(
        DbTransaction transaction, string customerId, CancellationToken ct = default)
        => Resolve().FindCustomerAsync(transaction, customerId, ct);

    public Task<IReadOnlyList<CustomerDto>> FindCustomersAsync(
        DbTransaction transaction, IReadOnlyList<string> customerIds, CancellationToken ct = default)
        => Resolve().FindCustomersAsync(transaction, customerIds, ct);

    private IBusinessMasterDirectory Resolve() =>
        _resolver.Resolve<IBusinessMasterDirectory>("Mdm", "businessMasterDirectory");
}
