using NexaOne.ServiceContracts.Qms;

namespace NexaOne.QMS.Application.Qms;

/// <summary>QMS-owned immutable evidence required to decide a production quality gate.</summary>
public sealed class ProductionQualityGateEvidence
{
    public string SpecId { get; set; } = string.Empty;
    public string? ResultId { get; set; }
    public DateTime? InspectedAt { get; set; }
    public bool? IsPass { get; set; }
    public string? InspectionId { get; set; }
    public bool? IsConfirmed { get; set; }
    public string? HeaderResult { get; set; }
    public int IsV2 { get; set; }
    public int IsCancelled { get; set; }
    public int IsSuperseded { get; set; }
}

/// <summary>Reads the latest evidence for every active process specification.</summary>
public interface IProductionQualityGateEvidenceRepository
{
    Task<IReadOnlyList<ProductionQualityGateEvidence>> GetLatestAsync(
        string lotId,
        string processId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the QMS policy that turns immutable inspection evidence into the small completion decision
/// consumed by POM. Cancellation and successor history never revive older evidence.
/// </summary>
public sealed class ProductionQualityGateService : IProductionQualityGateway
{
    private readonly IProductionQualityGateEvidenceRepository _repository;

    public ProductionQualityGateService(IProductionQualityGateEvidenceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<ProductionQualityGateResult> EvaluateAsync(
        string lotId,
        string processId,
        string? workOrderId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        _ = workOrderId;

        var latest = await _repository.GetLatestAsync(lotId.Trim(), processId.Trim(), ct);
        if (latest.Count == 0)
            return ProductionQualityGateResult.NotRequired();

        var passed = latest.Count(IsConfirmedPass);
        var failed = latest.FirstOrDefault(IsConfirmedFailure);
        if (failed is not null)
            return ProductionQualityGateResult.Failed(latest.Count, passed, failed.SpecId);

        var pending = latest.FirstOrDefault(row => !IsConfirmedPass(row));
        return pending is not null
            ? ProductionQualityGateResult.Pending(latest.Count, passed, pending.SpecId)
            : ProductionQualityGateResult.Passed(latest.Count);
    }

    private static bool IsConfirmedPass(ProductionQualityGateEvidence evidence) =>
        IsEffective(evidence)
        && evidence.ResultId is not null
        && evidence.InspectionId is not null
        && evidence.IsConfirmed == true
        && evidence.IsPass == true
        && (evidence.IsV2 == 1
            || string.Equals(evidence.HeaderResult, "Pass", StringComparison.OrdinalIgnoreCase));

    private static bool IsConfirmedFailure(ProductionQualityGateEvidence evidence) =>
        IsEffective(evidence)
        && evidence.ResultId is not null
        && evidence.InspectionId is not null
        && evidence.IsConfirmed == true
        && (evidence.IsPass == false
            || (evidence.IsV2 == 0
                && string.Equals(evidence.HeaderResult, "Fail", StringComparison.OrdinalIgnoreCase)));

    private static bool IsEffective(ProductionQualityGateEvidence evidence) =>
        evidence.IsCancelled == 0 && evidence.IsSuperseded == 0;
}
