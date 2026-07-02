using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class LotRepository : QueryRepository, ILotRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public LotRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Lot?> GetByIdAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_LOT WHERE LOT_ID = @lotId";
        var row = await QueryFirstOrDefaultAsync<LotRow>(sql, new { lotId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Lot>> GetByPlantAsync(
        string plantId, string? state = null, CancellationToken ct = default)
    {
        var sql = "SELECT * FROM POM_LOT WHERE PLANT_ID = @plantId";
        if (!string.IsNullOrWhiteSpace(state))
            sql += " AND LOT_STATE = @state";
        sql += " ORDER BY CREATED_AT DESC";
        var rows = await QueryAsync<LotRow>(sql, new { plantId, state = state?.Trim() }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Lot>> GetByWorkOrderAsync(string workOrderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_LOT WHERE WORK_ORDER_ID = @workOrderId ORDER BY LOT_ID";
        var rows = await QueryAsync<LotRow>(sql, new { workOrderId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private const string InsertSql = @"INSERT INTO POM_LOT
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

    public async Task AddAsync(Lot lot, CancellationToken ct = default)
        => await _processor.InsertAsync(InsertSql, LotRow.FromDomain(lot), ct);

    /// <summary>DATA-3 원자화 — Mixing 전 문장(투입 소비 UPDATE·혼합관계/이력 INSERT·출력 INSERT/UPDATE·outbox)을
    /// 단일 ExecuteManyAsync 트랜잭션으로 커밋한다. 어느 문장이 실패해도 전체 롤백(부분 커밋 불가).
    /// ExecuteManyAsync는 raw(감사 미주입)라 UPDATE 감사 컬럼을 PersistWithOutboxAsync와 동일 규약으로 명시 채운다.</summary>
    public async Task MixingPersistAsync(MixingPersistPlan plan, CancellationToken ct = default)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;

        var statements = new List<(string Sql, object? Param)>();
        foreach (var lot in plan.ConsumedInputs)
            statements.Add((UpdateSql, UpdateParam(lot, user, now)));
        foreach (var relation in plan.Relations)
            statements.Add(LotMixingRelationRepository.InsertStatement(relation));
        statements.Add(plan.IsNewOutput
            ? (InsertSql, (object?)LotRow.FromDomain(plan.Output))
            : (UpdateSql, UpdateParam(plan.Output, user, now)));
        foreach (var history in plan.Histories)
            statements.Add(LotHistoryRepository.InsertStatement(history));

        if (_outboxEnabled)
        {
            var events = plan.ConsumedInputs.Append(plan.Output)
                .SelectMany(l => l.DomainEvents.OfType<IOutboxEvent>());
            statements.AddRange(OutboxStatements.For(events, user, now));
        }

        await _processor.ExecuteManyAsync(ct, statements.ToArray());

        foreach (var lot in plan.ConsumedInputs) lot.ClearDomainEvents();
        plan.Output.ClearDomainEvents();
    }

    private const string UpdateSql = @"UPDATE POM_LOT SET
            QTY = @Qty, DEFECT_QTY = @DefectQty,
            LOT_STATE = @LotState, PROCESS_STATE = @ProcessState, CURRENT_STEP = @CurrentStep,
            EQUIPMENT_ID = @EquipmentId, RECIPE_DEF_ID = @RecipeDefId, RECIPE_DEF_VERSION = @RecipeDefVersion,
            CARRIER_ID = @CarrierId, IS_HOLD = @IsHold,
            TRACK_IN_USER = @TrackInUser, TRACK_IN_TIME = @TrackInTime,
            TRACK_OUT_USER = @TrackOutUser, TRACK_OUT_TIME = @TrackOutTime,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE LOT_ID = @LotId";

    public async Task UpdateAsync(Lot lot, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, LotRow.FromDomain(lot), ct);
            return;
        }
        // ADR-002 활성: Lot UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        // 이벤트가 없는 전이(Hold/ReleaseHold/IncreaseMixingQty)는 데이터 행만 쓰고 outbox INSERT는 생기지 않는다(정상).
        await PersistWithOutboxAsync(lot, ct);
    }

    // Lot 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 Lot 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(Lot lot, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(lot, user, now)),
        };
        statements.AddRange(OutboxStatements.For(lot.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        lot.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(Lot lot, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(LotRow.FromDomain(lot));
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
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
        // 읽기경로 감사 메타데이터 복원용(SELECT * + MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Lot ToDomain() => Lot.Restore(
            LotId, PlantId, WorkOrderId, ProductId, Qty, DefectQty,
            Enum.Parse<LotState>(LotState), Enum.Parse<LotProcessState>(ProcessState),
            RouteSteps.Split(Lot.RouteSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CurrentStep, EquipmentId, RecipeDefId, RecipeDefVersion, CarrierId,
            IsHold == "Y", TrackInUser, TrackInTime, TrackOutUser, TrackOutTime,
            CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

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
