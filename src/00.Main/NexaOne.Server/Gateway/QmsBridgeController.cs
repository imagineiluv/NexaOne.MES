using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/qms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class QmsBridgeController : ControllerBase
{
    private readonly IQmsBridge _bridge;
    public QmsBridgeController(IQmsBridge bridge) => _bridge = bridge;

    private string? ActorId => User.CurrentUserId();

    [HttpGet("defects")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetDefects([FromQuery] string lotId, CancellationToken ct)
        => Ok(await _bridge.GetDefectsByLotAsync(lotId, ct));

    [HttpPost("defects")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> RecordDefect([FromBody] RecordDefectRequest req, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        return (await _bridge.RecordDefectAsync(req.Id, req.LotId, req.EquipmentId,
            req.DefectClassId, req.DefectCount, req.DefectRate, actor, req.Remark, ct)).ToActionResult();
    }

    [HttpPost("defects/{defectId}/confirm")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> ConfirmDefect(string defectId, [FromBody] ConfirmDefectRequest? _, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        return (await _bridge.ConfirmDefectAsync(defectId, actor, ct)).ToActionResult();
    }

    [HttpGet("defect-classes")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetDefectClasses(CancellationToken ct)
        => Ok(await _bridge.GetDefectClassesAsync(ct));

    [HttpPost("defect-classes")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> CreateDefectClass([FromBody] CreateDefectClassRequest req, CancellationToken ct)
        => (await _bridge.CreateDefectClassAsync(req.Id, req.DefectClassName,
            req.Description ?? string.Empty, req.Severity, ct)).ToActionResult();

    [HttpGet("inspection-specs")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetInspectionSpecs([FromQuery] string? processId, CancellationToken ct)
        => Ok(await _bridge.GetInspectionSpecsAsync(processId, ct));

    [HttpPost("inspection-specs")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> CreateInspectionSpec([FromBody] CreateInspectionSpecRequest req, CancellationToken ct)
        => (await _bridge.CreateInspectionSpecAsync(req.Id, req.SpecName, req.ProcessId,
            req.ItemName, req.MeasureType, req.NominalValue, req.TolerancePlus,
            req.ToleranceMinus, ct)).ToActionResult();

    [HttpGet("inspection-results")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetInspectionResults([FromQuery] string lotId, CancellationToken ct)
        => Ok(await _bridge.GetInspectionResultsByLotAsync(lotId, ct));

    [HttpPost("inspection-results")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> RecordInspectionResult([FromBody] RecordInspectionResultRequest req, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        var result = string.IsNullOrWhiteSpace(req.InspectionType)
            ? await _bridge.RecordInspectionResultAsync(req.Id, req.SpecId, req.LotId,
                req.EquipmentId, actor, req.MeasuredValue, req.AttributeResult,
                req.IsPass, req.Remark, ct)
            : await _bridge.RecordInspectionExecutionAsync(req.InspectionType, req.Id,
                req.SpecId, req.LotId, req.EquipmentId, actor, req.MeasuredValue,
                req.AttributeResult, req.IsPass, req.Remark, ct);
        return result.ToActionResult();
    }

    [HttpGet("lots/{lotId}/inspection-status")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetLotInspectionStatus(string lotId, CancellationToken ct)
        => Ok(await _bridge.GetLotInspectionStatusAsync(lotId, ct));

    /// <summary>
    /// 여러 검사 항목을 하나의 서버 생성 검사 ID로 확정합니다. 헤더의 Idempotency-Key가 본문보다
    /// 우선하며, 동일 요청 재시도는 기존 응답(200), 최초 확정은 201을 반환합니다.
    /// </summary>
    [HttpPost("~/api/v2/qms/inspection-executions")]
    [RequirePermission(Permissions.QmsManage)]
    [ProducesResponseType<InspectionExecutionV2Dto>(StatusCodes.Status201Created)]
    [ProducesResponseType<InspectionExecutionV2Dto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordInspectionExecutionV2(
        [FromBody] RecordInspectionExecutionV2Dto req, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdempotencyKey;
        var result = await _bridge.RecordInspectionExecutionV2Async(
            req with { IdempotencyKey = key }, actor, ct);
        return result.ToActionResult(dto => dto.IsReplay
            ? Ok(dto)
            : StatusCode(StatusCodes.Status201Created, dto));
    }

    [HttpGet("~/api/v2/qms/inspection-executions/{inspectionId}")]
    [RequirePermission(Permissions.QmsRead)]
    [ProducesResponseType<InspectionExecutionV2Dto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInspectionExecutionV2(
        string inspectionId, CancellationToken ct)
        => (await _bridge.GetInspectionExecutionV2Async(inspectionId, ct)).ToActionResult();

    [HttpPost("~/api/v2/qms/inspection-executions/{inspectionId}/cancel")]
    [RequirePermission(Permissions.QmsManage)]
    [ProducesResponseType<InspectionExecutionV2Dto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelInspectionExecutionV2(
        string inspectionId,
        [FromBody] CancelInspectionExecutionV2Dto req,
        CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdempotencyKey;
        return (await _bridge.CancelInspectionExecutionV2Async(
            inspectionId, key, req.Reason, actor, ct)).ToActionResult();
    }

    [HttpGet("spc-params")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetSpcParams([FromQuery] string equipmentId, CancellationToken ct)
        => Ok(await _bridge.GetSpcParamsAsync(equipmentId, ct));

    [HttpPost("spc-params")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> CreateSpcParam([FromBody] CreateSpcParamRequest req, CancellationToken ct)
        => (await _bridge.CreateSpcParamAsync(req.Id, req.ParamName, req.EquipmentId,
            req.ProcessId, req.Mean, req.Ucl, req.Lcl, req.SampleSize,
            req.Usl, req.Lsl, ct)).ToActionResult();

    [HttpPost("spc-params/{paramId}/control-limits")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> UpdateControlLimits(string paramId, [FromBody] UpdateControlLimitsRequest req, CancellationToken ct)
        => (await _bridge.UpdateControlLimitsAsync(paramId, req.Mean, req.Ucl, req.Lcl, ct)).ToActionResult();

    [HttpPost("spc/limit-revisions")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> AddSpcLimitRevision([FromBody] AddSpcLimitRevisionRequest req, CancellationToken ct)
    {
        if (ActorId is null) return Unauthorized();
        return (await _bridge.AddSpcLimitRevisionAsync(req.Id, req.ParamId, req.RevisionNo,
            req.ChartType, req.CenterLine, req.Ucl, req.Lcl, req.EffectiveFrom,
            req.Reason ?? string.Empty, ct)).ToActionResult();
    }

    [HttpPost("spc/subgroups/evaluate")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> EvaluateSpcSubgroup([FromBody] EvaluateSpcSubgroupRequest req, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdempotencyKey;
        return (await _bridge.EvaluateSpcSubgroupAsync(req.SubgroupId, key,
            req.LimitRevisionId, req.ObservedAt, req.Values, req.SourceType,
            actor, ct)).ToActionResult();
    }

    [HttpGet("spc/violations")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetSpcViolations(
        [FromQuery] string? paramId, [FromQuery] string? subgroupId, CancellationToken ct)
        => Ok(await _bridge.GetSpcViolationsAsync(paramId, subgroupId, ct));

    [HttpPost("sampling-plans/revisions")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> AddSamplingPlanRevision(
        [FromBody] AddSamplingPlanRevisionRequest req, CancellationToken ct)
    {
        if (ActorId is null) return Unauthorized();
        return (await _bridge.AddSamplingPlanRevisionAsync(req.Id, req.PlanId,
            req.RevisionNo, req.Mode, req.LotSizeMin, req.LotSizeMax, req.SampleSize,
            req.AcceptanceNumber, req.RejectionNumber, req.Aql, req.StandardName,
            req.StandardVersion, req.EffectiveFrom, ct)).ToActionResult();
    }

    [HttpGet("sampling-plans/select")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> SelectSamplingPlan(
        [FromQuery] int lotSize, [FromQuery] DateTime? effectiveAt, CancellationToken ct)
        => (await _bridge.SelectSamplingPlanAsync(lotSize, effectiveAt ?? DateTime.UtcNow, ct)).ToActionResult();

    [HttpPost("sampling-plans/evaluate")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> EvaluateSampling(
        [FromBody] EvaluateSamplingRequest req, CancellationToken ct)
        => (await _bridge.EvaluateSamplingAsync(req.LotSize, req.InspectedQuantity,
            req.DefectQuantity, req.EffectiveAt ?? DateTime.UtcNow, ct)).ToActionResult();

    [HttpPost("ai/models/versions")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> RegisterAiModelVersion(
        [FromBody] RegisterAiModelVersionRequest req, CancellationToken ct)
    {
        if (ActorId is null) return Unauthorized();
        return (await _bridge.RegisterAiModelVersionAsync(req.Id, req.ModelId,
            req.VersionNo, req.ArtifactUri, req.ArtifactSha256, req.ConfidenceThreshold,
            req.EffectiveFrom, ct)).ToActionResult();
    }

    [HttpPost("ai/inferences")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> RecordAiInference(
        [FromBody] RecordAiInferenceRequest req, CancellationToken ct)
    {
        if (ActorId is null) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdempotencyKey;
        return (await _bridge.RecordAiInferenceAsync(req.Id, key, req.ModelVersionId,
            req.InspectionId, req.ImageUri, req.ImageSha256, req.RawVerdict,
            req.Confidence, req.InferredAt, ct)).ToActionResult();
    }

    [HttpGet("ai/inferences/{inferenceId}")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetAiInference(string inferenceId, CancellationToken ct)
        => (await _bridge.GetAiInferenceAsync(inferenceId, ct)).ToActionResult();

    [HttpGet("ai/inferences/{inferenceId}/reviews")]
    [RequirePermission(Permissions.QmsRead)]
    public async Task<IActionResult> GetAiReviews(string inferenceId, CancellationToken ct)
        => Ok(await _bridge.GetAiReviewsAsync(inferenceId, ct));

    [HttpPost("ai/inferences/{inferenceId}/reviews")]
    [RequirePermission(Permissions.QmsManage)]
    public async Task<IActionResult> ReviewAiInference(
        string inferenceId, [FromBody] ReviewAiInferenceRequest req, CancellationToken ct)
    {
        if (ActorId is not { } actor) return Unauthorized();
        return (await _bridge.ReviewAiInferenceAsync(req.Id, inferenceId, actor,
            req.Verdict, req.Reason, req.ReviewedAt ?? DateTime.UtcNow, ct)).ToActionResult();
    }
}

public record RecordDefectRequest(
    string Id, string LotId, string EquipmentId, string DefectClassId,
    int DefectCount, decimal DefectRate, string? Remark);
public record ConfirmDefectRequest(string? ConfirmerId = null);
public record CreateDefectClassRequest(
    string Id, string DefectClassName, string? Description, string Severity);
public record CreateInspectionSpecRequest(
    string Id, string SpecName, string ProcessId, string ItemName, string MeasureType,
    decimal? NominalValue, decimal? TolerancePlus, decimal? ToleranceMinus);
public record RecordInspectionResultRequest(
    string Id, string SpecId, string LotId, string EquipmentId,
    decimal? MeasuredValue, string? AttributeResult, bool? IsPass, string? Remark,
    string? InspectionType = null);
public record CreateSpcParamRequest(
    string Id, string ParamName, string EquipmentId, string ProcessId,
    decimal Mean, decimal Ucl, decimal Lcl, int SampleSize, decimal? Usl, decimal? Lsl);
public record UpdateControlLimitsRequest(decimal Mean, decimal Ucl, decimal Lcl);
public record AddSpcLimitRevisionRequest(
    string Id, string ParamId, int RevisionNo, string ChartType, decimal CenterLine,
    decimal Ucl, decimal Lcl, DateTime EffectiveFrom, string? Reason);
public record EvaluateSpcSubgroupRequest(
    string SubgroupId, string IdempotencyKey, string LimitRevisionId,
    DateTime ObservedAt, IReadOnlyList<decimal> Values, string SourceType);
public record AddSamplingPlanRevisionRequest(
    string Id, string PlanId, int RevisionNo, string Mode, int LotSizeMin,
    int? LotSizeMax, int? SampleSize, int AcceptanceNumber, int RejectionNumber,
    decimal Aql, string StandardName, string StandardVersion, DateTime EffectiveFrom);
public record EvaluateSamplingRequest(
    int LotSize, int InspectedQuantity, int DefectQuantity, DateTime? EffectiveAt);
public record RegisterAiModelVersionRequest(
    string Id, string ModelId, int VersionNo, string ArtifactUri,
    string ArtifactSha256, decimal ConfidenceThreshold, DateTime EffectiveFrom);
public record RecordAiInferenceRequest(
    string Id, string IdempotencyKey, string ModelVersionId, string InspectionId,
    string ImageUri, string ImageSha256, string RawVerdict, decimal Confidence, DateTime InferredAt);
public record ReviewAiInferenceRequest(
    string Id, string Verdict, string Reason, DateTime? ReviewedAt);
