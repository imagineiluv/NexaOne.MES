namespace NexaOne.Application.Query;

/// <summary>파일 기반 쿼리 레지스트리의 단일 등록 쿼리. ID로 식별되며 SQL은 @파라미터 바인딩을 쓴다(원시 보간 금지).</summary>
/// <param name="Id">쿼리 식별자(UI가 이 ID로 호출). 파일 전역에서 고유해야 한다.</param>
/// <param name="Sql">파라미터화된 SQL 문(@param). 선택 필터는 (@p IS NULL OR COL = @p) 패턴 권장.</param>
/// <param name="Source">진단용 — 정의가 로드된 파일명.</param>
/// <param name="RequiredPermission">실행에 필요한 권한(module:action). null/빈값이면 인증만으로 실행 가능.
/// 게이트웨이가 토큰의 permission 클레임(또는 전체권한 "*")과 대조해 집행한다.</param>
/// <param name="IsWrite">쓰기 쿼리(INSERT/UPDATE/DELETE) 여부. XML <c>kind="write"</c>로 선언한다.
/// 읽기(false)는 <c>POST /query/{id}</c>로 SELECT를, 쓰기(true)는 <c>POST /command/{id}</c>로 실행되며
/// 게이트웨이가 종류 불일치(읽기를 command로, 쓰기를 query로)를 거부한다. 쓰기 쿼리는 감사 컬럼을
/// <c>@currentUser</c>·<c>@utcNow</c>(게이트웨이가 토큰·UTC로 주입, 방언 무관 바운드 파라미터)로 채울 수 있다.</param>
public sealed record QueryDefinition(
    string Id, string Sql, string Source, string? RequiredPermission = null, bool IsWrite = false);
