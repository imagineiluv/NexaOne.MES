using System.Data.Common;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// MDM-owned plant, worker and product checks in the caller's live Serializable transaction.
/// The caller owns the connection, commit, rollback and disposal; these reads do not cache results.
/// </summary>
public interface IBusinessMasterDirectory : INexaModuleBridge
{
    /// <summary>Returns the stored canonical plant ID, or null for an invalid or missing key.</summary>
    Task<string?> FindPlantAsync(
        DbTransaction transaction, string plantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored canonical worker ID only while the worker is active in the specified plant.
    /// Invalid or missing keys and inactive or transferred workers return null.
    /// </summary>
    Task<string?> FindActiveWorkerAsync(
        DbTransaction transaction, string workerId, string plantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored canonical product ID and master data, including its raw ValidState
    /// ("Valid" denotes active), or null for an invalid or missing key. Inactive products are returned.
    /// </summary>
    Task<ProductDto?> FindProductAsync(
        DbTransaction transaction, string productId, CancellationToken ct = default);

    /// <summary>
    /// Returns existing masters for at most 128 distinct, nonblank, trimmed product IDs of at most
    /// 50 characters. Empty input returns an empty list; missing keys are omitted and inactive
    /// products are included. IDs retain their stored canonical spelling. Invalid input throws
    /// ArgumentException. Results are detached; the caller retains its live Serializable transaction.
    /// </summary>
    Task<IReadOnlyList<ProductDto>> FindProductsAsync(
        DbTransaction transaction, IReadOnlyList<string> productIds, CancellationToken ct = default);
}
