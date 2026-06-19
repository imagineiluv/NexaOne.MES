using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class ProductionOrderRepository : QueryRepository, IProductionOrderRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public ProductionOrderRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProductionOrder?> GetByIdAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_PRODUCTION_ORDER WHERE ORDER_ID = @orderId";
        var row = await QueryFirstOrDefaultAsync<OrderRow>(sql, new { orderId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ProductionOrder>> GetByPlanAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_PRODUCTION_ORDER WHERE PLAN_ID = @planId ORDER BY SCHEDULED_START";
        var rows = await QueryAsync<OrderRow>(sql, new { planId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<ProductionOrder>().ToList();
    }

    private const string InsertSql = @"INSERT INTO POM_PRODUCTION_ORDER
            (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY,
             SCHEDULED_START, SCHEDULED_END, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@OrderId, @PlanId, @EquipmentId, @ProductId, @OrderQty,
             @ScheduledStart, @ScheduledEnd, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE POM_PRODUCTION_ORDER SET
            STATUS = @Status, ACTUAL_QTY = @ActualQty,
            ACTUAL_START = @ActualStart, ACTUAL_END = @ActualEnd,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ORDER_ID = @OrderId";

    public async Task AddAsync(ProductionOrder order, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, OrderRow.FromDomain(order), ct);
    }

    public async Task UpdateAsync(ProductionOrder order, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, OrderRow.FromDomain(order), ct);
            return;
        }
        // ADR-002 활성: 오더 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(order, ct);
    }

    // 오더 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 오더 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    // DomainEvents가 비면(예: 이벤트 없는 필드 변경) 데이터 행만 기록되고 outbox INSERT는 없다 — 의도된 동작.
    private async Task PersistWithOutboxAsync(ProductionOrder order, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(order, user, now)),
        };
        statements.AddRange(OutboxStatements.For(order.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        order.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(ProductionOrder order, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("OrderId", order.Id);
        p.Add("Status", order.Status.ToString());
        p.Add("ActualQty", order.ActualQty);
        p.Add("ActualStart", order.ActualStart);
        p.Add("ActualEnd", order.ActualEnd);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class OrderRow
    {
        public string OrderId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public decimal OrderQty { get; set; }
        public decimal? ActualQty { get; set; }
        public DateTime ScheduledStart { get; set; }
        public DateTime ScheduledEnd { get; set; }
        public DateTime? ActualStart { get; set; }
        public DateTime? ActualEnd { get; set; }
        public string Status { get; set; } = "Issued";

        // 읽기경로 감사 메타데이터 — SELECT * + Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑된다.
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // 읽기경로는 Restore로 영속 상태를 직접 복원한다 — Create 후 전이 재생(replay)을 쓰면 전이가 발행하는
        // 도메인 이벤트가 모든 읽기마다 outbox로 새어 나가는 유령 이벤트(phantom)가 된다(ADR-002 읽기경로 안전).
        public ProductionOrder ToDomain() =>
            ProductionOrder.Restore(OrderId, PlanId, EquipmentId, ProductId, OrderQty, ActualQty,
                ScheduledStart, ScheduledEnd, ActualStart, ActualEnd,
                Enum.Parse<ProductionOrderStatus>(Status, ignoreCase: true),
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static OrderRow FromDomain(ProductionOrder o) => new()
        {
            OrderId = o.Id,
            PlanId = o.PlanId,
            EquipmentId = o.EquipmentId,
            ProductId = o.ProductId,
            OrderQty = o.OrderQty,
            ActualQty = o.ActualQty,
            ScheduledStart = o.ScheduledStart,
            ScheduledEnd = o.ScheduledEnd,
            ActualStart = o.ActualStart,
            ActualEnd = o.ActualEnd,
            Status = o.Status.ToString()
        };
    }
}
