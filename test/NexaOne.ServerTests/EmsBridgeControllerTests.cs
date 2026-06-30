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
using NexaOne.ServiceContracts.Ems;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>EMS 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008, S5) — modules OFF + 가짜 IEmsBridge 주입으로
/// 권한(403/200)·생성(200·검증 400)·상태전이(204·Conflict→409·NotFound→404)를 Spring/ALC 없이 결정적으로 검증한다.
/// 작업지시·보전계획·예비품 3개 애그리거트의 쓰기 경로(ems:manage 게이트)를 커버한다.</summary>
public sealed class EmsBridgeControllerTests : IClassFixture<EmsBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-ems-bridge-e2e-jwt-secret-key-at-least-32b";
    private const string Issuer = "nexaone-ems-bridge-test";
    private readonly BridgeFactory _factory;
    public EmsBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-ems-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s => s.AddSingleton<IEmsBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 상태전이는 id 규약으로 분기한다: "CONFLICT"→409, "MISSING"→404, 그 외 성공.
    // 생성은 woType/cycleType 등 검증 실패 입력이면 도메인 검증 Error를 흉내내 400으로 매핑되는지 본다.
    private sealed class FakeBridge : IEmsBridge
    {
        public Task<Result<WorkOrderDto>> CreateWorkOrderAsync(
            string woId, string equipmentId, string woType, string description, string assigneeId, CancellationToken ct = default)
            => Task.FromResult(woType is "PM" or "CM"
                ? Result.Success(new WorkOrderDto(woId, null, equipmentId, woType, description, assigneeId,
                    DateTime.UtcNow, null, null, "Issued", null, null))
                : Result.Failure<WorkOrderDto>(Error.Validation(nameof(woType), "Work order type must be 'PM' or 'CM'.")));

        public Task<Result> StartWorkOrderAsync(string woId, CancellationToken ct = default) => Transition(woId);
        public Task<Result> CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default) => Transition(woId);
        public Task<Result> CancelWorkOrderAsync(string woId, CancellationToken ct = default) => Transition(woId);

        public Task<Result<MaintenancePlanDto>> CreatePlanAsync(
            string planId, string planName, string equipmentId, string planType, string cycleType,
            DateTime scheduledDate, decimal estimatedHours, string assigneeId, CancellationToken ct = default)
            => Task.FromResult(estimatedHours > 0
                ? Result.Success(new MaintenancePlanDto(planId, planName, equipmentId, planType, cycleType,
                    scheduledDate, estimatedHours, assigneeId, "Planned"))
                : Result.Failure<MaintenancePlanDto>(Error.Validation("estimatedDurationHours", "Estimated duration must be positive.")));

        public Task<Result> StartPlanAsync(string planId, CancellationToken ct = default) => Transition(planId);
        public Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default) => Transition(planId);
        public Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default) => Transition(planId);

        public Task<Result<SparePartDto>> CreatePartAsync(
            string partId, string partName, string partNumber, string description, string unitOfMeasure,
            decimal currentStock, decimal minStock, decimal maxStock, string location,
            string? equipmentClassId, CancellationToken ct = default)
            => Task.FromResult(maxStock > minStock
                ? Result.Success(new SparePartDto(partId, partName, partNumber, description, unitOfMeasure,
                    currentStock, minStock, maxStock, location, equipmentClassId, currentStock <= minStock))
                : Result.Failure<SparePartDto>(Error.Validation("maxStock", "Max stock must be greater than min stock.")));

        public Task<Result> AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default)
            => Task.FromResult(partId == "MISSING"
                ? Result.Failure(Error.NotFound("SparePart", partId))
                : delta < -1000m
                    ? Result.Failure(Error.Validation(nameof(delta), "Insufficient stock."))
                    : Result.Success());

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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "ems-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // ── 권한 게이트 ──

    [Fact]
    public async Task CreateWorkOrder_without_ems_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/ems/work-orders",
            new { woId = "WO1", equipmentId = "EQ1", woType = "PM", description = "점검", assigneeId = "U1" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ems:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task AdjustStock_without_ems_manage_is_forbidden()
    {
        var res = await Client().PostAsJsonAsync("/api/v1/ems/spare-parts/P1/adjust-stock", new { delta = 5m });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "무권한 쓰기는 403");
    }

    // ── 작업지시 ──

    [Fact]
    public async Task CreateWorkOrder_with_ems_manage_returns_200_with_dto()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders",
            new { woId = "WO1", equipmentId = "EQ1", woType = "CM", description = "수리", assigneeId = "U1" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<WorkOrderDto>();
        dto!.WoId.Should().Be("WO1");
        dto.Status.Should().Be("Issued");
    }

    [Fact]
    public async Task CreateWorkOrder_invalid_type_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders",
            new { woId = "WO1", equipmentId = "EQ1", woType = "XX", description = "수리", assigneeId = "U1" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(Validation)는 400");
    }

    [Fact]
    public async Task StartWorkOrder_success_returns_204()
    {
        var res = await Client("ems:manage").PostAsync("/api/v1/ems/work-orders/WO1/start", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task CompleteWorkOrder_conflict_maps_to_409()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders/CONFLICT/complete",
            new { remark = "완료" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태전이 위반(Conflict)은 409");
    }

    [Fact]
    public async Task CancelWorkOrder_not_found_maps_to_404()
    {
        var res = await Client("ems:manage").PostAsync("/api/v1/ems/work-orders/MISSING/cancel", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 작업지시는 404");
    }

    // ── 보전계획 ──

    [Fact]
    public async Task CreatePlan_with_ems_manage_returns_200_with_dto()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/plans",
            new { planId = "PL1", planName = "월간점검", equipmentId = "EQ1", planType = "PM",
                  cycleType = "Monthly", scheduledDate = DateTime.UtcNow, estimatedHours = 2.5m, assigneeId = "U1" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<MaintenancePlanDto>();
        dto!.PlanId.Should().Be("PL1");
        dto.Status.Should().Be("Planned");
    }

    [Fact]
    public async Task CreatePlan_nonpositive_hours_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/plans",
            new { planId = "PL1", planName = "월간점검", equipmentId = "EQ1", planType = "PM",
                  cycleType = "Monthly", scheduledDate = DateTime.UtcNow, estimatedHours = 0m, assigneeId = "U1" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartPlan_conflict_maps_to_409()
    {
        var res = await Client("ems:manage").PostAsync("/api/v1/ems/plans/CONFLICT/start", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── 예비품 ──

    [Fact]
    public async Task CreatePart_with_ems_manage_returns_200_with_dto()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts",
            new { partId = "SP1", partName = "베어링", partNumber = "BR-001", description = "구동부", unitOfMeasure = "EA",
                  currentStock = 10m, minStock = 5m, maxStock = 50m, location = "A-1", equipmentClassId = (string?)null });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<SparePartDto>();
        dto!.PartId.Should().Be("SP1");
        dto.IsLowStock.Should().BeFalse();
    }

    [Fact]
    public async Task CreatePart_invalid_stock_bounds_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts",
            new { partId = "SP1", partName = "베어링", partNumber = "BR-001", description = "구동부", unitOfMeasure = "EA",
                  currentStock = 10m, minStock = 50m, maxStock = 5m, location = "A-1", equipmentClassId = (string?)null });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "max<=min 검증 실패는 400");
    }

    [Fact]
    public async Task AdjustStock_success_returns_204()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/SP1/adjust-stock", new { delta = -3m });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AdjustStock_missing_part_maps_to_404()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/MISSING/adjust-stock", new { delta = 1m });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdjustStock_insufficient_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/SP1/adjust-stock", new { delta = -5000m });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "재고 부족(Validation)은 400");
    }
}
