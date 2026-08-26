using NexaOne.Common;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public sealed class QmsService
{
    private readonly IDefectRepository _defectRepository;
    private readonly IDefectClassRepository _defectClassRepository;
    private readonly IInspectionSpecRepository _specRepository;
    private readonly IInspectionResultRepository _resultRepository;
    private readonly ISpcParamRepository _spcParamRepository;
    private readonly IQmsReferenceRepository _references;

    public QmsService(
        IDefectRepository defectRepository,
        IDefectClassRepository defectClassRepository,
        IInspectionSpecRepository specRepository,
        IInspectionResultRepository resultRepository,
        ISpcParamRepository spcParamRepository)
        : this(defectRepository, defectClassRepository, specRepository, resultRepository,
            spcParamRepository, AlwaysValidReferences.Instance)
    {
    }

    public QmsService(
        IDefectRepository defectRepository,
        IDefectClassRepository defectClassRepository,
        IInspectionSpecRepository specRepository,
        IInspectionResultRepository resultRepository,
        ISpcParamRepository spcParamRepository,
        IQmsReferenceRepository references)
    {
        _defectRepository = defectRepository;
        _defectClassRepository = defectClassRepository;
        _specRepository = specRepository;
        _resultRepository = resultRepository;
        _spcParamRepository = spcParamRepository;
        _references = references;
    }

    // ── Defects ───────────────────────────────────────────────────────────────

    public async Task<Result<Defect>> RecordDefectAsync(
        string defectId, string lotId, string equipmentId, string defectClassId,
        int count, decimal rate, string inspectorId, string? remark = null, CancellationToken ct = default)
    {
        var defectClass = await _defectClassRepository.GetByIdAsync(defectClassId, ct);
        if (defectClass is null || !defectClass.IsActive || defectClass.IsDeleted)
            return Result.Failure<Defect>(Error.NotFoundOf(nameof(DefectClass), defectClassId));
        if (!await _references.LotExistsAsync(lotId, ct))
            return Result.Failure<Defect>(Error.NotFoundOf("Lot", lotId));
        if (!await _references.EquipmentExistsAsync(equipmentId, ct))
            return Result.Failure<Defect>(Error.NotFoundOf("Equipment", equipmentId));
        if (!await _references.UserExistsAsync(inspectorId, ct))
            return Result.Failure<Defect>(Error.NotFoundOf("User", inspectorId));

        var result = Defect.Create(defectId, lotId, equipmentId, defectClassId, count, rate, DateTime.UtcNow, inspectorId, remark);
        if (result.IsFailure) return result;
        await _defectRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
    {
        var defect = await _defectRepository.GetByIdAsync(defectId, ct);
        if (defect is null)
            return Result.Failure(Error.NotFoundOf(nameof(Defect), defectId));
        var r = defect.Confirm(confirmerId);
        if (r.IsFailure) return r;
        await _defectRepository.UpdateAsync(defect, ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<Defect>>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<IReadOnlyList<Defect>>(Error.Validation(nameof(lotId), "Lot ID is required."));
        return Result.Success(await _defectRepository.GetByLotAsync(lotId, ct));
    }

    // ── Defect Classes ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<DefectClass>> GetDefectClassesAsync(CancellationToken ct = default)
        => _defectClassRepository.GetAllAsync(ct);

    public async Task<Result<DefectClass>> CreateDefectClassAsync(
        string defectClassId, string defectClassName, string description, string severity,
        CancellationToken ct = default)
    {
        var result = DefectClass.Create(defectClassId, defectClassName, description, severity);
        if (result.IsFailure) return result;
        await _defectClassRepository.AddAsync(result.Value, ct);
        return result;
    }

    // ── Inspection Specs ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<InspectionSpec>> GetInspectionSpecsAsync(string? processId = null, CancellationToken ct = default)
        => string.IsNullOrEmpty(processId)
            ? _specRepository.GetAllAsync(ct)
            : _specRepository.GetByProcessAsync(processId, ct);

    public async Task<Result<InspectionSpec>> CreateInspectionSpecAsync(
        string specId, string specName, string processId, string itemName, string measureType,
        decimal? nominalValue, decimal? tolerancePlus, decimal? toleranceMinus,
        CancellationToken ct = default)
    {
        if (!await _references.ProcessExistsAsync(processId, ct))
            return Result.Failure<InspectionSpec>(Error.NotFoundOf("Process", processId));
        var result = InspectionSpec.Create(specId, specName, processId, itemName, measureType,
            nominalValue, tolerancePlus, toleranceMinus);
        if (result.IsFailure) return result;
        await _specRepository.AddAsync(result.Value, ct);
        return result;
    }

    // ── Inspection Results ────────────────────────────────────────────────────

    public Task<IReadOnlyList<InspectionResult>> GetInspectionResultsByLotAsync(string lotId, CancellationToken ct = default)
        => _resultRepository.GetByLotAsync(lotId, ct);

    public Task<EffectiveLotInspectionStatus> GetEffectiveLotInspectionStatusAsync(
        string lotId, CancellationToken ct = default)
        => _resultRepository.GetEffectiveLotStatusAsync(lotId, ct);

    public async Task<Result<InspectionResult>> RecordInspectionResultAsync(
        string resultId, string specId, string lotId, string equipmentId,
        string inspectorId, decimal? measuredValue, string? attributeResult,
        bool? isPass, string? remark, CancellationToken ct = default)
        => await RecordInspectionExecutionCoreAsync(InspectionExecutionType.Process,
            resultId, specId, lotId, equipmentId, inspectorId,
            measuredValue, attributeResult, isPass, remark, ct);

    /// <summary>
    /// 수입·공정·출하 등록 화면에서 전달한 검사 유형을 검증한 뒤 공통 검사 도메인으로 저장합니다.
    /// 검사자와 검사시각은 각각 인증 사용자와 서버 UTC를 사용하므로 화면 값으로 위조할 수 없습니다.
    /// </summary>
    public async Task<Result<InspectionResult>> RecordInspectionExecutionAsync(
        string inspectionType, string resultId, string specId, string lotId, string equipmentId,
        string inspectorId, decimal? measuredValue, string? attributeResult,
        bool? isPass, string? remark, CancellationToken ct = default)
    {
        if (!Enum.TryParse<InspectionExecutionType>(inspectionType, true, out var parsedType)
            || !Enum.IsDefined(parsedType))
            return Result.Failure<InspectionResult>(Error.Validation(
                nameof(inspectionType), "Inspection type must be Incoming, Process, or Shipping."));

        return await RecordInspectionExecutionCoreAsync(parsedType,
            resultId, specId, lotId, equipmentId, inspectorId,
            measuredValue, attributeResult, isPass, remark, ct);
    }

    private async Task<Result<InspectionResult>> RecordInspectionExecutionCoreAsync(
        InspectionExecutionType inspectionType, string resultId, string specId,
        string lotId, string equipmentId, string inspectorId,
        decimal? measuredValue, string? attributeResult, bool? isPass,
        string? remark, CancellationToken ct)
    {
        var spec = await _specRepository.GetByIdAsync(specId, ct);
        if (spec is null)
            return Result.Failure<InspectionResult>(Error.NotFoundOf(nameof(InspectionSpec), specId));
        if (!spec.IsActive)
            return Result.Failure<InspectionResult>(Error.Conflict("Inspection spec is inactive."));
        if (!await _references.LotExistsAsync(lotId, ct))
            return Result.Failure<InspectionResult>(Error.NotFoundOf("Lot", lotId));
        if (!await _references.EquipmentExistsAsync(equipmentId, ct))
            return Result.Failure<InspectionResult>(Error.NotFoundOf("Equipment", equipmentId));
        if (!await _references.UserExistsAsync(inspectorId, ct))
            return Result.Failure<InspectionResult>(Error.NotFoundOf("User", inspectorId));

        var result = InspectionResult.Create(resultId, specId, lotId, equipmentId, DateTime.UtcNow,
            inspectorId, measuredValue, attributeResult, isPass,
            spec.NominalValue, spec.TolerancePlus, spec.ToleranceMinus, spec.MeasureType, remark,
            inspectionType);
        if (result.IsFailure) return result;
        await _resultRepository.AddAsync(result.Value, ct);
        return result;
    }

    /// <summary>
    /// 여러 규격 결과를 하나의 서버 생성 검사 ID로 원자 확정합니다. 동일 멱등키와 동일 지문은
    /// 저장된 집계를 재생하고, 같은 키의 다른 지문은 409로 매핑되는 Conflict를 반환합니다.
    /// </summary>
    public async Task<Result<InspectionExecutionOutcome>> RecordInspectionExecutionV2Async(
        RecordInspectionExecutionCommand command,
        string inspectorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || command.IdempotencyKey.Trim().Length > 150)
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(command.IdempotencyKey),
                "An Idempotency-Key of at most 150 characters is required."));
        if (command.Items is null || command.Items.Count == 0)
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(command.Items), "At least one inspection item is required."));
        if (!Enum.IsDefined(command.InspectionType) || !Enum.IsDefined(command.RelationType))
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(command.InspectionType), "Inspection or relation type is invalid."));

        var key = command.IdempotencyKey.Trim();
        var requestHash = InspectionExecutionRequestHasher.Compute(command, inspectorId);
        var existing = await _resultRepository.GetExecutionByIdempotencyKeyAsync(key, ct);
        if (existing is not null)
            return ReplayOrConflict(existing, requestHash);

        if (!await _references.LotExistsAsync(command.LotId, ct))
            return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf("Lot", command.LotId));
        if (!await _references.EquipmentExistsAsync(command.EquipmentId, ct))
            return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf("Equipment", command.EquipmentId));
        if (!await _references.UserExistsAsync(inspectorId, ct))
            return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf("User", inspectorId));

        InspectionExecution? parent = null;
        if (command.RelationType == InspectionExecutionRelationType.Original)
        {
            if (!string.IsNullOrWhiteSpace(command.ParentInspectionId))
                return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                    nameof(command.ParentInspectionId), "An original inspection cannot have a parent."));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(command.ParentInspectionId))
                return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                    nameof(command.ParentInspectionId), "Correction and reinspection require a parent."));
            parent = await _resultRepository.GetExecutionAsync(command.ParentInspectionId.Trim(), ct);
            if (parent is null)
                return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                    nameof(InspectionExecution), command.ParentInspectionId));
            if (!string.Equals(parent.LotId, command.LotId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                    "A correction or reinspection must remain on the parent lot."));
            if (parent.InspectionType != command.InspectionType)
                return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                    "A correction or reinspection cannot change the inspection type."));
        }

        var inspectedAt = DateTime.UtcNow;
        SamplingPlanRevision? plan = null;
        var samplingAccepted = command.DefectQuantity == 0;
        if (string.IsNullOrWhiteSpace(command.SamplingPlanRevisionId))
        {
            if (command.SampleQuantity != command.LotQuantity)
                return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                    nameof(command.SampleQuantity),
                    "Without a sampling-plan revision the entire lot must be inspected."));
        }
        else
        {
            plan = await _resultRepository.GetSamplingPlanRevisionAsync(
                command.SamplingPlanRevisionId.Trim(), ct);
            if (plan is null)
                return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                    nameof(SamplingPlanRevision), command.SamplingPlanRevisionId));
            if (plan.EffectiveFrom.ToUniversalTime() > inspectedAt)
                return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                    nameof(command.SamplingPlanRevisionId),
                    "The sampling-plan revision is not effective at the inspection time."));
            var decision = SamplingPlanCalculator.Evaluate(
                plan, command.LotQuantity, command.SampleQuantity, command.DefectQuantity);
            if (decision.IsFailure)
                return Result.Failure<InspectionExecutionOutcome>(decision.Error);
            if (decision.Value.Disposition == SamplingDisposition.Inconclusive)
                return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                    nameof(command.SampleQuantity), "The required sampling quantity is not complete."));
            samplingAccepted = decision.Value.Disposition == SamplingDisposition.Accept;
        }

        var inspectionId = NewId("QMSI");
        var items = new List<InspectionResult>(command.Items.Count);
        foreach (var input in command.Items)
        {
            var spec = await _specRepository.GetByIdAsync(input.SpecId, ct);
            if (spec is null)
                return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                    nameof(InspectionSpec), input.SpecId));
            if (!spec.IsActive)
                return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                    $"Inspection spec '{input.SpecId}' is inactive."));

            var item = InspectionResult.Create(
                NewId("QMSR"),
                spec.Id,
                command.LotId,
                command.EquipmentId,
                inspectedAt,
                inspectorId,
                input.MeasuredValue,
                input.AttributeResult,
                null,
                spec.NominalValue,
                spec.TolerancePlus,
                spec.ToleranceMinus,
                spec.MeasureType,
                input.Remark,
                command.InspectionType,
                inspectionId,
                input.SampleQuantity,
                input.DefectQuantity);
            if (item.IsFailure)
                return Result.Failure<InspectionExecutionOutcome>(item.Error);
            items.Add(item.Value);
        }

        var rootInspectionId = parent?.RootInspectionId ?? inspectionId;
        var aggregate = InspectionExecution.Create(
            inspectionId,
            command.InspectionType,
            command.RelationType,
            rootInspectionId,
            parent?.InspectionId,
            command.LotId,
            command.EquipmentId,
            command.LotQuantity,
            command.SampleQuantity,
            command.DefectQuantity,
            key,
            requestHash,
            inspectedAt,
            inspectorId,
            items,
            plan is null ? null : InspectionSamplingPlanSnapshot.FromRevision(plan),
            samplingAccepted,
            command.Remark);
        if (aggregate.IsFailure)
            return Result.Failure<InspectionExecutionOutcome>(aggregate.Error);

        var confirmation = InspectionExecutionHistory.Create(
            NewId("QMSE"), inspectionId, InspectionExecutionEventType.Confirmed,
            key, requestHash, inspectorId, inspectedAt, rootInspectionId,
            parent?.InspectionId, reason: command.Remark);
        if (confirmation.IsFailure)
            return Result.Failure<InspectionExecutionOutcome>(confirmation.Error);

        InspectionExecutionHistory? relationEvent = null;
        if (parent is not null)
        {
            var eventType = command.RelationType == InspectionExecutionRelationType.Correction
                ? InspectionExecutionEventType.Corrected
                : InspectionExecutionEventType.Reinspected;
            var relation = InspectionExecutionHistory.Create(
                NewId("QMSE"), parent.InspectionId, eventType,
                key, requestHash, inspectorId, inspectedAt, rootInspectionId,
                parent.InspectionId, inspectionId, command.Remark);
            if (relation.IsFailure)
                return Result.Failure<InspectionExecutionOutcome>(relation.Error);
            relationEvent = relation.Value;
        }

        try
        {
            await _resultRepository.AddExecutionAsync(
                aggregate.Value, confirmation.Value, relationEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unique-key 경쟁에서 패한 요청은 DB 승자를 다시 읽어 동일 지문이면 정상 재생한다.
            var winner = await _resultRepository.GetExecutionByIdempotencyKeyAsync(key, ct);
            if (winner is not null) return ReplayOrConflict(winner, requestHash);
            throw;
        }

        var persisted = await _resultRepository.GetExecutionAsync(inspectionId, ct)
            ?? aggregate.Value;
        return Result.Success(new InspectionExecutionOutcome(persisted, false));
    }

    public async Task<Result<InspectionExecutionOutcome>> GetInspectionExecutionV2Async(
        string inspectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inspectionId))
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(inspectionId), "Inspection ID is required."));
        var execution = await _resultRepository.GetExecutionAsync(inspectionId.Trim(), ct);
        return execution is null
            ? Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                nameof(InspectionExecution), inspectionId))
            : Result.Success(new InspectionExecutionOutcome(execution, false));
    }

    /// <summary>확정 헤더/결과를 변경하지 않고 취소 사건만 append합니다.</summary>
    public async Task<Result<InspectionExecutionOutcome>> CancelInspectionExecutionV2Async(
        string inspectionId,
        string idempotencyKey,
        string reason,
        string actorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 150)
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(idempotencyKey), "An Idempotency-Key of at most 150 characters is required."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<InspectionExecutionOutcome>(Error.Validation(
                nameof(reason), "A cancellation reason is required."));
        if (!await _references.UserExistsAsync(actorId, ct))
            return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf("User", actorId));

        var key = idempotencyKey.Trim();
        var hash = InspectionExecutionRequestHasher.ComputeCancellation(inspectionId, reason, actorId);
        var replay = await _resultRepository.GetHistoryByIdempotencyKeyAsync(
            inspectionId, key, ct);
        if (replay is not null)
            return await ReplayCancellationAsync(inspectionId, replay, hash, ct);

        var execution = await _resultRepository.GetExecutionAsync(inspectionId, ct);
        if (execution is null)
            return Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                nameof(InspectionExecution), inspectionId));
        if (execution.IsCancelled)
            return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                "The inspection execution is already cancelled."));

        var history = InspectionExecutionHistory.Create(
            NewId("QMSE"), execution.InspectionId, InspectionExecutionEventType.Cancelled,
            key, hash, actorId, DateTime.UtcNow, execution.RootInspectionId,
            execution.ParentInspectionId, reason: reason);
        if (history.IsFailure)
            return Result.Failure<InspectionExecutionOutcome>(history.Error);

        try
        {
            await _resultRepository.AppendHistoryAsync(history.Value, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var winner = await _resultRepository.GetHistoryByIdempotencyKeyAsync(
                inspectionId, key, ct);
            if (winner is not null)
                return await ReplayCancellationAsync(inspectionId, winner, hash, ct);
            var cancellationWinner = await _resultRepository.GetCancellationHistoryAsync(
                inspectionId, ct);
            if (cancellationWinner is not null)
                return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                    "The inspection execution was concurrently cancelled by another request."));
            throw;
        }

        var updated = await _resultRepository.GetExecutionAsync(inspectionId, ct)
            ?? execution;
        return Result.Success(new InspectionExecutionOutcome(updated, false));
    }

    private async Task<Result<InspectionExecutionOutcome>> ReplayCancellationAsync(
        string inspectionId,
        InspectionExecutionHistory existing,
        string requestHash,
        CancellationToken ct)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                "The idempotency key was already used for a different cancellation request."));
        var execution = await _resultRepository.GetExecutionAsync(inspectionId, ct);
        return execution is null
            ? Result.Failure<InspectionExecutionOutcome>(Error.NotFoundOf(
                nameof(InspectionExecution), inspectionId))
            : Result.Success(new InspectionExecutionOutcome(execution, true));
    }

    private static Result<InspectionExecutionOutcome> ReplayOrConflict(
        InspectionExecution existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase)
            ? Result.Success(new InspectionExecutionOutcome(existing, true))
            : Result.Failure<InspectionExecutionOutcome>(Error.Conflict(
                "The idempotency key was already used for a different inspection request."));

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // ── SPC Parameters ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<SpcParam>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default)
        => _spcParamRepository.GetByEquipmentAsync(equipmentId, ct);

    public async Task<Result<SpcParam>> CreateSpcParamAsync(
        string paramId, string paramName, string equipmentId, string processId,
        decimal mean, decimal ucl, decimal lcl, int sampleSize,
        decimal? usl, decimal? lsl, CancellationToken ct = default)
    {
        if (!await _references.EquipmentExistsAsync(equipmentId, ct))
            return Result.Failure<SpcParam>(Error.NotFoundOf("Equipment", equipmentId));
        if (!await _references.ProcessExistsAsync(processId, ct))
            return Result.Failure<SpcParam>(Error.NotFoundOf("Process", processId));
        var result = SpcParam.Create(paramId, paramName, equipmentId, processId, mean, ucl, lcl, sampleSize, usl, lsl);
        if (result.IsFailure) return result;
        await _spcParamRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> UpdateSpcControlLimitsAsync(
        string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
    {
        var param = await _spcParamRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFoundOf(nameof(SpcParam), paramId));
        var r = param.UpdateControlLimits(mean, ucl, lcl);
        if (r.IsFailure) return r;
        await _spcParamRepository.UpdateAsync(param, ct);
        return Result.Success();
    }

    private sealed class AlwaysValidReferences : IQmsReferenceRepository
    {
        public static readonly AlwaysValidReferences Instance = new();
        public Task<bool> LotExistsAsync(string lotId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> UserExistsAsync(string userId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
