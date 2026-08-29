using System.Security.Cryptography;
using System.Text;
using NexaOne.Common;

namespace NexaOne.POM.Application.Lots;

public sealed record LotDispositionCommand(
    string PlantId,
    string LotId,
    string? WorkOrderId,
    string? ProcessId,
    string? DefectExecutionId,
    string? DefectCode,
    string DispositionType,
    decimal Quantity,
    string? ReasonCode,
    string Reason,
    string ActorId,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId,
    string? SourceExecutionId);

public sealed record LotDispositionRecord(
    string DispositionId,
    string PlantId,
    string LotId,
    string? WorkOrderId,
    string? ProcessId,
    string? DefectExecutionId,
    string? DefectCode,
    string DispositionType,
    decimal Quantity,
    string? ReasonCode,
    string Reason,
    string DecidedBy,
    DateTime DecidedAt,
    string? SourceExecutionId,
    string IdempotencyKey,
    string RequestHash,
    string ClientChannel,
    string? DeviceId);

/// <summary>LOT/불량 증거 범위와 이미 처분된 수량을 함께 반환합니다.</summary>
public sealed record LotDispositionScope(
    string LotId,
    string PlantId,
    string? WorkOrderId,
    string? ProcessId,
    string? DefectExecutionId,
    string? DefectCode,
    decimal LotDefectQuantity,
    decimal LotDisposedQuantity,
    decimal EvidenceQuantity,
    decimal EvidenceDisposedQuantity)
{
    public decimal AvailableQuantity => Math.Max(0m,
        Math.Min(LotDefectQuantity - LotDisposedQuantity,
            EvidenceQuantity - EvidenceDisposedQuantity));
}

public interface ILotDispositionRepository
{
    Task<LotDispositionRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<LotDispositionScope?> GetScopeAsync(
        string plantId,
        string lotId,
        string? workOrderId,
        string? processId,
        string? defectExecutionId,
        string? defectCode,
        CancellationToken ct = default);

    Task<bool> TryAddAsync(LotDispositionRecord record, CancellationToken ct = default);
}

/// <summary>
/// 불량 관찰 증거를 업무 처분으로 확정합니다. 수량 할당과 멱등성은 저장소의 동일 트랜잭션 guard가
/// 최종 강제하고, 서비스는 사용자에게 구체적인 검증·충돌 결과를 제공합니다.
/// </summary>
public sealed class LotDispositionService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Scrap", "Rework", "Return", "UseAsIs", "Hold",
    };

    private readonly ILotDispositionRepository _repository;

    public LotDispositionService(ILotDispositionRepository repository) => _repository = repository;

    public async Task<Result<LotDispositionRecord>> RecordAsync(
        LotDispositionCommand command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure)
            return Result.Failure<LotDispositionRecord>(normalized.Error);
        var value = normalized.Value;
        var requestHash = ComputeRequestHash(value);

        var existing = await _repository.GetByIdempotencyKeyAsync(value.IdempotencyKey, ct);
        if (existing is not null)
            return Replay(existing, requestHash, value.IdempotencyKey);

        var scope = await _repository.GetScopeAsync(
            value.PlantId, value.LotId, value.WorkOrderId, value.ProcessId,
            value.DefectExecutionId, value.DefectCode, ct);
        if (scope is null)
            return Result.Failure<LotDispositionRecord>(Error.NotFound(
                "POM.LotDisposition.ScopeNotFound",
                "LOT, 작업지시 또는 불량 실행 증거가 요청 범위와 일치하지 않습니다."));
        if (value.Quantity > scope.AvailableQuantity)
            return Result.Failure<LotDispositionRecord>(Error.Conflict(
                "POM.LotDisposition.QuantityExceeded",
                $"처분 가능 불량 수량은 {scope.AvailableQuantity:0.####}입니다."));

        var record = new LotDispositionRecord(
            Guid.NewGuid().ToString("N"), scope.PlantId, scope.LotId,
            scope.WorkOrderId, scope.ProcessId ?? value.ProcessId,
            scope.DefectExecutionId, scope.DefectCode ?? value.DefectCode,
            value.DispositionType, value.Quantity, value.ReasonCode, value.Reason,
            value.ActorId, DateTime.UtcNow, value.SourceExecutionId,
            value.IdempotencyKey, requestHash, value.ClientChannel, value.DeviceId);

        var added = await _repository.TryAddAsync(record, ct);
        if (added) return Result.Success(record);

        existing = await _repository.GetByIdempotencyKeyAsync(value.IdempotencyKey, ct);
        if (existing is not null)
            return Replay(existing, requestHash, value.IdempotencyKey);

        return Result.Failure<LotDispositionRecord>(Error.Conflict(
            "POM.LotDisposition.ConcurrentAllocation",
            "다른 요청이 같은 불량 수량을 먼저 처분했습니다. 최신 처분 잔량을 조회한 뒤 다시 시도하십시오."));
    }

    private static Result<LotDispositionCommand> Normalize(LotDispositionCommand command)
    {
        var plantId = Required(command.PlantId);
        var lotId = Required(command.LotId);
        var actorId = Required(command.ActorId);
        var idempotencyKey = Required(command.IdempotencyKey);
        var type = CanonicalType(command.DispositionType);
        var channel = Required(command.ClientChannel).ToUpperInvariant();
        var reason = Required(command.Reason);
        var quantity = decimal.Round(command.Quantity, 4, MidpointRounding.AwayFromZero);

        if (plantId.Length is 0 or > 50 || lotId.Length is 0 or > 50)
            return Validation("PlantId/LotId", "PlantId와 LotId는 필수이며 50자를 초과할 수 없습니다.");
        if (actorId.Length is 0 or > 50)
            return Validation(nameof(command.ActorId), "로그인 작업자 식별자는 필수이며 50자를 초과할 수 없습니다.");
        if (idempotencyKey.Length is 0 or > 100)
            return Validation(nameof(command.IdempotencyKey), "IdempotencyKey는 필수이며 100자를 초과할 수 없습니다.");
        if (type is null)
            return Validation(nameof(command.DispositionType),
                "DispositionType은 Scrap, Rework, Return, UseAsIs 또는 Hold여야 합니다.");
        if (channel is not ("MES" or "MOBILE" or "POP"))
            return Validation(nameof(command.ClientChannel), "ClientChannel은 MES, MOBILE 또는 POP여야 합니다.");
        if (quantity < 0.0001m || quantity > 99_999_999_999_999.9999m)
            return Validation(nameof(command.Quantity), "Quantity는 DECIMAL(18,4) 범위의 양수여야 합니다.");
        if (reason.Length is 0 or > 500)
            return Validation(nameof(command.Reason), "처분 사유는 필수이며 500자를 초과할 수 없습니다.");

        var optional = new (string Name, string? Value, int Max)[]
        {
            (nameof(command.WorkOrderId), Trim(command.WorkOrderId), 50),
            (nameof(command.ProcessId), Trim(command.ProcessId), 50),
            (nameof(command.DefectExecutionId), Trim(command.DefectExecutionId), 50),
            (nameof(command.DefectCode), Trim(command.DefectCode), 50),
            (nameof(command.ReasonCode), Trim(command.ReasonCode), 50),
            (nameof(command.DeviceId), Trim(command.DeviceId), 100),
            (nameof(command.SourceExecutionId), Trim(command.SourceExecutionId), 50),
        };
        var tooLong = optional.FirstOrDefault(item => item.Value?.Length > item.Max);
        if (tooLong.Value is not null)
            return Validation(tooLong.Name, $"{tooLong.Name}은(는) {tooLong.Max}자를 초과할 수 없습니다.");
        if (optional[2].Value is not null && optional[3].Value is null)
            return Validation(nameof(command.DefectCode),
                "DefectExecutionId를 지정할 때 DefectCode도 함께 지정해야 합니다.");

        return Result.Success(command with
        {
            PlantId = plantId,
            LotId = lotId,
            WorkOrderId = optional[0].Value,
            ProcessId = optional[1].Value,
            DefectExecutionId = optional[2].Value,
            DefectCode = optional[3].Value,
            DispositionType = type,
            Quantity = quantity,
            ReasonCode = optional[4].Value,
            Reason = reason,
            ActorId = actorId,
            IdempotencyKey = idempotencyKey,
            ClientChannel = channel,
            DeviceId = optional[5].Value,
            SourceExecutionId = optional[6].Value,
        });
    }

    private static Result<LotDispositionRecord> Replay(
        LotDispositionRecord existing,
        string requestHash,
        string idempotencyKey) =>
        string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing)
            : Result.Failure<LotDispositionRecord>(Error.Conflict(
                "POM.LotDisposition.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}'는 다른 처분 요청에 이미 사용되었습니다."));

    private static string ComputeRequestHash(LotDispositionCommand command)
    {
        var fields = new[]
        {
            command.PlantId, command.LotId, command.WorkOrderId ?? "", command.ProcessId ?? "",
            command.DefectExecutionId ?? "", command.DefectCode ?? "", command.DispositionType,
            command.Quantity.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            command.ReasonCode ?? "", command.Reason, command.ActorId, command.IdempotencyKey,
            command.ClientChannel, command.DeviceId ?? "", command.SourceExecutionId ?? "",
        };
        var canonical = new StringBuilder();
        foreach (var field in fields)
        {
            canonical.Append(field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(field);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string? CanonicalType(string? value) =>
        AllowedTypes.FirstOrDefault(type => string.Equals(type, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Required(string? value) => value?.Trim() ?? string.Empty;
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Result<LotDispositionCommand> Validation(string code, string message) =>
        Result.Failure<LotDispositionCommand>(Error.Validation(code, message));
}
