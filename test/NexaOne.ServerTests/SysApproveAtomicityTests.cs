using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;
using NexaOne.SYS.Infrastructure;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SYS 사용자 승인 원자화(DATA-6) SQLite 통합검증 — 실제 SYS 리포지토리/서비스를 호스트 부트 DB에 직접
/// 구성해 (1)성공 승인이 SYS_USER 생성 + 신청 Approved 전환을 모두 커밋하고 (2)배치 중 SYS_USER INSERT가
/// PK 충돌로 실패하면 신청 UPDATE도 롤백돼 '사용자만 생성/신청만 전환'되는 부분 커밋이 불가능함을 검증한다.</summary>
public sealed class SysApproveAtomicityTests : IClassFixture<SysApproveAtomicityTests.ApproveFactory>
{
    private readonly ApproveFactory _factory;
    public SysApproveAtomicityTests(ApproveFactory factory) => _factory = factory;

    public sealed class ApproveFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-approve-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "sys-approve-e2e-jwt-secret-key-32bytes+!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-approve-test");
            builder.UseSetting("Jwt:Audience", "nexaone-approve-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private (UserRegistrationService Service, UserRequestRepository Requests) Build()
    {
        _ = _factory.CreateClient(); // 스키마 부트스트랩
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        };
        var config = new ConfigurationBuilder().Build(); // outbox off
        var requests = new UserRequestRepository(ds, config);
        return (new UserRegistrationService(requests, new UserRepository(ds, config)), requests);
    }

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        _ = _factory.CreateClient();
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void SeedRequest(string requestId, string userId)
        => Exec(@"INSERT INTO SYS_USER_REQUEST
            (REQUEST_ID, USER_ID, USER_NAME, EMAIL, DEPARTMENT, POSITION, PLANT_ID, LANGUAGE, STATUS,
             REQUEST_VERSION, REQUESTED_AT, TERMS_ACCEPTED_AT, TERMS_ACCEPTED_IP, CREATED_BY, UPDATED_BY)
            VALUES (@id, @user, '신청자', @user || '@x.com', '생산팀', '사원', 'PLANT01', 'KoKr', 'Request',
             1, @now, @now, '127.0.0.1', 'TEST', 'TEST')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", requestId);
            cmd.Parameters.AddWithValue("@user", userId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        });

    private T Scalar<T>(string sql, params (string Key, object Value)[] ps)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Successful_approve_commits_user_and_request_together()
    {
        var req = $"REQ_{Suffix()}";
        var user = $"newuser_{Suffix()}";
        SeedRequest(req, user);

        var (service, _) = Build();
        var result = await service.ApproveAsync(req, "admin", "VIEWER", PasswordHasher.Hash("Temp!Pass1"), DateTime.UtcNow);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : "");
        Scalar<long>("SELECT COUNT(*) FROM SYS_USER WHERE USER_ID=@u", ("@u", user)).Should().Be(1, "승인은 SYS_USER 행을 생성한다");
        Scalar<string>("SELECT STATUS FROM SYS_USER_REQUEST WHERE REQUEST_ID=@r", ("@r", req)).Should().Be("Approved",
            "신청도 같은 트랜잭션으로 Approved 전환돼야 한다");
    }

    [Fact]
    public async Task Failed_user_insert_rolls_back_request_update_no_partial_commit()
    {
        var req = $"REQ_{Suffix()}";
        var user = $"dupuser_{Suffix()}";
        SeedRequest(req, user);
        // 배치의 SYS_USER INSERT가 PK 충돌로 실패하도록 동일 USER_ID를 선점한다(서비스 사전검증을 우회해
        // 리포 배치를 직접 호출 — 사전검증과 커밋 사이의 레이스에서 발생 가능한 실제 시나리오).
        Exec("INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
             "VALUES (@u, '선점', 'x', @u || '@x.com', 'VIEWER', 'KoKr', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@u", user);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        });

        var (_, requests) = Build();
        var request = await requests.GetByIdAsync(req);
        request!.Approve("admin", DateTime.UtcNow).IsSuccess.Should().BeTrue();
        var domainUser = User.Create(user, "신청자", PasswordHasher.Hash("Temp!Pass1"), $"{user}@x.com", "VIEWER", LanguageType.KoKr).Value;

        var act = () => requests.ApprovePersistAsync(request, domainUser);
        await act.Should().ThrowAsync<Exception>("SYS_USER PK 충돌은 예외로 표면화돼야 한다");

        Scalar<string>("SELECT STATUS FROM SYS_USER_REQUEST WHERE REQUEST_ID=@r", ("@r", req)).Should().Be("Request",
            "롤백 후 신청은 여전히 대기 상태여야 한다(부분 커밋 불가 — DATA-6)");
    }
}
