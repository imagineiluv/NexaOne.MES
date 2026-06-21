using System.Text.RegularExpressions;
using FluentAssertions;
using NexaOne.Application.Query;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// 방언 패리티 회귀 테스트(TEST-4, no-DB). 명명 쿼리 게이트웨이는 모든 쿼리를 두 벌의 손관리 방언 사본으로
/// 보관한다: db/queries/{mssql,sqlite} 및 db/queries-auth/{mssql,sqlite}. 두 방언은 SQL 텍스트가
/// 정당하게 갈린다(MSSQL은 WITH (NOLOCK)·MERGE HOLDLOCK; SQLite는 INSERT ... ON CONFLICT·NOLOCK 없음).
/// 그러나 <b>방언 무관 계약</b>은 반드시 일치해야 한다: (1) 쿼리 ID 집합, (2) ID별 @param 토큰 집합,
/// (3) ID별 방언 무관 메타데이터(kind=read/write, requiredPermission). 모든 테스트는 SQLite로만 돌므로
/// MSSQL XML은 어떤 테스트도 로드/검증하지 않는다 → 운영 방언이 전 테스트 녹색인 채로 조용히 어긋날 수 있다
/// (한 방언에만 있는 ID, 한 방언에만 추가된 @param, 불일치 kind/requiredPermission). 이 테스트가 그걸 잠근다.
///
/// 로드 경로: 운영 파서(<see cref="FileQueryRegistry"/>)를 각 방언 폴더로 직접 가리켜 사용한다 —
/// 테스트가 검증하는 것이 곧 운영이 파싱하는 것이다. 레지스트리는 ID(Ids), SQL(QueryDefinition.Sql),
/// requiredPermission(RequiredPermission), kind(IsWrite)를 모두 노출하므로 별도 XDocument 파싱 불필요.
/// SQL 텍스트 자체는 절대 단언하지 않는다(NOLOCK/MERGE 차이는 합법) — ID·@param·메타데이터만 비교한다.
/// </summary>
public sealed class DialectParityTests
{
    // @paramName 토큰: '@' 다음 식별자(영문/밑줄 시작, 영숫자/밑줄 지속). 대소문자 무시·중복 제거하여 비교.
    // 서버 주입 토큰(@currentUser/@utcNow 등)도 일반 토큰과 동일하게 추출·비교한다 — 방언 간 어차피 일치해야 한다.
    private static readonly Regex ParamToken =
        new(@"@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IEnumerable<object[]> QueryTrees() => new[]
    {
        new object[] { "db/queries" },        // 공개 게이트웨이 트리
        new object[] { "db/queries-auth" },   // 격리 인증 트리
    };

    [Theory]
    [MemberData(nameof(QueryTrees))]
    public void Mssql_and_sqlite_dialects_are_in_lockstep(string treeRelativePath)
    {
        var treeRoot = ResolveRepoRelative(treeRelativePath);
        Directory.Exists(treeRoot).Should().BeTrue(
            $"방언 트리 경로가 해석돼야 한다(경로 해석 실패 시 테스트가 공허하게 통과하면 안 됨): {treeRoot}");

        // 운영 파서로 각 방언을 로드(overrideDirectory = 트리 루트; FileQueryRegistry가 내부에서 {root}/{dialect} 결합).
        var mssql = FileQueryRegistry.Load("mssql", treeRoot);
        var sqlite = FileQueryRegistry.Load("sqlite", treeRoot);

        var mssqlIds = mssql.Ids.ToHashSet(StringComparer.Ordinal);
        var sqliteIds = sqlite.Ids.ToHashSet(StringComparer.Ordinal);

        // 경로 해석/로드가 무음 실패해 공허하게 통과하는 것을 차단(양 방언 모두 비어있지 않아야 한다).
        mssqlIds.Should().NotBeEmpty($"'{treeRelativePath}/mssql'에서 쿼리가 로드돼야 한다(0개면 경로/로더 문제)");
        sqliteIds.Should().NotBeEmpty($"'{treeRelativePath}/sqlite'에서 쿼리가 로드돼야 한다(0개면 경로/로더 문제)");

        // (1) 동일 ID 집합 — 한 방언에만 있는 ID를 정확히 지목.
        var onlyInMssql = mssqlIds.Except(sqliteIds, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var onlyInSqlite = sqliteIds.Except(mssqlIds, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        onlyInMssql.Should().BeEmpty(
            $"[{treeRelativePath}] mssql에만 존재하는 쿼리 ID(방언 드리프트): {string.Join(", ", onlyInMssql)}");
        onlyInSqlite.Should().BeEmpty(
            $"[{treeRelativePath}] sqlite에만 존재하는 쿼리 ID(방언 드리프트): {string.Join(", ", onlyInSqlite)}");

        // 공유 ID에 대해서만 (2) @param 토큰 집합 + (3) 방언 무관 메타데이터를 비교.
        foreach (var id in mssqlIds.Intersect(sqliteIds, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            mssql.TryGet(id, out var mDef).Should().BeTrue($"[{treeRelativePath}] mssql 정의 '{id}' 조회");
            sqlite.TryGet(id, out var sDef).Should().BeTrue($"[{treeRelativePath}] sqlite 정의 '{id}' 조회");
            mDef.Should().NotBeNull();
            sDef.Should().NotBeNull();

            // (2) ID별 @param 토큰 집합 일치(대소문자 무시, 중복 제거). 한 방언에만 추가/누락된 파라미터를 지목.
            var mParams = ExtractParams(mDef!.Sql);
            var sParams = ExtractParams(sDef!.Sql);
            var paramsOnlyMssql = mParams.Except(sParams, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var paramsOnlySqlite = sParams.Except(mParams, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            paramsOnlyMssql.Should().BeEmpty(
                $"[{treeRelativePath}] '{id}' — mssql에만 있는 @param: {Fmt(paramsOnlyMssql)} (mssql={Fmt(mParams)} vs sqlite={Fmt(sParams)})");
            paramsOnlySqlite.Should().BeEmpty(
                $"[{treeRelativePath}] '{id}' — sqlite에만 있는 @param: {Fmt(paramsOnlySqlite)} (mssql={Fmt(mParams)} vs sqlite={Fmt(sParams)})");

            // (3) 방언 무관 메타데이터 — kind(read/write)와 requiredPermission이 ID마다 동일해야 한다.
            mDef.IsWrite.Should().Be(sDef.IsWrite,
                $"[{treeRelativePath}] '{id}' — kind(write 여부) 방언 불일치: mssql.IsWrite={mDef.IsWrite}, sqlite.IsWrite={sDef.IsWrite}");
            (mDef.RequiredPermission ?? "<none>").Should().Be(sDef.RequiredPermission ?? "<none>",
                $"[{treeRelativePath}] '{id}' — requiredPermission 방언 불일치: " +
                $"mssql='{mDef.RequiredPermission ?? "<none>"}', sqlite='{sDef.RequiredPermission ?? "<none>"}'");
        }
    }

    private static HashSet<string> ExtractParams(string sql) =>
        ParamToken.Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Fmt(IEnumerable<string> tokens)
    {
        var ordered = tokens.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return ordered.Count == 0 ? "{}" : "{" + string.Join(", ", ordered) + "}";
    }

    /// <summary>리포 루트(NexaOne.sln 보유 디렉터리)를 BaseDirectory에서 상위로 탐색해 상대 경로를 결합한다
    /// (HostModulesBootSmokeTests.ResolveHostBinDir와 동일 규약). 테스트 bin은 test/.../bin/Debug/net8.0 깊이.</summary>
    private static string ResolveRepoRelative(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaOne.sln"))) dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("리포 루트(NexaOne.sln) 미발견 — 방언 트리 경로 해석 실패.");
        return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
