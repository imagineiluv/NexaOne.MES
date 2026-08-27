using NexaOne.POM.Domain.Mrp;

namespace NexaOne.POM.Application.Mrp;

/// <summary>POM MRP가 요구하는 독립 수요 입력 포트입니다.</summary>
public interface IMrpDemandSource
{
    Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default);
}
