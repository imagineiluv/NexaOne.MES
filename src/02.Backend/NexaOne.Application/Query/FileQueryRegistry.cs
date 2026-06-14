using System.Xml.Linq;

namespace NexaOne.Application.Query;

/// <summary>
/// db/queries/{dialect}/*.xml 의 쿼리 정의를 시작 시 1회 로드하는 <see cref="IQueryRegistry"/> 구현.
/// 파일 구조는 &lt;query id&gt;&lt;statement&gt; 모양이되, SQL은 @파라미터 바인딩을 쓴다
/// (원시 문자열 보간/조건 템플릿 비사용 — 주입 방지). 방언별 폴더(mssql/sqlite)로 분리한다.
/// </summary>
/// <remarks>
/// 디렉터리 해석: 명시 override → 없으면 AppContext.BaseDirectory에서 상위로 올라가며 db/queries를 찾는다
/// (db/migrations와 동일 규약 — 테스트·배포 양쪽에서 발견). 중복 ID는 시작 시 즉시 실패(fail-fast)시킨다.
/// </remarks>
public sealed class FileQueryRegistry : IQueryRegistry
{
    private readonly Dictionary<string, QueryDefinition> _queries;

    public string Dialect { get; }
    public IReadOnlyCollection<string> Ids => _queries.Keys;

    private FileQueryRegistry(string dialect, Dictionary<string, QueryDefinition> queries)
    {
        Dialect = dialect;
        _queries = queries;
    }

    public bool TryGet(string queryId, out QueryDefinition? definition)
        => _queries.TryGetValue(queryId, out definition);

    /// <summary>방언 폴더의 쿼리 XML을 모두 로드한다. 폴더가 없으면 빈 레지스트리를 반환(쿼리 게이트웨이 미사용 환경 허용).</summary>
    public static FileQueryRegistry Load(string dialect, string? overrideDirectory = null)
    {
        var baseDir = ResolveQueriesRoot(overrideDirectory);
        var queries = new Dictionary<string, QueryDefinition>(StringComparer.Ordinal);
        if (baseDir is null)
            return new FileQueryRegistry(dialect, queries);

        var dialectDir = Path.Combine(baseDir, dialect);
        if (!Directory.Exists(dialectDir))
            return new FileQueryRegistry(dialect, queries);

        foreach (var file in Directory.GetFiles(dialectDir, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var def in ParseFile(file))
            {
                if (!queries.TryAdd(def.Id, def))
                    throw new InvalidOperationException(
                        $"Duplicate query id '{def.Id}' (dialect={dialect}, file={Path.GetFileName(file)}). 쿼리 ID는 방언 전역에서 고유해야 한다.");
            }
        }
        return new FileQueryRegistry(dialect, queries);
    }

    private static IEnumerable<QueryDefinition> ParseFile(string path)
    {
        var source = Path.GetFileName(path);
        var doc = XDocument.Load(path);
        foreach (var q in doc.Descendants("query"))
        {
            var id = (string?)q.Attribute("id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            // <statement> 자식이 있으면 그 값을, 없으면 query 요소 값을 SQL로 본다(CDATA/텍스트 모두 .Value로 취득).
            var statement = q.Element("statement");
            var sql = (statement?.Value ?? q.Value).Trim();
            if (sql.Length == 0) continue;
            // requiredPermission 속성(선택) — 비면 인증만으로 실행 가능.
            var perm = ((string?)q.Attribute("requiredPermission"))?.Trim();
            // kind 속성(선택, 기본 read) — "write"면 쓰기 쿼리(INSERT/UPDATE/DELETE), command 게이트웨이 전용.
            var isWrite = string.Equals(((string?)q.Attribute("kind"))?.Trim(), "write", StringComparison.OrdinalIgnoreCase);
            // 쓰기 쿼리는 requiredPermission 선언이 필수다(시작 시 fail-fast). 권한 없는 쓰기 쿼리가 등록되면
            // command 게이트웨이가 인증만으로 임의 INSERT/UPDATE/DELETE를 허용하게 되므로(권한 누락=무방비 쓰기) 차단한다.
            if (isWrite && string.IsNullOrEmpty(perm))
                throw new InvalidOperationException(
                    $"Write query '{id!.Trim()}' (file={source}) must declare requiredPermission. 쓰기 쿼리는 권한 선언이 필수다.");
            yield return new QueryDefinition(
                id!.Trim(), sql, source, string.IsNullOrEmpty(perm) ? null : perm, isWrite);
        }
    }

    // override가 있으면 그 디렉터리를, 없으면 BaseDirectory에서 상위로 db/queries를 탐색한다.
    private static string? ResolveQueriesRoot(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Directory.Exists(overrideDirectory) ? overrideDirectory : null;

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "db", "queries");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
