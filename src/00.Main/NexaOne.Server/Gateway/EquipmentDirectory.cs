using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>호스트 조립 루트(Default ALC) 어댑터 — EST 플러그인의 교차 모듈 설비 디렉터리 포트
/// (IEquipmentDirectory)를 구현한다. 호스트는 모든 모듈 스키마를 정당하게 알 수 있으므로 MDM_EQUIPMENT를
/// 공유 데이터소스(EesDataSource) 직접 read로 조회하고, ADR-006에 따라 EST 플러그인은 MDM 스키마를
/// 보유하지 않는다(포트만 ServiceContracts에 공유, 구현은 호스트 소유). plantId로 소속 설비 ID를 돌려줘
/// 플러그인이 자기 테이블(EST_EQUIPMENT_ALARM)을 그 ID들로 필터링하게 한다. 컬럼은 V002__MDM_EQUIPMENT와 1:1 정합.</summary>
public sealed class EquipmentDirectory : QueryRepository, IEquipmentDirectory
{
    public EquipmentDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = "SELECT EQUIPMENT_ID FROM MDM_EQUIPMENT WHERE PLANT_ID = @plantId";
        return await QueryAsync<string>(sql, new { plantId }, ct);
    }
}
