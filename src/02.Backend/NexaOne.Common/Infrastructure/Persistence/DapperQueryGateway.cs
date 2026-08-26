using Dapper;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// <see cref="IQueryGateway"/>의 Dapper 구현(ADR-001). 공급자(<see cref="IDatabaseProvider"/>) 연결을
/// 단일 지점에서 열고 Dapper로 실행한다. 명명 쿼리는 카탈로그로 해석한다. 읽기 경로의 chokepoint.
/// </summary>
public sealed class DapperQueryGateway : IQueryGateway
{
    private readonly IDatabaseProvider _provider;
    private readonly DatabaseEndpoint _endpoint;
    private readonly IQueryCatalog _catalog;

    public DapperQueryGateway(EesDataSource dataSource, IQueryCatalog? catalog = null)
    {
        _provider = dataSource.Provider;
        _endpoint = dataSource.CreateEndpoint();
        _catalog = catalog ?? InMemoryQueryCatalog.Shared;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var result = await conn.QueryAsync<T>(sql, param).ConfigureAwait(false);
        return result.ToList();
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param).ConfigureAwait(false);
    }

    public async Task<TScalar?> ExecuteScalarAsync<TScalar>(string sql, object? param = null, CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<TScalar>(sql, param).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<T>> QueryNamedAsync<T>(string queryName, object? param = null, CancellationToken ct = default)
        => QueryAsync<T>(_catalog.Resolve(queryName), param, ct);
}
