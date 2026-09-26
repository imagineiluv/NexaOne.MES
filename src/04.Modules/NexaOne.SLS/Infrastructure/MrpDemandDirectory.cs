using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.SLS.Infrastructure;

/// <summary>
/// 확정·생산 중 수주의 미납 수량을 MRP 수요로 반환하는 읽기 전용 디렉터리입니다.
/// SLS 상태를 변경하지 않으며 docs/adr/0002의 임시 POM projection을 대체합니다.
/// </summary>
public sealed class MrpDemandDirectory : QueryRepository, IMrpDemandDirectory
{
    public MrpDemandDirectory(EesDataSource dataSource) : base(dataSource) { }

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
