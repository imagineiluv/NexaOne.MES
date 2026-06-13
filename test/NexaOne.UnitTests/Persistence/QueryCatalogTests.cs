using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Persistence;

/// <summary>ADR-001 — 명명 쿼리 카탈로그(등록/해석/대소문자/검증)를 검증. 게이트웨이 실행 경로는 통합 영역.</summary>
public sealed class QueryCatalogTests
{
    [Fact]
    public void Register_and_resolve_roundtrip()
    {
        var catalog = new InMemoryQueryCatalog();
        catalog.Register("mdm.equipment.byId", "SELECT * FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID=@id");

        catalog.Resolve("mdm.equipment.byId").Should().Contain("MDM_EQUIPMENT");
        catalog.TryResolve("mdm.equipment.byId", out var sql).Should().BeTrue();
        sql.Should().NotBeEmpty();
    }

    [Fact]
    public void Resolve_unknown_throws()
    {
        var catalog = new InMemoryQueryCatalog();
        var act = () => catalog.Resolve("nope");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void TryResolve_unknown_returns_false()
    {
        var catalog = new InMemoryQueryCatalog();
        catalog.TryResolve("nope", out var sql).Should().BeFalse();
        sql.Should().BeEmpty();
    }

    [Fact]
    public void Register_is_case_insensitive_and_overwrites()
    {
        var catalog = new InMemoryQueryCatalog();
        catalog.Register("Q1", "SELECT 1");
        catalog.Register("q1", "SELECT 2");
        catalog.Resolve("Q1").Should().Be("SELECT 2", "대소문자 무시 키로 덮어쓴다");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_blank_name(string? name)
    {
        var catalog = new InMemoryQueryCatalog();
        var act = () => catalog.Register(name!, "SELECT 1");
        act.Should().Throw<ArgumentException>();
    }
}
