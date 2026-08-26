using NexaOne.Common;

namespace NexaOne.EMS.Application.Ems;

/// <summary>재고 조정과 같은 트랜잭션에 기록하는 예비부품 원장 행이다.</summary>
public sealed record SparePartStockTransaction(
    string InoutId,
    string PartId,
    string TransactionType,
    decimal Quantity,
    decimal BalanceBefore,
    decimal BalanceAfter,
    string ActorId,
    DateTime TransactionAt,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId = null,
    string? CorrelationId = null,
    string? WorkOrderId = null,
    string? EquipmentId = null,
    string? FromLocation = null,
    string? ToLocation = null,
    string? Remark = null,
    SparePartUsage? Usage = null)
{
    public decimal Delta => BalanceAfter - BalanceBefore;
}

/// <summary>
/// 설비에서 실제 소비된 예비부품 원장. 재고 입출고와 같은 트랜잭션에 기록하며 로그인 사용자를
/// <see cref="UsedBy"/>에 보존한다.
/// </summary>
public sealed record SparePartUsage(
    string UsageId,
    string InoutId,
    string PartId,
    string? BomItemId,
    string EquipmentId,
    string? WorkOrderId,
    decimal Quantity,
    string UsedBy,
    DateTime UsedAt,
    string? RemovalReason = null);

/// <summary>예비부품 조정의 감사·추적 입력. 로그인 사용자는 Command.ActorId에만 존재한다.</summary>
public sealed record SparePartAdjustmentContext(
    MaintenanceCommandContext Command,
    string? TransactionType = null,
    string? WorkOrderId = null,
    string? EquipmentId = null,
    string? FromLocation = null,
    string? ToLocation = null,
    string? Remark = null,
    string? BomItemId = null)
{
    public Result<string> ResolveTransactionType(decimal delta)
    {
        if (delta == 0)
            return Result.Failure<string>(
                Error.Validation(nameof(delta), "Stock adjustment must not be zero."));

        var type = string.IsNullOrWhiteSpace(TransactionType)
            ? delta > 0 ? "Incoming" : "Usage"
            : TransactionType.Trim();
        var allowed = delta > 0
            ? new[] { "Incoming", "Adjustment" }
            : new[] { "Usage", "Scrap", "Adjustment" };
        if (!allowed.Contains(type, StringComparer.OrdinalIgnoreCase))
            return Result.Failure<string>(Error.Validation(
                nameof(TransactionType),
                delta > 0
                    ? "Positive stock adjustment type must be Incoming or Adjustment."
                    : "Negative stock adjustment type must be Usage, Scrap, or Adjustment."));

        return Result.Success(allowed.First(x => string.Equals(x, type, StringComparison.OrdinalIgnoreCase)));
    }
}
