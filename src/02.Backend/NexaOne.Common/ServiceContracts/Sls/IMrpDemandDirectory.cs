namespace NexaOne.ServiceContracts.Sls;

/// <summary>SLS 소유 영역이 MRP에 공급하는 독립 수요 항목입니다.</summary>
public sealed record MrpDemand(
    string ItemId,
    decimal Qty,
    DateTime? DueDate,
    string SourceRef,
    string? PlantId = null);

/// <summary>
/// POM MRP가 SLS에 요구하는 독립 수요 입력 계약입니다. 확정·생산 중 수주의 미납 수량만 반환하며
/// 상태를 변경하지 않습니다(docs/adr/0002의 소유 모듈 인수 조건).
/// </summary>
public interface IMrpDemandDirectory : INexaModuleBridge
{
    Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default);
}
