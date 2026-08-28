using System.Globalization;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Infrastructure;

/// <summary>POM LOT TrackOut 원장을 OEE 생산 증거 snapshot으로 제공하는 owner adapter입니다.</summary>
public sealed class OeeProductionDirectory : QueryRepository, IOeeProductionDirectory
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public OeeProductionDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("Production window end must be after start.", nameof(toUtc));

        var rows = await QueryAsync<TrackOutRow>(
            @"SELECT h.LOT_HISTORY_ID, h.LOT_ID, l.PRODUCT_ID, h.PROCESS_ID, h.QTY, h.DEFECT_QTY,
                     h.TRACK_IN_TIME, h.TRACK_OUT_TIME, '' AS QUANTITY_UOM
              FROM POM_LOT_HISTORY h
              JOIN POM_LOT l ON l.PLANT_ID = h.PLANT_ID AND l.LOT_ID = h.LOT_ID
              WHERE h.EXECUTION_ID = 'TrackOut' AND h.PLANT_ID = @plant
                AND h.EQUIPMENT_ID = @equipment
                AND h.TRACK_OUT_TIME >= @from AND h.TRACK_OUT_TIME < @to",
            new
            {
                plant = plantId,
                equipment = equipmentId,
                from = Format(fromUtc),
                to = Format(toUtc),
            }, ct);
        var trackOuts = rows.Select(static row => new OeeTrackOutDto(
            row.ProductId,
            row.ProcessId,
            row.Qty,
            row.TrackInTime,
            row.TrackOutTime,
            row.QuantityUom,
            row.LotId)).ToArray();
        var lotOutputs = rows.Select(static row => new OeeLotOutputDto(
            row.LotHistoryId.ToString(CultureInfo.InvariantCulture),
            row.LotId,
            row.ProcessId,
            row.Qty,
            row.DefectQty,
            row.QuantityUom)).ToArray();

        return new OeeProductionWindowDto(
            rows.Count,
            rows.Sum(static row => row.Qty),
            rows.Sum(static row => row.DefectQty),
            trackOuts,
            lotOutputs);
    }

    private static string Format(DateTime value)
        => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private sealed class TrackOutRow
    {
        public long LotHistoryId { get; set; }
        public string LotId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string ProcessId { get; set; } = string.Empty;
        public decimal Qty { get; set; }
        public decimal DefectQty { get; set; }
        public DateTime? TrackInTime { get; set; }
        public DateTime TrackOutTime { get; set; }
        public string QuantityUom { get; set; } = string.Empty;
    }
}
