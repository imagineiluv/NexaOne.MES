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
        public FakeBridge Bridge { get; } = new();
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-ems-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // 이 슬라이스는 가짜 IEmsBridge의 HTTP/인증 계약만 검증한다. 개발 DB 시드와
            // 전체 마이그레이션에 결합하지 않아 병렬 스키마 작업이 컨트롤러 테스트를 가리지 않게 한다.
            builder.UseEnvironment("Testing");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureTestServices(s => s.AddSingleton<IEmsBridge>(Bridge));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 상태전이는 id 규약으로 분기한다: "CONFLICT"→409, "MISSING"→404, 그 외 성공.
    // 생성은 woType/cycleType 등 검증 실패 입력이면 도메인 검증 Error를 흉내내 400으로 매핑되는지 본다.
    public sealed class FakeBridge : IEmsBridge
    {
        public EmsCommandContextDto? LastCommand { get; private set; }
        public SparePartAdjustmentDto? LastAdjustment { get; private set; }
        public string? LastPartActor { get; private set; }
        public int InvocationCount { get; private set; }

        private void Capture(EmsCommandContextDto command)
        {
            LastCommand = command;
            InvocationCount++;
        }

        public Task<Result<WorkOrderDto>> CreateWorkOrderAsync(
            string woId, string equipmentId, string woType, string description, string assigneeId,
            string? maintenancePlanId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Task.FromResult(woType is "PM" or "BM" or "CM"
                ? Result.Success(new WorkOrderDto(woId, null, equipmentId, woType, description, assigneeId,
                    DateTime.UtcNow, null, null, "Issued", null, null))
                : Result.Failure<WorkOrderDto>(Error.Validation(nameof(woType), "Work order type must be 'PM' or 'BM'.")));
        }

        public Task<Result> StartWorkOrderAsync(
            string woId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(woId);
        }
        public Task<Result> CompleteWorkOrderAsync(
            string woId, string remark, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(woId);
        }
        public Task<Result> CancelWorkOrderAsync(
            string woId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(woId);
        }

        public Task<Result<MaintenancePlanDto>> CreatePlanAsync(
            string planId, string planName, string equipmentId, string planType, string cycleType,
            DateTime scheduledDate, decimal estimatedHours, string assigneeId,
            EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Task.FromResult(estimatedHours > 0
                ? Result.Success(new MaintenancePlanDto(planId, planName, equipmentId, planType, cycleType,
                    scheduledDate, estimatedHours, assigneeId, "Planned"))
                : Result.Failure<MaintenancePlanDto>(Error.Validation("estimatedDurationHours", "Estimated duration must be positive.")));
        }

        public Task<Result> StartPlanAsync(
            string planId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(planId);
        }

        public Task<Result> CompletePlanAsync(
            string planId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(planId);
        }

        public Task<Result> CancelPlanAsync(
            string planId, EmsCommandContextDto command, CancellationToken ct = default)
        {
            Capture(command);
            return Transition(planId);
        }

        public Task<Result<SparePartDto>> CreatePartAsync(
            string partId, string partName, string partNumber, string description, string unitOfMeasure,
            decimal currentStock, decimal minStock, decimal maxStock, string location,
            string? equipmentClassId, string actorId, CancellationToken ct = default)
        {
            LastPartActor = actorId;
            InvocationCount++;
            return Task.FromResult(maxStock > minStock
                ? Result.Success(new SparePartDto(partId, partName, partNumber, description, unitOfMeasure,
                    currentStock, minStock, maxStock, location, equipmentClassId, currentStock <= minStock))
                : Result.Failure<SparePartDto>(Error.Validation("maxStock", "Max stock must be greater than min stock.")));
        }

        public Task<Result> AdjustStockAsync(
            string partId, SparePartAdjustmentDto adjustment, CancellationToken ct = default)
        {
            Capture(adjustment.Command);
            LastAdjustment = adjustment;
            var delta = adjustment.Delta;
            return Task.FromResult(partId == "MISSING"
                ? Result.Failure(Error.NotFoundOf("SparePart", partId))   // 다국어 키(error.notFound) 실린 표준 NotFound
                : delta < -1000m
                    ? Result.Failure(Error.Validation(nameof(delta), "Insufficient stock."))
                    : Result.Success());
        }

        private static Task<Result> Transition(string id) => Task.FromResult(id switch
        {
            "CONFLICT" => Result.Failure(Error.Conflict("Invalid state transition.")),
            "MISSING"  => Result.Failure(Error.NotFound("Entity", id)),
            _          => Result.Success(),
        });
    }

    private HttpClient Client(params string[] permissions) => ClientCore("ems-bridge-tester", permissions);

    private HttpClient ClientWithoutActor(params string[] permissions) => ClientCore(null, permissions);

    private HttpClient ClientCore(string? actor, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>();
        if (actor is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, actor));
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
            new { woId = "WO1", equipmentId = "EQ1", woType = "PM", description = "점검", assigneeId = "U1", idempotencyKey = "wo-forbidden" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ems:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task AdjustStock_without_ems_manage_is_forbidden()
    {
        var res = await Client().PostAsJsonAsync("/api/v1/ems/spare-parts/P1/adjust-stock", new { delta = 5m, idempotencyKey = "part-forbidden" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "무권한 쓰기는 403");
    }

    // ── 작업지시 ──

    [Fact]
    public async Task CreateWorkOrder_with_ems_manage_returns_200_with_dto()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders",
            new { woId = "WO1", equipmentId = "EQ1", woType = "CM", description = "수리", assigneeId = "U1", idempotencyKey = "wo-create" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<WorkOrderDto>();
        dto!.WoId.Should().Be("WO1");
        dto.Status.Should().Be("Issued");
    }

    [Fact]
    public async Task CreateWorkOrder_invalid_type_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders",
            new { woId = "WO1", equipmentId = "EQ1", woType = "XX", description = "수리", assigneeId = "U1", idempotencyKey = "wo-invalid" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(Validation)는 400");
    }

    [Fact]
    public async Task StartWorkOrder_success_returns_204()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders/WO1/start",
            new { idempotencyKey = "wo-start", clientChannel = "POP", deviceId = "PANEL-01", correlationId = "corr-wo" });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
        _factory.Bridge.LastCommand.Should().Be(new EmsCommandContextDto(
            "ems-bridge-tester", "wo-start", "POP", "PANEL-01", "corr-wo"));
    }

    [Fact]
    public async Task CompleteWorkOrder_conflict_maps_to_409()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders/CONFLICT/complete",
            new { remark = "완료", idempotencyKey = "wo-complete-conflict" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태전이 위반(Conflict)은 409");
    }

    [Fact]
    public async Task CancelWorkOrder_not_found_maps_to_404()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/work-orders/MISSING/cancel",
            new { idempotencyKey = "wo-cancel-missing" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 작업지시는 404");
    }

    // ── 보전계획 ──

    [Fact]
    public async Task CreatePlan_with_ems_manage_returns_200_with_dto()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/plans",
            new { planId = "PL1", planName = "월간점검", equipmentId = "EQ1", planType = "PM",
                  cycleType = "Monthly", scheduledDate = DateTime.UtcNow, estimatedHours = 2.5m, assigneeId = "U1",
                  idempotencyKey = "plan-create", clientChannel = "POP", deviceId = "PANEL-02",
                  correlationId = "corr-plan-create", actorId = "payload-actor-must-be-ignored" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<MaintenancePlanDto>();
        dto!.PlanId.Should().Be("PL1");
        dto.Status.Should().Be("Planned");
        _factory.Bridge.LastCommand.Should().Be(new EmsCommandContextDto(
            "ems-bridge-tester", "plan-create", "POP", "PANEL-02", "corr-plan-create"));
    }

    [Fact]
    public async Task CreatePlan_nonpositive_hours_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/plans",
            new { planId = "PL1", planName = "월간점검", equipmentId = "EQ1", planType = "PM",
                  cycleType = "Monthly", scheduledDate = DateTime.UtcNow, estimatedHours = 0m, assigneeId = "U1",
                  idempotencyKey = "plan-invalid" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartPlan_conflict_maps_to_409()
    {
        var res = await Client("ems:manage").PostAsJsonAsync(
            "/api/v1/ems/plans/CONFLICT/start",
            new { idempotencyKey = "plan-start-conflict" });
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
        _factory.Bridge.LastPartActor.Should().Be("ems-bridge-tester");
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
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/SP1/adjust-stock",
            new
            {
                delta = -3m, idempotencyKey = "part-usage", transactionType = "Usage",
                workOrderId = "WO1", equipmentId = "EQ1", bomItemId = "BOM1"
            });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Bridge.LastAdjustment.Should().NotBeNull();
        _factory.Bridge.LastAdjustment!.Command.ActorId.Should().Be("ems-bridge-tester");
        _factory.Bridge.LastAdjustment.EquipmentId.Should().Be("EQ1");
        _factory.Bridge.LastAdjustment.BomItemId.Should().Be("BOM1");
    }

    [Fact]
    public async Task AdjustStock_missing_part_maps_to_404()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/MISSING/adjust-stock",
            new { delta = 1m, idempotencyKey = "part-missing" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdjustStock_insufficient_maps_to_400()
    {
        var res = await Client("ems:manage").PostAsJsonAsync("/api/v1/ems/spare-parts/SP1/adjust-stock",
            new { delta = -5000m, idempotencyKey = "part-short" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "재고 부족(Validation)은 400");
    }

    // ── 서버 오류 메시지 다국어(P3-14) — Error.MessageKey를 Accept-Language로 번역 ──

    private static async Task<string?> ReadDescriptionAsync(HttpClient client, string acceptLanguage)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ems/spare-parts/MISSING/adjust-stock")
        {
            Content = JsonContent.Create(new { delta = 1m, idempotencyKey = $"part-missing-{acceptLanguage}" }),
        };
        req.Headers.Add("Accept-Language", acceptLanguage);
        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        return body?.Description;
    }

    private sealed record ErrorBody(string Code, string Description);

    [Fact]
    public async Task NotFound_description_is_english_when_accept_language_en()
    {
        // en-US 요청 → 응답 경계 필터가 error.notFound(EnUs) 리소스로 Description 치환.
        var desc = await ReadDescriptionAsync(Client("ems:manage"), "en-US");
        desc.Should().Be("SparePart 'MISSING' was not found.");
    }

    [Fact]
    public async Task NotFound_description_stays_korean_without_english_accept_language()
    {
        // ko-KR(또는 미지정) → 한국어 원문(Description) 유지 — 한국어는 기본이라 번역하지 않는다.
        var desc = await ReadDescriptionAsync(Client("ems:manage"), "ko-KR");
        desc.Should().Be("SparePart 'MISSING'을(를) 찾을 수 없습니다.");
    }

    [Fact]
    public async Task WorkOrder_execution_without_actor_claim_is_unauthorized_before_bridge()
    {
        var before = _factory.Bridge.InvocationCount;
        var res = await ClientWithoutActor("ems:manage").PostAsJsonAsync(
            "/api/v1/ems/work-orders/WO1/start",
            new { idempotencyKey = "wo-no-actor" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Bridge.InvocationCount.Should().Be(before);
    }

    [Fact]
    public async Task MaintenancePlan_execution_without_actor_claim_is_unauthorized_before_bridge()
    {
        var before = _factory.Bridge.InvocationCount;
        var res = await ClientWithoutActor("ems:manage").PostAsJsonAsync(
            "/api/v1/ems/plans/PL1/start",
            new { idempotencyKey = "plan-no-actor" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Bridge.InvocationCount.Should().Be(before);
    }
}
