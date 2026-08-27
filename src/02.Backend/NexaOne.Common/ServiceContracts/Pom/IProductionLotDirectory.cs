using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// POM이 소유한 생산 LOT를 다른 모듈에 축소 snapshot으로 제공하는 directory 계약입니다.
/// 소비 모듈은 이 계약을 통해 LOT 존재와 제품만 확인하며 POM_LOT 물리 스키마를 조회하지 않습니다.
/// </summary>
[NexaModuleBridge("Pom", "productionLotDirectory")]
public interface IProductionLotDirectory : INexaModuleBridge
{
    /// <summary>생산 LOT가 존재하면 검사 대상 제품을 포함한 snapshot을 반환합니다.</summary>
    Task<ProductionLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default);
}

public sealed record ProductionLotDirectoryEntry(string LotId, string ProductId);
