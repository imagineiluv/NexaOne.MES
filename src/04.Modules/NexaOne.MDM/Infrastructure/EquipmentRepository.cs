using System.Data;
using System.Text.Json;
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
        var actor = AuditActor();
        var now = DateTime.UtcNow;
        var row = EquipmentRow.FromDomain(equipment);
        row.CreatedBy = actor;
        row.CreatedAt = now;
        row.UpdatedBy = actor;
        row.UpdatedAt = now;
        await _processor.ExecuteManyAsync(
            ct,
            (sql, row),
            (InsertHistorySql, HistoryParam(null, equipment, actor, now)));
    }

    public async Task UpdateAsync(Equipment equipment, CancellationToken ct = default)
    {
        var before = await GetByIdAsync(equipment.Id, ct);
        if (before is null)
            throw new DBConcurrencyException($"Equipment '{equipment.Id}' disappeared before it could be updated.");

        // 설비 변경 + immutable before/after 이력 + 선택적 outbox를 한 트랜잭션으로 묶는다.
        // 첫 UPDATE는 읽은 원본 값 전체를 guard로 사용하므로 이력의 BEFORE가 실제 승자 상태와 달라질 수 없다.
        await PersistWithHistoryAsync(before, equipment, ct);
    }

    // 설비 UPDATE — ChangeParent가 바꾸는 PARENT_EQUIPMENT_ID까지 영속한다(기본·활성 경로 공유; 이전 off 경로의 누락 교정).
    private const string UpdateSql = @"UPDATE MDM_EQUIPMENT SET
            EQUIPMENT_NAME = @EquipmentName, DESCRIPTION = @Description, EQUIPMENT_TYPE = @EquipmentType,
            PARENT_EQUIPMENT_ID = @ParentEquipmentId,
            VENDOR = @Vendor, MODEL = @Model, VALID_STATE = @ValidState, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE EQUIPMENT_ID = @EquipmentId
              AND EQUIPMENT_NAME = @BeforeEquipmentName
              AND DESCRIPTION = @BeforeDescription
              AND EQUIPMENT_TYPE = @BeforeEquipmentType
              AND ((PARENT_EQUIPMENT_ID = @BeforeParentEquipmentId)
                   OR (PARENT_EQUIPMENT_ID IS NULL AND @BeforeParentEquipmentId IS NULL))
              AND VENDOR = @BeforeVendor
              AND MODEL = @BeforeModel
              AND VALID_STATE = @BeforeValidState";

    private const string InsertHistorySql = @"INSERT INTO MDM_EQUIPMENT_CHANGE_HISTORY
            (CHANGE_ID, EQUIPMENT_ID, CHANGE_TYPE, ACTOR_ID, BEFORE_STATE_JSON,
             AFTER_STATE_JSON, CHANGED_AT, CREATED_BY, CREATED_AT)
            VALUES
            (@ChangeId, @EquipmentId, @ChangeType, @ActorId, @BeforeStateJson,
             @AfterStateJson, @ChangedAt, @ActorId, @ChangedAt)";

    // 설비 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 설비 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithHistoryAsync(
        Equipment before,
        Equipment equipment,
        CancellationToken ct)
    {
        var user = AuditActor();
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(before, equipment, user, now)),
            (InsertHistorySql, HistoryParam(before, equipment, user, now)),
        };
        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(
                equipment.DomainEvents.OfType<IOutboxEvent>(), user, now));

        if (!await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray()))
            throw new DBConcurrencyException(
                $"Equipment '{equipment.Id}' changed concurrently; reload before retrying.");
        if (_outboxEnabled) equipment.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(
        Equipment before,
        Equipment equipment,
        string user,
        DateTime now)
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
        p.Add("BeforeEquipmentName", before.EquipmentName);
        p.Add("BeforeDescription", before.Description);
        p.Add("BeforeEquipmentType", before.EquipmentType);
        p.Add("BeforeParentEquipmentId", before.ParentEquipmentId);
        p.Add("BeforeVendor", before.Vendor);
        p.Add("BeforeModel", before.Model);
        p.Add("BeforeValidState", before.ValidState);
        return p;
    }

    private static object HistoryParam(
        Equipment? before,
        Equipment after,
        string actor,
        DateTime now) => new
    {
        ChangeId = $"ECH_{Guid.NewGuid():N}",
        EquipmentId = after.Id,
        ChangeType = ChangeType(before, after),
        ActorId = actor,
        BeforeStateJson = before is null ? null : Snapshot(before),
        AfterStateJson = Snapshot(after),
        ChangedAt = now,
    };

    private static string ChangeType(Equipment? before, Equipment after)
    {
        if (before is null) return "Create";
        if (!string.Equals(before.ValidState, after.ValidState, StringComparison.OrdinalIgnoreCase))
            return string.Equals(after.ValidState, "Valid", StringComparison.OrdinalIgnoreCase)
                ? "Activate"
                : "Deactivate";
        return !string.Equals(
                before.ParentEquipmentId,
                after.ParentEquipmentId,
                StringComparison.OrdinalIgnoreCase)
            ? "ParentChange"
            : "Update";
    }

    private static string Snapshot(Equipment equipment) => JsonSerializer.Serialize(new
    {
        EquipmentId = equipment.Id,
        equipment.EquipmentName,
        equipment.Description,
        equipment.PlantId,
        equipment.AreaId,
        equipment.EquipmentType,
        equipment.ParentEquipmentId,
        equipment.Vendor,
        equipment.Model,
        equipment.EquipmentClassId,
        equipment.ValidState,
    });

    private static string AuditActor()
    {
        var actor = CurrentUserContext.UserId?.Trim();
        return string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor;
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
