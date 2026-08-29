using FluentAssertions;
using Microsoft.Data.SqlClient;
using NexaOne.Application.Query;
using System.Text.RegularExpressions;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>MSSQL 방언 실서버 파싱 검증 — DialectParityTests(정적 계약 패리티)가 못 잡는 "MSSQL 문법 오류"를
/// 실제 SQL Server 파서로 잠근다(예: mssql 파일에 LIMIT 혼입, MERGE 세미콜론 누락, TOP 위치 오류).
/// SET PARSEONLY ON은 파싱만 수행(객체/파라미터 바인딩·실행 없음)이라 대상 DB 스키마와 무관하게 안전하다.
///
/// env-gate: NEXAONE_MSSQL_TEST_CONN(연결 문자열) 미설정이면 소프트 스킵(단언 없이 통과) — CI/로컬 SQLite
/// 환경에서 무해하고, MSSQL 접근 가능한 환경(자사 dev 등)에서 변수만 세팅하면 전 쿼리를 실검증한다.
/// 접속 정보는 저장소에 절대 넣지 않는다(환경변수 전용).</summary>
[Trait("Category", "MssqlContract")]
public sealed class MssqlDialectSyntaxTests
{
    public static IEnumerable<object[]> QueryTrees() => new[]
    {
        new object[] { "src/00.Main/NexaOne.Server/config/db/queries" },        // 공개 게이트웨이 트리
        new object[] { "src/00.Main/NexaOne.Server/config/db/queries-auth" },   // 격리 인증 트리
    };

    [Theory]
    [MemberData(nameof(QueryTrees))]
    public void Every_mssql_query_parses_on_real_sql_server(string treeRelativePath)
    {
        var connString = MssqlContractDatabase.GetConnectionStringOrThrowIfRequired();
        if (string.IsNullOrWhiteSpace(connString))
            return;   // 소프트 스킵 — MSSQL 미가용 환경(로컬 SQLite/CI). 게이트 변수 설정 시에만 실검증.

        var registry = FileQueryRegistry.Load(
            "mssql",
            RepositorySource.GetDirectory(treeRelativePath));
        registry.Ids.Should().NotBeEmpty($"'{treeRelativePath}/mssql' 로드 실패 시 공허 통과 금지");

        using var conn = new SqlConnection(connString);
        conn.Open();

        var failures = new List<string>();
        foreach (var id in registry.Ids.OrderBy(x => x, StringComparer.Ordinal))
        {
            registry.TryGet(id, out var def);
            try
            {
                using var cmd = conn.CreateCommand();
                // PARSEONLY는 배치 단위 토글 — 쿼리 SQL을 같은 배치에 넣어 파서만 태운다(실행 없음).
                // SQL Server still resolves variable names while parsing, so declare each Dapper
                // placeholder with a permissive type solely for this syntax contract.
                var sql = DeclareParseOnlyParameters(def!.Sql);
                cmd.CommandText = "SET PARSEONLY ON;\n" + sql + "\nSET PARSEONLY OFF;";
                cmd.ExecuteNonQuery();
            }
            catch (SqlException ex)
            {
                failures.Add($"{id}: {ex.Message.ReplaceLineEndings(" ")}");
            }
        }

        failures.Should().BeEmpty(
            $"[{treeRelativePath}] MSSQL 파서가 거부한 쿼리(방언 문법 오류): {string.Join(" | ", failures)}");
    }

    private static string DeclareParseOnlyParameters(string sql)
    {
        var names = Regex.Matches(sql, @"(?<!@)@[A-Za-z_][A-Za-z0-9_]*")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
            return sql;

        var declarations = string.Join(
            Environment.NewLine,
            names.Select(name => $"DECLARE {name} nvarchar(4000);"));
        return declarations + Environment.NewLine + sql;
    }

}
