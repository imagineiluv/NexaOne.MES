using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// 작업지시 애그리거트와 실행 이력을 저장한다. 상태 변경과 실행 이력 INSERT는 같은 트랜잭션으로 묶고,
/// <c>VERSION_NO</c> 조건을 사용해 오래된 화면이나 단말의 동시 수정을 거부한다.
/// </summary>
public sealed class PomWorkOrderRepository : QueryRepository, IPomWorkOrderRepository
{
    private readonly ServiceObjectProcessor _processor;

    /// <summary>공유 MES 데이터소스에 작업지시 저장소를 연결한다.</summary>
    public PomWorkOrderRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    /// <summary>작업지시 ID로 애그리거트를 복원한다.</summary>
    public async Task<PomWorkOrder?> GetByIdAsync(string workOrderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_WORK_ORDER WHERE WORK_ORDER_ID = @workOrderId";
        var row = await QueryFirstOrDefaultAsync<WorkOrderRow>(sql, new { workOrderId }, ct);
        return row?.ToDomain();
    }

    /// <summary>생산관리오더에 속한 작업지시를 라우팅 순서로 조회한다.</summary>
    public async Task<IReadOnlyList<PomWorkOrder>> GetByProductionOrderAsync(
        string productionOrderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_WORK_ORDER WHERE PRODUCTION_ORDER_ID = @productionOrderId ORDER BY COALESCE(ROUTING_STEP_NO, 2147483647), WORK_ORDER_ID";
        var rows = await QueryAsync<WorkOrderRow>(sql, new { productionOrderId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>동일 단말 요청이 이미 처리됐는지 멱등 키로 확인한다.</summary>
    public async Task<bool> ExecutionExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY = @idempotencyKey";
        return await CountAsync(sql, new { idempotencyKey }, ct) > 0;
    }

    /// <summary>재시도 응답 복원에 사용할 기존 실행 이력을 조회한다.</summary>
    public async Task<PomWorkOrderExecution?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_WORK_ORDER_EXECUTION WHERE IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<WorkOrderExecutionRow>(sql, new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    private const string InsertSql = @"INSERT INTO POM_WORK_ORDER
        (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCTION_ORDER_ID, SALES_ORDER_ID,
         AREA_ID, EQUIPMENT_ID, WORK_ORDER_TYPE, PRODUCT_ID, ROUTING_ID, ROUTING_STEP_NO,
         ROUTING_SCOPE, PROCESS_ID, WORK_CENTER_ID, PLAN_START_DATE, PLAN_END_DATE, PLAN_QTY, START_QTY,
         COMPLETE_QTY, SCRAP_QTY, OWNER_ID, STATUS, IS_HOLD, STARTED_AT, COMPLETED_AT,
         DESCRIPTION, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
        (@WorkOrderId, @PlantId, @WorkOrderName, @ProductionOrderId, @SalesOrderId,
         @AreaId, @EquipmentId, @WorkOrderType, @ProductId, @RoutingId, @RoutingStepNo,
         @RoutingScope, @ProcessId, @WorkCenterId, @PlanStartDate, @PlanEndDate, @PlanQty, @StartQty,
         @CompleteQty, @ScrapQty, @OwnerId, @Status, @IsHold, @StartedAt, @CompletedAt,
         @Description, @VersionNo, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    // VERSION_NO를 조건과 증가식에 함께 사용해 읽기 이후 다른 단말이 저장한 변경을 덮어쓰지 않는다.
    private const string UpdateSql = @"UPDATE POM_WORK_ORDER SET
        PLANT_ID = @PlantId, WORK_ORDER_NAME = @WorkOrderName, PRODUCTION_ORDER_ID = @ProductionOrderId,
        SALES_ORDER_ID = @SalesOrderId, AREA_ID = @AreaId, EQUIPMENT_ID = @EquipmentId,
        WORK_ORDER_TYPE = @WorkOrderType, PRODUCT_ID = @ProductId, ROUTING_ID = @RoutingId,
        ROUTING_STEP_NO = @RoutingStepNo, ROUTING_SCOPE = @RoutingScope,
        PROCESS_ID = @ProcessId, WORK_CENTER_ID = @WorkCenterId,
        PLAN_START_DATE = @PlanStartDate, PLAN_END_DATE = @PlanEndDate, PLAN_QTY = @PlanQty,
        START_QTY = @StartQty, COMPLETE_QTY = @CompleteQty, SCRAP_QTY = @ScrapQty,
        OWNER_ID = @OwnerId, STATUS = @Status, IS_HOLD = @IsHold, STARTED_AT = @StartedAt,
        COMPLETED_AT = @CompletedAt, DESCRIPTION = @Description,
        UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt, VERSION_NO = VERSION_NO + 1
        WHERE WORK_ORDER_ID = @WorkOrderId AND VERSION_NO = @VersionNo";

    // 실행 이력은 수정하지 않는 감사 원장이며 IDEMPOTENCY_KEY 고유 제약이 중복 커밋을 방어한다.
    private const string InsertExecutionSql = @"INSERT INTO POM_WORK_ORDER_EXECUTION
        (EXECUTION_ID, WORK_ORDER_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
         GOOD_QTY, DEFECT_QTY, USER_ID, EQUIPMENT_ID, CLIENT_CHANNEL, DEVICE_ID,
         OCCURRED_AT, REMARK, EXPECTED_VERSION, RESULT_VERSION, CREATED_BY, CREATED_AT)
        VALUES
        (@ExecutionId, @WorkOrderId, @IdempotencyKey, @Action, @FromStatus, @ToStatus,
         @GoodQty, @DefectQty, @UserId, @EquipmentId, @ClientChannel, @DeviceId,
         @OccurredAt, @Remark, @ExpectedVersion, @ResultVersion, @UserId, @OccurredAt)";

    /// <summary>새 작업지시를 저장한다.</summary>
    public Task AddAsync(PomWorkOrder workOrder, CancellationToken ct = default)
        => _processor.InsertAsync(InsertSql, WorkOrderRow.FromDomain(workOrder), ct);

    /// <summary>낙관적 버전 조건으로 작업지시를 갱신하고 성공한 경우 메모리 버전도 증가시킨다.</summary>
    public async Task<bool> UpdateAsync(PomWorkOrder workOrder, CancellationToken ct = default)
    {
        var updated = await _processor.ExecuteGuardedManyAsync(
            ct, (UpdateSql, WorkOrderRow.FromDomain(workOrder)));
        if (updated) workOrder.AcceptPersistedVersion();
        return updated;
    }

    /// <summary>
    /// 작업지시 상태 갱신과 단말 실행 이력 기록을 한 트랜잭션으로 처리해 부분 커밋을 방지한다.
    /// </summary>
    public async Task<bool> UpdateWithExecutionAsync(
        PomWorkOrder workOrder, PomWorkOrderExecution execution, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = string.IsNullOrWhiteSpace(execution.UserId) ? "SYSTEM" : execution.UserId;
        var row = WorkOrderRow.FromDomain(workOrder, user, now);
        var executionRow = new
        {
            execution.ExecutionId,
            execution.WorkOrderId,
            execution.IdempotencyKey,
            Action = execution.Action.ToString(),
            FromStatus = execution.FromStatus.ToString(),
            ToStatus = execution.ToStatus.ToString(),
            execution.GoodQty,
            execution.DefectQty,
            UserId = user,
            execution.EquipmentId,
            execution.ClientChannel,
            execution.DeviceId,
            execution.OccurredAt,
            execution.Remark,
            execution.ExpectedVersion,
            execution.ResultVersion
        };
        var updated = await _processor.ExecuteGuardedManyAsync(ct,
            (UpdateSql, row),
            (InsertExecutionSql, executionRow));
        if (updated) workOrder.AcceptPersistedVersion();
        return updated;
    }

    /// <summary>실행 이력 DB 행을 도메인 감사 모델로 변환하는 내부 매핑 형식이다.</summary>
    private sealed class WorkOrderExecutionRow
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string WorkOrderId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string FromStatus { get; set; } = string.Empty;
        public string ToStatus { get; set; } = string.Empty;
        public decimal? GoodQty { get; set; }
        public decimal? DefectQty { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? EquipmentId { get; set; }
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Remark { get; set; }
        public int? ExpectedVersion { get; set; }
        public int? ResultVersion { get; set; }

        /// <summary>문자열 상태와 동작을 강한 형식의 도메인 값으로 복원한다.</summary>
        public PomWorkOrderExecution ToDomain() => new(
            ExecutionId,
            WorkOrderId,
            IdempotencyKey,
            Enum.Parse<PomWorkOrderAction>(Action, ignoreCase: true),
            Enum.Parse<PomWorkOrderStatus>(FromStatus, ignoreCase: true),
            Enum.Parse<PomWorkOrderStatus>(ToStatus, ignoreCase: true),
            GoodQty,
            DefectQty,
            UserId,
            EquipmentId,
            ClientChannel,
            DeviceId,
            OccurredAt,
            Remark,
            ExpectedVersion,
            ResultVersion);
    }

    /// <summary>작업지시 테이블 행과 도메인 애그리거트 사이의 변환을 담당하는 내부 매핑 형식이다.</summary>
    private sealed class WorkOrderRow
    {
        public string WorkOrderId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string? WorkOrderName { get; set; }
        public string ProductionOrderId { get; set; } = string.Empty;
        public string? SalesOrderId { get; set; }
        public string? AreaId { get; set; }
        public string? EquipmentId { get; set; }
        public string? WorkOrderType { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string? RoutingId { get; set; }
        public string RoutingScope { get; set; } = nameof(PomWorkOrderRoutingScope.Unbound);
        public int? RoutingStepNo { get; set; }
        public string? ProcessId { get; set; }
        public string? WorkCenterId { get; set; }
        public DateTime? PlanStartDate { get; set; }
        public DateTime? PlanEndDate { get; set; }
        public decimal PlanQty { get; set; }
        public decimal StartQty { get; set; }
        public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; }
        public string? OwnerId { get; set; }
        public string Status { get; set; } = nameof(PomWorkOrderStatus.Created);
        public string IsHold { get; set; } = "N";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Description { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int VersionNo { get; set; } = 1;

        /// <summary>저장된 작업지시 행을 검증 없는 복원 경로로 애그리거트화한다.</summary>
        public PomWorkOrder ToDomain() => PomWorkOrder.Restore(
            WorkOrderId, ProductionOrderId, PlantId, WorkOrderName, SalesOrderId, AreaId,
            EquipmentId, WorkOrderType, ProductId, RoutingId,
            Enum.Parse<PomWorkOrderRoutingScope>(RoutingScope, ignoreCase: true), RoutingStepNo, ProcessId,
            WorkCenterId, PlanStartDate, PlanEndDate, PlanQty, StartQty, CompleteQty, ScrapQty,
            OwnerId, Enum.Parse<PomWorkOrderStatus>(Status, ignoreCase: true),
            string.Equals(IsHold, "Y", StringComparison.OrdinalIgnoreCase), StartedAt, CompletedAt,
            Description, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, VersionNo);

        /// <summary>도메인 작업지시와 현재 감사 문맥을 DB 파라미터 행으로 변환한다.</summary>
        public static WorkOrderRow FromDomain(PomWorkOrder workOrder, string? user = null, DateTime? now = null)
        {
            var stamp = now ?? workOrder.UpdatedAt ?? DateTime.UtcNow;
            var actor = user ?? workOrder.UpdatedBy ?? CurrentUserContext.UserId ?? "SYSTEM";
            return new WorkOrderRow
            {
                WorkOrderId = workOrder.Id,
                PlantId = workOrder.PlantId,
                WorkOrderName = workOrder.WorkOrderName,
                ProductionOrderId = workOrder.ProductionOrderId,
                SalesOrderId = workOrder.SalesOrderId,
                AreaId = workOrder.AreaId,
                EquipmentId = workOrder.EquipmentId,
                WorkOrderType = workOrder.WorkOrderType,
                ProductId = workOrder.ProductId,
                RoutingId = workOrder.RoutingId,
                RoutingScope = workOrder.RoutingScope.ToString(),
                RoutingStepNo = workOrder.RoutingStepNo,
                ProcessId = workOrder.ProcessId,
                WorkCenterId = workOrder.WorkCenterId,
                PlanStartDate = workOrder.PlanStartDate,
                PlanEndDate = workOrder.PlanEndDate,
                PlanQty = workOrder.PlanQty,
                StartQty = workOrder.StartQty,
                CompleteQty = workOrder.CompleteQty,
                ScrapQty = workOrder.ScrapQty,
                OwnerId = workOrder.OwnerId,
                Status = workOrder.Status.ToString(),
                IsHold = workOrder.IsHold ? "Y" : "N",
                StartedAt = workOrder.StartedAt,
                CompletedAt = workOrder.CompletedAt,
                Description = workOrder.Description,
                CreatedBy = string.IsNullOrWhiteSpace(workOrder.CreatedBy) ? actor : workOrder.CreatedBy,
                CreatedAt = workOrder.CreatedAt,
                UpdatedBy = actor,
                UpdatedAt = stamp,
                VersionNo = workOrder.VersionNo
            };
        }
    }
}
