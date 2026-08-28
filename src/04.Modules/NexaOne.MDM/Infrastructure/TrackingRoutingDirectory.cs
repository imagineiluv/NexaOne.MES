using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.MDM.Infrastructure;

/// <summary>MDM 제품 라우팅을 LOT 실행용 축소 snapshot으로 제공하는 adapter입니다.</summary>
public sealed class TrackingRoutingDirectory : QueryRepository, ITrackingRoutingDirectory
{
    public TrackingRoutingDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<TrackingProductRouting?> GetProductRoutingAsync(
        string routingId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT R.ROUTING_ID, R.PRODUCT_ID, S.STEP_NO,
                   CASE WHEN P.PROCESS_ID IS NULL THEN NULL ELSE S.PROCESS_ID END AS PROCESS_ID
            FROM MDM_ROUTING R
            LEFT JOIN MDM_ROUTING_STEP S ON S.ROUTING_ID = R.ROUTING_ID
            LEFT JOIN MDM_PROCESS P ON P.PROCESS_ID = S.PROCESS_ID
            WHERE R.ROUTING_ID = @routingId
            ORDER BY S.STEP_NO";
        var rows = (await QueryAsync<RoutingRow>(sql, new { routingId }, ct)).ToList();
        if (rows.Count == 0) return null;

        return new TrackingProductRouting(
            rows[0].RoutingId,
            rows[0].ProductId,
            rows.Where(static row => row.StepNo.HasValue)
                .Select(static row => new TrackingRoutingStep(
                    row.StepNo!.Value,
                    row.ProcessId?.Trim() ?? string.Empty))
                .ToList());
    }

    private sealed class RoutingRow
    {
        public string RoutingId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public int? StepNo { get; set; }
        public string? ProcessId { get; set; }
    }
}
