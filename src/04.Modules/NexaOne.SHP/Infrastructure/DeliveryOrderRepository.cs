using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.SHP.Infrastructure;

public sealed class DeliveryOrderRepository : QueryRepository, IDeliveryOrderRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public DeliveryOrderRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DeliveryOrder?> GetByIdAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SHP_DELIVERY_ORDER WHERE ORDER_ID = @orderId";
        var row = await QueryFirstOrDefaultAsync<OrderRow>(sql, new { orderId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<DeliveryOrder>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SHP_DELIVERY_ORDER
            WHERE PLANT_ID = @plantId
              AND (@from IS NULL OR REQUESTED_DATE >= @from)
              AND (@to IS NULL OR REQUESTED_DATE <= @to)";
        var rows = await QueryAsync<OrderRow>(sql, new { plantId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<DeliveryOrder>().ToList();
    }

    public async Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM SHP_DELIVERY_ORDER WHERE STATUS = @status";
        return await CountAsync(sql, new { status }, ct);
    }

    private const string InsertSql = @"INSERT INTO SHP_DELIVERY_ORDER
            (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@OrderId, @CustomerName, @PlantId, @RequestedDate, @Status, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE SHP_DELIVERY_ORDER SET
            STATUS = @Status, SHIPPED_DATE = @ShippedDate, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ORDER_ID = @OrderId";

    public async Task AddAsync(DeliveryOrder order, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, OrderRow.FromDomain(order), ct);
    }

    public async Task UpdateAsync(DeliveryOrder order, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, OrderRow.FromDomain(order), ct);
            return;
        }
        // ADR-002 활성: 주문 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(order, ct);
    }

    // 주문 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 주문 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(DeliveryOrder order, CancellationToken ct)
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

    private static Dapper.DynamicParameters UpdateParam(DeliveryOrder order, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("OrderId", order.Id);
        p.Add("Status", order.Status.ToString());
        p.Add("ShippedDate", order.ShippedDate);
        p.Add("Remark", order.Remark);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class OrderRow
    {
        public string OrderId { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string PlantId { get; set; } = "";
        public DateTime RequestedDate { get; set; }
        public DateTime? ShippedDate { get; set; }
        public string Status { get; set; } = "Draft";
        public string? Remark { get; set; }

        public DeliveryOrder ToDomain() =>
            DeliveryOrder.Restore(OrderId, CustomerName, PlantId, RequestedDate,
                Enum.Parse<DeliveryOrderStatus>(Status, ignoreCase: true), ShippedDate, Remark);

        public static OrderRow FromDomain(DeliveryOrder o) => new()
        {
            OrderId = o.Id,
            CustomerName = o.CustomerName,
            PlantId = o.PlantId,
            RequestedDate = o.RequestedDate,
            ShippedDate = o.ShippedDate,
            Status = o.Status.ToString(),
            Remark = o.Remark
        };
    }
}
