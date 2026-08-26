namespace NexaOne.QMS.Application.Qms;

/// <summary>QMS 플러그인 경계 내에서 외부 모듈의 참조 유효성을 확인한다.</summary>
public interface IQmsReferenceRepository
{
    /// <summary>로트가 생산 영역에 존재하는지 확인한다.</summary>
    Task<bool> LotExistsAsync(string lotId, CancellationToken ct = default);

    /// <summary>설비가 활성 상태로 존재하는지 확인한다.</summary>
    Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default);

    /// <summary>공정이 기준정보 영역에 존재하는지 확인한다.</summary>
    Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default);

    /// <summary>사용자가 활성·미삭제 상태로 존재하는지 확인한다.</summary>
    Task<bool> UserExistsAsync(string userId, CancellationToken ct = default);
}
