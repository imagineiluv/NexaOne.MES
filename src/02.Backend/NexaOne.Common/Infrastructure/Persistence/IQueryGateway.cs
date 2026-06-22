namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// 모든 읽기 데이터 접근의 단일 게이트웨이(ADR-001). 연결 획득·실행·명명 쿼리 해석·관측을 한 지점에 집중해
/// 비전의 "모든 데이터 접근이 Query Engine을 통과"를 리포지토리 시그니처 변경 없이 달성한다.
/// 타입 매핑은 Dapper를 그대로 사용한다.
/// </summary>
public interface IQueryGateway
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<TScalar?> ExecuteScalarAsync<TScalar>(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>카탈로그에 등록된 명명 쿼리를 실행한다(ADR-001 점진 이관).</summary>
    Task<IReadOnlyList<T>> QueryNamedAsync<T>(string queryName, object? param = null, CancellationToken ct = default);
}
