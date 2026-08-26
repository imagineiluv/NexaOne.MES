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

    // db 트리는 호스트 프로젝트 config로 이관됨(src/00.Main/NexaOne.Server/config/db/). 소스 원본을 직접 검증한다.
    private const string DbRoot = "src/00.Main/NexaOne.Server/config/db";
    public static IEnumerable<object[]> QueryTrees() => new[]
    {
        new object[] { $"{DbRoot}/queries" },        // 공개 게이트웨이 트리
        new object[] { $"{DbRoot}/queries-auth" },   // 격리 인증 트리
    };

    [Theory]
    [MemberData(nameof(QueryTrees))]
    public void Mssql_and_sqlite_dialects_are_in_lockstep(string treeRelativePath)
    {
        var treeRoot = RepositorySource.GetDirectory(treeRelativePath);
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
            mDef.IsPublic.Should().Be(sDef.IsPublic,
                $"[{treeRelativePath}] '{id}' — access=public 방언 불일치");
        }
    }

    [Fact]
    public void Qms_execution_queries_expose_effective_state_and_only_effective_sampling_revisions()
    {
        var root = RepositorySource.GetDirectory($"{DbRoot}/queries");
        var mssql = FileQueryRegistry.Load("mssql", root);
        var sqlite = FileQueryRegistry.Load("sqlite", root);

        foreach (var id in new[]
                 {
                     "QMS.IncomingInspectionList",
                     "QMS.ProcessInspectionList",
                     "QMS.ShippingInspectionList"
                 })
        {
            mssql.TryGet(id, out var m).Should().BeTrue();
            sqlite.TryGet(id, out var s).Should().BeTrue();
            foreach (var alias in new[] { "IS_CANCELLED", "IS_SUPERSEDED", "EFFECTIVE_RESULT" })
            {
                m!.Sql.Should().Contain(alias);
                s!.Sql.Should().Contain(alias);
            }
        }

        mssql.TryGet("QMS.SamplingPlanRevisionCombo", out var mCombo).Should().BeTrue();
        sqlite.TryGet("QMS.SamplingPlanRevisionCombo", out var sCombo).Should().BeTrue();
        mCombo!.Sql.Should().Contain("EFFECTIVE_FROM <= GETUTCDATE()", Exactly.Once());
        sCombo!.Sql.Should().Contain("EFFECTIVE_FROM <= CURRENT_TIMESTAMP", Exactly.Once());
    }

    [Fact]
    public void Pom_operator_ledgers_have_equivalent_500_row_caps_in_both_dialects()
    {
        var root = RepositorySource.GetDirectory($"{DbRoot}/queries");
        var mssql = FileQueryRegistry.Load("mssql", root);
        var sqlite = FileQueryRegistry.Load("sqlite", root);

        foreach (var id in new[]
                 {
                     "POM.RouteExceptionList",
                     "POM.RouteDeviationTimeline",
                     "POM.LotDefectExecutionList"
                 })
        {
            mssql.TryGet(id, out var mssqlDefinition).Should().BeTrue();
            sqlite.TryGet(id, out var sqliteDefinition).Should().BeTrue();

            mssqlDefinition!.Sql.Should().MatchRegex(@"(?is)^\s*SELECT\s+TOP\s+500\b",
                $"{id} must keep an explicit MSSQL operator-screen safety cap");
            sqliteDefinition!.Sql.Should().MatchRegex(@"(?is)\bLIMIT\s+500\s*$",
                $"{id} must keep the same SQLite operator-screen safety cap");
        }
    }

    [Fact]
    public void Pom_automatic_return_timeline_projects_track_out_execution_provenance_in_both_dialects()
    {
        var root = RepositorySource.GetDirectory($"{DbRoot}/queries");
        var mssql = FileQueryRegistry.Load("mssql", root);
        var sqlite = FileQueryRegistry.Load("sqlite", root);
        mssql.TryGet("POM.RouteDeviationTimeline", out var mssqlDefinition).Should().BeTrue();
        sqlite.TryGet("POM.RouteDeviationTimeline", out var sqliteDefinition).Should().BeTrue();

        foreach (var sql in new[] { mssqlDefinition!.Sql, sqliteDefinition!.Sql })
        {
            sql.Should().Contain("LEFT JOIN POM_LOT_EXECUTION RETURN_EXECUTION");
            sql.Should().Contain("RETURN_EXECUTION.LOT_ID = H.LOT_ID");
            sql.Should().Contain("RETURN_EXECUTION.EXECUTION_ID = H.IDEMPOTENCY_KEY");
            sql.Should().Contain("RETURN_EXECUTION.ACTION = 'TrackOut'");
            foreach (var column in new[]
                     {
                         "FROM_STEP", "TO_STEP", "FROM_PROCESS_ID", "TO_PROCESS_ID",
                         "CLIENT_CHANNEL", "DEVICE_ID", "EXPECTED_VERSION", "RESULT_VERSION", "CREATED_BY"
                     })
                sql.Should().Contain($"RETURN_EXECUTION.{column}");
        }
    }

    [Fact]
    public void Qms_v2_mssql_migration_contains_sqlite_equivalent_lineage_and_evidence_guards()
    {
        var sql = File.ReadAllText(RepositorySource.GetFile(
            $"{DbRoot}/migrations/V097__QMS_INSPECTION_EXECUTION_V2.sql"));

        foreach (var guard in new[]
                 {
                     "TR_QMS_V2_INSPECTION_LINEAGE",
                     "TR_QMS_V2_EVENT_LINEAGE",
                     "TR_QMS_V2_RESULT_IMMUTABLE",
                     "TR_QMS_V2_RESULT_INTEGRITY",
                     "UX_QMS_INSPECTION_RESULT_SPEC",
                     "UX_QMS_INSPECTION_EVENT_CANCELLED",
                     "TR_QMS_AI_MODEL_VERSION_APPEND_ONLY",
                     "TR_QMS_AI_INFERENCE_INTEGRITY",
                     "TR_QMS_AI_INFERENCE_APPEND_ONLY",
                     "TR_QMS_AI_REVIEW_APPEND_ONLY"
                 })
            sql.Should().Contain(guard);
        sql.Should().Contain("Original QMS v2 inspections must be their own root.");
        sql.Should().Contain("cannot accept additional result rows");
        sql.Should().Contain("ON QMS_INSPECTION_RESULT AFTER INSERT, UPDATE AS");
        sql.Should().Contain("FROM inserted R", AtLeast.Once());
        sql.Should().Contain("LEFT JOIN deleted D ON D.RESULT_ID = N.RESULT_ID");
        sql.Should().Contain("I.IDEMPOTENCY_KEY IS NULL");
        sql.Should().Contain("I.LOT_ID <> R.LOT_ID");
        sql.Should().Contain("I.EQUIPMENT_ID <> R.EQUIPMENT_ID");
        sql.Should().Contain("S.IS_ACTIVE = 1");
        sql.Should().Contain("R.SAMPLE_QTY > I.SAMPLE_QTY");
        sql.Should().Contain("R.DEFECT_QTY > I.DEFECT_QTY");
        sql.Should().Contain("R.ATTRIBUTE_RESULT IS NULL");
        sql.Should().Contain("X.RESULT_ID <> R.RESULT_ID");
        sql.Should().Contain("at least one result item before confirmation");

        foreach (var trigger in new[]
                 {
                     "TR_QMS_V2_RESULT_IMMUTABLE",
                     "TR_QMS_V2_RESULT_INTEGRITY",
                     "TR_QMS_V2_EVENT_LINEAGE"
                 })
            Regex.Matches(sql, $@"CREATE\s+TRIGGER\s+{trigger}\b", RegexOptions.IgnoreCase)
                .Should().ContainSingle($"migration reruns are tracked by version and each trigger must be declared once: {trigger}");
    }

    private static HashSet<string> ExtractParams(string sql) =>
        ParamToken.Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Fmt(IEnumerable<string> tokens)
    {
        var ordered = tokens.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return ordered.Count == 0 ? "{}" : "{" + string.Join(", ", ordered) + "}";
    }

}
