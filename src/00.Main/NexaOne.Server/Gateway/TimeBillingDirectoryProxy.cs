using System.Data.Common;
using NexaOne.ServiceContracts.Hr;

namespace NexaOne.Server.Gateway;

public sealed class TimeBillingDirectoryProxy(ModuleBeanResolver resolver) : ITimeBillingDirectory
{
    private readonly ModuleBeanResolver _resolver = resolver
        ?? throw new ArgumentNullException(nameof(resolver));

    public Task<TimeBillingOccurrence?> GetTimeEntryInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, Guid timeEntryId, CancellationToken ct = default)
        => Resolve().GetTimeEntryInTransactionAsync(transaction, tenantId, organizationId,
            timeEntryId, ct);

    public Task<IReadOnlyList<TimeBillingOccurrence>> FindTimeEntriesInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId,
        IReadOnlyList<Guid> timeEntryIds, CancellationToken ct = default)
        => Resolve().FindTimeEntriesInTransactionAsync(transaction, tenantId, organizationId,
            timeEntryIds, ct);

    private ITimeBillingDirectory Resolve() =>
        _resolver.Resolve<ITimeBillingDirectory>("Hr", "timeBillingDirectory");
}
