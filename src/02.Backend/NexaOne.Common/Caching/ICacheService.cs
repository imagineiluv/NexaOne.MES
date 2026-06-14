namespace NexaOne.Common.Caching;

/// <summary>
/// 애플리케이션 캐시 추상화 — 읽기 빈도가 높고 갱신이 드문 마스터/조회 데이터를 캐시한다.
/// 구현은 구성(Cache:Provider)에 따라 인메모리(기본) 또는 Redis로 전환된다.
/// 캐시 대상·무효화는 기능별로 개발자가 선택한다(쓰기 시 해당 키를 Remove로 무효화).
/// </summary>
public interface ICacheService
{
    /// <summary>키가 있으면 캐시 값을, 없으면 factory로 생성·저장 후 반환한다(TTL 미지정 시 구현 기본값).</summary>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>키를 무효화한다(쓰기 후 호출). 없는 키는 무시.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}
