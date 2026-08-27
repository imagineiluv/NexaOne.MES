using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class LotRepository : QueryRepository, ILotRepository, IAtomicLotRepository, IRouteExceptionRepository
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
             LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, CONTROL_MODE, RETURN_STEP,
             EQUIPMENT_ID, RECIPE_DEF_ID, RECIPE_DEF_VERSION, CARRIER_ID, IS_HOLD,
             TRACK_IN_USER, TRACK_IN_TIME, TRACK_OUT_USER, TRACK_OUT_TIME, VERSION_NO,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@LotId, @PlantId, @WorkOrderId, @ProductId, @Qty, @DefectQty,
             @LotState, @ProcessState, @RouteSteps, @CurrentStep, @ControlMode, @ReturnStep,
             @EquipmentId, @RecipeDefId, @RecipeDefVersion, @CarrierId, @IsHold,
             @TrackInUser, @TrackInTime, @TrackOutUser, @TrackOutTime, @VersionNo,
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
        statements.Add(plan.IsNewOutput
            ? (InsertSql, (object?)LotRow.FromDomain(plan.Output))
            : (UpdateSql, UpdateParam(plan.Output, user, now)));
        foreach (var relation in plan.Relations)
            statements.Add(LotMixingRelationRepository.InsertStatement(relation));
        foreach (var history in plan.Histories)
            statements.Add(LotHistoryRepository.InsertStatement(history));

        if (_outboxEnabled)
        {
            var events = plan.ConsumedInputs.Append(plan.Output)
                .SelectMany(l => l.DomainEvents.OfType<IOutboxEvent>());
            statements.AddRange(OutboxStatements.For(events, user, now));
        }

        var persisted = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        if (!persisted)
            throw new System.Data.DBConcurrencyException("A mixing input lot was changed by another request.");

        foreach (var lot in plan.ConsumedInputs) lot.AcceptPersistedVersion();
        if (!plan.IsNewOutput) plan.Output.AcceptPersistedVersion();

        foreach (var lot in plan.ConsumedInputs) lot.ClearDomainEvents();
        plan.Output.ClearDomainEvents();
    }

    private const string UpdateSql = @"UPDATE POM_LOT SET
            QTY = @Qty, DEFECT_QTY = @DefectQty,
            LOT_STATE = @LotState, PROCESS_STATE = @ProcessState, ROUTE_STEPS = @RouteSteps,
            CURRENT_STEP = @CurrentStep, CONTROL_MODE = @ControlMode, RETURN_STEP = @ReturnStep,
            EQUIPMENT_ID = @EquipmentId, RECIPE_DEF_ID = @RecipeDefId, RECIPE_DEF_VERSION = @RecipeDefVersion,
            CARRIER_ID = @CarrierId, IS_HOLD = @IsHold,
            TRACK_IN_USER = @TrackInUser, TRACK_IN_TIME = @TrackInTime,
            TRACK_OUT_USER = @TrackOutUser, TRACK_OUT_TIME = @TrackOutTime,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt, VERSION_NO = VERSION_NO + 1
            WHERE LOT_ID = @LotId AND VERSION_NO = @VersionNo";

    public async Task UpdateAsync(Lot lot, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            var updated = await _processor.ExecuteGuardedManyAsync(ct, (UpdateSql, UpdateParam(lot, CurrentUserContext.UserId ?? "SYSTEM", DateTime.UtcNow)));
            if (updated) lot.AcceptPersistedVersion();
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
        var updated = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        if (!updated) return;
        lot.AcceptPersistedVersion();
        lot.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(Lot lot, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(LotRow.FromDomain(lot));
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);

        return p;
    }
    public async Task<LotExecutionRecord?> GetExecutionAsync(string idempotencyKey, CancellationToken ct = default)
    {
        const string sql = @"SELECT LOT_ID AS LotId, ACTION AS Action, IDEMPOTENCY_KEY AS IdempotencyKey,
            REQUEST_HASH AS RequestHash, EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion
            FROM POM_LOT_EXECUTION WHERE IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<LotExecutionRow>(sql, new { idempotencyKey }, ct);
        return row is null ? null : new LotExecutionRecord(
            row.LotId, row.Action, row.IdempotencyKey, row.RequestHash,
            checked((int)row.ExpectedVersion), checked((int)row.ResultVersion));
    }

    // SQLite materializes INTEGER as Int64 while SQL Server materializes INT as Int32. Reading
    // into Int64 first keeps the shared persistence path provider-neutral.
    private sealed class LotExecutionRow
    {
        public string LotId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public long ExpectedVersion { get; set; }
        public long ResultVersion { get; set; }
    }

    private const string InsertLotExecutionSql = @"INSERT INTO POM_LOT_EXECUTION
        (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
         EXPECTED_VERSION, RESULT_VERSION, ROUTE_EXCEPTION_ID, FROM_STEP, TO_STEP,
         FROM_PROCESS_ID, TO_PROCESS_ID, CONTROL_MODE, CLIENT_CHANNEL, DEVICE_ID, REASON,
         CREATED_BY, CREATED_AT)
        VALUES (@ExecutionId, @LotId, @Action, @IdempotencyKey, @RequestHash,
         @ExpectedVersion, @ResultVersion, @RouteExceptionId, @FromStep, @ToStep,
         @FromProcessId, @ToProcessId, @ControlMode, @ClientChannel, @DeviceId, @Reason,
         @CreatedBy, @CreatedAt)";

    private const string InsertLotDefectExecutionSql = @"INSERT INTO POM_LOT_DEFECT_EXECUTION
        (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
         EXECUTION_USER, CLIENT_CHANNEL, DEVICE_ID, OCCURRED_AT, CREATED_AT)
        VALUES (@ExecutionId, @LotId, @PlantId, @ProcessId, @DefectCode, @DefectQty,
         @ExecutionUser, @ClientChannel, @DeviceId, @OccurredAt, @CreatedAt)";

    private const string UpdateWorkOrderSql = @"UPDATE POM_WORK_ORDER SET
        START_QTY = @StartQty, COMPLETE_QTY = @CompleteQty, SCRAP_QTY = @ScrapQty,
        STATUS = @Status, IS_HOLD = @IsHold, STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
        UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt, VERSION_NO = VERSION_NO + 1
        WHERE WORK_ORDER_ID = @WorkOrderId AND VERSION_NO = @VersionNo";

    private const string InsertWorkOrderExecutionSql = @"INSERT INTO POM_WORK_ORDER_EXECUTION
        (EXECUTION_ID, WORK_ORDER_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
         GOOD_QTY, DEFECT_QTY, USER_ID, EQUIPMENT_ID, CLIENT_CHANNEL, DEVICE_ID,
         OCCURRED_AT, REMARK, EXPECTED_VERSION, RESULT_VERSION, CREATED_BY, CREATED_AT)
        VALUES (@ExecutionId, @WorkOrderId, @IdempotencyKey, @Action, @FromStatus, @ToStatus,
         @GoodQty, @DefectQty, @UserId, @EquipmentId, @ClientChannel, @DeviceId,
         @OccurredAt, @Remark, @ExpectedVersion, @ResultVersion, @UserId, @OccurredAt)";

    public async Task<LotTransitionPersistResult> PersistTransitionAsync(
        LotTransitionPersistPlan plan,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = string.IsNullOrWhiteSpace(plan.Histories.FirstOrDefault()?.ExecutionUser)
            ? CurrentUserContext.UserId ?? "SYSTEM"
            : plan.Histories[0].ExecutionUser;
        var executionId = plan.ExecutionId ?? Guid.NewGuid().ToString("N");
        var defectExecutions = plan.DefectExecutions ?? Array.Empty<LotDefectExecution>();
        ValidateDefectExecutions(plan, executionId, defectExecutions);
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(plan.Lot, user, now))
        };
        statements.AddRange(plan.Histories.Select(LotHistoryRepository.InsertStatement));

        if (plan.WorkOrder is not null || plan.WorkOrderExecution is not null)
        {
            if (plan.WorkOrder is null || plan.WorkOrderExecution is null)
                throw new InvalidOperationException("Work-order state and execution must be persisted together.");
            var workOrder = plan.WorkOrder;
            var execution = plan.WorkOrderExecution;
            statements.Add((UpdateWorkOrderSql, new
            {
                WorkOrderId = workOrder.Id,
                workOrder.StartQty,
                workOrder.CompleteQty,
                workOrder.ScrapQty,
                Status = workOrder.Status.ToString(),
                IsHold = workOrder.IsHold ? "Y" : "N",
                workOrder.StartedAt,
                workOrder.CompletedAt,
                UpdatedBy = user,
                UpdatedAt = now,
                workOrder.VersionNo
            }));
            statements.Add((InsertWorkOrderExecutionSql, new
            {
                execution.ExecutionId,
                execution.WorkOrderId,
                execution.IdempotencyKey,
                Action = execution.Action.ToString(),
                FromStatus = execution.FromStatus.ToString(),
                ToStatus = execution.ToStatus.ToString(),
                execution.GoodQty,
                execution.DefectQty,
                execution.UserId,
                execution.EquipmentId,
                execution.ClientChannel,
                execution.DeviceId,
                execution.OccurredAt,
                execution.Remark,
                execution.ExpectedVersion,
                execution.ResultVersion
            }));
        }

        statements.Add((InsertLotExecutionSql, new
        {
            ExecutionId = executionId,
            LotId = plan.Lot.Id,
            plan.Action,
            plan.IdempotencyKey,
            plan.RequestHash,
            plan.ExpectedVersion,
            ResultVersion = plan.ExpectedVersion + 1,
            RouteExceptionId = plan.RoutingAudit?.RouteExceptionId,
            FromStep = plan.RoutingAudit?.FromStepIndex,
            ToStep = plan.RoutingAudit?.ToStepIndex,
            FromProcessId = plan.RoutingAudit?.FromProcessId,
            ToProcessId = plan.RoutingAudit?.ToProcessId,
            ControlMode = (plan.RoutingAudit?.ControlMode ?? plan.Lot.ControlMode).ToString(),
            ClientChannel = plan.RoutingAudit?.ClientChannel,
            DeviceId = plan.RoutingAudit?.DeviceId,
            Reason = plan.RoutingAudit?.Reason,
            CreatedBy = user,
            CreatedAt = now
        }));

        // The execution header and every defect-code detail are intentionally appended in the
        // same guarded transaction. A detail failure must roll back the LOT and execution header.
        statements.AddRange(defectExecutions.Select(defect =>
            (InsertLotDefectExecutionSql, (object?)new
            {
                defect.ExecutionId,
                defect.LotId,
                defect.PlantId,
                defect.ProcessId,
                defect.DefectCode,
                defect.DefectQty,
                defect.ExecutionUser,
                defect.ClientChannel,
                defect.DeviceId,
                defect.OccurredAt,
                CreatedAt = now
            })));

        if (plan.RouteException is not null)
            statements.Add(RouteExceptionUpdateStatement(
                plan.RouteException, RouteExceptionStatus.Approved, now));

        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(plan.Lot.DomainEvents.OfType<IOutboxEvent>(), user, now));

        bool persisted;
        try
        {
            persisted = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        }
        catch (DBConcurrencyException)
        {
            // The first LOT guard returns false; later guards report the same optimistic race as
            // DBConcurrencyException. Normalize both repository-internal signals at this boundary.
            return LotTransitionPersistResult.Conflict;
        }
        catch (DbException exception) when (IsLotTransitionIdempotencyRace(exception))
        {
            // A concurrent request can pass the pre-read and then lose either append-only
            // idempotency constraint. Only those two known unique keys are a semantic conflict;
            // foreign-key, check, trigger and connectivity faults must still escape unchanged.
            return LotTransitionPersistResult.Conflict;
        }
        if (!persisted) return LotTransitionPersistResult.Conflict;

        plan.Lot.AcceptPersistedVersion();
        plan.WorkOrder?.AcceptPersistedVersion();
        plan.Lot.ClearDomainEvents();
        return LotTransitionPersistResult.Persisted;
    }

    private static bool IsLotTransitionIdempotencyRace(DbException exception)
    {
        var uniqueViolation = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!uniqueViolation) return false;

        return exception.Message.Contains(
                   "UX_POM_LOT_HISTORY_IDEMPOTENCY",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "POM_LOT_HISTORY.IDEMPOTENCY_KEY",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "UQ_POM_LOT_EXECUTION_KEY",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "POM_LOT_EXECUTION.IDEMPOTENCY_KEY",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateDefectExecutions(
        LotTransitionPersistPlan plan,
        string executionId,
        IReadOnlyList<LotDefectExecution> defects)
    {
        if (defects.Count == 0) return;
        if (!string.Equals(plan.Action, LotExecutionId.TrackOut, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Defect details can only be attached to TrackOut.");
        if (defects.Select(d => d.DefectCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != defects.Count)
            throw new InvalidOperationException("A defect code can occur only once per TrackOut execution.");

        foreach (var defect in defects)
        {
            if (!string.Equals(defect.ExecutionId, executionId, StringComparison.Ordinal) ||
                !string.Equals(defect.LotId, plan.Lot.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(defect.PlantId, plan.Lot.PlantId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Defect detail is not bound to its LOT execution.");
            if (string.IsNullOrWhiteSpace(defect.ProcessId) ||
                string.IsNullOrWhiteSpace(defect.DefectCode) ||
                string.IsNullOrWhiteSpace(defect.ExecutionUser))
                throw new InvalidOperationException("Defect process, code, and execution user are required.");
            if (defect.DefectQty <= 0)
                throw new InvalidOperationException("Defect detail quantity must be greater than zero.");
            if (defect.ClientChannel is not ("MES" or "MOBILE" or "POP"))
                throw new InvalidOperationException("Defect client channel must be MES, MOBILE, or POP.");
        }
    }

    private const string InsertRouteExceptionSql = @"INSERT INTO POM_ROUTE_EXCEPTION
        (EXCEPTION_ID, LOT_ID, PLANT_ID, DEVIATION_TYPE, FROM_STEP, TO_STEP,
         FROM_PROCESS_ID, TO_PROCESS_ID, BOUND_LOT_VERSION, REASON, STATUS,
         REQUESTED_BY, REQUESTED_AT, EXPIRES_AT, REVIEWED_BY, REVIEWED_AT, REVIEW_REASON,
         APPLIED_BY, APPLIED_AT, APPLIED_EXECUTION_ID, CLIENT_CHANNEL, DEVICE_ID,
         CREATED_AT, UPDATED_AT)
        VALUES
        (@ExceptionId, @LotId, @PlantId, @DeviationType, @FromStep, @ToStep,
         @FromProcessId, @ToProcessId, @BoundLotVersion, @Reason, @Status,
         @RequestedBy, @RequestedAt, @ExpiresAt, @ReviewedBy, @ReviewedAt, @ReviewReason,
         @AppliedBy, @AppliedAt, @AppliedExecutionId, @ClientChannel, @DeviceId,
         @RequestedAt, @RequestedAt)";

    private const string UpdateRouteExceptionSql = @"UPDATE POM_ROUTE_EXCEPTION SET
        STATUS = @Status, REVIEWED_BY = @ReviewedBy, REVIEWED_AT = @ReviewedAt,
        REVIEW_REASON = @ReviewReason, REVIEW_CLIENT_CHANNEL = @ReviewClientChannel,
        REVIEW_DEVICE_ID = @ReviewDeviceId, APPLIED_BY = @AppliedBy, APPLIED_AT = @AppliedAt,
        APPLIED_EXECUTION_ID = @AppliedExecutionId, UPDATED_AT = @UpdatedAt
        WHERE EXCEPTION_ID = @ExceptionId AND STATUS = @ExpectedStatus";

    /// <summary>예외 요청 ID로 승인 원장을 복원한다.</summary>
    public async Task<RouteExceptionRequest?> GetRouteExceptionAsync(
        string exceptionId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_ROUTE_EXCEPTION WHERE EXCEPTION_ID = @exceptionId";
        var row = await QueryFirstOrDefaultAsync<RouteExceptionRow>(sql, new { exceptionId }, ct);
        return row?.ToDomain();
    }

    /// <summary>LOT의 최신 예외 요청부터 승인 원장을 조회한다.</summary>
    public async Task<IReadOnlyList<RouteExceptionRequest>> GetRouteExceptionsByLotAsync(
        string lotId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM POM_ROUTE_EXCEPTION
            WHERE LOT_ID = @lotId ORDER BY REQUESTED_AT DESC, EXCEPTION_ID DESC";
        var rows = await QueryAsync<RouteExceptionRow>(sql, new { lotId }, ct);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>새 라우팅 예외 요청을 append-only 원장에 추가한다.</summary>
    public async Task<RouteExceptionAddResult> TryAddRouteExceptionAsync(
        RouteExceptionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var affected = await _processor.InsertAsync(
                InsertRouteExceptionSql, RouteExceptionRow.FromDomain(request), ct);
            if (affected != 1)
                throw new InvalidOperationException(
                    $"Route exception insert affected {affected} rows; expected exactly one.");
            return RouteExceptionAddResult.Added;
        }
        catch (DbException exception) when (IsRouteExceptionIdentityRace(exception))
        {
            return RouteExceptionAddResult.AlreadyExists;
        }
    }

    private static bool IsRouteExceptionIdentityRace(DbException exception)
    {
        var uniqueViolation = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!uniqueViolation) return false;

        return exception.Message.Contains(
                   "PK_POM_ROUTE_EXCEPTION",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "POM_ROUTE_EXCEPTION.EXCEPTION_ID",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>예상 상태가 일치할 때만 승인·반려·적용 상태를 변경한다.</summary>
    public async Task<bool> UpdateRouteExceptionAsync(
        RouteExceptionRequest request,
        RouteExceptionStatus expectedStatus,
        CancellationToken ct = default)
        => await _processor.ExecuteGuardedManyAsync(
            ct, RouteExceptionUpdateStatement(request, expectedStatus, DateTime.UtcNow));

    private static (string Sql, object? Param) RouteExceptionUpdateStatement(
        RouteExceptionRequest request,
        RouteExceptionStatus expectedStatus,
        DateTime updatedAt) => (UpdateRouteExceptionSql, new
    {
        ExceptionId = request.Id,
        Status = request.Status.ToString(),
        request.ReviewedBy,
        request.ReviewedAt,
        request.ReviewReason,
        request.ReviewClientChannel,
        request.ReviewDeviceId,
        request.AppliedBy,
        request.AppliedAt,
        request.AppliedExecutionId,
        ExpectedStatus = expectedStatus.ToString(),
        UpdatedAt = updatedAt
    });

    private sealed class RouteExceptionRow
    {
        public string ExceptionId { get; set; } = string.Empty;
        public string LotId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string DeviationType { get; set; } = string.Empty;
        public int FromStep { get; set; }
        public int ToStep { get; set; }
        public string FromProcessId { get; set; } = string.Empty;
        public string ToProcessId { get; set; } = string.Empty;
        public int BoundLotVersion { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewReason { get; set; }
        public string? ReviewClientChannel { get; set; }
        public string? ReviewDeviceId { get; set; }
        public string? AppliedBy { get; set; }
        public DateTime? AppliedAt { get; set; }
        public string? AppliedExecutionId { get; set; }
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }

        public RouteExceptionRequest ToDomain() => RouteExceptionRequest.Restore(
            ExceptionId, LotId, PlantId,
            Enum.Parse<RouteDeviationType>(DeviationType, ignoreCase: true),
            FromStep, ToStep, FromProcessId, ToProcessId, BoundLotVersion, Reason,
            Enum.Parse<RouteExceptionStatus>(Status, ignoreCase: true),
            RequestedBy, RequestedAt, ExpiresAt, ReviewedBy, ReviewedAt, ReviewReason,
            ReviewClientChannel, ReviewDeviceId,
            AppliedBy, AppliedAt, AppliedExecutionId, ClientChannel, DeviceId);

        public static RouteExceptionRow FromDomain(RouteExceptionRequest request) => new()
        {
            ExceptionId = request.Id,
            LotId = request.LotId,
            PlantId = request.PlantId,
            DeviationType = request.DeviationType.ToString(),
            FromStep = request.FromStepIndex,
            ToStep = request.ToStepIndex,
            FromProcessId = request.FromProcessId,
            ToProcessId = request.ToProcessId,
            BoundLotVersion = request.BoundLotVersion,
            Reason = request.Reason,
            Status = request.Status.ToString(),
            RequestedBy = request.RequestedBy,
            RequestedAt = request.RequestedAt,
            ExpiresAt = request.ExpiresAt,
            ReviewedBy = request.ReviewedBy,
            ReviewedAt = request.ReviewedAt,
            ReviewReason = request.ReviewReason,
            ReviewClientChannel = request.ReviewClientChannel,
            ReviewDeviceId = request.ReviewDeviceId,
            AppliedBy = request.AppliedBy,
            AppliedAt = request.AppliedAt,
            AppliedExecutionId = request.AppliedExecutionId,
            ClientChannel = request.ClientChannel,
            DeviceId = request.DeviceId
        };
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
        public string ControlMode { get; set; } = nameof(RoutingControlMode.Strict);
        public int? ReturnStep { get; set; }
        public string? EquipmentId { get; set; }
        public string? RecipeDefId { get; set; }
        public int? RecipeDefVersion { get; set; }
        public string? CarrierId { get; set; }
        public string IsHold { get; set; } = "N";
        public string? TrackInUser { get; set; }
        public DateTime? TrackInTime { get; set; }
        public string? TrackOutUser { get; set; }
        public int VersionNo { get; set; } = 1;
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
            CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, VersionNo,
            Enum.TryParse<RoutingControlMode>(ControlMode, true, out var controlMode)
                ? controlMode
                : RoutingControlMode.Strict,
            ReturnStep);

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
            ControlMode = lot.ControlMode.ToString(),
            ReturnStep = lot.ReturnStepIndex,
            EquipmentId = lot.EquipmentId,
            RecipeDefId = lot.RecipeDefId,
            RecipeDefVersion = lot.RecipeDefVersion,
            CarrierId = lot.CarrierId,
            IsHold = lot.IsHold ? "Y" : "N",
            TrackInUser = lot.TrackInUser,
            TrackInTime = lot.TrackInTime,
            TrackOutUser = lot.TrackOutUser,
            TrackOutTime = lot.TrackOutTime,
            VersionNo = lot.VersionNo
        };
    }
}
