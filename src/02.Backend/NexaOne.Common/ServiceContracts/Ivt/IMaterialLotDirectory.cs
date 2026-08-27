using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>
/// IVT가 소유한 자재 LOT를 다른 모듈에 축소 snapshot으로 제공하는 directory 계약입니다.
/// 소비 모듈은 이 계약을 통해 LOT 존재와 자재만 확인하며 IVT_MATERIAL_LOT 물리 스키마를 조회하지 않습니다.
/// </summary>
[NexaModuleBridge("Ivt", "materialLotDirectory")]
public interface IMaterialLotDirectory : INexaModuleBridge
{
    /// <summary>자재 LOT가 존재하면 검사 대상 자재를 포함한 snapshot을 반환합니다.</summary>
    Task<MaterialLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default);
}

public sealed record MaterialLotDirectoryEntry(string LotId, string MaterialId);
