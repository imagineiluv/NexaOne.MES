using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class EquipmentRepository : QueryRepository, IEquipmentRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public EquipmentRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Equipment?> GetByIdAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @equipmentId";
        var row = await QueryFirstOrDefaultAsync<EquipmentRow>(sql, new { equipmentId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Equipment>> GetAllByPlantAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_EQUIPMENT WHERE PLANT_ID = @plantId";
        var rows = await QueryAsync<EquipmentRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Equipment>().ToList();
    }

    public async Task<bool> ExistsAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID = @equipmentId";
        var count = await CountAsync(sql, new { equipmentId }, ct);
        return count > 0;
    }

    public async Task AddAsync(Equipment equipment, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_EQUIPMENT
            (EQUIPMENT_ID, EQUIPMENT_NAME, DESCRIPTION, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
             PARENT_EQUIPMENT_ID, VENDOR, MODEL, EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@EquipmentId, @EquipmentName, @Description, @PlantId, @AreaId, @EquipmentType,
             @ParentEquipmentId, @Vendor, @Model, @EquipmentClassId, @ValidState, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, EquipmentRow.FromDomain(equipment), ct);
    }

    public async Task UpdateAsync(Equipment equipment, CancellationToken ct = default)
    {
        // 기본(outbox off): 단건 UPDATE(감사 자동주입), 적체 없음. 기본·활성 경로가 동일 UpdateSql을 공유한다.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, EquipmentRow.FromDomain(equipment), ct);
            return;
        }
        // ADR-002 활성: 설비 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        // (비활성/활성/상위변경만 이벤트를 발행하며, UpdateInfo 같은 비이벤트 편집은 DomainEvents가 비어 outbox INSERT가 0건이다.)
        await PersistWithOutboxAsync(equipment, ct);
    }

    // 설비 UPDATE — ChangeParent가 바꾸는 PARENT_EQUIPMENT_ID까지 영속한다(기본·활성 경로 공유; 이전 off 경로의 누락 교정).
    private const string UpdateSql = @"UPDATE MDM_EQUIPMENT SET
            EQUIPMENT_NAME = @EquipmentName, DESCRIPTION = @Description, EQUIPMENT_TYPE = @EquipmentType,
            PARENT_EQUIPMENT_ID = @ParentEquipmentId,
            VENDOR = @Vendor, MODEL = @Model, VALID_STATE = @ValidState, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE EQUIPMENT_ID = @EquipmentId";

    // 설비 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 설비 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(Equipment equipment, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(equipment, user, now)),
        };
        statements.AddRange(OutboxStatements.For(equipment.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        equipment.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(Equipment equipment, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("EquipmentId", equipment.Id);
        p.Add("EquipmentName", equipment.EquipmentName);
        p.Add("Description", equipment.Description);
        p.Add("EquipmentType", equipment.EquipmentType);
        p.Add("ParentEquipmentId", equipment.ParentEquipmentId);
        p.Add("Vendor", equipment.Vendor);
        p.Add("Model", equipment.Model);
        p.Add("ValidState", equipment.ValidState);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class EquipmentRow
    {
        public string EquipmentId { get; set; } = "";
        public string EquipmentName { get; set; } = "";
        public string Description { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string AreaId { get; set; } = "";
        public string EquipmentType { get; set; } = "";
        public string? ParentEquipmentId { get; set; }
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public string EquipmentClassId { get; set; } = "";
        public string ValidState { get; set; } = "Valid";

        // 감사 컬럼(읽기경로 복원용) — Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑(SELECT *).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Equipment ToDomain() =>
            Equipment.Restore(EquipmentId, EquipmentName, Description, PlantId, AreaId, EquipmentType,
                ParentEquipmentId, Vendor, Model, EquipmentClassId, ValidState,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static EquipmentRow FromDomain(Equipment e) => new()
        {
            EquipmentId = e.Id,
            EquipmentName = e.EquipmentName,
            Description = e.Description,
            PlantId = e.PlantId,
            AreaId = e.AreaId,
            EquipmentType = e.EquipmentType,
            ParentEquipmentId = e.ParentEquipmentId,
            Vendor = e.Vendor,
            Model = e.Model,
            EquipmentClassId = e.EquipmentClassId,
            ValidState = e.ValidState
        };
    }
}
