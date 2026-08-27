using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.EMS.Application.SpareParts;

/// <summary>
/// 예비부품 정책·공급처·설비 BOM의 검증, 멱등성, 낙관적 버전과 보충 계산을 숨기는 deep module.
/// </summary>
public sealed class SparePartService
{
    private static readonly string[] Criticalities = ["Critical", "High", "Medium", "Low"];
    private readonly ISparePartManagementRepository _repository;
    private readonly IVendorDirectory _vendorDirectory;
    private readonly IEquipmentDirectory _equipmentDirectory;

    public SparePartService(
        ISparePartManagementRepository repository,
        IVendorDirectory vendorDirectory,
        IEquipmentDirectory equipmentDirectory)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _vendorDirectory = vendorDirectory ?? throw new ArgumentNullException(nameof(vendorDirectory));
        _equipmentDirectory = equipmentDirectory
                              ?? throw new ArgumentNullException(nameof(equipmentDirectory));
    }

    public async Task<Result<SparePartStockPolicyRecord>> SaveStockPolicyAsync(
        SparePartStockPolicyCommand command,
        CancellationToken ct = default)
    {
        var error = ValidatePolicy(command);
        if (error is not null) return Result.Failure<SparePartStockPolicyRecord>(error);

        var actor = command.ActorId!.Trim();
        var partId = command.PartId.Trim();
        var key = command.IdempotencyKey.Trim();
        var hash = Hash("StockPolicy", partId, command.SafetyStock, command.ReorderPoint,
            command.TargetStock, command.ReservedQuantity, command.AverageDailyUsage,
            command.ServiceLevel, command.ReviewCycleDays, command.IsActive,
            command.ExpectedVersion, actor);

        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<SparePartStockPolicyRecord>(replay, "StockPolicy", partId, hash);
        var legacyReplay = await _repository.GetStockPolicyByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, partId, hash);

        var existing = await _repository.GetStockPolicyAsync(partId, ct);
        var stateError = ValidateWriteState(existing, command.ExpectedVersion, "stock policy", partId);
        if (stateError is not null) return Result.Failure<SparePartStockPolicyRecord>(stateError);
        if (!await _repository.PartExistsAsync(partId, ct))
            return Result.Failure<SparePartStockPolicyRecord>(Missing("SparePart", partId));

        var now = DateTime.UtcNow;
        var record = new SparePartStockPolicyRecord(
            partId, command.SafetyStock, command.ReorderPoint, command.TargetStock,
            command.ReservedQuantity, command.AverageDailyUsage, command.ServiceLevel,
            command.ReviewCycleDays, command.IsActive, command.ExpectedVersion + 1,
            key, hash, existing?.CreatedBy ?? actor, existing?.CreatedAt ?? now, actor, now);
        var write = NewCommand("StockPolicy", partId, key, hash, command.ExpectedVersion, record, actor, now);
        var persisted = command.ExpectedVersion == 0
            ? await _repository.TryCreateStockPolicyAsync(record, write, ct)
            : await _repository.TryUpdateStockPolicyAsync(record, command.ExpectedVersion, write, ct);
        if (persisted) return record;

        return await ResolvePolicyRaceAsync(partId, key, hash, command.ExpectedVersion, ct);
    }

    public async Task<Result<SparePartSupplierRecord>> SaveSupplierAsync(
        SparePartSupplierCommand command,
        CancellationToken ct = default)
    {
        var error = ValidateSupplier(command);
        if (error is not null) return Result.Failure<SparePartSupplierRecord>(error);

        var id = command.PartSupplierId.Trim();
        var partId = command.PartId.Trim();
        var vendorId = command.VendorId.Trim();
        var actor = command.ActorId!.Trim();
        var key = command.IdempotencyKey.Trim();
        var vendorPartNumber = Text(command.VendorPartNumber);
        var currency = Text(command.Currency)?.ToUpperInvariant();
        var hash = Hash("Supplier", id, partId, vendorId, vendorPartNumber,
            command.LeadTimeDays, command.MinimumOrderQuantity, command.UnitPrice, currency,
            command.IsPrimary, command.IsActive, command.ExpectedVersion, actor);

        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<SparePartSupplierRecord>(replay, "Supplier", id, hash);
        var legacyReplay = await _repository.GetSupplierByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, id, hash);

        var existing = await _repository.GetSupplierAsync(id, ct);
        var stateError = ValidateWriteState(existing, command.ExpectedVersion, "supplier", id);
        if (stateError is not null) return Result.Failure<SparePartSupplierRecord>(stateError);
        if (!await _repository.PartExistsAsync(partId, ct))
            return Result.Failure<SparePartSupplierRecord>(Missing("SparePart", partId));
        if (!await _vendorDirectory.VendorExistsAsync(vendorId, ct))
            return Result.Failure<SparePartSupplierRecord>(Missing("Vendor", vendorId));
        if (command.IsPrimary && command.IsActive
            && await _repository.HasOtherActivePrimarySupplierAsync(partId, id, ct))
            return Result.Failure<SparePartSupplierRecord>(Error.Conflict(
                "EMS.SparePart.PrimarySupplierConflict",
                $"Part '{partId}' already has an active primary supplier."));

        var now = DateTime.UtcNow;
        var record = new SparePartSupplierRecord(
            id, partId, vendorId, vendorPartNumber, command.LeadTimeDays,
            command.MinimumOrderQuantity, command.UnitPrice, currency, command.IsPrimary,
            command.IsActive, command.ExpectedVersion + 1, key, hash,
            existing?.CreatedBy ?? actor, existing?.CreatedAt ?? now, actor, now);
        var write = NewCommand("Supplier", id, key, hash, command.ExpectedVersion, record, actor, now);
        var persisted = command.ExpectedVersion == 0
            ? await _repository.TryCreateSupplierAsync(record, write, ct)
            : await _repository.TryUpdateSupplierAsync(record, command.ExpectedVersion, write, ct);
        if (persisted) return record;

        return await ResolveSupplierRaceAsync(id, key, hash, command.ExpectedVersion, ct);
    }

    public async Task<Result<EquipmentPartBomRecord>> SaveEquipmentBomAsync(
        EquipmentPartBomCommand command,
        CancellationToken ct = default)
    {
        var error = ValidateBom(command);
        if (error is not null) return Result.Failure<EquipmentPartBomRecord>(error);

        var id = command.BomItemId.Trim();
        var partId = command.PartId.Trim();
        var equipmentId = Text(command.EquipmentId);
        var equipmentClassId = Text(command.EquipmentClassId);
        var criticality = CanonicalCriticality(command.Criticality);
        var positionCode = Text(command.PositionCode);
        var actor = command.ActorId!.Trim();
        var key = command.IdempotencyKey.Trim();
        var hash = Hash("EquipmentBom", id, partId, command.QuantityPer, equipmentId,
            equipmentClassId, criticality, command.ReplacementCycleDays,
            command.ReplacementCycleCount, positionCode, command.IsActive,
            command.ExpectedVersion, actor);

        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<EquipmentPartBomRecord>(replay, "EquipmentBom", id, hash);
        var legacyReplay = await _repository.GetEquipmentBomByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, id, hash);

        var existing = await _repository.GetEquipmentBomAsync(id, ct);
        var stateError = ValidateWriteState(existing, command.ExpectedVersion, "equipment BOM", id);
        if (stateError is not null) return Result.Failure<EquipmentPartBomRecord>(stateError);
        if (!await _repository.PartExistsAsync(partId, ct))
            return Result.Failure<EquipmentPartBomRecord>(Missing("SparePart", partId));
        if (equipmentId is not null
            && await _equipmentDirectory.GetEquipmentAsync(equipmentId, ct) is null)
            return Result.Failure<EquipmentPartBomRecord>(Missing("Equipment", equipmentId));
        if (equipmentClassId is not null
            && !await _equipmentDirectory.EquipmentClassExistsAsync(equipmentClassId, ct))
            return Result.Failure<EquipmentPartBomRecord>(Missing("EquipmentClass", equipmentClassId));

        var now = DateTime.UtcNow;
        var record = new EquipmentPartBomRecord(
            id, equipmentId, equipmentClassId, partId, command.QuantityPer, criticality,
            command.ReplacementCycleDays, command.ReplacementCycleCount, positionCode,
            command.IsActive, command.ExpectedVersion + 1, key, hash,
            existing?.CreatedBy ?? actor, existing?.CreatedAt ?? now, actor, now);
        var write = NewCommand("EquipmentBom", id, key, hash, command.ExpectedVersion, record, actor, now);
        var persisted = command.ExpectedVersion == 0
            ? await _repository.TryCreateEquipmentBomAsync(record, write, ct)
            : await _repository.TryUpdateEquipmentBomAsync(record, command.ExpectedVersion, write, ct);
        if (persisted) return record;

        return await ResolveBomRaceAsync(id, key, hash, command.ExpectedVersion, ct);
    }

    public async Task<Result<SparePartReplenishmentDto>> RecommendReplenishmentAsync(
        string partId,
        CancellationToken ct = default)
    {
        var normalized = Text(partId);
        if (normalized is null || normalized.Length > 50)
            return Result.Failure<SparePartReplenishmentDto>(
                Error.Validation(nameof(partId), "PartId is required and cannot exceed 50 characters."));

        var input = await _repository.GetReplenishmentInputAsync(normalized, ct);
        if (input is null)
            return Result.Failure<SparePartReplenishmentDto>(Missing("SparePartStockPolicy", normalized));
        if (!input.Policy.IsActive)
            return Result.Failure<SparePartReplenishmentDto>(Error.Conflict(
                "EMS.SparePart.InactiveStockPolicy", $"Part '{normalized}' has no active stock policy."));

        var supplier = input.Suppliers
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.LeadTimeDays)
            .ThenBy(x => x.UnitPrice ?? decimal.MaxValue)
            .ThenBy(x => x.PartSupplierId, StringComparer.Ordinal)
            .FirstOrDefault();
        var available = input.CurrentStock - input.Policy.ReservedQuantity;
        var leadTimeDemand = decimal.Round(
            input.Policy.AverageDailyUsage * (supplier?.LeadTimeDays ?? 0),
            4,
            MidpointRounding.AwayFromZero);
        var effectiveReorder = Math.Max(
            input.Policy.ReorderPoint,
            input.Policy.SafetyStock + leadTimeDemand);
        var effectiveTarget = Math.Max(input.Policy.TargetStock, effectiveReorder);
        var shouldOrder = available <= effectiveReorder;
        var recommended = shouldOrder ? Math.Max(0m, effectiveTarget - available) : 0m;
        if (recommended > 0m && supplier?.MinimumOrderQuantity is > 0m)
            recommended = Math.Max(recommended, supplier.MinimumOrderQuantity.Value);
        recommended = decimal.Round(recommended, 4, MidpointRounding.AwayFromZero);

        var reason = !shouldOrder
            ? "Available stock is above the effective reorder point."
            : supplier is null
                ? "Replenishment is required, but no active supplier is configured."
                : supplier.IsPrimary
                    ? "Replenishment is required; the active primary supplier was selected."
                    : "Replenishment is required; the fastest active supplier was selected.";
        return Result.Success(new SparePartReplenishmentDto(
            normalized, input.CurrentStock, input.Policy.ReservedQuantity, available,
            input.Policy.SafetyStock, input.Policy.ReorderPoint, leadTimeDemand,
            effectiveReorder, input.Policy.TargetStock, effectiveTarget, recommended,
            shouldOrder, supplier?.PartSupplierId, supplier?.VendorId, supplier?.LeadTimeDays,
            supplier?.MinimumOrderQuantity, reason));
    }

    private async Task<Result<SparePartStockPolicyRecord>> ResolvePolicyRaceAsync(
        string id, string key, string hash, int expectedVersion, CancellationToken ct)
    {
        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<SparePartStockPolicyRecord>(replay, "StockPolicy", id, hash);
        var legacyReplay = await _repository.GetStockPolicyByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, id, hash);
        var current = await _repository.GetStockPolicyAsync(id, ct);
        return Result.Failure<SparePartStockPolicyRecord>(RaceError(current is not null, expectedVersion, id));
    }

    private async Task<Result<SparePartSupplierRecord>> ResolveSupplierRaceAsync(
        string id, string key, string hash, int expectedVersion, CancellationToken ct)
    {
        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<SparePartSupplierRecord>(replay, "Supplier", id, hash);
        var legacyReplay = await _repository.GetSupplierByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, id, hash);
        var current = await _repository.GetSupplierAsync(id, ct);
        return Result.Failure<SparePartSupplierRecord>(RaceError(current is not null, expectedVersion, id));
    }

    private async Task<Result<EquipmentPartBomRecord>> ResolveBomRaceAsync(
        string id, string key, string hash, int expectedVersion, CancellationToken ct)
    {
        var replay = await _repository.GetCommandAsync(key, ct);
        if (replay is not null)
            return Replay<EquipmentPartBomRecord>(replay, "EquipmentBom", id, hash);
        var legacyReplay = await _repository.GetEquipmentBomByIdempotencyKeyAsync(key, ct);
        if (legacyReplay is not null) return ReplayLegacy(legacyReplay, id, hash);
        var current = await _repository.GetEquipmentBomAsync(id, ct);
        return Result.Failure<EquipmentPartBomRecord>(RaceError(current is not null, expectedVersion, id));
    }

    private static Result<T> Replay<T>(
        SparePartMasterCommandRecord command,
        string expectedType,
        string expectedId,
        string hash) where T : class
    {
        if (!string.Equals(command.EntityType, expectedType, StringComparison.Ordinal)
            || !string.Equals(command.EntityId, expectedId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.RequestHash, hash, StringComparison.Ordinal))
            return Result.Failure<T>(Error.Conflict(
                "EMS.SparePart.IdempotencyConflict",
                "The idempotency key was already used for different spare-part data."));
        var stored = JsonSerializer.Deserialize<T>(command.ResultJson);
        return stored is not null
            ? Result.Success(stored)
            : Result.Failure<T>(Error.Conflict(
                "EMS.SparePart.IdempotencyStateConflict",
                "The persisted spare-part command result is invalid."));
    }

    private static SparePartMasterCommandRecord NewCommand<T>(
        string entityType,
        string entityId,
        string key,
        string hash,
        int expectedVersion,
        T result,
        string actor,
        DateTime at) where T : class => new(
        $"SPC_{Guid.NewGuid():N}", entityType, entityId, key, hash, expectedVersion,
        expectedVersion + 1, JsonSerializer.Serialize(result), actor, at);

    private static Result<T> ReplayLegacy<T>(T stored, string expectedId, string hash)
        where T : class
    {
        var (id, storedHash) = stored switch
        {
            SparePartStockPolicyRecord x => (x.PartId, x.LastRequestHash),
            SparePartSupplierRecord x => (x.PartSupplierId, x.LastRequestHash),
            EquipmentPartBomRecord x => (x.BomItemId, x.LastRequestHash),
            _ => throw new InvalidOperationException("Unsupported spare-part replay record."),
        };
        return string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(storedHash, hash, StringComparison.Ordinal)
            ? Result.Success(stored)
            : Result.Failure<T>(Error.Conflict(
                "EMS.SparePart.IdempotencyConflict",
                "The idempotency key was already used for different spare-part data."));
    }

    private static Error? ValidateWriteState<T>(T? existing, int expectedVersion, string entity, string id)
        where T : class
    {
        var version = existing switch
        {
            SparePartStockPolicyRecord x => x.Version,
            SparePartSupplierRecord x => x.Version,
            EquipmentPartBomRecord x => x.Version,
            _ => 0,
        };
        if (expectedVersion == 0 && existing is not null)
            return Error.Conflict("EMS.SparePart.IdentityConflict", $"The {entity} '{id}' already exists.");
        if (expectedVersion > 0 && existing is null)
            return Missing(entity, id);
        if (existing is not null && version != expectedVersion)
            return Error.Conflict("EMS.SparePart.VersionConflict",
                $"The {entity} '{id}' is version {version}, not {expectedVersion}.");
        return null;
    }

    private static Error RaceError(bool identityExists, int expectedVersion, string id)
        => identityExists || expectedVersion > 0
            ? Error.Conflict("EMS.SparePart.VersionConflict",
                $"Spare-part data '{id}' changed concurrently; reload and retry.")
            : Error.Conflict("EMS.SparePart.IdempotencyConflict",
                "The idempotency key was already used by another spare-part command.");

    private static Error? ValidatePolicy(SparePartStockPolicyCommand? c)
    {
        if (c is null) return Error.Validation(nameof(c), "Command is required.");
        var common = ValidateCommon(c.PartId, c.ExpectedVersion, c.IdempotencyKey, c.ActorId);
        if (common is not null) return common;
        if (c.SafetyStock < 0m) return Error.Validation(nameof(c.SafetyStock), "SafetyStock cannot be negative.");
        if (c.ReorderPoint < 0m) return Error.Validation(nameof(c.ReorderPoint), "ReorderPoint cannot be negative.");
        if (c.TargetStock < c.SafetyStock || c.TargetStock < c.ReorderPoint)
            return Error.Validation(nameof(c.TargetStock), "TargetStock must cover SafetyStock and ReorderPoint.");
        if (c.ReservedQuantity < 0m) return Error.Validation(nameof(c.ReservedQuantity), "ReservedQuantity cannot be negative.");
        if (c.AverageDailyUsage < 0m) return Error.Validation(nameof(c.AverageDailyUsage), "AverageDailyUsage cannot be negative.");
        if (c.ServiceLevel is < 0m or > 1m) return Error.Validation(nameof(c.ServiceLevel), "ServiceLevel must be between 0 and 1.");
        if (c.ReviewCycleDays is <= 0) return Error.Validation(nameof(c.ReviewCycleDays), "ReviewCycleDays must be positive.");
        return null;
    }

    private static Error? ValidateSupplier(SparePartSupplierCommand? c)
    {
        if (c is null) return Error.Validation(nameof(c), "Command is required.");
        var common = ValidateCommon(c.PartSupplierId, c.ExpectedVersion, c.IdempotencyKey, c.ActorId);
        if (common is not null) return common;
        if (!ValidId(c.PartId)) return Error.Validation(nameof(c.PartId), "PartId is required and cannot exceed 50 characters.");
        if (!ValidId(c.VendorId)) return Error.Validation(nameof(c.VendorId), "VendorId is required and cannot exceed 50 characters.");
        if (Text(c.VendorPartNumber)?.Length > 100) return Error.Validation(nameof(c.VendorPartNumber), "VendorPartNumber cannot exceed 100 characters.");
        if (c.LeadTimeDays < 0) return Error.Validation(nameof(c.LeadTimeDays), "LeadTimeDays cannot be negative.");
        if (c.MinimumOrderQuantity is <= 0m) return Error.Validation(nameof(c.MinimumOrderQuantity), "MinimumOrderQuantity must be positive.");
        if (c.UnitPrice is < 0m) return Error.Validation(nameof(c.UnitPrice), "UnitPrice cannot be negative.");
        var currency = Text(c.Currency);
        if ((c.UnitPrice is null) != (currency is null))
            return Error.Validation(nameof(c.Currency), "UnitPrice and Currency must be supplied together.");
        if (currency?.Length > 10) return Error.Validation(nameof(c.Currency), "Currency cannot exceed 10 characters.");
        if (c.IsPrimary && !c.IsActive) return Error.Validation(nameof(c.IsPrimary), "A primary supplier must be active.");
        return null;
    }

    private static Error? ValidateBom(EquipmentPartBomCommand? c)
    {
        if (c is null) return Error.Validation(nameof(c), "Command is required.");
        var common = ValidateCommon(c.BomItemId, c.ExpectedVersion, c.IdempotencyKey, c.ActorId);
        if (common is not null) return common;
        if (!ValidId(c.PartId)) return Error.Validation(nameof(c.PartId), "PartId is required and cannot exceed 50 characters.");
        var equipment = Text(c.EquipmentId);
        var equipmentClass = Text(c.EquipmentClassId);
        if ((equipment is null) == (equipmentClass is null))
            return Error.Validation("EquipmentScope", "Exactly one of EquipmentId or EquipmentClassId is required.");
        if (equipment?.Length > 50) return Error.Validation(nameof(c.EquipmentId), "EquipmentId cannot exceed 50 characters.");
        if (equipmentClass?.Length > 50) return Error.Validation(nameof(c.EquipmentClassId), "EquipmentClassId cannot exceed 50 characters.");
        if (c.QuantityPer <= 0m) return Error.Validation(nameof(c.QuantityPer), "QuantityPer must be positive.");
        if (Text(c.Criticality) is not null && CanonicalCriticality(c.Criticality) is null)
            return Error.Validation(nameof(c.Criticality), "Criticality must be Critical, High, Medium, or Low.");
        if (c.ReplacementCycleDays is <= 0) return Error.Validation(nameof(c.ReplacementCycleDays), "ReplacementCycleDays must be positive.");
        if (c.ReplacementCycleCount is <= 0m) return Error.Validation(nameof(c.ReplacementCycleCount), "ReplacementCycleCount must be positive.");
        if (Text(c.PositionCode)?.Length > 100) return Error.Validation(nameof(c.PositionCode), "PositionCode cannot exceed 100 characters.");
        return null;
    }

    private static Error? ValidateCommon(string id, int expectedVersion, string key, string? actor)
    {
        if (!ValidId(id)) return Error.Validation(nameof(id), "Identifier is required and cannot exceed 50 characters.");
        if (expectedVersion < 0) return Error.Validation(nameof(expectedVersion), "ExpectedVersion cannot be negative.");
        var normalizedKey = Text(key);
        if (normalizedKey is null || normalizedKey.Length > 100)
            return Error.Validation(nameof(key), "IdempotencyKey is required and cannot exceed 100 characters.");
        var normalizedActor = Text(actor);
        if (normalizedActor is null || normalizedActor.Length > 50)
            return Error.Validation(nameof(actor), "An authenticated ActorId is required and cannot exceed 50 characters.");
        return null;
    }

    private static Error Missing(string entity, string id)
        => Error.NotFound("Error.NotFound", $"{entity} '{id}' was not found.");

    private static bool ValidId(string? value) => Text(value) is { Length: > 0 and <= 50 };
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? CanonicalCriticality(string? value)
    {
        var text = Text(value);
        return text is null ? null : Criticalities.FirstOrDefault(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(params object?[] values)
    {
        var canonical = new StringBuilder();
        foreach (var value in values)
        {
            var text = value switch
            {
                null => string.Empty,
                decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
                bool flag => flag ? "1" : "0",
                DateTime instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty,
            };
            canonical.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
