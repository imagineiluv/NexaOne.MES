namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// 명명 쿼리 레지스트리(ADR-001) — 키→SQL. 리포지토리에 흩어진 인라인 <c>const string</c> SQL을
/// 중앙 카탈로그로 점진 이관하기 위한 표면. 인라인 SQL도 계속 허용하며, 등록된 키는 이름으로 조회한다.
/// </summary>
public interface IQueryCatalog
{
    void Register(string name, string sql);
    bool TryResolve(string name, out string sql);
    string Resolve(string name);
}
