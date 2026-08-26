using NexaOne.QMS.Application.Qms;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.UnitTests.Qms;

public sealed class ProductionQualityGateServiceTests
{
    [Fact]
    public async Task No_active_specification_is_not_required()
    {
        var result = await Service().EvaluateAsync("LOT-1", "PROC-1", null);
        result.Should().Be(ProductionQualityGateResult.NotRequired());
    }

    [Fact]
    public async Task Every_confirmed_v2_pass_allows_completion()
    {
        var result = await Service(Pass("A"), Pass("B"))
            .EvaluateAsync("LOT-1", "PROC-1", null);
        result.Should().Be(ProductionQualityGateResult.Passed(2));
    }

    [Fact]
    public async Task Confirmed_failure_takes_precedence_over_pending_evidence()
    {
        var result = await Service(Pending("A"), Failure("B"))
            .EvaluateAsync("LOT-1", "PROC-1", null);
        result.Should().Be(ProductionQualityGateResult.Failed(2, 0, "B"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Cancelled_or_superseded_latest_evidence_stays_pending(
        bool cancelled,
        bool superseded)
    {
        var evidence = Pass("A");
        evidence.IsCancelled = cancelled ? 1 : 0;
        evidence.IsSuperseded = superseded ? 1 : 0;

        var result = await Service(evidence).EvaluateAsync("LOT-1", "PROC-1", null);
        result.Should().Be(ProductionQualityGateResult.Pending(1, 0, "A"));
    }

    [Fact]
    public async Task Confirmed_legacy_pass_requires_matching_header_result()
    {
        var evidence = Pass("A");
        evidence.IsV2 = 0;
        evidence.HeaderResult = "Pass";

        var result = await Service(evidence).EvaluateAsync("LOT-1", "PROC-1", null);
        result.Should().Be(ProductionQualityGateResult.Passed(1));
    }

    private static ProductionQualityGateService Service(
        params ProductionQualityGateEvidence[] evidence) =>
        new(new StubRepository(evidence));

    private static ProductionQualityGateEvidence Pass(string specId) => new()
    {
        SpecId = specId,
        ResultId = $"RESULT-{specId}",
        InspectionId = $"INSPECTION-{specId}",
        IsConfirmed = true,
        IsPass = true,
        IsV2 = 1,
        HeaderResult = "Pass",
    };

    private static ProductionQualityGateEvidence Failure(string specId)
    {
        var evidence = Pass(specId);
        evidence.IsPass = false;
        evidence.HeaderResult = "Fail";
        return evidence;
    }

    private static ProductionQualityGateEvidence Pending(string specId) => new() { SpecId = specId };

    private sealed class StubRepository(IReadOnlyList<ProductionQualityGateEvidence> evidence)
        : IProductionQualityGateEvidenceRepository
    {
        public Task<IReadOnlyList<ProductionQualityGateEvidence>> GetLatestAsync(
            string lotId, string processId, CancellationToken cancellationToken = default) =>
            Task.FromResult(evidence);
    }
}
