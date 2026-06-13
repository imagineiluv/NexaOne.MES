using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentStateMatrixRepository : QueryRepository, IEquipmentStateMatrixRepository
{
    private readonly ServiceObjectProcessor _processor;

    public EquipmentStateMatrixRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<EquipmentStateMatrix>> GetByPlantAsync(
        string plantId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT PLANT_ID, FROM_STATE_ID, TO_STATE_ID, ALLOW_FLAG, SET_STATE_ID, REQUIRE_REASON, VALID_STATE
            FROM EST_STATE_MATRIX
            WHERE PLANT_ID = @plantId AND VALID_STATE = 'Valid'
            ORDER BY FROM_STATE_ID, TO_STATE_ID";
        var rows = await QueryAsync<MatrixRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<EquipmentStateMatrix?> FindAsync(
        string plantId, string fromState, string toState, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT PLANT_ID, FROM_STATE_ID, TO_STATE_ID, ALLOW_FLAG, SET_STATE_ID, REQUIRE_REASON, VALID_STATE
            FROM EST_STATE_MATRIX
            WHERE PLANT_ID = @plantId AND FROM_STATE_ID = @fromState AND TO_STATE_ID = @toState
              AND VALID_STATE = 'Valid'";
        var row = await QueryFirstOrDefaultAsync<MatrixRow>(sql, new { plantId, fromState, toState }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<EquipmentStateMatrix>> GetAllowedTransitionsAsync(
        string plantId, string fromState, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT PLANT_ID, FROM_STATE_ID, TO_STATE_ID, ALLOW_FLAG, SET_STATE_ID, REQUIRE_REASON, VALID_STATE
            FROM EST_STATE_MATRIX
            WHERE PLANT_ID = @plantId AND FROM_STATE_ID = @fromState
              AND ALLOW_FLAG = 'Y' AND VALID_STATE = 'Valid'
            ORDER BY TO_STATE_ID";
        var rows = await QueryAsync<MatrixRow>(sql, new { plantId, fromState }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(EquipmentStateMatrix matrix, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO EST_STATE_MATRIX
                (PLANT_ID, FROM_STATE_ID, TO_STATE_ID, ALLOW_FLAG, SET_STATE_ID, REQUIRE_REASON, VALID_STATE)
            VALUES
                (@PlantId, @FromStateId, @ToStateId, @AllowFlag, @SetStateId, @RequireReason, @ValidState)";
        await _processor.InsertAsync(sql, MatrixRow.FromDomain(matrix), ct);
    }

    public async Task UpdateAsync(EquipmentStateMatrix matrix, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE EST_STATE_MATRIX SET
                ALLOW_FLAG = @AllowFlag, SET_STATE_ID = @SetStateId,
                REQUIRE_REASON = @RequireReason, VALID_STATE = @ValidState
            WHERE PLANT_ID = @PlantId AND FROM_STATE_ID = @FromStateId AND TO_STATE_ID = @ToStateId";
        await _processor.UpdateAsync(sql, MatrixRow.FromDomain(matrix), ct);
    }

    private sealed class MatrixRow
    {
        public string PlantId { get; set; } = "";
        public string FromStateId { get; set; } = "";
        public string ToStateId { get; set; } = "";
        public string AllowFlag { get; set; } = "N";
        public string SetStateId { get; set; } = "";
        public string RequireReason { get; set; } = "N";
        public string ValidState { get; set; } = "Valid";

        public EquipmentStateMatrix ToDomain()
            => EquipmentStateMatrix.Create(
                PlantId, FromStateId, ToStateId,
                AllowFlag == "Y",
                SetStateId,
                RequireReason == "Y");

        public static MatrixRow FromDomain(EquipmentStateMatrix m) => new()
        {
            PlantId       = m.PlantId,
            FromStateId   = m.FromStateId,
            ToStateId     = m.ToStateId,
            AllowFlag     = m.AllowFlag     ? "Y" : "N",
            SetStateId    = m.SetStateId,
            RequireReason = m.RequireReason ? "Y" : "N",
            ValidState    = m.ValidState
        };
    }
}
