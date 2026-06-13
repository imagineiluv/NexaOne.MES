using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// 읽기 리포지토리 기반 클래스. ADR-001에 따라 직접 연결을 열지 않고 <see cref="IQueryGateway"/>(단일
/// 게이트웨이)로 위임한다 — 모든 읽기 데이터 접근이 게이트웨이를 통과한다. 하위 클래스의 protected
/// 메서드 시그니처는 불변이므로 44개 리포지토리는 변경 없이 게이트웨이 경유로 전환된다.
/// </summary>
public class QueryRepository
{
    private readonly IQueryGateway _gateway;

    public QueryRepository(EesDataSource dataSource)
        => _gateway = new DapperQueryGateway(dataSource);

    protected Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql, object? param = null, CancellationToken ct = default)
        => _gateway.QueryAsync<T>(sql, param, ct);

    protected Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql, object? param = null, CancellationToken ct = default)
        => _gateway.QueryFirstOrDefaultAsync<T>(sql, param, ct);

    protected async Task<IReadOnlyList<T>> QueryPagedAsync<T>(
        string baseSql,
        string orderBy,
        int pageIndex,
        int pageSize,
        INexaOneEESDbCapability dialect,
        object? param = null,
        CancellationToken ct = default)
    {
        // baseSql은 ORDER BY를 포함하지 않아야 하며, 결정적 페이징을 위해 orderBy(정렬 키)를 전달한다.
        var pagedSql = dialect.WrapPaged(baseSql, orderBy, pageIndex * pageSize, pageSize);
        return await QueryAsync<T>(pagedSql, param, ct).ConfigureAwait(false);
    }

    protected async Task<int> CountAsync(
        string countSql, object? param = null, CancellationToken ct = default)
        => await _gateway.ExecuteScalarAsync<int>(countSql, param, ct).ConfigureAwait(false);

    /// <summary>카탈로그에 등록된 명명 쿼리를 실행한다(ADR-001 — 인라인 SQL 점진 이관용).</summary>
    protected Task<IReadOnlyList<T>> QueryNamedAsync<T>(
        string queryName, object? param = null, CancellationToken ct = default)
        => _gateway.QueryNamedAsync<T>(queryName, param, ct);
}
