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
using NexaOne.ServiceContracts.Rms;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>RMS 얇은 브리지 컨트롤러 HTTP 매핑 검증 — modules OFF + 가짜 IRecipeApprovalBridge 주입으로
/// Result→HTTP(200/204/409/404/400)·쓰기 권한 403·읽기 200을 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class RmsBridgeControllerTests : IClassFixture<RmsBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase3c-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-bridge-test";
    private readonly BridgeFactory _factory;
    public RmsBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-rms-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s => s.AddSingleton<IRecipeApprovalBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — recipeId/paramId 센티넬 값으로 Result 분기를 결정해 컨트롤러의 Result→HTTP 매핑만 격리 검증한다.
    private sealed class FakeBridge : IRecipeApprovalBridge
    {
        public Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeDto>>(
                new[] { new RecipeDto("R1", "n", "d", "EC1", 1, "Draft", null, null, null) });

        public Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeDto>>(
                new[] { new RecipeDto("R1", "n", "d", "EC1", 1, "Draft", null, null, null) });

        public Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
            => Task.FromResult(recipeId switch
            {
                "__notfound__" => Result.Failure<RecipeDto>(Error.NotFound("Recipe", recipeId)),
                _ => Result.Success(new RecipeDto(recipeId, "n", "d", "EC1", 1, "Released", null, null, null)),
            });

        public Task<Result<RecipeDto>> CreateRecipeAsync(string recipeId, string name, string desc, string equipmentClassId, CancellationToken ct = default)
            => Task.FromResult(recipeId switch
            {
                "__validation__" => Result.Failure<RecipeDto>(Error.Validation("x", "bad")),
                _ => Result.Success(new RecipeDto(recipeId, name, desc, equipmentClassId, 1, "Draft", null, null, null)),
            });

        public Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default) => MapResult(recipeId);
        public Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default) => MapResult(recipeId);
        public Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default) => MapResult(recipeId);
        public Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default) => MapResult(recipeId);
        public Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default) => MapResult(recipeId);

        public Task<Result<RecipeDto>> CreateNewVersionAsync(string sourceRecipeId, string newRecipeId, CancellationToken ct = default)
            => Task.FromResult(sourceRecipeId switch
            {
                "__conflict__" => Result.Failure<RecipeDto>(Error.Conflict("c")),
                _ => Result.Success(new RecipeDto(newRecipeId, "n", "d", "EC1", 2, "Draft", null, null, null)),
            });

        public Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeParamDto>>(
                new[] { new RecipeParamDto("P1", recipeId, "p", "v", "u", 1) });

        public Task<Result<RecipeParamDto>> AddParamAsync(string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder, CancellationToken ct = default)
            => Task.FromResult(recipeId switch
            {
                "__conflict__" => Result.Failure<RecipeParamDto>(Error.Conflict("c")),
                _ => Result.Success(new RecipeParamDto(paramId, recipeId, paramName, paramValue, unit, sortOrder)),
            });

        public Task<Result> UpdateParamAsync(string paramId, string newValue, CancellationToken ct = default) => MapResult(paramId);
        public Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default) => MapResult(paramId);

        // 비-제네릭 Result 분기 공통: __conflict__→409, __notfound__→404, 그 외 성공(→204).
        private static Task<Result> MapResult(string id) => Task.FromResult(id switch
        {
            "__conflict__" => Result.Failure(Error.Conflict("c")),
            "__notfound__" => Result.Failure(Error.NotFound("Recipe", "x")),
            _ => Result.Success(),
        });
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Create_recipe_success_returns_200_with_dto()
    {
        var res = await Client("rms:manage").PostAsJsonAsync("/api/v1/rms/recipes",
            new { recipeId = "R1", name = "n", description = "d", equipmentClassId = "EC1" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<RecipeDto>();
        dto!.RecipeId.Should().Be("R1");
        dto.ApprovalState.Should().Be("Draft");
    }

    [Fact]
    public async Task Approve1_conflict_maps_to_409()
    {
        var res = await Client("rms:manage").PutAsync("/api/v1/rms/recipes/__conflict__/approve1", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태위반(Conflict)은 409로 매핑");
    }

    [Fact]
    public async Task RequestApproval_notfound_maps_to_404()
    {
        var res = await Client("rms:manage").PutAsync("/api/v1/rms/recipes/__notfound__/request-approval", null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재(NotFound)는 404로 매핑");
    }

    [Fact]
    public async Task Create_recipe_validation_maps_to_400()
    {
        var res = await Client("rms:manage").PostAsJsonAsync("/api/v1/rms/recipes",
            new { recipeId = "__validation__", name = "n", description = "d", equipmentClassId = "EC1" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "검증실패(Validation)는 400으로 매핑");
    }

    [Fact]
    public async Task Write_without_rms_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/rms/recipes",
            new { recipeId = "R1", name = "n", description = "d", equipmentClassId = "EC1" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "rms:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task Approve1_success_returns_204()
    {
        var res = await Client("rms:manage").PutAsync("/api/v1/rms/recipes/R1/approve1", null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "비제네릭 Result 성공은 204(NoContent)로 매핑");
    }

    [Fact]
    public async Task GetRecipes_by_state_returns_200_for_reader()
    {
        var res = await Client().GetAsync("/api/v1/rms/recipes?state=Draft");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "읽기는 인증만 있으면 rms:manage 없이 200");
        var rows = await res.Content.ReadFromJsonAsync<List<RecipeDto>>();
        rows!.Should().ContainSingle(r => r.RecipeId == "R1");
    }
}
