namespace NexaOne.Common.Caching;

/// <summary>
/// 애플리케이션 캐시 추상화 — 읽기 빈도가 높고 갱신이 드문 마스터/조회 데이터를 캐시한다.
/// 현재 인메모리 구현을 사용하며 구현의 선택과 수명은 호스트가 소유한다.
/// 캐시 대상·무효화는 기능별로 개발자가 선택한다(쓰기 시 해당 키를 Remove로 무효화).
/// </summary>
public interface ICacheService
{
    /// <summary>키가 있으면 캐시 값을, 없으면 factory로 생성 후 반환한다(TTL 미지정 시 구현 기본값).
    /// 무효화 이전에 시작한 조회는 그 결과를 반환할 수 있으나 이후 캐시에 다시 게시하지 않는다.
    /// ct는 조회 대기를 취소하며 factory 자체의 취소/수명은 호출자가 소유한다. 동시 miss의 병합은 보장하지 않는다.</summary>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>키와 진행 중 조회의 게시 권한을 무효화한다(쓰기 후 호출). 없는 키는 무시.
    /// 인메모리 구현은 이미 완료된 쓰기를 가리지 않도록 ct가 취소되어도 로컬 무효화를 수행한다.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}
