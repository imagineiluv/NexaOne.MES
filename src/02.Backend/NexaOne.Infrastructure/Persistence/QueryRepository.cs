using Dapper;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public class QueryRepository
{
    private readonly IDatabaseProvider _provider;
    private readonly DatabaseEndpoint _endpoint;

    public QueryRepository(EesDataSource dataSource)
    {
        _provider = dataSource.Provider;
        _endpoint = dataSource.CreateEndpoint();
    }

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var result = await conn.QueryAsync<T>(sql, param).ConfigureAwait(false);
        return result.ToList();
    }

    protected async Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param).ConfigureAwait(false);
    }

    protected async Task<IReadOnlyList<T>> QueryPagedAsync<T>(
        string baseSql,
        int pageIndex,
        int pageSize,
        INexaOneEESDbCapability dialect,
        object? param = null,
        CancellationToken ct = default)
    {
        var pagedSql = dialect.WrapPaged(baseSql, pageIndex * pageSize, pageSize);
        return await QueryAsync<T>(pagedSql, param, ct).ConfigureAwait(false);
    }

    protected async Task<int> CountAsync(
        string countSql,
        object? param = null,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(countSql, param).ConfigureAwait(false);
    }
}
