using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Infrastructure;

public sealed class RecipeRepository : QueryRepository, IRecipeRepository
{
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
    {
        const string sql = "SELECT * FROM RMS_RECIPE WHERE EQUIPMENT_CLASS_ID = @equipmentClassId";
        var rows = await QueryAsync<RecipeRow>(sql, new { equipmentClassId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Recipe>().ToList();
    }

    public async Task<IReadOnlyList<Recipe>> GetByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM RMS_RECIPE WHERE APPROVAL_STATE = @state";
        var rows = await QueryAsync<RecipeRow>(sql, new { state = state.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Recipe>().ToList();
    }

    public async Task<int> GetCountByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM RMS_RECIPE WHERE APPROVAL_STATE = @state";
        return await CountAsync(sql, new { state = state.ToString() }, ct);
    }

    public async Task AddAsync(Recipe recipe, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO RMS_RECIPE
            (RECIPE_ID, RECIPE_NAME, DESCRIPTION, EQUIPMENT_CLASS_ID, VERSION, APPROVAL_STATE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@RecipeId, @RecipeName, @Description, @EquipmentClassId, @Version, @ApprovalState,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, RecipeRow.FromDomain(recipe), ct);
    }

    private const string UpdateSql = @"UPDATE RMS_RECIPE SET
            RECIPE_NAME = @RecipeName, DESCRIPTION = @Description, VERSION = @Version,
            APPROVAL_STATE = @ApprovalState, FIRST_APPROVER_ID = @FirstApproverId,
            SECOND_APPROVER_ID = @SecondApproverId, RELEASED_AT = @ReleasedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE RECIPE_ID = @RecipeId";

    public async Task UpdateAsync(Recipe recipe, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, RecipeRow.FromDomain(recipe), ct);
            return;
        }
        // ADR-002 활성: 레시피 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(recipe, ct);
    }

    // 레시피 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 레시피 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    // (DomainEvents가 비면 — 예: 이벤트 없는 필드 변경 — 데이터 행만 쓰고 outbox INSERT는 없다.)
    private async Task PersistWithOutboxAsync(Recipe recipe, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(recipe, user, now)),
        };
        statements.AddRange(OutboxStatements.For(recipe.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        recipe.ClearDomainEvents();
    }

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

        public Recipe ToDomain()
        {
            // 영속 상태(Version/ApprovalState/승인자/ReleasedAt)를 그대로 복원한다 —
            // Create는 Draft/Version=1로 강제해 승인 상태를 유실하므로 Restore를 사용한다.
            if (!Enum.TryParse<RecipeApprovalState>(ApprovalState, out var state)) state = RecipeApprovalState.Draft;
            return Recipe.Restore(
                RecipeId, RecipeName, Description, EquipmentClassId, Version, state,
                FirstApproverId, SecondApproverId, ReleasedAt);
        }

        public static RecipeRow FromDomain(Recipe r) => new()
        {
            RecipeId = r.Id,
            RecipeName = r.RecipeName,
            Description = r.Description,
            EquipmentClassId = r.EquipmentClassId,
            Version = r.Version,
            ApprovalState = r.ApprovalState.ToString(),
            FirstApproverId = r.FirstApproverId,
            SecondApproverId = r.SecondApproverId,
            ReleasedAt = r.ReleasedAt
        };
    }
}
