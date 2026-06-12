using NexaOne.Infrastructure.Persistence;
using NexaOne.PPM.Application.Lots;
using NexaOne.PPM.Domain;

namespace NexaOne.PPM.Infrastructure;

public sealed class LotRepository : QueryRepository, ILotRepository
{
    private readonly ServiceObjectProcessor _processor;

    public LotRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<Lot?> GetByIdAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM PPM_LOT WITH(NOLOCK) WHERE LOT_ID = @lotId";
        var row = await QueryFirstOrDefaultAsync<LotRow>(sql, new { lotId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Lot>> GetByPlantAsync(
        string plantId, string? state = null, CancellationToken ct = default)
    {
        var sql = "SELECT * FROM PPM_LOT WITH(NOLOCK) WHERE PLANT_ID = @plantId";
        if (!string.IsNullOrWhiteSpace(state))
            sql += " AND LOT_STATE = @state";
        sql += " ORDER BY CREATED_AT DESC";
        var rows = await QueryAsync<LotRow>(sql, new { plantId, state = state?.Trim() }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Lot>> GetByWorkOrderAsync(string workOrderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM PPM_LOT WITH(NOLOCK) WHERE WORK_ORDER_ID = @workOrderId ORDER BY LOT_ID";
        var rows = await QueryAsync<LotRow>(sql, new { workOrderId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Lot lot, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO PPM_LOT
            (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY,
             LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP,
             EQUIPMENT_ID, RECIPE_DEF_ID, RECIPE_DEF_VERSION, CARRIER_ID, IS_HOLD,
             TRACK_IN_USER, TRACK_IN_TIME, TRACK_OUT_USER, TRACK_OUT_TIME,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@LotId, @PlantId, @WorkOrderId, @ProductId, @Qty, @DefectQty,
             @LotState, @ProcessState, @RouteSteps, @CurrentStep,
             @EquipmentId, @RecipeDefId, @RecipeDefVersion, @CarrierId, @IsHold,
             @TrackInUser, @TrackInTime, @TrackOutUser, @TrackOutTime,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, LotRow.FromDomain(lot), ct);
    }

    public async Task UpdateAsync(Lot lot, CancellationToken ct = default)
    {
        const string sql = @"UPDATE PPM_LOT SET
            QTY = @Qty, DEFECT_QTY = @DefectQty,
            LOT_STATE = @LotState, PROCESS_STATE = @ProcessState, CURRENT_STEP = @CurrentStep,
            EQUIPMENT_ID = @EquipmentId, RECIPE_DEF_ID = @RecipeDefId, RECIPE_DEF_VERSION = @RecipeDefVersion,
            CARRIER_ID = @CarrierId, IS_HOLD = @IsHold,
            TRACK_IN_USER = @TrackInUser, TRACK_IN_TIME = @TrackInTime,
            TRACK_OUT_USER = @TrackOutUser, TRACK_OUT_TIME = @TrackOutTime,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE LOT_ID = @LotId";
        await _processor.UpdateAsync(sql, LotRow.FromDomain(lot), ct);
    }

    private sealed class LotRow
    {
        public string LotId { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string? WorkOrderId { get; set; }
        public string ProductId { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal DefectQty { get; set; }
        public string LotState { get; set; } = "Created";
        public string ProcessState { get; set; } = "Idle";
        public string RouteSteps { get; set; } = "";
        public int CurrentStep { get; set; }
        public string? EquipmentId { get; set; }
        public string? RecipeDefId { get; set; }
        public int? RecipeDefVersion { get; set; }
        public string? CarrierId { get; set; }
        public string IsHold { get; set; } = "N";
        public string? TrackInUser { get; set; }
        public DateTime? TrackInTime { get; set; }
        public string? TrackOutUser { get; set; }
        public DateTime? TrackOutTime { get; set; }

        public Lot ToDomain() => Lot.Restore(
            LotId, PlantId, WorkOrderId, ProductId, Qty, DefectQty,
            Enum.Parse<LotState>(LotState), Enum.Parse<LotProcessState>(ProcessState),
            RouteSteps.Split(Lot.RouteSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CurrentStep, EquipmentId, RecipeDefId, RecipeDefVersion, CarrierId,
            IsHold == "Y", TrackInUser, TrackInTime, TrackOutUser, TrackOutTime);

        public static LotRow FromDomain(Lot lot) => new()
        {
            LotId = lot.Id,
            PlantId = lot.PlantId,
            WorkOrderId = lot.WorkOrderId,
            ProductId = lot.ProductId,
            Qty = lot.Qty,
            DefectQty = lot.DefectQty,
            LotState = lot.State.ToString(),
            ProcessState = lot.ProcessState.ToString(),
            RouteSteps = string.Join(Lot.RouteSeparator, lot.RouteSteps),
            CurrentStep = lot.CurrentStepIndex,
            EquipmentId = lot.EquipmentId,
            RecipeDefId = lot.RecipeDefId,
            RecipeDefVersion = lot.RecipeDefVersion,
            CarrierId = lot.CarrierId,
            IsHold = lot.IsHold ? "Y" : "N",
            TrackInUser = lot.TrackInUser,
            TrackInTime = lot.TrackInTime,
            TrackOutUser = lot.TrackOutUser,
            TrackOutTime = lot.TrackOutTime
        };
    }
}
