using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class LotMixingRelationRepository : QueryRepository, ILotMixingRelationRepository
{
    private readonly ServiceObjectProcessor _processor;

    public LotMixingRelationRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    private const string InsertSql = @"INSERT INTO POM_LOT_MIXING_RELATION
            (PLANT_ID, OUTPUT_LOT_ID, INPUT_LOT_ID, INPUT_QTY, MIXING_RATE, CONSUMED_AT, CONSUMED_BY)
            VALUES
            (@PlantId, @OutputLotId, @InputLotId, @InputQty, @MixingRate, @ConsumedAt, @ConsumedBy)";

    public async Task AddAsync(LotMixingRelation relation, CancellationToken ct = default)
        => await _processor.InsertAsync(InsertSql, MixingRow.FromDomain(relation), ct);

    /// <summary>Mixing 원자화 배치(DATA-3)용 INSERT 문장 — LotRepository.MixingPersistAsync가 수집한다.</summary>
    internal static (string Sql, object? Param) InsertStatement(LotMixingRelation relation)
        => (InsertSql, MixingRow.FromDomain(relation));

    public async Task<IReadOnlyList<LotMixingRelation>> GetByOutputLotAsync(
        string plantId, string outputLotId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM POM_LOT_MIXING_RELATION
            WHERE PLANT_ID = @plantId AND OUTPUT_LOT_ID = @outputLotId
            ORDER BY INPUT_LOT_ID";
        var rows = await QueryAsync<MixingRow>(sql, new { plantId, outputLotId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed class MixingRow
    {
        public string PlantId { get; set; } = "";
        public string OutputLotId { get; set; } = "";
        public string InputLotId { get; set; } = "";
        public decimal InputQty { get; set; }
        public decimal? MixingRate { get; set; }
        public DateTime ConsumedAt { get; set; }
        public string ConsumedBy { get; set; } = "";

        public LotMixingRelation ToDomain() => new(
            PlantId, OutputLotId, InputLotId, InputQty, MixingRate, ConsumedAt, ConsumedBy);

        public static MixingRow FromDomain(LotMixingRelation r) => new()
        {
            PlantId = r.PlantId,
            OutputLotId = r.OutputLotId,
            InputLotId = r.InputLotId,
            InputQty = r.InputQty,
            MixingRate = r.MixingRate,
            ConsumedAt = r.ConsumedAt,
            ConsumedBy = r.ConsumedBy
        };
    }
}
