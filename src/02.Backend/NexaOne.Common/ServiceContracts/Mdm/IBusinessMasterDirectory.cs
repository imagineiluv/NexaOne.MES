using System.Data.Common;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// MDM-owned plant and worker checks in the caller's live Serializable transaction.
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
}
