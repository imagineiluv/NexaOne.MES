using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using System.Data.Common;

namespace NexaOne.RMS.Infrastructure;

public sealed class RecipeRepository : QueryRepository, IRecipeRepository
{
    private const string InsertSql = @"INSERT INTO RMS_RECIPE
            (RECIPE_ID, RECIPE_NAME, DESCRIPTION, EQUIPMENT_CLASS_ID, VERSION, APPROVAL_STATE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT
             @RecipeId, @RecipeName, @Description, @EquipmentClassId, @Version, @ApprovalState,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt
            WHERE NOT EXISTS (
                SELECT 1 FROM RMS_RECIPE_COMMAND WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
              AND NOT EXISTS (
                SELECT 1 FROM RMS_RECIPE WHERE RECIPE_ID = @RecipeId)";

    private const string InsertParamSql = @"INSERT INTO RMS_RECIPE_PARAM
            (PARAM_ID, RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER, VERSION_NO,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@ParamId, @RecipeId, @ParamName, @ParamValue, @Unit, @SortOrder, @VersionNo,
                    @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string InsertWriteSql = @"INSERT INTO RMS_RECIPE_COMMAND
            (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID,
             SOURCE_RECIPE_ID, ACTOR_ID, CREATED_AT)
            VALUES (@CommandId, @CommandType, @IdempotencyKey, @RequestHash, @RecipeId,
                    @SourceRecipeId, @ActorId, @CreatedAt)";

    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public RecipeRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Recipe?> GetByIdAsync(string recipeId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM RMS_RECIPE WHERE RECIPE_ID = @recipeId";
        var row = await QueryFirstOrDefaultAsync<RecipeRow>(sql, new { recipeId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Recipe>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default)
        => await GetAsync(equipmentClassId, null, ct);

    public async Task<IReadOnlyList<Recipe>> GetByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
        => await GetAsync(null, state, ct);

    public async Task<IReadOnlyList<Recipe>> GetAsync(
        string? equipmentClassId,
        RecipeApprovalState? state,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM RMS_RECIPE
            WHERE (@equipmentClassId IS NULL OR EQUIPMENT_CLASS_ID = @equipmentClassId)
              AND (@state IS NULL OR APPROVAL_STATE = @state)
            ORDER BY RECIPE_ID, VERSION";
        var normalizedClassId = string.IsNullOrWhiteSpace(equipmentClassId)
            ? null
            : equipmentClassId.Trim();
        var rows = await QueryAsync<RecipeRow>(sql, new
        {
            equipmentClassId = normalizedClassId,
            state = state?.ToString(),
        }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Recipe>().ToList();
    }

    public async Task<int> GetCountByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM RMS_RECIPE WHERE APPROVAL_STATE = @state";
        return await CountAsync(sql, new { state = state.ToString() }, ct);
    }

    public async Task<RecipeWriteRecord?> GetWriteByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        const string sql = @"SELECT COMMAND_ID AS CommandId, COMMAND_TYPE AS CommandType,
            IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
            RECIPE_ID AS RecipeId, SOURCE_RECIPE_ID AS SourceRecipeId,
            ACTOR_ID AS ActorId, CREATED_AT AS CreatedAt
            FROM RMS_RECIPE_COMMAND WHERE IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<RecipeWriteRow>(sql, new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAddAsync(
        Recipe recipe, RecipeWriteRecord write, CancellationToken ct = default)
    {
        var row = RecipeRow.FromDomain(recipe, write.ActorId, write.CreatedAt);
        var parameters = MergeWrite(row, write);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (InsertSql, parameters), (InsertWriteSql, parameters));
        }
        catch (DbException)
        {
            if (await GetWriteByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null)
                return false;
            if (await GetByIdAsync(recipe.Id, ct) is not null)
                return false;
            throw;
        }
    }

    /// <summary>
    /// 새 버전 header와 복제된 parameter를 한 트랜잭션으로 기록한다. parameter INSERT 하나라도 실패하면
    /// header까지 롤백되어 빈 Recipe 버전이 남지 않는다.
    /// </summary>
    public async Task<bool> TryAddVersionAsync(
        Recipe recipe,
        IReadOnlyList<RecipeParam> parameters,
        RecipeWriteRecord write,
        CancellationToken ct = default)
    {
        var recipeRow = RecipeRow.FromDomain(recipe, write.ActorId, write.CreatedAt);
        var recipeParameters = MergeWrite(recipeRow, write);

        var statements = new List<(string Sql, object? Param)>
        {
            (InsertSql, recipeParameters),
        };
        statements.AddRange(parameters.Select(parameter =>
            (InsertParamSql, (object?)new
            {
                ParamId = parameter.Id,
                RecipeId = parameter.RecipeId,
                parameter.ParamName,
                parameter.ParamValue,
                parameter.Unit,
                parameter.SortOrder,
                VersionNo = parameter.Version,
                CreatedBy = write.ActorId,
                CreatedAt = write.CreatedAt,
                UpdatedBy = write.ActorId,
                UpdatedAt = write.CreatedAt,
            })));
        statements.Add((InsertWriteSql, write));

        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        }
        catch (DbException)
        {
            if (await GetWriteByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null)
                return false;
            if (await GetByIdAsync(recipe.Id, ct) is not null)
                return false;
            throw;
        }
    }

    private const string TransitionSql = @"UPDATE RMS_RECIPE SET
            RECIPE_NAME = @RecipeName, DESCRIPTION = @Description, VERSION = @Version,
            APPROVAL_STATE = @ApprovalState, FIRST_APPROVER_ID = @FirstApproverId,
            SECOND_APPROVER_ID = @SecondApproverId, RELEASED_AT = @ReleasedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE RECIPE_ID = @RecipeId AND APPROVAL_STATE = @ExpectedState
              AND NOT EXISTS (
                  SELECT 1 FROM RMS_RECIPE_APPROVAL_HISTORY H
                  WHERE H.IDEMPOTENCY_KEY = @IdempotencyKey)";

    private const string InsertApprovalHistorySql = @"INSERT INTO RMS_RECIPE_APPROVAL_HISTORY
            (HISTORY_ID, IDEMPOTENCY_KEY, REQUEST_HASH, RECIPE_ID,
             FROM_STATE, TO_STATE, CHANGED_BY, REASON, CHANGED_AT)
            VALUES
            (@HistoryId, @IdempotencyKey, @RequestHash, @RecipeId,
             @FromState, @ToState, @ChangedBy, @Reason, @ChangedAt)";

    public async Task<bool> TryTransitionAsync(
        Recipe recipe,
        RecipeApprovalState expectedState,
        RecipeTransitionWrite transition,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var update = UpdateParam(recipe, transition.ActorId, now);
        update.Add("ExpectedState", expectedState.ToString());
        update.Add("IdempotencyKey", transition.IdempotencyKey);
        var statements = new List<(string Sql, object? Param)>
        {
            (TransitionSql, update),
            (InsertApprovalHistorySql, new
            {
                HistoryId = $"RAH_{Guid.NewGuid():N}",
                transition.IdempotencyKey,
                transition.RequestHash,
                RecipeId = recipe.Id,
                FromState = expectedState.ToString(),
                ToState = recipe.ApprovalState.ToString(),
                ChangedBy = transition.ActorId,
                Reason = string.IsNullOrWhiteSpace(transition.Reason) ? null : transition.Reason.Trim(),
                ChangedAt = now,
            }),
        };
        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(
                recipe.DomainEvents.OfType<IOutboxEvent>(), transition.ActorId, now));

        bool changed;
        try
        {
            changed = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        }
        catch (DbException)
        {
            if (await GetApprovalHistoryByIdempotencyKeyAsync(
                    transition.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
        if (changed) recipe.ClearDomainEvents();
        return changed;
    }

    public async Task<RecipeApprovalHistoryRecord?> GetApprovalHistoryByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ApprovalHistoryRow>(
            ApprovalHistorySelectSql + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<RecipeApprovalHistoryRecord>> GetApprovalHistoryAsync(
        string recipeId, CancellationToken ct = default)
    {
        var sql = ApprovalHistorySelectSql + @"
            WHERE RECIPE_ID = @recipeId
            ORDER BY CHANGED_AT, HISTORY_ID";
        var rows = await QueryAsync<ApprovalHistoryRow>(sql, new { recipeId }, ct);
        return rows.Select(row => row.ToRecord()).ToList();
    }

    private const string ApprovalHistorySelectSql = @"SELECT
            HISTORY_ID AS HistoryId, IDEMPOTENCY_KEY AS IdempotencyKey,
            REQUEST_HASH AS RequestHash, RECIPE_ID AS RecipeId, FROM_STATE AS FromState,
            TO_STATE AS ToState, CHANGED_BY AS ChangedBy, REASON AS Reason,
            CHANGED_AT AS ChangedAt
            FROM RMS_RECIPE_APPROVAL_HISTORY";

    private static Dapper.DynamicParameters UpdateParam(Recipe recipe, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("RecipeId", recipe.Id);
        p.Add("RecipeName", recipe.RecipeName);
        p.Add("Description", recipe.Description);
        p.Add("Version", recipe.Version);
        p.Add("ApprovalState", recipe.ApprovalState.ToString());
        p.Add("FirstApproverId", recipe.FirstApproverId);
        p.Add("SecondApproverId", recipe.SecondApproverId);
        p.Add("ReleasedAt", recipe.ReleasedAt);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private static Dapper.DynamicParameters MergeWrite(RecipeRow row, RecipeWriteRecord write)
    {
        var parameters = new Dapper.DynamicParameters(row);
        parameters.Add("CommandId", write.CommandId);
        parameters.Add("CommandType", write.CommandType);
        parameters.Add("IdempotencyKey", write.IdempotencyKey);
        parameters.Add("RequestHash", write.RequestHash);
        parameters.Add("SourceRecipeId", write.SourceRecipeId);
        parameters.Add("ActorId", write.ActorId);
        return parameters;
    }

    private sealed class RecipeRow
    {
        public string RecipeId { get; set; } = "";
        public string RecipeName { get; set; } = "";
        public string Description { get; set; } = "";
        public string EquipmentClassId { get; set; } = "";
        public int Version { get; set; } = 1;
        public string ApprovalState { get; set; } = "Draft";
        public string? FirstApproverId { get; set; }
        public string? SecondApproverId { get; set; }
        public DateTime? ReleasedAt { get; set; }

        // 읽기경로 감사 메타데이터 — Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 등 자동 매핑(SELECT *).
        public string    CreatedBy { get; set; } = "";
        public DateTime  CreatedAt { get; set; }
        public string?   UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public Recipe ToDomain()
        {
            // 영속 상태(Version/ApprovalState/승인자/ReleasedAt/감사값)를 그대로 복원한다 —
            // Create는 Draft/Version=1로 강제해 승인 상태를 유실하므로 Restore를 사용한다.
            if (!Enum.TryParse<RecipeApprovalState>(ApprovalState, out var state)) state = RecipeApprovalState.Draft;
            return Recipe.Restore(
                RecipeId, RecipeName, Description, EquipmentClassId, Version, state,
                FirstApproverId, SecondApproverId, ReleasedAt,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
        }

        public static RecipeRow FromDomain(Recipe r, string? actor = null, DateTime? at = null) => new()
        {
            RecipeId = r.Id,
            RecipeName = r.RecipeName,
            Description = r.Description,
            EquipmentClassId = r.EquipmentClassId,
            Version = r.Version,
            ApprovalState = r.ApprovalState.ToString(),
            FirstApproverId = r.FirstApproverId,
            SecondApproverId = r.SecondApproverId,
            ReleasedAt = r.ReleasedAt,
            CreatedBy = actor ?? r.CreatedBy,
            CreatedAt = at ?? r.CreatedAt,
            UpdatedBy = actor ?? r.UpdatedBy,
            UpdatedAt = at ?? r.UpdatedAt,
        };
    }

    private sealed class ApprovalHistoryRow
    {
        public string HistoryId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string RecipeId { get; set; } = "";
        public string FromState { get; set; } = "Draft";
        public string ToState { get; set; } = "Draft";
        public string ChangedBy { get; set; } = "";
        public string? Reason { get; set; }
        public DateTime ChangedAt { get; set; }

        public RecipeApprovalHistoryRecord ToRecord()
            => new(
                HistoryId,
                IdempotencyKey,
                RequestHash,
                RecipeId,
                Enum.Parse<RecipeApprovalState>(FromState, true),
                Enum.Parse<RecipeApprovalState>(ToState, true),
                ChangedBy,
                Reason,
                ChangedAt);
    }

    private sealed class RecipeWriteRow
    {
        public string CommandId { get; set; } = "";
        public string CommandType { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string RecipeId { get; set; } = "";
        public string? SourceRecipeId { get; set; }
        public string ActorId { get; set; } = "";
        public DateTime CreatedAt { get; set; }

        public RecipeWriteRecord ToRecord() => new(
            CommandId, CommandType, IdempotencyKey, RequestHash, RecipeId,
            SourceRecipeId, ActorId, CreatedAt);
    }
}
