using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Domain.Mrp;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// SLS 업무 모듈 코드가 아직 없는 전환기의 단일 MRP 수요 projection입니다.
/// docs/adr/0002-temporary-sls-mrp-demand-projection.md의 제거 조건이 충족되면 SLS 소유 bridge로 교체합니다.
/// </summary>
public sealed class LegacySalesOrderMrpProjection : QueryRepository, IMrpDemandSource
{
    public LegacySalesOrderMrpProjection(EesDataSource dataSource) : base(dataSource) { }

    public async Task<IReadOnlyList<MrpDemand>> GetOpenDemandsAsync(CancellationToken ct = default)
    {
        var rows = await QueryAsync<DemandRow>(
            "SELECT SALES_ORDER_ID AS SalesOrderId, PRODUCT_ID AS ProductId, " +
            "(PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) AS OpenQuantity, PLAN_END_DATE AS DueDate, " +
            "PLANT_ID AS PlantId FROM SLS_SALES_ORDER " +
            "WHERE STATUS IN ('Confirmed', 'Producing') AND PRODUCT_ID IS NOT NULL " +
            "AND (PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) > 0",
            null,
            ct);
        return rows.Select(static row => new MrpDemand(
            row.ProductId,
            row.OpenQuantity,
            AsDate(row.DueDate),
            row.SalesOrderId,
            row.PlantId)).ToArray();
    }

    private static DateTime? AsDate(object? value) => value switch
    {
        null => null,
        DateTime date => date,
        string text when DateTime.TryParse(text, out var date) => date,
        _ => null,
    };

    private sealed class DemandRow
    {
        public string SalesOrderId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public decimal OpenQuantity { get; set; }
        public object? DueDate { get; set; }
        public string? PlantId { get; set; }
    }
}
