using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcCollectDataRepository : QueryRepository, IFdcCollectDataRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcCollectDataRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(
        string parameterId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_COLLECT_DATA
            WHERE PARAMETER_ID = @parameterId
              AND COLLECTED_AT >= @from
              AND COLLECTED_AT <= @to
            ORDER BY COLLECTED_AT";
        var rows = await QueryAsync<DataRow>(sql, new { parameterId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcCollectData>().ToList();
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetLatestAsync(
        string parameterId, int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT TOP (@limit) * FROM FDC_COLLECT_DATA
            WHERE PARAMETER_ID = @parameterId
            ORDER BY COLLECTED_AT DESC";
        var rows = await QueryAsync<DataRow>(sql, new { parameterId, limit }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcCollectData>().Reverse().ToList();
    }

    public async Task AddAsync(FdcCollectData data, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_COLLECT_DATA
            (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY, LOWER_LIMIT, UPPER_LIMIT)
            VALUES
            (@CollectId, @EquipmentId, @ParameterId, @Value, @CollectedAt, @Quality, @LowerLimit, @UpperLimit)";
        await _processor.InsertAsync(sql, DataRow.FromDomain(data), ct);
    }

    public async Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default)
    {
        foreach (var item in data)
            await AddAsync(item, ct);
    }

    private sealed class DataRow
    {
        public string  CollectId   { get; set; } = "";
        public string  EquipmentId { get; set; } = "";
        public string  ParameterId { get; set; } = "";
        public decimal Value       { get; set; }
        public DateTime CollectedAt { get; set; }
        public string  Quality     { get; set; } = "Good";
        public decimal LowerLimit  { get; set; }
        public decimal UpperLimit  { get; set; }

        public FdcCollectData? ToDomain() =>
            FdcCollectData.Create(CollectId, EquipmentId, ParameterId, Value, CollectedAt, Quality, LowerLimit, UpperLimit)
                          .Value;

        public static DataRow FromDomain(FdcCollectData d) => new()
        {
            CollectId   = d.Id,
            EquipmentId = d.EquipmentId,
            ParameterId = d.ParameterId,
            Value       = d.Value,
            CollectedAt = d.CollectedAt,
            Quality     = d.Quality,
            LowerLimit  = d.LowerLimit,
            UpperLimit  = d.UpperLimit
        };
    }
}
