using NexaOne.QMS.Application.Qms;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.QMS.Infrastructure;

/// <summary>QMS가 참조하는 로트·공정의 존재와 설비·사용자의 활성 상태를 확인한다.</summary>
public sealed class QmsReferenceRepository : IQmsReferenceRepository
{
    private readonly IProductionLotDirectory _productionLots;
    private readonly IMaterialLotDirectory _materialLots;
    private readonly IEquipmentDirectory _equipment;
    private readonly IProcessDirectory _processes;
    private readonly IUserDirectory _users;

    /// <summary>각 마스터의 소유 모듈 directory로 QMS 참조 검증 adapter를 생성합니다.</summary>
    public QmsReferenceRepository(
        IProductionLotDirectory productionLots,
        IMaterialLotDirectory materialLots,
        IEquipmentDirectory equipment,
        IProcessDirectory processes,
        IUserDirectory users)
    {
        _productionLots = productionLots ?? throw new ArgumentNullException(nameof(productionLots));
        _materialLots = materialLots ?? throw new ArgumentNullException(nameof(materialLots));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _users = users ?? throw new ArgumentNullException(nameof(users));
    }

    /// <summary>
    /// 생산 LOT뿐 아니라 수입검사의 자재 LOT도 검사 후보로 인정합니다.
    /// QMS는 외부 모듈 엔티티를 참조만 하며, 변경은 각 소유 모듈을 통해 수행합니다.
    /// </summary>
    public async Task<bool> LotExistsAsync(string lotId, CancellationToken ct = default)
        => await _productionLots.GetLotAsync(lotId, ct) is not null
           || await _materialLots.GetLotAsync(lotId, ct) is not null;

    /// <summary>설비의 활성 상태 존재 여부를 확인한다.</summary>
    public async Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default)
        => (await _equipment.GetEquipmentAsync(equipmentId, ct))?.IsValid == true;

    /// <summary>공정의 존재 여부를 확인한다.</summary>
    public Task<bool> ProcessExistsAsync(string processId, CancellationToken ct = default)
        => _processes.ProcessExistsAsync(processId, ct);

    /// <summary>사용자의 활성·미삭제 상태 존재 여부를 확인한다.</summary>
    public Task<bool> UserExistsAsync(string userId, CancellationToken ct = default)
        => _users.IsActiveAsync(userId, ct);
}
