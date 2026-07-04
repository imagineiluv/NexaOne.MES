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

    public QmsService(
        IDefectRepository defectRepository,
        IDefectClassRepository defectClassRepository,
        IInspectionSpecRepository specRepository,
        IInspectionResultRepository resultRepository,
        ISpcParamRepository spcParamRepository)
    {
        _defectRepository = defectRepository;
        _defectClassRepository = defectClassRepository;
        _specRepository = specRepository;
        _resultRepository = resultRepository;
        _spcParamRepository = spcParamRepository;
    }

    // ── Defects ───────────────────────────────────────────────────────────────

    public async Task<Result<Defect>> RecordDefectAsync(
        string defectId, string lotId, string equipmentId, string defectClassId,
        int count, decimal rate, string inspectorId, CancellationToken ct = default)
    {
        var result = Defect.Create(defectId, lotId, equipmentId, defectClassId, count, rate, DateTime.UtcNow, inspectorId);
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
        var result = InspectionSpec.Create(specId, specName, processId, itemName, measureType,
            nominalValue, tolerancePlus, toleranceMinus);
        if (result.IsFailure) return result;
        await _specRepository.AddAsync(result.Value, ct);
        return result;
    }

    // ── Inspection Results ────────────────────────────────────────────────────

    public Task<IReadOnlyList<InspectionResult>> GetInspectionResultsByLotAsync(string lotId, CancellationToken ct = default)
        => _resultRepository.GetByLotAsync(lotId, ct);

    public async Task<Result<InspectionResult>> RecordInspectionResultAsync(
        string resultId, string specId, string lotId, string equipmentId,
        string inspectorId, decimal? measuredValue, string? attributeResult,
        bool? isPass, string? remark, CancellationToken ct = default)
    {
        var spec = await _specRepository.GetByIdAsync(specId, ct);
        if (spec is null)
            return Result.Failure<InspectionResult>(Error.NotFoundOf(nameof(InspectionSpec), specId));

        var result = InspectionResult.Create(resultId, specId, lotId, equipmentId, DateTime.UtcNow,
            inspectorId, measuredValue, attributeResult, isPass,
            spec.NominalValue, spec.TolerancePlus, spec.ToleranceMinus, spec.MeasureType, remark);
        if (result.IsFailure) return result;
        await _resultRepository.AddAsync(result.Value, ct);
        return result;
    }

    // ── SPC Parameters ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<SpcParam>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default)
        => _spcParamRepository.GetByEquipmentAsync(equipmentId, ct);

    public async Task<Result<SpcParam>> CreateSpcParamAsync(
        string paramId, string paramName, string equipmentId, string processId,
        decimal mean, decimal ucl, decimal lcl, int sampleSize,
        decimal? usl, decimal? lsl, CancellationToken ct = default)
    {
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
}
