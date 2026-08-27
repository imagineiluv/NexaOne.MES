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
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IRecipeApprovalBridge>(new FakeBridge());
                s.AddSingleton<IRecipeExecutionBridge>(new FakeExecutionBridge());
            });
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
        private static readonly RecipeDto[] Recipes =
        [
            new("R1", "draft-a", "d", "EC1", 1, "Draft", null, null, null),
            new("R2", "released-b", "d", "EC2", 2, "Released", "a1", "a2", DateTime.UtcNow),
        ];

        public Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeDto>>(string.IsNullOrWhiteSpace(equipmentClassId)
                ? Recipes
                : Recipes.Where(recipe => recipe.EquipmentClassId == equipmentClassId).ToArray());

        public Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeDto>>(
                Recipes.Where(recipe => recipe.ApprovalState.Equals(state, StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
            => Task.FromResult(recipeId switch
            {
                "__notfound__" => Result.Failure<RecipeDto>(Error.NotFound("Recipe", recipeId)),
                _ => Result.Success(new RecipeDto(recipeId, "n", "d", "EC1", 1, "Released", null, null, null)),
            });

        public static RecipeCreateCommand? LastCreate { get; private set; }
        public static RecipeVersionCreateCommand? LastVersionCreate { get; private set; }
        public static RecipeParamAddCommand? LastParamAdd { get; private set; }
        public static RecipeParamDeleteCommand? LastParamDelete { get; private set; }

        public Task<Result<RecipeDto>> CreateRecipeAsync(
            RecipeCreateCommand command, CancellationToken ct = default)
        {
            LastCreate = command;
            return Task.FromResult(command.RecipeId switch
            {
                "__validation__" => Result.Failure<RecipeDto>(Error.Validation("x", "bad")),
                _ => Result.Success(new RecipeDto(
                    command.RecipeId, command.Name, command.Description,
                    command.EquipmentClassId, 1, "Draft", null, null, null)),
            });
        }

        public static RecipeCommandContext? LastApprovalContext { get; private set; }
        public static RecipeParamUpdateCommand? LastParamUpdate { get; private set; }

        public Task<Result> RequestApprovalAsync(
            string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        {
            LastApprovalContext = context;
            return MapResult(recipeId);
        }

        public Task<Result> Approve1Async(
            string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        {
            LastApprovalContext = context;
            return MapResult(recipeId);
        }

        public Task<Result> Approve2Async(
            string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        {
            LastApprovalContext = context;
            return MapResult(recipeId);
        }

        public Task<Result> ReleaseAsync(
            string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        {
            LastApprovalContext = context;
            return MapResult(recipeId);
        }

        public Task<Result> RejectAsync(
            string recipeId, string reason, RecipeCommandContext context, CancellationToken ct = default)
        {
            LastApprovalContext = context;
            return MapResult(recipeId);
        }

        public Task<Result<RecipeDto>> CreateNewVersionAsync(
            RecipeVersionCreateCommand command, CancellationToken ct = default)
        {
            LastVersionCreate = command;
            return Task.FromResult(command.SourceRecipeId switch
            {
                "__conflict__" => Result.Failure<RecipeDto>(Error.Conflict("c")),
                _ => Result.Success(new RecipeDto(command.NewRecipeId, "n", "d", "EC1", 2, "Draft", null, null, null)),
            });
        }

        public Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeParamDto>>(
                new[] { new RecipeParamDto("P1", recipeId, "p", "v", "u", 1, 1) });

        public Task<Result<RecipeParamDto>> AddParamAsync(
            RecipeParamAddCommand command, CancellationToken ct = default)
        {
            LastParamAdd = command;
            return Task.FromResult(command.RecipeId switch
            {
                "__conflict__" => Result.Failure<RecipeParamDto>(Error.Conflict("c")),
                _ => Result.Success(new RecipeParamDto(
                    command.ParamId, command.RecipeId, command.ParamName, command.ParamValue,
                    command.Unit, command.SortOrder, 1)),
            });
        }

        public Task<Result> UpdateParamAsync(
            RecipeParamUpdateCommand command, CancellationToken ct = default)
        {
            LastParamUpdate = command;
            return MapResult(command.ParamId);
        }
        public Task<Result> DeleteParamAsync(
            RecipeParamDeleteCommand command, CancellationToken ct = default)
        {
            LastParamDelete = command;
            return MapResult(command.ParamId);
        }

        // 비-제네릭 Result 분기 공통: __conflict__→409, __notfound__→404, 그 외 성공(→204).
        private static Task<Result> MapResult(string id) => Task.FromResult(id switch
        {
            "__conflict__" => Result.Failure(Error.Conflict("c")),
            "__notfound__" => Result.Failure(Error.NotFound("Recipe", "x")),
            _ => Result.Success(),
        });
    }

    private sealed class FakeExecutionBridge : IRecipeExecutionBridge
    {
        public static RecipeAssignmentCommand? LastAssignment { get; private set; }
        public static RecipeExecutionCommand? LastExecution { get; private set; }

        public Task<Result<RecipeAssignmentDto>> AssignAsync(
            RecipeAssignmentCommand command, CancellationToken ct = default)
        {
            LastAssignment = command;
            return Task.FromResult(Result.Success(new RecipeAssignmentDto(
                command.AssignmentId, command.EquipmentId, command.EquipmentClassId,
                command.RecipeId, command.RecipeVersion, command.EffectiveFrom ?? DateTime.UtcNow,
                null, command.ActorId ?? "", true)));
        }

        public Task<IReadOnlyList<RecipeAssignmentDto>> GetAssignmentsAsync(
            string? equipmentId = null,
            string? equipmentClassId = null,
            bool activeOnly = true,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeAssignmentDto>>(new[]
            {
                new RecipeAssignmentDto("A1", equipmentId ?? "EQ01", equipmentClassId,
                    "R1", 1, DateTime.UtcNow, null, "operator", true),
            });

        public Task<Result<RecipeExecutionSnapshotDto>> RecordExecutionAsync(
            RecipeExecutionCommand command, CancellationToken ct = default)
        {
            LastExecution = command;
            return Task.FromResult(Result.Success(new RecipeExecutionSnapshotDto(
                command.ExecutionId, command.IdempotencyKey, command.PlantId, command.EquipmentId,
                command.ProcessLotId, command.WorkOrderId, command.ProcessId,
                command.RecipeId, command.RecipeVersion, "{}", "[]",
                command.ConditionSnapshotJson, command.ActorId ?? "", command.AppliedAt,
                command.Source, command.TraceId, false)));
        }

        public Task<Result<RecipeExecutionSnapshotDto>> GetExecutionAsync(
            string executionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new RecipeExecutionSnapshotDto(
                executionId, "idem", "PLANT01", "EQ01", null, null, null,
                "R1", 1, "{}", "[]", null, "operator", DateTime.UtcNow,
                "Equipment", null, false)));
    }

    private HttpClient Client(params string[] permissions)
        => Client(includeActor: true, permissions);

    private HttpClient Client(bool includeActor, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>();
        if (includeActor)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, "bridge-tester"));
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static Task<HttpResponseMessage> PutCommandAsync(
        HttpClient client, string url, object? body = null, string key = "rms-command-test")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostCommandAsync(
        HttpClient client, string url, object body, string key = "rms-command-test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteCommandAsync(
        HttpClient client, string url, string key = "rms-command-test")
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Create_recipe_success_returns_200_with_dto()
    {
        var res = await PostCommandAsync(Client("rms:manage"), "/api/v1/rms/recipes",
            new { recipeId = "R1", name = "n", description = "d", equipmentClassId = "EC1" },
            "create-r1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<RecipeDto>();
        dto!.RecipeId.Should().Be("R1");
        dto.ApprovalState.Should().Be("Draft");
        FakeBridge.LastCreate.Should().Be(new RecipeCreateCommand(
            "R1", "n", "d", "EC1", "create-r1", "bridge-tester"));
    }

    [Fact]
    public async Task Approve1_conflict_maps_to_409()
    {
        var res = await PutCommandAsync(
            Client("rms:manage"), "/api/v1/rms/recipes/__conflict__/approve1");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태위반(Conflict)은 409로 매핑");
    }

    [Fact]
    public async Task RequestApproval_notfound_maps_to_404()
    {
        var res = await PutCommandAsync(
            Client("rms:manage"), "/api/v1/rms/recipes/__notfound__/request-approval");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재(NotFound)는 404로 매핑");
    }

    [Fact]
    public async Task Create_recipe_validation_maps_to_400()
    {
        var res = await PostCommandAsync(Client("rms:manage"), "/api/v1/rms/recipes",
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
        var res = await PutCommandAsync(
            Client("rms:manage"), "/api/v1/rms/recipes/R1/approve1", key: "approve-1-key");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "비제네릭 Result 성공은 204(NoContent)로 매핑");
        FakeBridge.LastApprovalContext.Should().Be(
            new RecipeCommandContext("bridge-tester", "approve-1-key"));
    }

    [Fact]
    public async Task Released_parameter_update_conflict_maps_to_409()
    {
        var res = await PutCommandAsync(
            Client("rms:manage"),
            "/api/v1/rms/recipes/params/__conflict__",
            new { newValue = "190", expectedVersion = 3 },
            "param-update-key");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        FakeBridge.LastParamUpdate.Should().Be(new RecipeParamUpdateCommand(
            "__conflict__", "190", 3, "param-update-key", "bridge-tester"));
    }

    [Fact]
    public async Task Released_parameter_delete_conflict_maps_to_409()
    {
        var res = await DeleteCommandAsync(Client("rms:manage"),
            "/api/v1/rms/recipes/params/__conflict__?expectedVersion=3",
            "param-delete-key");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        FakeBridge.LastParamDelete.Should().Be(new RecipeParamDeleteCommand(
            "__conflict__", 3, "param-delete-key", "bridge-tester"));
    }

    [Fact]
    public async Task New_version_and_parameter_add_preserve_authenticated_actor_and_header_key()
    {
        var client = Client("rms:manage");
        var version = await PostCommandAsync(
            client, "/api/v1/rms/recipes/R1/new-version",
            new { newRecipeId = "R1_V2" }, "version-key");
        var add = await PostCommandAsync(
            client, "/api/v1/rms/recipes/R1/params",
            new { paramId = "P1", paramName = "Temperature", paramValue = "180", unit = "C", sortOrder = 1 },
            "param-add-key");

        version.StatusCode.Should().Be(HttpStatusCode.OK);
        add.StatusCode.Should().Be(HttpStatusCode.OK);
        FakeBridge.LastVersionCreate.Should().Be(new RecipeVersionCreateCommand(
            "R1", "R1_V2", "version-key", "bridge-tester"));
        FakeBridge.LastParamAdd.Should().Be(new RecipeParamAddCommand(
            "P1", "R1", "Temperature", "180", "C", 1,
            "param-add-key", "bridge-tester"));
    }

    [Fact]
    public async Task Recipe_create_without_idempotency_header_is_bad_request()
    {
        var res = await Client("rms:manage").PostAsJsonAsync("/api/v1/rms/recipes",
            new { recipeId = "R_NO_KEY", name = "n", description = "d", equipmentClassId = "EC1" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetRecipes_by_state_returns_200_for_reader()
    {
        var res = await Client("rms:read").GetAsync("/api/v1/rms/recipes?state=Draft");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "rms:read 보유 조회는 200");
        var rows = await res.Content.ReadFromJsonAsync<List<RecipeDto>>();
        rows!.Should().ContainSingle(r => r.RecipeId == "R1");
    }

    [Fact]
    public async Task GetRecipes_without_filters_returns_all_recipes()
    {
        var res = await Client("rms:read").GetAsync("/api/v1/rms/recipes");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<RecipeDto>>();
        rows!.Select(recipe => recipe.RecipeId).Should().BeEquivalentTo("R1", "R2");
    }

    [Fact]
    public async Task GetRecipes_combines_equipment_class_and_state_filters()
    {
        var res = await Client("rms:read").GetAsync(
            "/api/v1/rms/recipes?equipmentClassId=EC2&state=Released");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<RecipeDto>>();
        rows.Should().ContainSingle(recipe => recipe.RecipeId == "R2");
    }

    [Fact]
    public async Task GetRecipes_combined_filter_does_not_leak_other_equipment_class()
    {
        var res = await Client("rms:read").GetAsync(
            "/api/v1/rms/recipes?equipmentClassId=EC1&state=Released");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<RecipeDto>>();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Recipe_read_without_rms_read_is_forbidden()
    {
        var res = await Client("fdc:read").GetAsync("/api/v1/rms/recipes");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approval_without_identity_claim_is_unauthorized_and_never_uses_system_actor()
    {
        var res = await PutCommandAsync(
            Client(includeActor: false, "rms:manage"),
            "/api/v1/rms/recipes/R1/approve1");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Approval_without_idempotency_header_is_bad_request()
    {
        var res = await Client("rms:manage")
            .PutAsync("/api/v1/rms/recipes/R1/approve1", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Assignment_overwrites_body_actor_with_authenticated_identity()
    {
        var res = await Client("rms:manage").PostAsJsonAsync("/api/v1/rms/assignments", new
        {
            assignmentId = "A1",
            equipmentId = "EQ01",
            equipmentClassId = (string?)null,
            recipeId = "R1",
            recipeVersion = 1,
            actorId = "spoofed",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        FakeExecutionBridge.LastAssignment!.ActorId.Should().Be("bridge-tester");
    }

    [Fact]
    public async Task Execution_preserves_header_idempotency_key_and_authenticated_actor()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rms/executions")
        {
            Content = JsonContent.Create(new
            {
                executionId = "EXE1",
                idempotencyKey = "body-key",
                plantId = "PLANT01",
                equipmentId = "EQ01",
                recipeId = "R1",
                recipeVersion = 1,
                appliedAt = DateTime.UtcNow,
                source = "Equipment",
                actorId = "spoofed",
            }),
        };
        request.Headers.Add("Idempotency-Key", "header-key");

        var res = await Client("rms:manage").SendAsync(request);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        FakeExecutionBridge.LastExecution!.IdempotencyKey.Should().Be("header-key");
        FakeExecutionBridge.LastExecution.ActorId.Should().Be("bridge-tester");
    }

    [Fact]
    public async Task Assignment_read_requires_rms_read()
    {
        (await Client("fdc:read").GetAsync("/api/v1/rms/assignments"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("rms:read").GetAsync("/api/v1/rms/assignments"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
