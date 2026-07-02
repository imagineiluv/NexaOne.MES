using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common;
using NexaOne.ServiceContracts.Sys;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SYS 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008, S7) — modules OFF + 가짜 ISysBridge 주입으로
/// 권한(403/200)·생성(200·검증 400·충돌 409)·상태전이(204·NotFound→404)를 Spring/ALC 없이 결정적으로 검증한다.
/// 역할 관리·신청 생명주기(§19.3 익명 신청/중복확인 + sys:manage 조회/승인/반려)·사용자 비활성을 커버한다.
/// 승인은 역할 검증(SEC-1 재사용, 실 SQLite SYS_ROLE)·임시 비밀번호 발급(응답 1회 노출)까지 본다.
/// 보안 가드(S7): 자격증명/비밀번호/로그인·잠금 해제는 브리지에 없다(인증 경로 소유).</summary>
public sealed class SysBridgeControllerTests : IClassFixture<SysBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-sys-bridge-e2e-jwt-secret-key-at-least-32b";
    private const string Issuer = "nexaone-sys-bridge-test";
    private readonly BridgeFactory _factory;
    public SysBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-sys-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureTestServices(s => s.AddSingleton<ISysBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 상태전이/조회는 id 규약으로 분기한다: "CONFLICT"→409, "MISSING"→404, 그 외 성공.
    // 생성/반려는 검증 실패 입력(빈 이름/빈 사유)이면 도메인 검증 Error를 흉내내 400으로 매핑되는지 본다.
    private sealed class FakeBridge : ISysBridge
    {
        public Task<Result<RoleDto>> CreateRoleAsync(string roleId, string roleName, string description, CancellationToken ct = default)
            => Task.FromResult(roleId switch
            {
                "CONFLICT" => Result.Failure<RoleDto>(Error.Conflict($"Role '{roleId}' already exists.")),
                _ when string.IsNullOrWhiteSpace(roleName)
                    => Result.Failure<RoleDto>(Error.Validation(nameof(roleName), "Role name is required.")),
                _ => Result.Success(new RoleDto(roleId, roleName, description, new[] { "sys:manage" })),
            });

        public Task<Result> AddPermissionAsync(string roleId, string permission, CancellationToken ct = default) => Transition(roleId);
        public Task<Result> RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default) => Transition(roleId);

        public Task<Result<UserRequestDto>> RejectRequestAsync(string requestId, string rejectedBy, string reason, CancellationToken ct = default)
            => Task.FromResult(requestId switch
            {
                "MISSING" => Result.Failure<UserRequestDto>(Error.NotFound("UserRequest", requestId)),
                _ when string.IsNullOrWhiteSpace(reason)
                    => Result.Failure<UserRequestDto>(Error.Validation("UserRequest.ReasonRequired", "반려 사유는 필수입니다.")),
                _ => Result.Success(Snapshot(requestId, "Rejected") with { RejectReason = reason, RejectedBy = rejectedBy }),
            });

        public Task<bool> IsUserIdAvailableAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(userId != "TAKEN");

        public Task<Result<UserRequestDto>> CreateRequestAsync(UserRegistrationRequestDto request, CancellationToken ct = default)
            => Task.FromResult(request.UserId switch
            {
                "CONFLICT" => Result.Failure<UserRequestDto>(Error.Conflict($"이미 처리 대기 중인 신청이 있습니다: {request.UserId}")),
                _ when !request.TermsAccepted
                    => Result.Failure<UserRequestDto>(Error.Validation("UserRequest.TermsRequired", "약관 동의는 필수입니다.")),
                _ => Result.Success(Snapshot("REQ-NEW", "Request") with { UserId = request.UserId }),
            });

        public Task<Result<IReadOnlyList<UserRequestDto>>> GetRequestsAsync(
            string? plantId = null, string? status = null, string? userId = null,
            string? userName = null, string? email = null,
            DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
            => Task.FromResult(from.HasValue && to.HasValue && from > to
                ? Result.Failure<IReadOnlyList<UserRequestDto>>(
                    Error.Validation("UserRequest.InvalidPeriod", "조회 시작일이 종료일보다 늦습니다."))
                : Result.Success<IReadOnlyList<UserRequestDto>>(new[] { Snapshot("REQ1", "Request") }));

        public Task<Result<UserRequestDto>> ApproveRequestAsync(
            string requestId, string roleId, string approvedBy, string tempPasswordHash, CancellationToken ct = default)
            => Task.FromResult(requestId switch
            {
                "MISSING" => Result.Failure<UserRequestDto>(Error.NotFound("UserRequest", requestId)),
                "CONFLICT" => Result.Failure<UserRequestDto>(Error.Conflict("이미 존재하는 사용자입니다: u1")),
                _ => Result.Success(Snapshot(requestId, "Approved") with { ApprovedBy = approvedBy }),
            });

        public Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default) => Transition(userId);

        private static UserRequestDto Snapshot(string requestId, string status)
            => new(requestId, "u1", "사용자", "u1@x.com", "부서", "사원",
                null, "P1", "KoKr", null, null, null, null, status, 1,
                new DateTime(2026, 7, 1), new DateTime(2026, 7, 1), null, null, null, null, null);

        private static Task<Result> Transition(string id) => Task.FromResult(id switch
        {
            "CONFLICT" => Result.Failure(Error.Conflict("Invalid state transition.")),
            "MISSING"  => Result.Failure(Error.NotFound("Entity", id)),
            _          => Result.Success(),
        });
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "sys-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // ── 권한 게이트 ──

    [Fact]
    public async Task CreateRole_without_sys_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/sys/admin/roles",
            new { roleId = "OP", roleName = "운영자", description = "" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "sys:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task DeactivateUser_without_sys_manage_is_forbidden()
    {
        var res = await Client().PostAsync("/api/v1/sys/admin/users/u1/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "무권한 쓰기는 403");
    }

    // ── 역할 관리 ──

    [Fact]
    public async Task CreateRole_with_sys_manage_returns_200_with_dto()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/roles",
            new { roleId = "OP", roleName = "운영자", description = "운영자 역할" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<RoleDto>();
        dto!.RoleId.Should().Be("OP");
        dto.RoleName.Should().Be("운영자");
        dto.Permissions.Should().Contain("sys:manage");
    }

    [Fact]
    public async Task CreateRole_blank_name_maps_to_400()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/roles",
            new { roleId = "OP", roleName = "", description = "" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(Validation)는 400");
    }

    [Fact]
    public async Task CreateRole_conflict_maps_to_409()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/roles",
            new { roleId = "CONFLICT", roleName = "중복", description = "" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "이미 존재하는 역할(Conflict)은 409");
    }

    [Fact]
    public async Task AddPermission_success_returns_204()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/roles/OP/permissions",
            new { permission = "mdm:manage" });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 갱신은 204");
    }

    [Fact]
    public async Task AddPermission_missing_role_maps_to_404()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/roles/MISSING/permissions",
            new { permission = "mdm:manage" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 역할은 404");
    }

    [Fact]
    public async Task RemovePermission_success_returns_204()
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/sys/admin/roles/OP/permissions")
        {
            Content = JsonContent.Create(new { permission = "mdm:manage" }),
        };
        var res = await Client("sys:manage").SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemovePermission_missing_role_maps_to_404()
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/sys/admin/roles/MISSING/permissions")
        {
            Content = JsonContent.Create(new { permission = "mdm:manage" }),
        };
        var res = await Client("sys:manage").SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 신청 반려 ──

    [Fact]
    public async Task RejectRequest_with_sys_manage_returns_200_with_dto()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/REQ1/reject",
            new { reason = "검토 결과 반려" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<UserRequestDto>();
        dto!.RequestId.Should().Be("REQ1");
        dto.Status.Should().Be("Rejected");
        dto.RejectReason.Should().Be("검토 결과 반려");
    }

    [Fact]
    public async Task RejectRequest_blank_reason_maps_to_400()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/REQ1/reject",
            new { reason = "" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "반려 사유 누락(Validation)은 400");
    }

    [Fact]
    public async Task RejectRequest_missing_maps_to_404()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/MISSING/reject",
            new { reason = "사유" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 신청은 404");
    }

    // ── 사용자 비활성 ──

    [Fact]
    public async Task DeactivateUser_success_returns_204()
    {
        var res = await Client("sys:manage").PostAsync("/api/v1/sys/admin/users/u1/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeactivateUser_missing_maps_to_404()
    {
        var res = await Client("sys:manage").PostAsync("/api/v1/sys/admin/users/MISSING/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Wildcard_permission_allows_write()
    {
        var res = await Client("*").PostAsync("/api/v1/sys/admin/users/u1/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "와일드카드(*) 권한은 통과");
    }

    // ── 신청 생명주기(§19.3) — 익명 신청/중복확인 + 조회/승인 ──

    [Fact]
    public async Task Availability_is_anonymous_and_reports_taken_id()
    {
        var anon = _factory.CreateClient();
        var ok = await anon.GetFromJsonAsync<AvailabilityPayload>(
            "/api/v1/sys/admin/user-requests/availability?userId=newbie");
        ok!.Available.Should().BeTrue("미사용 ID는 사용 가능");

        var taken = await anon.GetFromJsonAsync<AvailabilityPayload>(
            "/api/v1/sys/admin/user-requests/availability?userId=TAKEN");
        taken!.Available.Should().BeFalse("기존 사용자/대기 신청 ID는 사용 불가");
    }

    [Fact]
    public async Task CreateUserRequest_is_anonymous_and_returns_dto()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/v1/sys/admin/user-requests",
            new { userId = "newbie", userName = "신규", email = "n@x.com", department = "부서", position = "사원", plantId = "P1", termsAccepted = true });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "가입 신청은 익명 진입점");
        var dto = await res.Content.ReadFromJsonAsync<UserRequestDto>();
        dto!.UserId.Should().Be("newbie");
        dto.Status.Should().Be("Request");
    }

    [Fact]
    public async Task CreateUserRequest_without_terms_maps_to_400()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/v1/sys/admin/user-requests",
            new { userId = "newbie", userName = "신규", email = "n@x.com", department = "부서", position = "사원", plantId = "P1", termsAccepted = false });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "약관 미동의(Validation)는 400");
    }

    [Fact]
    public async Task GetUserRequests_without_sys_manage_is_forbidden()
    {
        var res = await Client("fdc:read").GetAsync("/api/v1/sys/admin/user-requests");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "신청 목록은 sys:manage 전용");
    }

    [Fact]
    public async Task GetUserRequests_with_sys_manage_returns_list()
    {
        var res = await Client("sys:manage").GetFromJsonAsync<List<UserRequestDto>>("/api/v1/sys/admin/user-requests");
        res!.Should().ContainSingle(r => r.RequestId == "REQ1");
    }

    [Fact]
    public async Task Approve_without_role_maps_to_400()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/REQ1/approve",
            new { roleId = "" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "승인 시 roleId는 필수(ROLE_REQUIRED)");
    }

    [Fact]
    public async Task Approve_unknown_role_maps_to_400()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/REQ1/approve",
            new { roleId = "NO_SUCH_ROLE" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "SEC-1과 동일하게 미존재/비활성 역할은 INVALID_ROLE 400");
    }

    [Fact]
    public async Task Approve_success_returns_request_and_temp_password()
    {
        // ADMIN은 V031 시드로 빈 DB 부팅 시 항상 존재 — 역할 검증(실 SQLite)을 통과한다.
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/REQ1/approve",
            new { roleId = "ADMIN" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ApprovalPayload>();
        body!.Request.Status.Should().Be("Approved");
        body.TempPassword.Should().NotBeNullOrWhiteSpace("임시 비밀번호는 승인 응답에 1회 노출");
        body.TempPassword.Length.Should().BeGreaterThanOrEqualTo(12);
        body.TempPassword.Should().MatchRegex("[A-Z]").And.MatchRegex("[a-z]").And.MatchRegex("[0-9]",
            "§19.2.2 정책 문자 클래스 보장");
    }

    [Fact]
    public async Task Approve_missing_request_maps_to_404()
    {
        var res = await Client("sys:manage").PostAsJsonAsync("/api/v1/sys/admin/user-requests/MISSING/approve",
            new { roleId = "ADMIN" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record AvailabilityPayload(bool Available);
    private sealed record ApprovalPayload(UserRequestDto Request, string TempPassword);
}
