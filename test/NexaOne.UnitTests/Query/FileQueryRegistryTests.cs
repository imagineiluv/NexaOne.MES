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
              <query id="MDM.PlantList" access="public"><statement><![CDATA[ SELECT PLANT_ID FROM MDM_PLANT ]]></statement></query>
              <query id="MDM.AreaList" requiredPermission="mdm:read"><statement><![CDATA[ SELECT AREA_ID FROM MDM_AREA WHERE PLANT_ID = @plantId ]]></statement></query>
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
              <query id="Q.Open" access="public"><statement>SELECT 1</statement></query>
              <query id="Q.Secured" requiredPermission="sys:manage"><statement>SELECT 2</statement></query>
            </queries>
            """);
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Open", out var open).Should().BeTrue();
        open!.RequiredPermission.Should().BeNull("권한 미선언 쿼리는 인증만으로 실행");
        open.IsPublic.Should().BeTrue("access=public이 명시된 읽기 쿼리");
        reg.TryGet("Q.Secured", out var secured).Should().BeTrue();
        secured!.RequiredPermission.Should().Be("sys:manage");
        secured.IsPublic.Should().BeFalse();
    }

    [Fact]
    public void Load_parses_kind_write_attribute()
    {
        // 쓰기 쿼리는 권한 선언이 필수(fail-fast 가드)이므로 requiredPermission을 함께 둔다.
        WriteDialectFile("sqlite", "W.xml", """
            <queries>
              <query id="Q.Read" access="public"><statement>SELECT 1</statement></query>
              <query id="Q.Write" kind="write" requiredPermission="t:manage"><statement>INSERT INTO T(A) VALUES(@a)</statement></query>
            </queries>
            """);
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Read", out var read).Should().BeTrue();
        read!.IsWrite.Should().BeFalse("kind 미선언은 읽기(read) 쿼리");
        reg.TryGet("Q.Write", out var write).Should().BeTrue();
        write!.IsWrite.Should().BeTrue("kind=\"write\"는 쓰기 쿼리로 표시돼야 한다");
    }

    [Fact]
    public void Load_throws_when_write_query_missing_required_permission()
    {
        // 보안 fail-fast — 쓰기 쿼리(kind="write")가 권한을 선언하지 않으면 시작 시 즉시 실패해야 한다
        // (권한 없는 쓰기 쿼리는 command 게이트웨이에서 인증만으로 임의 쓰기를 허용하므로).
        WriteDialectFile("sqlite", "W.xml",
            "<queries><query id=\"Q.Unsafe\" kind=\"write\"><statement>INSERT INTO T(A) VALUES(@a)</statement></query></queries>");

        var act = () => FileQueryRegistry.Load("sqlite", _root);
        act.Should().Throw<InvalidOperationException>("권한 없는 쓰기 쿼리는 시작 시 차단되어야 한다")
            .WithMessage("*Q.Unsafe*");
    }

    [Fact]
    public void Load_throws_when_read_query_has_no_access_policy()
    {
        WriteDialectFile("sqlite", "R.xml",
            "<queries><query id=\"Q.Unclassified\"><statement>SELECT 1</statement></query></queries>");

        var act = () => FileQueryRegistry.Load("sqlite", _root);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Q.Unclassified*requiredPermission*access=\"public\"*");
    }

    [Theory]
    [InlineData("access=\"authenticated\"")]
    [InlineData("access=\"public\" requiredPermission=\"sys:manage\"")]
    public void Load_throws_when_read_access_policy_is_invalid(string attributes)
    {
        WriteDialectFile("sqlite", "R.xml",
            $"<queries><query id=\"Q.Invalid\" {attributes}><statement>SELECT 1</statement></query></queries>");

        var act = () => FileQueryRegistry.Load("sqlite", _root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Q.Invalid*");
    }

    [Fact]
    public void Load_allows_write_query_with_permission_and_explicit_public_read()
    {
        // 쓰기 쿼리는 권한이 있으면 허용, 읽기 쿼리는 권한 없이도 허용(회귀 가드).
        WriteDialectFile("sqlite", "OK.xml", """
            <queries>
              <query id="Q.Read" access="public"><statement>SELECT 1</statement></query>
              <query id="Q.Write" kind="write" requiredPermission="mdm:manage"><statement>INSERT INTO T(A) VALUES(@a)</statement></query>
            </queries>
            """);
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Read", out var read).Should().BeTrue();
        read!.RequiredPermission.Should().BeNull("명시적 public 읽기는 별도 업무 권한 없이 실행 가능");
        read.IsPublic.Should().BeTrue();
        reg.TryGet("Q.Write", out var write).Should().BeTrue();
        write!.IsWrite.Should().BeTrue();
        write.RequiredPermission.Should().Be("mdm:manage");
    }

    [Fact]
    public void TryGet_returns_false_for_unknown_id()
    {
        WriteDialectFile("sqlite", "X.xml",
            "<queries><query id=\"A.B\" access=\"public\"><statement>SELECT 1</statement></query></queries>");
        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("NOPE", out var def).Should().BeFalse();
        def.Should().BeNull();
    }

    [Fact]
    public void Load_throws_on_duplicate_id_across_files()
    {
        WriteDialectFile("sqlite", "A.xml",
            "<queries><query id=\"DUP\" access=\"public\"><statement>SELECT 1</statement></query></queries>");
        WriteDialectFile("sqlite", "B.xml",
            "<queries><query id=\"DUP\" access=\"public\"><statement>SELECT 2</statement></query></queries>");

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
            "<queries><query id=\"ONLY.MSSQL\" access=\"public\"><statement>SELECT 1</statement></query></queries>");
        WriteDialectFile("sqlite", "S.xml",
            "<queries><query id=\"ONLY.SQLITE\" access=\"public\"><statement>SELECT 1</statement></query></queries>");

        FileQueryRegistry.Load("mssql", _root).TryGet("ONLY.MSSQL", out _).Should().BeTrue();
        FileQueryRegistry.Load("mssql", _root).TryGet("ONLY.SQLITE", out _).Should().BeFalse();
        FileQueryRegistry.Load("sqlite", _root).TryGet("ONLY.SQLITE", out _).Should().BeTrue();
    }

    // 정의를 조용히 버리면 게이트웨이는 404로만, 화면 seed는 원인 없는 실패로만 드러난다.
    // 로더가 거부해야 기동 시점에 드러난다.
    [Fact]
    public void Load_rejects_a_query_without_an_id()
    {
        WriteDialectFile("sqlite", "NoId.xml", """
            <queries>
              <query access="public"><statement>SELECT 1</statement></query>
            </queries>
            """);

        var act = () => FileQueryRegistry.Load("sqlite", _root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*without an id*");
    }

    [Fact]
    public void Load_rejects_a_query_with_an_empty_statement()
    {
        WriteDialectFile("sqlite", "Empty.xml", """
            <queries>
              <query id="Q.Empty" access="public"><statement>   </statement></query>
            </queries>
            """);

        var act = () => FileQueryRegistry.Load("sqlite", _root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Q.Empty*empty statement*");
    }

    // kind 오타는 read로 조용히 분류되어 INSERT/UPDATE/DELETE 문을 /query 경로에 올린다.
    [Fact]
    public void Load_rejects_an_unsupported_kind_instead_of_treating_it_as_read()
    {
        WriteDialectFile("sqlite", "Typo.xml", """
            <queries>
              <query id="Q.Typo" kind="wrte" requiredPermission="sys:manage"><statement>DELETE FROM T</statement></query>
            </queries>
            """);

        var act = () => FileQueryRegistry.Load("sqlite", _root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported kind*");
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("WRITE")]
    public void Load_accepts_the_declared_kinds(string kind)
    {
        WriteDialectFile("sqlite", "Kinds.xml", $"""
            <queries>
              <query id="Q.Kind" kind="{kind}" requiredPermission="sys:manage"><statement>SELECT 1</statement></query>
            </queries>
            """);

        var reg = FileQueryRegistry.Load("sqlite", _root);

        reg.TryGet("Q.Kind", out var def).Should().BeTrue();
        def!.IsWrite.Should().Be(!string.Equals(kind, "read", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 임시 폴더 정리 실패 무시 */ }
    }
}
