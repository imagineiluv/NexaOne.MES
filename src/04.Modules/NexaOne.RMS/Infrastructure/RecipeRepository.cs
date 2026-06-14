using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Infrastructure;

public sealed class RecipeRepository : QueryRepository, IRecipeRepository
{
    private readonly ServiceObjectProcessor _processor;

    public RecipeRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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

    public async Task UpdateAsync(Recipe recipe, CancellationToken ct = default)
    {
        const string sql = @"UPDATE RMS_RECIPE SET
            RECIPE_NAME = @RecipeName, DESCRIPTION = @Description, VERSION = @Version,
            APPROVAL_STATE = @ApprovalState, FIRST_APPROVER_ID = @FirstApproverId,
            SECOND_APPROVER_ID = @SecondApproverId, RELEASED_AT = @ReleasedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE RECIPE_ID = @RecipeId";
        await _processor.UpdateAsync(sql, RecipeRow.FromDomain(recipe), ct);
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
