using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>호스트 조립 루트(Default ALC) 어댑터 — POM 플러그인의 교차 모듈 마스터 검증 포트
/// (ITrackingMasterGateway, 설계 19.4.2)를 구현한다. 호스트는 모든 모듈 스키마를 정당하게 알 수 있으므로
/// MDM/RMS/QMS를 공유 데이터소스(EesDataSource) 직접 read로 검증하고, ADR-006에 따라 POM 플러그인은
/// 타 모듈 스키마를 보유하지 않는다(포트만 ServiceContracts에 공유, 구현은 호스트 소유). 도메인 검증
/// 로직(상태 전이/불변식)은 Lot/LotTrackingService에 있고, 여기서는 마스터 존재·상태 확인만 한다(읽기 전용).
/// 컬럼은 V002__MDM_EQUIPMENT / V005__RMS_RECIPE / V030__QMS_MASTER(QMS_DEFECT_CLASS)와 1:1 정합.</summary>
public sealed class TrackingMasterGateway : QueryRepository, ITrackingMasterGateway
{
    public TrackingMasterGateway(EesDataSource dataSource) : base(dataSource) { }

    public async Task<TrackingEquipmentInfo?> GetEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT EQUIPMENT_ID, PLANT_ID, EQUIPMENT_CLASS_ID, VALID_STATE
            FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @equipmentId";
        var row = await QueryFirstOrDefaultAsync<EquipmentRow>(sql, new { equipmentId }, ct);
        if (row is null) return null;
        // VALID_STATE 'Active'만 사용 가능(설계 19.4.4 가용 설비). 그 외는 IsValid=false로 전달해 서비스가 Conflict로 거부.
        return new TrackingEquipmentInfo(
            row.EQUIPMENT_ID, row.PLANT_ID, row.EQUIPMENT_CLASS_ID,
            string.Equals(row.VALID_STATE, "Active", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsUsableRecipeAsync(
        string recipeDefId, int? recipeDefVersion, string equipmentClassId, CancellationToken ct = default)
    {
        // Released 상태이고 설비 클래스가 일치해야 한다. 버전이 주어지면 현재 Recipe 버전과 일치해야 한다(설계 19.4.4).
        const string sql = @"SELECT COUNT(*) FROM RMS_RECIPE
            WHERE RECIPE_ID = @recipeDefId
              AND APPROVAL_STATE = 'Released'
              AND EQUIPMENT_CLASS_ID = @equipmentClassId
              AND (@recipeDefVersion IS NULL OR VERSION = @recipeDefVersion)";
        return await CountAsync(sql, new { recipeDefId, recipeDefVersion, equipmentClassId }, ct) > 0;
    }

    public async Task<bool> IsValidDefectCodeAsync(string defectCode, CancellationToken ct = default)
    {
        // QMS 결함 분류(활성·미삭제)에 존재해야 한다(설계 19.4.5 불량 코드 검증).
        const string sql = @"SELECT COUNT(*) FROM QMS_DEFECT_CLASS
            WHERE DEFECT_CLASS_ID = @defectCode AND IS_ACTIVE = 1 AND IS_DELETED = 0";
        return await CountAsync(sql, new { defectCode }, ct) > 0;
    }

    // SELECT 컬럼명과 1:1(대문자 그대로) — Dapper 기본 매핑. 도메인/DTO로 노출되지 않는 내부 행 타입.
    private sealed class EquipmentRow
    {
        public string EQUIPMENT_ID { get; set; } = "";
        public string PLANT_ID { get; set; } = "";
        public string EQUIPMENT_CLASS_ID { get; set; } = "";
        public string VALID_STATE { get; set; } = "";
    }
}
