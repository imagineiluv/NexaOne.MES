using NexaOne.Application.Query;

namespace NexaOne.UnitTests.Query;

/// <summary>파일 기반 쿼리 레지스트리 로딩/조회/중복검출 검증. 임시 디렉터리에 방언 폴더+XML을 만들어 로드한다.</summary>
public sealed class FileQueryRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nexa-queries-{Guid.NewGuid():N}");

    private string WriteDialectFile(string dialect, string fileName, string xml)
    {
        var dir = Path.Combine(_root, dialect);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Load_parses_queries_and_resolves_by_id()
    {
        WriteDialectFile("sqlite", "MDM.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <queries module="MDM">
              <query id="MDM.PlantList"><statement><![CDATA[ SELECT PLANT_ID FROM MDM_PLANT ]]></statement></query>
              <query id="MDM.AreaList"><statement><![CDATA[ SELECT AREA_ID FROM MDM_AREA WHERE PLANT_ID = @plantId ]]></statement></query>
            </queries>
            """);

        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.Dialect.Should().Be("sqlite");
        reg.Ids.Should().BeEquivalentTo(new[] { "MDM.PlantList", "MDM.AreaList" });
        reg.TryGet("MDM.PlantList", out var def).Should().BeTrue();
        def!.Sql.Should().Contain("FROM MDM_PLANT");
        reg.TryGet("MDM.AreaList", out var def2).Should().BeTrue();
        def2!.Sql.Should().Contain("@plantId");
    }

    [Fact]
    public void Load_parses_required_permission_attribute()
    {
        WriteDialectFile("sqlite", "P.xml", """
            <queries>
              <query id="Q.Open"><statement>SELECT 1</statement></query>
              <query id="Q.Secured" requiredPermission="sys:manage"><statement>SELECT 2</statement></query>
            </queries>
            """);
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Open", out var open).Should().BeTrue();
        open!.RequiredPermission.Should().BeNull("권한 미선언 쿼리는 인증만으로 실행");
        reg.TryGet("Q.Secured", out var secured).Should().BeTrue();
        secured!.RequiredPermission.Should().Be("sys:manage");
    }

    [Fact]
    public void Load_parses_kind_write_attribute()
    {
        WriteDialectFile("sqlite", "W.xml", """
            <queries>
              <query id="Q.Read"><statement>SELECT 1</statement></query>
              <query id="Q.Write" kind="write"><statement>INSERT INTO T(A) VALUES(@a)</statement></query>
            </queries>
            """);
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Read", out var read).Should().BeTrue();
        read!.IsWrite.Should().BeFalse("kind 미선언은 읽기(read) 쿼리");
        reg.TryGet("Q.Write", out var write).Should().BeTrue();
        write!.IsWrite.Should().BeTrue("kind=\"write\"는 쓰기 쿼리로 표시돼야 한다");
    }

    [Fact]
    public void TryGet_returns_false_for_unknown_id()
    {
        WriteDialectFile("sqlite", "X.xml",
            "<queries><query id=\"A.B\"><statement>SELECT 1</statement></query></queries>");
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("NOPE", out var def).Should().BeFalse();
        def.Should().BeNull();
    }

    [Fact]
    public void Load_throws_on_duplicate_id_across_files()
    {
        WriteDialectFile("sqlite", "A.xml",
            "<queries><query id=\"DUP\"><statement>SELECT 1</statement></query></queries>");
        WriteDialectFile("sqlite", "B.xml",
            "<queries><query id=\"DUP\"><statement>SELECT 2</statement></query></queries>");

        var act = () => FileQueryRegistry.Load("sqlite", _root);
        act.Should().Throw<InvalidOperationException>("중복 쿼리 ID는 시작 시 즉시 실패해야 한다")
            .WithMessage("*DUP*");
    }

    [Fact]
    public void Load_missing_dialect_dir_returns_empty_registry()
    {
        // mssql 폴더가 없으면 빈 레지스트리(쿼리 게이트웨이 미사용 환경에서도 무해 기동).
        var reg = FileQueryRegistry.Load("mssql", _root);
        reg.Ids.Should().BeEmpty();
        reg.TryGet("anything", out _).Should().BeFalse();
    }

    [Fact]
    public void Load_isolates_by_dialect_folder()
    {
        WriteDialectFile("mssql", "M.xml",
            "<queries><query id=\"ONLY.MSSQL\"><statement>SELECT 1</statement></query></queries>");
        WriteDialectFile("sqlite", "S.xml",
            "<queries><query id=\"ONLY.SQLITE\"><statement>SELECT 1</statement></query></queries>");

        FileQueryRegistry.Load("mssql", _root).TryGet("ONLY.MSSQL", out _).Should().BeTrue();
        FileQueryRegistry.Load("mssql", _root).TryGet("ONLY.SQLITE", out _).Should().BeFalse();
        FileQueryRegistry.Load("sqlite", _root).TryGet("ONLY.SQLITE", out _).Should().BeTrue();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 임시 폴더 정리 실패 무시 */ }
    }
}
