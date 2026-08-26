using System.Text;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.POM.Infrastructure;

public sealed class LotHistoryRepository : QueryRepository, ILotHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public LotHistoryRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    // LOT_HISTORY_ID는 IDENTITY, CREATED_AT은 DB DEFAULT에 위임
    private const string InsertSql = @"INSERT INTO POM_LOT_HISTORY
            (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, RECIPE_DEF_ID, RECIPE_DEF_VERSION,
             TRACK_IN_TIME, TRACK_OUT_TIME, EXECUTION_ID, EXECUTION_USER,
             QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE, REASON, IDEMPOTENCY_KEY)
            VALUES
            (@PlantId, @LotId, @EquipmentId, @ProcessId, @RecipeDefId, @RecipeDefVersion,
             @TrackInTime, @TrackOutTime, @ExecutionId, @ExecutionUser,
             @Qty, @DefectQty, @LotState, @ProcessState, @Reason, @IdempotencyKey)";

    public async Task AddAsync(LotHistory history, CancellationToken ct = default)
        => await _processor.InsertAsync(InsertSql, HistoryRow.FromDomain(history), ct);

    /// <summary>Mixing 원자화 배치(DATA-3)용 INSERT 문장 — LotRepository.MixingPersistAsync가 수집한다.</summary>
    internal static (string Sql, object? Param) InsertStatement(LotHistory history)
        => (InsertSql, HistoryRow.FromDomain(history));

    public async Task<IReadOnlyList<LotHistory>> GetByLotAsync(
        string plantId, string lotId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM POM_LOT_HISTORY
            WHERE PLANT_ID = @plantId AND LOT_ID = @lotId
            ORDER BY LOT_HISTORY_ID";
        var rows = await QueryAsync<HistoryRow>(sql, new { plantId, lotId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<LotHistory>> SearchAsync(
        string plantId, string? lotId, string? equipmentId, string? processId,
        DateTime? from, DateTime? to, int maxRows, CancellationToken ct = default)
    {
        // 설계 19.4.6 인덱스(LOT/EQP/PROC + TRACK_IN_TIME)를 타는 동적 필터 + 행수 상한.
        // baseSql엔 ORDER BY를 붙이지 않는다 — _dialect.WrapPaged가 정렬과 페이징(MSSQL OFFSET/FETCH,
        // SQLite LIMIT/OFFSET)을 방언별로 부착한다. maxRows는 정수 리터럴로 임베드되므로 파라미터에서 제외한다.
        var baseSql = new StringBuilder(
            "SELECT * FROM POM_LOT_HISTORY WHERE PLANT_ID = @plantId");
        if (!string.IsNullOrWhiteSpace(lotId)) baseSql.Append(" AND LOT_ID = @lotId");
        if (!string.IsNullOrWhiteSpace(equipmentId)) baseSql.Append(" AND EQUIPMENT_ID = @equipmentId");
        if (!string.IsNullOrWhiteSpace(processId)) baseSql.Append(" AND PROCESS_ID = @processId");
        if (from.HasValue) baseSql.Append(" AND TRACK_IN_TIME >= @from");
        if (to.HasValue) baseSql.Append(" AND TRACK_IN_TIME <= @to");

        var sql = _dialect.WrapPaged(baseSql.ToString(), "LOT_HISTORY_ID DESC", 0, maxRows);

        var rows = await QueryAsync<HistoryRow>(
            sql, new { plantId, lotId, equipmentId, processId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed class HistoryRow
    {
        public long LotHistoryId { get; set; }
        public string PlantId { get; set; } = "";
        public string LotId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string ProcessId { get; set; } = "";
        public string? RecipeDefId { get; set; }
        public int? RecipeDefVersion { get; set; }
        public DateTime? TrackInTime { get; set; }
        public DateTime? TrackOutTime { get; set; }
        public string ExecutionId { get; set; } = "";
        public string ExecutionUser { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal DefectQty { get; set; }
        public string LotState { get; set; } = "";
        public string ProcessState { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? Reason { get; set; }
        public string? IdempotencyKey { get; set; }

        public LotHistory ToDomain() => new(
            LotHistoryId, PlantId, LotId, EquipmentId, ProcessId, RecipeDefId, RecipeDefVersion,
            TrackInTime, TrackOutTime, ExecutionId, ExecutionUser,
            Qty, DefectQty, LotState, ProcessState, CreatedAt, Reason, IdempotencyKey);

        public static HistoryRow FromDomain(LotHistory h) => new()
        {
            LotHistoryId = h.LotHistoryId,
            PlantId = h.PlantId,
            LotId = h.LotId,
            EquipmentId = h.EquipmentId,
            ProcessId = h.ProcessId,
            RecipeDefId = h.RecipeDefId,
            RecipeDefVersion = h.RecipeDefVersion,
            TrackInTime = h.TrackInTime,
            TrackOutTime = h.TrackOutTime,
            ExecutionId = h.ExecutionId,
            ExecutionUser = h.ExecutionUser,
            Qty = h.Qty,
            DefectQty = h.DefectQty,
            LotState = h.LotState,
            ProcessState = h.ProcessState,
            CreatedAt = h.CreatedAt,
            Reason = h.Reason,
            IdempotencyKey = h.IdempotencyKey
        };
    }
}
