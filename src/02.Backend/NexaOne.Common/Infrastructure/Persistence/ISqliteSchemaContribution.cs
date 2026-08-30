using System.Data.Common;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// A module-owned, idempotent SQLite schema reconciliation step.
/// </summary>
/// <remarks>
/// The host owns the open connection and transaction. Implementations must not close the
/// connection, commit the transaction, or retain either object after <see cref="Apply"/> returns.
/// </remarks>
public interface ISqliteSchemaContribution
{
    /// <summary>A stable diagnostic identity, unique across all loaded modules.</summary>
    string Id { get; }

    /// <summary>Reconciles the module schema inside the host-owned transaction.</summary>
    void Apply(DbConnection connection, DbTransaction transaction);
}
