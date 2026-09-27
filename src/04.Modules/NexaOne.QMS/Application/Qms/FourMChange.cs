using System.Data.Common;

namespace NexaOne.QMS.Application.Qms;

public sealed record FourMChange(
    string ChangeId,
    string? ChangeNo,
    string ChangeType,
    string? EquipmentId,
    string? ProductId,
    DateTime ChangeDate,
    string Description,
    string ApprovalStatus,
    string RequestedBy);

public interface IFourMChangeRepository
{
    Task<FourMChange?> GetByIdAsync(string changeId, CancellationToken ct = default);
    Task InsertAsync(FourMChange change, DbTransaction transaction, CancellationToken ct);
    Task<bool> TryDecideAsync(
        string changeId, string status, string actorId, DateTime decidedAt,
        DbTransaction transaction, CancellationToken ct);
}
