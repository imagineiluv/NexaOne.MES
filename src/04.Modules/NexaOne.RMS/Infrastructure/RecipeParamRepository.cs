using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Infrastructure;

public sealed class RecipeParamRepository : QueryRepository, IRecipeParamRepository
{
    private readonly ServiceObjectProcessor _processor;

    public RecipeParamRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<RecipeParam>> GetByRecipeAsync(string recipeId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM RMS_RECIPE_PARAM
            WHERE RECIPE_ID = @recipeId ORDER BY SORT_ORDER";
        var rows = await QueryAsync<ParamRow>(sql, new { recipeId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<RecipeParam?> GetByIdAsync(string paramId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM RMS_RECIPE_PARAM WHERE PARAM_ID = @paramId";
        var row = await QueryFirstOrDefaultAsync<ParamRow>(sql, new { paramId }, ct);
        return row?.ToDomain();
    }

    public async Task AddAsync(RecipeParam param, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO RMS_RECIPE_PARAM
            (PARAM_ID, RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER)
            VALUES (@ParamId, @RecipeId, @ParamName, @ParamValue, @Unit, @SortOrder)";
        await _processor.InsertAsync(sql, ParamRow.FromDomain(param), ct);
    }

    public async Task UpdateAsync(RecipeParam param, CancellationToken ct = default)
    {
        const string sql = @"UPDATE RMS_RECIPE_PARAM SET
            PARAM_NAME = @ParamName, PARAM_VALUE = @ParamValue,
            UNIT = @Unit, SORT_ORDER = @SortOrder
            WHERE PARAM_ID = @ParamId";
        await _processor.UpdateAsync(sql, ParamRow.FromDomain(param), ct);
    }

    public async Task DeleteAsync(string paramId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM RMS_RECIPE_PARAM WHERE PARAM_ID = @paramId";
        await _processor.DeleteAsync(sql, new { paramId }, ct);
    }

    private sealed class ParamRow
    {
        public string ParamId    { get; set; } = "";
        public string RecipeId   { get; set; } = "";
        public string ParamName  { get; set; } = "";
        public string ParamValue { get; set; } = "";
        public string Unit       { get; set; } = "";
        public int    SortOrder  { get; set; }

        public RecipeParam ToDomain() =>
            RecipeParam.Restore(ParamId, RecipeId, ParamName, ParamValue, Unit, SortOrder);

        public static ParamRow FromDomain(RecipeParam p) => new()
        {
            ParamId    = p.Id,
            RecipeId   = p.RecipeId,
            ParamName  = p.ParamName,
            ParamValue = p.ParamValue,
            Unit       = p.Unit,
            SortOrder  = p.SortOrder
        };
    }
}
