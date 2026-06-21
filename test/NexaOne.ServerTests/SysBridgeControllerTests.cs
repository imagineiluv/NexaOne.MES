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
/// 역할 관리(CreateRole/Add·RemovePermission)·신청 반려(RejectRequest)·사용자 비활성(DeactivateUser)의 쓰기 경로
/// (sys:manage 게이트)를 커버한다. 보안 가드(S7): 자격증명/비밀번호/로그인·승인(다중 애그리거트)·잠금 해제는
/// 브리지에 없으므로 검증 대상이 아니다(인증 경로 소유, 의도적 보류).</summary>
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
                _ => Result.Success(new UserRequestDto(requestId, "u1", "사용자", "u1@x.com", "부서", "사원",
                    "P1", "Rejected", 1, reason, rejectedBy)),
            });

        public Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default) => Transition(userId);

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
}
