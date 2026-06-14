using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class DefectRepository : QueryRepository, IDefectRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public DefectRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Defect?> GetByIdAsync(string defectId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT WHERE DEFECT_ID = @defectId";
        var row = await QueryFirstOrDefaultAsync<DefectRow>(sql, new { defectId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Defect>> GetByLotAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM QMS_DEFECT WHERE LOT_ID = @lotId";
        var rows = await QueryAsync<DefectRow>(sql, new { lotId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Defect>().ToList();
    }

    public async Task<IReadOnlyList<Defect>> GetByEquipmentAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM QMS_DEFECT
            WHERE EQUIPMENT_ID = @equipmentId AND INSPECTED_AT BETWEEN @from AND @to";
        var rows = await QueryAsync<DefectRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Defect>().ToList();
    }

    public async Task AddAsync(Defect defect, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO QMS_DEFECT
            (DEFECT_ID, LOT_ID, EQUIPMENT_ID, DEFECT_CLASS_ID, DEFECT_COUNT, DEFECT_RATE,
             INSPECTED_AT, INSPECTOR_ID, REMARK, IS_CONFIRMED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@DefectId, @LotId, @EquipmentId, @DefectClassId, @DefectCount, @DefectRate,
             @InspectedAt, @InspectorId, @Remark, @IsConfirmed,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, DefectRow.FromDomain(defect), ct);
    }

    private const string UpdateSql = @"UPDATE QMS_DEFECT SET
            DEFECT_COUNT = @DefectCount, DEFECT_RATE = @DefectRate,
            IS_CONFIRMED = @IsConfirmed, CONFIRMED_AT = @ConfirmedAt, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE DEFECT_ID = @DefectId";

    public async Task UpdateAsync(Defect defect, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, DefectRow.FromDomain(defect), ct);
            return;
        }
        // ADR-002 활성: 부적합 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(defect, ct);
    }

    // 부적합 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 부적합 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(Defect defect, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(defect, user, now)),
        };
        statements.AddRange(OutboxStatements.For(defect.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        defect.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(Defect defect, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("DefectId", defect.Id);
        p.Add("DefectCount", defect.DefectCount);
        p.Add("DefectRate", defect.DefectRate);
        p.Add("IsConfirmed", defect.IsConfirmed);
        p.Add("ConfirmedAt", defect.ConfirmedAt);
        p.Add("Remark", defect.Remark);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class DefectRow
    {
        public string DefectId { get; set; } = "";
        public string LotId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string DefectClassId { get; set; } = "";
        public int DefectCount { get; set; }
        public decimal DefectRate { get; set; }
        public DateTime InspectedAt { get; set; }
        public string InspectorId { get; set; } = "";
        public string? Remark { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Defect ToDomain() =>
            Defect.Restore(DefectId, LotId, EquipmentId, DefectClassId, DefectCount, DefectRate, InspectedAt,
                InspectorId, Remark, IsConfirmed, ConfirmedAt, UpdatedBy, UpdatedAt);

        public static DefectRow FromDomain(Defect d) => new()
        {
            DefectId = d.Id,
            LotId = d.LotId,
            EquipmentId = d.EquipmentId,
            DefectClassId = d.DefectClassId,
            DefectCount = d.DefectCount,
            DefectRate = d.DefectRate,
            InspectedAt = d.InspectedAt,
            InspectorId = d.InspectorId,
            Remark = d.Remark,
            IsConfirmed = d.IsConfirmed,
            ConfirmedAt = d.ConfirmedAt
        };
    }
}
