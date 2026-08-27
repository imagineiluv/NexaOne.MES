using NexaOne.Infrastructure.Persistence;
using NexaOne.Common;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using System.Data.Common;

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

    public async Task<RecipeParamWriteRecord?> GetWriteByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<WriteRow>(
            WriteSelectSql + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    /// <summary>
    /// 레시피 상태 확인과 parameter 쓰기를 한 SQL 문장으로 결합한다. 서비스가 Draft를 읽은 직후
    /// 다른 요청이 승인 절차를 시작해도 최종 DB 상태가 Draft가 아니면 영향 행 0으로 거부된다.
    /// </summary>
    public async Task<bool> TryAddAsync(
        RecipeParam param, RecipeParamWriteRecord write, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO RMS_RECIPE_PARAM
            (PARAM_ID, RECIPE_ID, PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER, VERSION_NO,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @ParamId, @RecipeId, @ParamName, @ParamValue, @Unit, @SortOrder,
                   @ResultVersion, @ChangedBy, @ChangedAt, @ChangedBy, @ChangedAt
            FROM RMS_RECIPE
            WHERE RECIPE_ID = @RecipeId AND APPROVAL_STATE = 'Draft'
              AND NOT EXISTS (
                  SELECT 1 FROM RMS_RECIPE_PARAM WHERE PARAM_ID = @ParamId)
              AND NOT EXISTS (
                  SELECT 1 FROM RMS_RECIPE_PARAM_COMMAND WHERE IDEMPOTENCY_KEY = @IdempotencyKey)";
        var parameters = WriteParameters(write);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (sql, parameters), (InsertWriteSql, parameters));
        }
        catch (DbException)
        {
            if (await GetWriteByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null)
                return false;
            if (await GetByIdAsync(write.ParamId, ct) is not null)
                return false;
            throw;
        }
    }

    public async Task<bool> TryUpdateAsync(
        RecipeParamWriteRecord write, CancellationToken ct = default)
    {
        const string sql = @"UPDATE RMS_RECIPE_PARAM SET
            PARAM_VALUE = @ParamValue, VERSION_NO = @ResultVersion,
            UPDATED_BY = @ChangedBy, UPDATED_AT = @ChangedAt
            WHERE PARAM_ID = @ParamId
              AND VERSION_NO = @ExpectedVersion
              AND EXISTS (
                  SELECT 1 FROM RMS_RECIPE R
                  WHERE R.RECIPE_ID = RMS_RECIPE_PARAM.RECIPE_ID
                    AND R.APPROVAL_STATE = 'Draft')
              AND NOT EXISTS (
                  SELECT 1 FROM RMS_RECIPE_PARAM_COMMAND C
                  WHERE C.IDEMPOTENCY_KEY = @IdempotencyKey)";
        var parameters = WriteParameters(write);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (sql, parameters), (InsertWriteSql, parameters));
        }
        catch (DbException)
        {
            if (await GetWriteByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
    }

    public async Task<bool> TryDeleteAsync(
        RecipeParamWriteRecord write, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM RMS_RECIPE_PARAM
            WHERE PARAM_ID = @ParamId
              AND VERSION_NO = @ExpectedVersion
              AND EXISTS (
                  SELECT 1 FROM RMS_RECIPE R
                  WHERE R.RECIPE_ID = RMS_RECIPE_PARAM.RECIPE_ID
                    AND R.APPROVAL_STATE = 'Draft')
              AND NOT EXISTS (
                  SELECT 1 FROM RMS_RECIPE_PARAM_COMMAND C
                  WHERE C.IDEMPOTENCY_KEY = @IdempotencyKey)";
        var parameters = WriteParameters(write);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (sql, parameters), (InsertWriteSql, parameters));
        }
        catch (DbException)
        {
            if (await GetWriteByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
    }

    private const string InsertWriteSql = @"INSERT INTO RMS_RECIPE_PARAM_COMMAND
            (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH, PARAM_ID, RECIPE_ID,
             PARAM_NAME, PARAM_VALUE, UNIT, SORT_ORDER, EXPECTED_VERSION, RESULT_VERSION,
             CHANGED_BY, CHANGED_AT)
            VALUES
            (@CommandId, @CommandType, @IdempotencyKey, @RequestHash, @ParamId, @RecipeId,
             @ParamName, @ParamValue, @Unit, @SortOrder, @ExpectedVersion, @ResultVersion,
             @ChangedBy, @ChangedAt)";

    private const string WriteSelectSql = @"SELECT
            COMMAND_ID AS CommandId, COMMAND_TYPE AS CommandType,
            IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
            PARAM_ID AS ParamId, RECIPE_ID AS RecipeId, PARAM_NAME AS ParamName,
            PARAM_VALUE AS ParamValue, UNIT AS Unit, SORT_ORDER AS SortOrder,
            EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion,
            CHANGED_BY AS ChangedBy, CHANGED_AT AS ChangedAt
            FROM RMS_RECIPE_PARAM_COMMAND";

    private static object WriteParameters(RecipeParamWriteRecord write) => new
    {
        write.CommandId,
        write.CommandType,
        write.IdempotencyKey,
        write.RequestHash,
        write.ParamId,
        write.RecipeId,
        write.ParamName,
        write.ParamValue,
        write.Unit,
        write.SortOrder,
        write.ExpectedVersion,
        write.ResultVersion,
        write.ChangedBy,
        write.ChangedAt,
    };

    private sealed class ParamRow
    {
        public string ParamId    { get; set; } = "";
        public string RecipeId   { get; set; } = "";
        public string ParamName  { get; set; } = "";
        public string ParamValue { get; set; } = "";
        public string Unit       { get; set; } = "";
        public int    SortOrder  { get; set; }
        public int    VersionNo  { get; set; } = 1;

        public RecipeParam ToDomain() =>
            RecipeParam.Restore(ParamId, RecipeId, ParamName, ParamValue, Unit, SortOrder, VersionNo);

        public static ParamRow FromDomain(RecipeParam p) => new()
        {
            ParamId    = p.Id,
            RecipeId   = p.RecipeId,
            ParamName  = p.ParamName,
            ParamValue = p.ParamValue,
            Unit       = p.Unit,
            SortOrder  = p.SortOrder,
            VersionNo  = p.Version,
        };
    }

    private sealed class WriteRow
    {
        public string CommandId { get; set; } = "";
        public string CommandType { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ParamId { get; set; } = "";
        public string RecipeId { get; set; } = "";
        public string? ParamName { get; set; }
        public string? ParamValue { get; set; }
        public string? Unit { get; set; }
        public int? SortOrder { get; set; }
        public int? ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ChangedBy { get; set; } = "";
        public DateTime ChangedAt { get; set; }

        public RecipeParamWriteRecord ToRecord() => new(
            CommandId, CommandType, IdempotencyKey, RequestHash, ParamId, RecipeId,
            ParamName, ParamValue, Unit, SortOrder, ExpectedVersion, ResultVersion,
            ChangedBy, ChangedAt);
    }
}
