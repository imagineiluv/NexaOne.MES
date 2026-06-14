namespace NexaOne.Application.Query;

/// <summary>
/// 파일 기반 쿼리 레지스트리 — 시작 시 방언 폴더의 쿼리 XML을 로드해 ID로 조회 가능하게 한다.
/// UI/게이트웨이가 쿼리 ID로 SQL을 얻어 파라미터 바인딩으로 실행한다(저코드 경로).
/// 컴파일된 타입드 리포지토리(고코드 경로)와 공존하며 기능별로 개발자가 선택해 사용한다.
/// </summary>
public interface IQueryRegistry
{
    /// <summary>쿼리 ID로 SQL을 조회한다. 미등록이면 false.</summary>
    bool TryGet(string queryId, out string sql);

    /// <summary>등록된 모든 쿼리 ID(진단/관리용).</summary>
    IReadOnlyCollection<string> Ids { get; }

    /// <summary>로드된 방언(mssql/sqlite) — 진단용.</summary>
    string Dialect { get; }
}
