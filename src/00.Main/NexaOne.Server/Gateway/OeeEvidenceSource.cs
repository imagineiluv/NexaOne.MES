using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>
/// EST의 OEE 증거 인터페이스를 MDM 계획 directory와 POM 생산 directory로 조합하는 호스트 adapter입니다.
/// 호스트는 물리 스키마를 읽지 않고 두 owner snapshot의 제품 단위만 결합합니다.
/// </summary>
public sealed class OeeEvidenceSource : IOeeEvidenceSource
{
    private readonly IOeePlanDirectory _planDirectory;
    private readonly IOeeProductionDirectory _productionDirectory;

    public OeeEvidenceSource(
        IOeePlanDirectory planDirectory,
        IOeeProductionDirectory productionDirectory)
    {
        _planDirectory = planDirectory ?? throw new ArgumentNullException(nameof(planDirectory));
        _productionDirectory = productionDirectory ?? throw new ArgumentNullException(nameof(productionDirectory));
    }

    public Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default)
        => _planDirectory.LoadPlanAsync(targetEquipmentIds, localDay, ct);

    public async Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        var production = await _productionDirectory.LoadProductionAsync(
            plantId,
            equipmentId,
            fromUtc,
            toUtc,
            ct);
        if (production.TrackOuts.Count == 0)
            return production;

        var units = await _planDirectory.LoadProductUnitsAsync(
            production.TrackOuts.Select(static trackOut => trackOut.ProductId).ToArray(),
            ct);
        var trackOuts = production.TrackOuts
            .Select(trackOut => trackOut with
            {
                QuantityUom = units.GetValueOrDefault(trackOut.ProductId, string.Empty),
            })
            .ToArray();
        var unitsByLot = trackOuts
            .Where(static trackOut => !string.IsNullOrWhiteSpace(trackOut.ProcessLotId))
            .GroupBy(static trackOut => trackOut.ProcessLotId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().QuantityUom,
                StringComparer.Ordinal);
        var lotOutputs = production.LotOutputs?
            .Select(output => output with
            {
                Unit = unitsByLot.GetValueOrDefault(output.ProcessLotId, string.Empty),
            })
            .ToArray();

        return production with { TrackOuts = trackOuts, LotOutputs = lotOutputs };
    }

    public Task<IReadOnlyList<OeePlantLocalDateDto>> LoadPlantLocalDatesAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime utcNow,
        CancellationToken ct = default)
        => _planDirectory.LoadPlantLocalDatesAsync(targetEquipmentIds, utcNow, ct);
}
