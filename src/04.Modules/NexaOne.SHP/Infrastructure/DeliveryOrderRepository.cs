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
            (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS, IS_HOLD, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@OrderId, @CustomerName, @PlantId, @RequestedDate, @Status, @IsHold, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE SHP_DELIVERY_ORDER SET
            STATUS = @Status, IS_HOLD = @IsHold, SHIPPED_DATE = @ShippedDate, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ORDER_ID = @OrderId AND STATUS = @ExpectedStatus AND IS_HOLD = @ExpectedHold";

    public async Task AddAsync(DeliveryOrder order, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, OrderRow.FromDomain(order), ct);
    }

    public async Task<bool> TryUpdateAsync(
        DeliveryOrder order, DeliveryOrderStatus expectedStatus, bool expectedHeld,
        CancellationToken ct = default)
    {
        // 상태 CAS가 실패하면 outbox도 기록하지 않는다. 성공한 경우에만 이벤트를 비운다.
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(order, expectedStatus, expectedHeld, user, now)),
        };
        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(order.DomainEvents.OfType<IOutboxEvent>(), user, now));
        var updated = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        if (!updated) return false;
        order.ClearDomainEvents();
        return true;
    }

    private static Dapper.DynamicParameters UpdateParam(
        DeliveryOrder order, DeliveryOrderStatus expectedStatus, bool expectedHeld,
        string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("OrderId", order.Id);
        p.Add("Status", order.Status.ToString());
        p.Add("ExpectedStatus", expectedStatus.ToString());
        p.Add("IsHold", order.IsHeld ? "Y" : "N");
        p.Add("ExpectedHold", expectedHeld ? "Y" : "N");
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
        public string IsHold { get; set; } = "N";
        public string? Remark { get; set; }

        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 도메인에 그대로 복원한다.
        // Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 등 자동 매핑(SELECT *).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public DeliveryOrder ToDomain() =>
            DeliveryOrder.Restore(OrderId, CustomerName, PlantId, RequestedDate,
                Enum.Parse<DeliveryOrderStatus>(Status, ignoreCase: true), ShippedDate, Remark,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, IsHold == "Y");

        public static OrderRow FromDomain(DeliveryOrder o) => new()
        {
            OrderId = o.Id,
            CustomerName = o.CustomerName,
            PlantId = o.PlantId,
            RequestedDate = o.RequestedDate,
            ShippedDate = o.ShippedDate,
            Status = o.Status.ToString(),
            IsHold = o.IsHeld ? "Y" : "N",
            Remark = o.Remark
        };
    }
}
