using System.Text.RegularExpressions;

namespace NexaOne.Server.Gateway;

/// <summary>등록 read 쿼리를 원문 무수정으로 서버 페이징(page+count) SQL로 감싸는 순수 빌더(제네릭 LIMIT 소급).
/// 240여 명명 쿼리에 @limit/@offset·count 짝을 일일이 추가하는 대신, 게이트웨이가 방언별 페이징 절을 부착한다.
/// <para>규칙: (1)최상위(괄호 깊이 0) 후행 ORDER BY를 분리해 count에서는 제거(MSSQL 서브쿼리 ORDER BY 불법),
/// page에서는 유지. (2)자체 상한 보유 쿼리(top-level LIMIT/OFFSET/FETCH, SELECT TOP)는 페이징 불가(false) —
/// 호출측(MetaScreen)이 기존 전량 경로로 폴백한다. (3)페이징 파라미터는 @__limit/@__offset(기존 @limit 관례와
/// 충돌 방지). MSSQL OFFSET-FETCH는 ORDER BY가 필수라 무순서 쿼리엔 ORDER BY (SELECT NULL)을 붙인다.</para></summary>
public static partial class PagedSqlBuilder
{
    /// <summary>페이징 SQL 생성. 자체 상한 보유 등 감쌀 수 없는 쿼리면 false(호출측 폴백).</summary>
    public static bool TryBuild(string sql, string provider, out string pageSql, out string countSql)
    {
        pageSql = countSql = string.Empty;
        var body = StripLineComments(sql).Trim();
        if (body.Length == 0) return false;

        // 자체 상한 보유(top-level LIMIT/OFFSET/FETCH, SELECT TOP) — 이중 페이징 불가.
        if (HasTopLevelKeyword(body, "LIMIT") || HasTopLevelKeyword(body, "OFFSET")
            || HasTopLevelKeyword(body, "FETCH") || SelectTop().IsMatch(body))
            return false;

        // 최상위 후행 ORDER BY 분리 — count는 core만(정렬 무의미+MSSQL 불법), page는 정렬 유지.
        var orderIdx = LastTopLevelOrderBy(body);
        var core = orderIdx < 0 ? body : body[..orderIdx].TrimEnd();
        var orderBy = orderIdx < 0 ? "" : body[orderIdx..].Trim();

        countSql = $"SELECT COUNT(*) FROM (\n{core}\n) AS q";

        var isMsSql = provider.Contains("mssql", StringComparison.OrdinalIgnoreCase)
                   || provider.Contains("sqlserver", StringComparison.OrdinalIgnoreCase);
        pageSql = isMsSql
            // MSSQL OFFSET-FETCH는 ORDER BY 필수 — 무순서 쿼리는 (SELECT NULL)로 형식 충족(임의 순서 명시).
            ? $"{core}\n{(orderBy.Length > 0 ? orderBy : "ORDER BY (SELECT NULL)")}\nOFFSET @__offset ROWS FETCH NEXT @__limit ROWS ONLY"
            : $"{core}\n{orderBy}\nLIMIT @__limit OFFSET @__offset".Replace("\n\n", "\n");
        return true;
    }

    // 최상위 깊이(괄호 0, 문자열 밖)의 마지막 ORDER BY 시작 인덱스(-1=없음). 서브쿼리 내부 ORDER BY는 무시.
    private static int LastTopLevelOrderBy(string sql)
    {
        var last = -1;
        var depth = 0;
        var inString = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (inString) { if (ch == '\'') inString = false; continue; }
            switch (ch)
            {
                case '\'': inString = true; continue;
                case '(': depth++; continue;
                case ')': depth--; continue;
            }
            if (depth != 0) continue;
            if ((ch is 'o' or 'O') && IsWordBoundary(sql, i) && MatchesKeyword(sql, i, "ORDER"))
            {
                var j = SkipWs(sql, i + 5);
                if (MatchesKeyword(sql, j, "BY")) last = i;
            }
        }
        return last;
    }

    private static bool HasTopLevelKeyword(string sql, string keyword)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (inString) { if (ch == '\'') inString = false; continue; }
            switch (ch)
            {
                case '\'': inString = true; continue;
                case '(': depth++; continue;
                case ')': depth--; continue;
            }
            if (depth != 0) continue;
            if (char.ToUpperInvariant(ch) == keyword[0] && IsWordBoundary(sql, i) && MatchesKeyword(sql, i, keyword))
                return true;
        }
        return false;
    }

    private static bool MatchesKeyword(string sql, int idx, string keyword)
        => idx + keyword.Length <= sql.Length
        && string.Compare(sql, idx, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (idx + keyword.Length == sql.Length || !char.IsLetterOrDigit(sql[idx + keyword.Length]));

    private static bool IsWordBoundary(string sql, int idx)
        => idx == 0 || (!char.IsLetterOrDigit(sql[idx - 1]) && sql[idx - 1] != '_' && sql[idx - 1] != '@');

    private static int SkipWs(string sql, int idx)
    {
        while (idx < sql.Length && char.IsWhiteSpace(sql[idx])) idx++;
        return idx;
    }

    private static string StripLineComments(string sql)
        => LineComment().Replace(sql, "");

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^\s*SELECT\s+(DISTINCT\s+)?TOP\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectTop();
}
