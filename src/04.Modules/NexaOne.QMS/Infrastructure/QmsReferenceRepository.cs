using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;

namespace NexaOne.QMS.Infrastructure;

/// <summary>QMS가 참조하는 로트·공정의 존재와 설비·사용자의 활성 상태를 확인한다.</summary>
public sealed class QmsReferenceRepository : QueryRepository, IQmsReferenceRepository
{
    /// <summary>QMS 데이터 소스로 참조 검증 저장소를 생성한다.</summary>
    public QmsReferenceRepository(EesDataSource dataSource) : base(dataSource) { }

    /// <summary>
    /// 생산 LOT뿐 아니라 수입검사의 자재 LOT도 검사 후보로 인정합니다.
    /// QMS는 외부 모듈 엔티티를 참조만 하며, 변경은 각 소유 모듈을 통해 수행합니다.
    /// </summary>
    public Task<bool> LotExistsAsync(string lotId, CancellationToken ct = default)
        => ExistsAsync(@"SELECT
            (SELECT COUNT(*) FROM POM_LOT WHERE LOT_ID = @id) +
            (SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID = @id)", lotId, ct);

    /// <summary>설비의 활성 상태 존재 여부를 확인한다.</summary>
    public Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default)
        => ExistsAsync("SELECT COUNT(*) FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @id AND VALID_STATE = 'Valid'", equipmentId, ct);

    /// <summary>공정의 존재 여부를 확인한다.</summary>
    public Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default)
        => ExistsAsync("SELECT COUNT(*) FROM MDM_PROCESS WHERE PROCESS_ID = @id", processId, ct);

    /// <summary>사용자의 활성·미삭제 상태 존재 여부를 확인한다.</summary>
    public Task<bool> UserExistsAsync(string userId, CancellationToken ct = default)
        => ExistsAsync("SELECT COUNT(*) FROM SYS_USER WHERE USER_ID = @id AND IS_ACTIVE = 1 AND IS_DELETED = 0", userId, ct);

    private async Task<bool> ExistsAsync(string sql, string id, CancellationToken ct)
        => await CountAsync(sql, new { id }, ct) > 0;
}
