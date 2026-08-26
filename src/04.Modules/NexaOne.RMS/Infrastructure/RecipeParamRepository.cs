using NexaOne.Infrastructure.Persistence;
using NexaOne.Common;
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

    /// <summary>
    /// 레시피 상태 확인과 parameter 쓰기를 한 SQL 문장으로 결합한다. 서비스가 Draft를 읽은 직후
    /// 다른 요청이 Release해도 최종 DB 상태가 Released이면 영향 행 0으로 거부된다.
    /// </summary>
    public Task<bool> TryAddIfRecipeEditableAsync(RecipeParam param, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO RMS_RECIPE_PARAM
            (PARAM_ID, RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER)
            SELECT @ParamId, @RecipeId, @ParamName, @ParamValue, @Unit, @SortOrder
            FROM RMS_RECIPE
            WHERE RECIPE_ID = @RecipeId AND APPROVAL_STATE <> 'Released'";
        return _processor.ExecuteGuardedManyAsync(ct, (sql, ParamRow.FromDomain(param)));
    }

    public Task<bool> TryUpdateIfRecipeEditableAsync(RecipeParam param, CancellationToken ct = default)
    {
        const string sql = @"UPDATE RMS_RECIPE_PARAM SET
            PARAM_NAME = @ParamName, PARAM_VALUE = @ParamValue,
            UNIT = @Unit, SORT_ORDER = @SortOrder,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PARAM_ID = @ParamId
              AND EXISTS (
                  SELECT 1 FROM RMS_RECIPE R
                  WHERE R.RECIPE_ID = RMS_RECIPE_PARAM.RECIPE_ID
                    AND R.APPROVAL_STATE <> 'Released')";
        var row = ParamRow.FromDomain(param);
        return _processor.ExecuteGuardedManyAsync(ct, (sql, new
        {
            row.ParamId,
            row.ParamName,
            row.ParamValue,
            row.Unit,
            row.SortOrder,
            UpdatedBy = CurrentUserContext.UserId ?? "SYSTEM",
            UpdatedAt = DateTime.UtcNow,
        }));
    }

    public Task<bool> TryDeleteIfRecipeEditableAsync(string paramId, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM RMS_RECIPE_PARAM
            WHERE PARAM_ID = @paramId
              AND EXISTS (
                  SELECT 1 FROM RMS_RECIPE R
                  WHERE R.RECIPE_ID = RMS_RECIPE_PARAM.RECIPE_ID
                    AND R.APPROVAL_STATE <> 'Released')";
        return _processor.ExecuteGuardedManyAsync(ct, (sql, new { paramId }));
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
