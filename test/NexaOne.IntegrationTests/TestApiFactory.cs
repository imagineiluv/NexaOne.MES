using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.API.Services;

namespace NexaOne.IntegrationTests;

/// <summary>
/// API 인프로세스 기동용 WebApplicationFactory. 실 MSSQL 없이 통합 테스트를 돌리기 위해
/// SQLite(임시 파일 DB)로 전환하고(Database:Provider=Sqlite) 마이그레이션 스키마를 부트스트랩한다.
/// 부팅 fail-fast(§18.7 — JWT 비밀키 강도 검증)를 통과하도록 테스트 전용 강한 Jwt:SecretKey도 주입한다.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private const string TestSecret = "integration-test-only-jwt-secret-key-at-least-32-bytes-long";
    private const string TestIssuer = "nexaone-test";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"nexaone-test-{Guid.NewGuid():N}.db");

    // Foreign Keys=False — 런타임 SQLite 연결의 FK 강제를 결정론적으로 끈다. Microsoft.Data.Sqlite는
    // 연결별 PRAGMA foreign_keys 기본값/풀링 상태에 따라 FK를 비결정적으로 강제해(부트스트랩의 PRAGMA OFF는
    // 그 연결 한정) 동일 테스트가 단독 통과·전체 실패하는 플래키를 유발했다. 통합 테스트의 목적은
    // 마이그레이션 완전성·방언(테이블 존재/컬럼 정합/SELECT·UPSERT)이며 FK 제약은 테이블 생성 시 파싱으로
    // 검증된다. 운영(MSSQL)은 FK를 그대로 강제한다.
    private string ConnString => $"Data Source={_dbPath};Foreign Keys=False";

    public TestApiFactory()
    {
        // 부팅 fail-fast(JWT 강도 검증)·DB 설정을 확실히 덮어쓰기 위해 환경변수로 주입한다.
        // (최소 호스팅 모델 + WebApplicationFactory에서 ConfigureAppConfiguration은 appsettings 치환자에
        //  밀릴 수 있어, appsettings보다 우선순위가 높은 환경변수를 사용한다.)
        Environment.SetEnvironmentVariable("Jwt__SecretKey", TestSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", TestIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", TestIssuer);
        Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
        Environment.SetEnvironmentVariable("ConnectionStrings__NexaOne", ConnString);

        // 호스트가 요청을 처리하기 전에 SQLite 임시 DB에 스키마를 생성한다(리포지토리가 테이블을 찾도록).
        SqliteSchemaBootstrapper.Apply(ConnString);
    }

    /// <summary>앱의 JwtService로 유효한 토큰을 발급해 Authorization 헤더가 설정된 클라이언트를 반환한다.
    /// permissions 미지정 시 "*"(ADMIN 전체 권한)로 발급한다.</summary>
    public HttpClient CreateAuthenticatedClient(params string[] permissions)
    {
        var client = CreateClient();
        var jwt = Services.GetRequiredService<IJwtService>();
        var perms = permissions.Length > 0 ? permissions : new[] { "*" };
        var token = jwt.GenerateAccessToken("test-admin", "Test Admin", "DEFAULT", new[] { "ADMIN" }, permissions: perms);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* 임시 파일 정리 실패는 무시 */ }
    }
}
