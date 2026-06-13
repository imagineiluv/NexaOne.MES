using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Persistence;

/// <summary>
/// SqliteEesDbCapability가 생성하는 SQLite 방언 SQL이 실제 SQLite에서 유효하고 의미가 맞는지 검증한다
/// (BuildUpsertSql=ON CONFLICT 업서트, WrapPaged=LIMIT/OFFSET). 리포지토리 포팅의 기반 보증.
/// </summary>
public sealed class SqliteEesDbCapabilityTests
{
    private readonly SqliteEesDbCapability _cap = new();

    private static SqliteConnection OpenWithTable()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        conn.Execute("CREATE TABLE T (K TEXT PRIMARY KEY, V TEXT, N INTEGER);");
        return conn;
    }

    [Fact]
    public void BuildUpsertSql_inserts_then_updates_on_conflict()
    {
        using var conn = OpenWithTable();
        var sql = _cap.BuildUpsertSql("T", new[] { "K" }, new[] { "V", "N" });

        conn.Execute(sql, new { K = "k1", V = "first", N = 1 });
        conn.Execute(sql, new { K = "k1", V = "second", N = 2 });   // 같은 키 → UPDATE

        var rows = conn.QuerySingle<int>("SELECT COUNT(*) FROM T WHERE K = 'k1'");
        var v = conn.QuerySingle<string>("SELECT V FROM T WHERE K = 'k1'");
        rows.Should().Be(1, "ON CONFLICT 업서트로 행이 중복 생성되지 않아야 한다");
        v.Should().Be("second", "충돌 시 데이터 컬럼이 갱신되어야 한다");
    }

    [Fact]
    public void BuildUpsertSql_with_DynamicParameters_executes_upsert()
    {
        // 리포지토리가 ServiceObjectProcessor.ExecuteAsync(raw)로 실제 사용하는 경로 검증:
        // BuildUpsertSql(@COLUMN_NAME) + DynamicParameters(컬럼명 키)를 Dapper로 실행한다.
        using var conn = OpenWithTable();
        var sql = _cap.BuildUpsertSql("T", new[] { "K" }, new[] { "V", "N" });

        var p1 = new DynamicParameters();
        p1.Add("K", "k1"); p1.Add("V", "first"); p1.Add("N", 1);
        conn.Execute(sql, p1);

        var p2 = new DynamicParameters();
        p2.Add("K", "k1"); p2.Add("V", "second"); p2.Add("N", 2);
        conn.Execute(sql, p2);   // 충돌 → UPDATE

        conn.QuerySingle<int>("SELECT COUNT(*) FROM T").Should().Be(1);
        conn.QuerySingle<string>("SELECT V FROM T WHERE K = 'k1'").Should().Be("second");
    }

    [Fact]
    public void WrapPaged_applies_limit_and_offset_with_ordering()
    {
        using var conn = OpenWithTable();
        for (var i = 1; i <= 5; i++)
            conn.Execute("INSERT INTO T (K, V, N) VALUES (@K, @V, @N)", new { K = $"k{i}", V = $"v{i}", N = i });

        var paged = _cap.WrapPaged("SELECT K FROM T", "N DESC", offset: 1, limit: 2);
        var keys = conn.Query<string>(paged).ToList();

        keys.Should().Equal("k4", "k3");   // N DESC → 5,4,3,2,1 / offset 1 skip k5 / limit 2 → k4,k3
    }

    [Fact]
    public void WrapPaged_without_orderby_still_limits()
    {
        using var conn = OpenWithTable();
        for (var i = 1; i <= 5; i++)
            conn.Execute("INSERT INTO T (K, V, N) VALUES (@K, @V, @N)", new { K = $"k{i}", V = $"v{i}", N = i });

        var paged = _cap.WrapPaged("SELECT K FROM T", orderBy: "", offset: 0, limit: 3);
        conn.Query<string>(paged).Should().HaveCount(3);
    }
}
