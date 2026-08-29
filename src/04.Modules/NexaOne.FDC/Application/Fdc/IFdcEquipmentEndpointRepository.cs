using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcEquipmentEndpointRepository
{
    Task<FdcEquipmentEndpoint?> GetByIdAsync(string endpointId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcEquipmentEndpoint>> GetActiveByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    /// <summary>전체 활성 엔드포인트 — FDC worker가 직접 소유할 PLC 연결 목록을 로드하는 데 사용.</summary>
    Task<IReadOnlyList<FdcEquipmentEndpoint>> GetAllActiveAsync(CancellationToken ct = default);
    Task AddAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default);
    Task UpdateAsync(FdcEquipmentEndpoint endpoint, CancellationToken ct = default);
}
