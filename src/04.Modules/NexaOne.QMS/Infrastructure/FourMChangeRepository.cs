using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;

namespace NexaOne.QMS.Infrastructure;

public sealed class FourMChangeRepository : QueryRepository, IFourMChangeRepository
{
    public FourMChangeRepository(EesDataSource dataSource) : base(dataSource) { }

    public async Task<FourMChange?> GetByIdAsync(string changeId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<Row>(
            "SELECT CHANGE_ID AS ChangeId, CHANGE_NO AS ChangeNo, CHANGE_TYPE AS ChangeType, " +
            "EQUIPMENT_ID AS EquipmentId, PRODUCT_ID AS ProductId, CHANGE_DATE AS ChangeDate, " +
            "DESCRIPTION AS Description, APPROVAL_STATUS AS ApprovalStatus, " +
            "REQUESTED_BY AS RequestedBy FROM QMS_4M_CHANGE WHERE CHANGE_ID = @changeId",
            new { changeId }, ct);
        return row?.ToDomain();
    }

    public async Task InsertAsync(FourMChange change, DbTransaction transaction, CancellationToken ct)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("Transaction is closed.");
        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO QMS_4M_CHANGE (CHANGE_ID, CHANGE_NO, CHANGE_TYPE, EQUIPMENT_ID, " +
            "PRODUCT_ID, CHANGE_DATE, DESCRIPTION, REQUESTED_BY, APPROVAL_STATUS, " +
            "CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) VALUES " +
            "(@ChangeId, @ChangeNo, @ChangeType, @EquipmentId, @ProductId, @ChangeDate, " +
            "@Description, @RequestedBy, 'Pending', @RequestedBy, @now, @RequestedBy, @now)",
            new { change.ChangeId, change.ChangeNo, change.ChangeType, change.EquipmentId,
                  change.ProductId, change.ChangeDate, change.Description, change.RequestedBy, now },
            transaction, cancellationToken: ct));
    }

    public async Task<bool> TryDecideAsync(
        string changeId, string status, string actorId, DateTime decidedAt,
        DbTransaction transaction, CancellationToken ct)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("Transaction is closed.");
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE QMS_4M_CHANGE SET APPROVAL_STATUS = @status, " +
            "APPROVED_BY = CASE WHEN @status = 'Approved' THEN @actorId ELSE NULL END, " +
            "APPROVED_AT = CASE WHEN @status = 'Approved' THEN @decidedAt ELSE NULL END, " +
            "UPDATED_BY = @actorId, UPDATED_AT = @decidedAt " +
            "WHERE CHANGE_ID = @changeId AND APPROVAL_STATUS = 'Pending'",
            new { changeId, status, actorId, decidedAt }, transaction, cancellationToken: ct));
        return changed == 1;
    }

    private sealed class Row
    {
        public string ChangeId { get; set; } = string.Empty;
        public string? ChangeNo { get; set; }
        public string ChangeType { get; set; } = string.Empty;
        public string? EquipmentId { get; set; }
        public string? ProductId { get; set; }
        public DateTime ChangeDate { get; set; }
        public string Description { get; set; } = string.Empty;
        public string ApprovalStatus { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;

        public FourMChange ToDomain() => new(
            ChangeId, ChangeNo, ChangeType, EquipmentId, ProductId, ChangeDate,
            Description, ApprovalStatus, RequestedBy);
    }
}
