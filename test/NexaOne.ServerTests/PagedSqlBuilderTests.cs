using FluentAssertions;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>제네릭 서버 페이징 SQL 빌더(순수) — 방언별 페이징 절 부착·후행 ORDER BY 분리(count에서 제거)·
/// 자체 상한 보유 쿼리 거부(폴백 신호)를 검증한다. 실제 실행은 GatewayPagedQueryTests가 담당.</summary>
public sealed class PagedSqlBuilderTests
{
    [Fact]
    public void Sqlite_appends_limit_offset_and_count_strips_order_by()
    {
        var ok = PagedSqlBuilder.TryBuild(
            "SELECT PLANT_ID, PLANT_NAME FROM MDM_PLANT WHERE (@plantId IS NULL OR PLANT_ID = @plantId) ORDER BY PLANT_ID DESC",
            "Sqlite", out var page, out var count);

        ok.Should().BeTrue();
        page.Should().Contain("ORDER BY PLANT_ID DESC").And.Contain("LIMIT @__limit OFFSET @__offset");
        count.Should().StartWith("SELECT COUNT(*) FROM (").And.NotContain("ORDER BY", "count 서브쿼리엔 정렬이 없어야 한다(MSSQL 불법·무의미)");
    }

    [Fact]
    public void Mssql_uses_offset_fetch_and_adds_null_order_when_missing()
    {
        // 정렬 있는 쿼리 — 원 정렬 유지 + OFFSET-FETCH.
        PagedSqlBuilder.TryBuild("SELECT A FROM T ORDER BY A", "MsSql", out var page1, out _).Should().BeTrue();
        page1.Should().Contain("ORDER BY A").And.Contain("OFFSET @__offset ROWS FETCH NEXT @__limit ROWS ONLY");

        // 무정렬 쿼리 — OFFSET-FETCH는 ORDER BY 필수라 (SELECT NULL) 부착.
        PagedSqlBuilder.TryBuild("SELECT A FROM T", "MsSql", out var page2, out _).Should().BeTrue();
        page2.Should().Contain("ORDER BY (SELECT NULL)");
    }

    [Fact]
    public void Subquery_order_by_is_not_split()
    {
        // 서브쿼리 내부 ORDER BY(깊이>0)는 후행 정렬로 오인하면 안 된다.
        var ok = PagedSqlBuilder.TryBuild(
            "SELECT * FROM (SELECT A FROM T ORDER BY A LIMIT 5) s WHERE s.A > @min",
            "Sqlite", out var page, out var count);

        // 서브쿼리 LIMIT은 깊이>0이라 자체 상한으로 치지 않는다 — 페이징 가능해야 한다.
        ok.Should().BeTrue();
        count.Should().Contain("(SELECT A FROM T ORDER BY A LIMIT 5)", "서브쿼리 원문은 보존");
        page.Should().EndWith("LIMIT @__limit OFFSET @__offset");
    }

    [Theory]
    [InlineData("SELECT A FROM T ORDER BY A LIMIT 500")]                    // sqlite 자체 상한
    [InlineData("SELECT A FROM T ORDER BY A OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY")] // mssql 자체 상한
    [InlineData("SELECT A FROM T LIMIT @limit OFFSET @offset")]             // CountQueryId 화면의 수동 페이징
    [InlineData("SELECT TOP 10 A FROM T")]                                  // mssql TOP
    public void Queries_with_own_limit_are_rejected(string sql)
        => PagedSqlBuilder.TryBuild(sql, "Sqlite", out _, out _)
            .Should().BeFalse("자체 상한 보유 쿼리는 이중 페이징 불가 — 호출측 전량 폴백");

    [Fact]
    public void Line_comments_do_not_break_detection()
    {
        var ok = PagedSqlBuilder.TryBuild(
            "-- limit 관련 주석은 무시돼야 한다\nSELECT A FROM T ORDER BY A",
            "Sqlite", out var page, out _);
        ok.Should().BeTrue();
        page.Should().Contain("LIMIT @__limit");
    }
}
