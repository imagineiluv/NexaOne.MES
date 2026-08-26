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
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>POM 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008, S6) — modules OFF + 가짜 IPomBridge 주입으로
/// 권한(403/200)·생성(200·검증 400)·상태전이(204·Conflict→409·NotFound→404)를 Spring/ALC 없이 결정적으로 검증한다.
/// 생산계획·생산오더·Lot 추적(TrackIn/TrackOut/Hold/Release)의 쓰기 경로(pom:manage 게이트)를 커버한다.
/// Lot Mixing(다중 애그리거트)은 브리지에 없으므로 검증 대상이 아니다(의도적 보류).</summary>
public sealed class PomBridgeControllerTests : IClassFixture<PomBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-pom-bridge-e2e-jwt-secret-key-at-least-32b";
    private const string Issuer = "nexaone-pom-bridge-test";
    private readonly BridgeFactory _factory;
    private static string? _lastHoldChannel;
    private static string? _lastHoldDeviceId;
    private static string? _lastReviewChannel;
    private static string? _lastReviewDeviceId;
    public PomBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-pom-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s => s.AddSingleton<IPomBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 상태전이는 id 규약으로 분기한다: "CONFLICT"→409, "MISSING"→404, 그 외 성공.
    // 생성은 검증 실패 입력(빈 이름/비양수 수량)이면 도메인 검증 Error를 흉내내 400으로 매핑되는지 본다.
    private sealed class FakeBridge : IPomBridge
    {
        public Task<Result<ProductionPlanDto>> CreatePlanAsync(
            string planId, string planName, string plantId, string productId, decimal plannedQty,
            DateTime plannedStartDate, DateTime plannedEndDate, CancellationToken ct = default)
            => Task.FromResult(plannedQty > 0
                ? Result.Success(new ProductionPlanDto(planId, planName, plantId, productId, plannedQty,
                    plannedStartDate, plannedEndDate, "Draft", null))
                : Result.Failure<ProductionPlanDto>(Error.Validation(nameof(plannedQty), "Planned quantity must be positive.")));

        public Task<Result> ReleasePlanAsync(string planId, CancellationToken ct = default) => Transition(planId);
        public Task<Result> StartPlanAsync(string planId, CancellationToken ct = default) => Transition(planId);
        public Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default) => Transition(planId);
        public Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default) => Transition(planId);

        public Task<Result<ProductionOrderDto>> CreateOrderAsync(
            string orderId, string planId, string equipmentId, string productId, decimal orderQty,
            DateTime scheduledStart, DateTime scheduledEnd, CancellationToken ct = default)
            => Task.FromResult(orderQty > 0
                ? Result.Success(new ProductionOrderDto(orderId, planId, equipmentId, productId, orderQty,
                    null, scheduledStart, scheduledEnd, null, null, "Issued"))
                : Result.Failure<ProductionOrderDto>(Error.Validation(nameof(orderQty), "Order quantity must be positive.")));

        public Task<Result> StartOrderAsync(string orderId, CancellationToken ct = default) => Transition(orderId);
        public Task<Result> CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default)
            => Task.FromResult(actualQty < 0
                ? Result.Failure(Error.Validation(nameof(actualQty), "Actual quantity must be non-negative."))
                : Transition(orderId).Result);
        public Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default) => Transition(orderId);

        public Task<Result<LotDto>> CreateLotAsync(
            string plantId, string lotId, string? workOrderId, string productId,
            decimal qty, IReadOnlyList<string> routeSteps, string user, CancellationToken ct = default)
            => Task.FromResult(qty > 0 && routeSteps.Count > 0
                ? Result.Success(Lot(lotId, plantId, productId, qty, "Queued"))
                : Result.Failure<LotDto>(Error.Validation(nameof(qty), "Lot quantity must be positive and route required.")));

        public Task<Result<LotDto>> TrackInAsync(
            string plantId, string lotId, string equipmentId,
            string? recipeDefId, int? recipeDefVersion, string user, CancellationToken ct = default,
            int? expectedVersion = null, string? idempotencyKey = null,
            string clientChannel = "MES", string? deviceId = null)
            => Task.FromResult(lotId switch
            {
                "MISSING" => Result.Failure<LotDto>(Error.NotFound("Lot", lotId)),
                "CONFLICT" => Result.Failure<LotDto>(Error.Conflict($"Lot 상태에서는 TrackIn할 수 없습니다.")),
                _ => Result.Success(Lot(lotId, plantId, "PROD01", 10m, "Processing")),
            });

        public Task<Result<LotDto>> TrackOutAsync(
            string plantId, string lotId, string equipmentId, decimal qty,
            IReadOnlyList<LotDefectInput>? defects, string? carrierId, string user, CancellationToken ct = default,
            int? expectedVersion = null, string? idempotencyKey = null,
            string clientChannel = "MES", string? deviceId = null)
            => Task.FromResult(lotId switch
            {
                "MISSING" => Result.Failure<LotDto>(Error.NotFound("Lot", lotId)),
                "CONFLICT" => Result.Failure<LotDto>(Error.Conflict("TrackIn 설비와 TrackOut 설비가 일치해야 합니다.")),
                "QUALITY_PENDING" => Result.Failure<LotDto>(Error.Conflict(
                    "Production quality gate is Pending; final TrackOut is blocked.")),
                _ when (defects?.Any(d => d.DefectQty < 0) ?? false)
                    => Result.Failure<LotDto>(Error.Validation("defects", "불량수량은 음수일 수 없습니다.")),
                _ => Result.Success(Lot(lotId, plantId, "PROD01", qty, "Completed")),
            });

        public Task<Result> HoldLotAsync(string lotId, string user, CancellationToken ct = default,
            int? expectedVersion = null, string? idempotencyKey = null, string? reason = null,
            string clientChannel = "MES", string? deviceId = null)
        {
            _lastHoldChannel = clientChannel;
            _lastHoldDeviceId = deviceId;
            return Transition(lotId);
        }
        public Task<Result> ReleaseLotHoldAsync(string lotId, string user, CancellationToken ct = default,
            int? expectedVersion = null, string? idempotencyKey = null, string? reason = null,
            string clientChannel = "MES", string? deviceId = null) => Transition(lotId);

        public Task<Result<LotRoutingContextDto>> GetLotRoutingContextAsync(
            string lotId, CancellationToken ct = default)
            => Task.FromResult(lotId == "MISSING"
                ? Result.Failure<LotRoutingContextDto>(Error.NotFound("Lot", lotId))
                : Result.Success(new LotRoutingContextDto(
                    Lot(lotId, "P1", "PROD01", 10m, "Queued"),
                    "Flexible", 0, "CUT", 1, "ASSY", null, null, false, Array.Empty<RouteExceptionDto>())));

        public Task<Result<RoutingPolicyDecisionDto>> EvaluateLotRoutingAsync(
            string plantId, string lotId, string deviationType, int targetStepIndex,
            string? reason, string? exceptionId = null, CancellationToken ct = default)
        {
            var decision = lotId switch
            {
                "STRICT" => new RoutingPolicyDecisionDto(
                    "Block", "ROUTE_PREDECESSOR_REQUIRED", "이전 공정이 완료되지 않았습니다.",
                    "Strict", deviationType, 0, targetStepIndex, true, null, false),
                "FLEX" => new RoutingPolicyDecisionDto(
                    "ApprovalRequired", "ROUTE_APPROVAL_REQUIRED", "관리자 승인이 필요합니다.",
                    "Flexible", deviationType, 0, targetStepIndex, true, null, false),
                _ => new RoutingPolicyDecisionDto(
                    "AllowWithWarning", "ROUTE_DEVIATION_ALLOWED", "사유와 함께 즉시 적용됩니다.",
                    "NoControl", deviationType, 0, targetStepIndex, true, exceptionId, true),
            };
            return Task.FromResult(Result.Success(decision));
        }

        public Task<Result<LotDto>> ChangeLotRoutingControlModeAsync(
            string plantId, string lotId, string controlMode, string reason, string user,
            int expectedVersion, string idempotencyKey, string clientChannel,
            string? deviceId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success(Lot(lotId, plantId, "PROD01", 10m, "Queued") with
            {
                ControlMode = controlMode,
                VersionNo = expectedVersion + 1,
            }));

        public Task<Result<LotDto>> ApplyLotRouteDeviationAsync(
            string plantId, string lotId, string deviationType, int targetStepIndex,
            string reason, string user, int expectedVersion, string idempotencyKey,
            string? exceptionId, string clientChannel, string? deviceId = null,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(Lot(lotId, plantId, "PROD01", 10m, "Queued") with
            {
                CurrentStepIndex = targetStepIndex,
                CurrentProcessId = "ASSY",
                VersionNo = expectedVersion + 1,
            }));

        public Task<Result<RouteExceptionDto>> RequestLotRouteExceptionAsync(
            string exceptionId, string plantId, string lotId, string deviationType,
            int targetStepIndex, string reason, string user, int expectedVersion,
            DateTime expiresAt, string clientChannel, string? deviceId = null,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(RouteException(
                exceptionId, lotId, plantId, deviationType, targetStepIndex,
                reason, user, expectedVersion, expiresAt, clientChannel, deviceId, "Requested")));

        public Task<Result<RouteExceptionDto>> ApproveLotRouteExceptionAsync(
            string exceptionId, string reviewer, string? reason = null, CancellationToken ct = default,
            string clientChannel = "MES", string? deviceId = null)
        {
            _lastReviewChannel = clientChannel;
            _lastReviewDeviceId = deviceId;
            return Task.FromResult(Result.Success(RouteException(
                exceptionId, "LOT1", "P1", "Bypass", 1, "설비 고장",
                "operator", 1, DateTime.UtcNow.AddMinutes(30), "MES", null, "Approved") with
            {
                ReviewedBy = reviewer,
                ReviewedAt = DateTime.UtcNow,
                ReviewReason = reason,
            }));
        }

        public Task<Result<RouteExceptionDto>> RejectLotRouteExceptionAsync(
            string exceptionId, string reviewer, string? reason = null, CancellationToken ct = default,
            string clientChannel = "MES", string? deviceId = null)
        {
            _lastReviewChannel = clientChannel;
            _lastReviewDeviceId = deviceId;
            return Task.FromResult(Result.Success(RouteException(
                exceptionId, "LOT1", "P1", "Bypass", 1, "설비 고장",
                "operator", 1, DateTime.UtcNow.AddMinutes(30), "MES", null, "Rejected") with
            {
                ReviewedBy = reviewer,
                ReviewedAt = DateTime.UtcNow,
                ReviewReason = reason,
            }));
        }

        public Task<Result<RouteExceptionDto>> GetLotRouteExceptionAsync(
            string exceptionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(RouteException(
                exceptionId, "LOT1", "P1", "Bypass", 1, "설비 고장",
                "operator", 1, DateTime.UtcNow.AddMinutes(30), "MES", null, "Requested")));

        public Task<Result<LotDto>> MixingTrackInOutAsync(
            string plantId, string outputLotId, string productId, string equipmentId,
            IReadOnlyList<string> outputRouteSteps, IReadOnlyList<MixingInputDto> inputs,
            string user, CancellationToken ct = default)
            => Task.FromResult(outputLotId switch
            {
                "MISSING" => Result.Failure<LotDto>(Error.NotFound("Lot", "IN-MISSING")),
                _ when inputs.Count == 0
                    => Result.Failure<LotDto>(Error.Validation("inputs", "투입 Lot이 1건 이상 필요합니다.")),
                _ => Result.Success(Lot(outputLotId, plantId, productId, inputs.Sum(i => i.InQty), "Created")),
            });

        private static LotDto Lot(string lotId, string plantId, string productId, decimal qty, string state)
            => new(lotId, plantId, null, productId, qty, 0m, state, "Idle", new[] { "CUT", "ASSY" }, 0,
                "CUT", null, null, null, null, false);

        private static RouteExceptionDto RouteException(
            string exceptionId, string lotId, string plantId, string deviationType,
            int targetStepIndex, string reason, string requestedBy, int version,
            DateTime expiresAt, string channel, string? deviceId, string status)
            => new(
                exceptionId, lotId, plantId, deviationType, 0, targetStepIndex,
                "CUT", "ASSY", version, reason, status, requestedBy,
                DateTime.UtcNow, expiresAt, null, null, null, null, null, null,
                channel, deviceId);

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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "pom-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private HttpClient ClientWithoutActor(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = permissions.Select(permission =>
            new Claim(NexaOne.Common.Security.Permissions.ClaimType, permission));
        var token = new JwtSecurityToken(
            Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // ── 권한 게이트 ──

    [Fact]
    public async Task CreatePlan_without_pom_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/pom/plans",
            new { planId = "PL1", planName = "주간계획", plantId = "P1", productId = "PROD01",
                  plannedQty = 100m, plannedStartDate = DateTime.UtcNow, plannedEndDate = DateTime.UtcNow.AddDays(1) });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "pom:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task HoldLot_without_pom_execute_is_forbidden()
    {
        var res = await Client().PostAsync("/api/v1/pom/lots/LOT1/hold", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "무권한 쓰기는 403");
    }

    // ── 생산계획 ──

    [Fact]
    public async Task CreatePlan_with_pom_manage_returns_200_with_dto()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/plans",
            new { planId = "PL1", planName = "주간계획", plantId = "P1", productId = "PROD01",
                  plannedQty = 100m, plannedStartDate = DateTime.UtcNow, plannedEndDate = DateTime.UtcNow.AddDays(1) });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<ProductionPlanDto>();
        dto!.PlanId.Should().Be("PL1");
        dto.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task CreatePlan_nonpositive_qty_maps_to_400()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/plans",
            new { planId = "PL1", planName = "주간계획", plantId = "P1", productId = "PROD01",
                  plannedQty = 0m, plannedStartDate = DateTime.UtcNow, plannedEndDate = DateTime.UtcNow.AddDays(1) });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(Validation)는 400");
    }

    [Fact]
    public async Task ReleasePlan_success_returns_204()
    {
        var res = await Client("pom:manage").PostAsync("/api/v1/pom/plans/PL1/release", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task StartPlan_conflict_maps_to_409()
    {
        var res = await Client("pom:manage").PostAsync("/api/v1/pom/plans/CONFLICT/start", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태전이 위반(Conflict)은 409");
    }

    [Fact]
    public async Task CancelPlan_not_found_maps_to_404()
    {
        var res = await Client("pom:manage").PostAsync("/api/v1/pom/plans/MISSING/cancel", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 계획은 404");
    }

    // ── 생산오더 ──

    [Fact]
    public async Task CreateOrder_with_pom_manage_returns_200_with_dto()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/orders",
            new { orderId = "WO1", planId = "PL1", equipmentId = "EQ1", productId = "PROD01", orderQty = 50m,
                  scheduledStart = DateTime.UtcNow, scheduledEnd = DateTime.UtcNow.AddHours(8) });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<ProductionOrderDto>();
        dto!.OrderId.Should().Be("WO1");
        dto.Status.Should().Be("Issued");
    }

    [Fact]
    public async Task CreateOrder_nonpositive_qty_maps_to_400()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/orders",
            new { orderId = "WO1", planId = "PL1", equipmentId = "EQ1", productId = "PROD01", orderQty = 0m,
                  scheduledStart = DateTime.UtcNow, scheduledEnd = DateTime.UtcNow.AddHours(8) });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartOrder_success_returns_204()
    {
        var res = await Client("pom:manage").PostAsync("/api/v1/pom/orders/WO1/start", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CompleteOrder_negative_actual_maps_to_400()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/orders/WO1/complete",
            new { actualQty = -1m });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "음수 실적은 검증 실패(400)");
    }

    [Fact]
    public async Task CompleteOrder_conflict_maps_to_409()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/orders/CONFLICT/complete",
            new { actualQty = 50m });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Lot 추적 ──

    [Fact]
    public async Task CreateLot_with_pom_manage_returns_200_with_dto()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/lots",
            new { plantId = "P1", lotId = "LOT1", workOrderId = (string?)null, productId = "PROD01",
                  qty = 10m, routeSteps = new[] { "CUT", "ASSY" } });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<LotDto>();
        dto!.LotId.Should().Be("LOT1");
        dto.State.Should().Be("Queued");
    }

    [Fact]
    public async Task CreateLot_without_actor_claim_fails_closed()
    {
        var res = await ClientWithoutActor("pom:manage").PostAsJsonAsync("/api/v1/pom/lots",
            new
            {
                plantId = "P1", lotId = "LOT-NO-ACTOR", workOrderId = (string?)null,
                productId = "PROD01", qty = 10m, routeSteps = new[] { "CUT" }
            });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TrackIn_success_returns_200()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-in",
            new { plantId = "P1", equipmentId = "EQ1", recipeDefId = "RCP01", recipeDefVersion = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<LotDto>();
        dto!.State.Should().Be("Processing");
    }

    [Fact]
    public async Task TrackIn_missing_lot_maps_to_404()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/MISSING/track-in",
            new { plantId = "P1", equipmentId = "EQ1", recipeDefId = (string?)null, recipeDefVersion = (int?)null });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TrackOut_conflict_maps_to_409()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/CONFLICT/track-out",
            new { plantId = "P1", equipmentId = "EQ1", qty = 10m, defects = (object?)null, carrierId = (string?)null });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TrackOut_quality_gate_conflict_maps_to_409()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/QUALITY_PENDING/track-out",
            new { plantId = "P1", equipmentId = "EQ1", qty = 10m, defects = (object?)null, carrierId = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TrackOut_negative_defect_maps_to_400()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-out",
            new { plantId = "P1", equipmentId = "EQ1", qty = 10m,
                  defects = new[] { new { defectCode = "D1", defectQty = -1m } }, carrierId = (string?)null });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TrackOut_defect_total_above_quantity_is_rejected_before_mutation()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-out",
            new
            {
                plantId = "P1", equipmentId = "EQ1", qty = 10m,
                defects = new[] { new { defectCode = "D1", defectQty = 11m } },
                carrierId = (string?)null, clientChannel = "POP", deviceId = "KIOSK-03"
            });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TrackOut_null_defect_row_is_rejected_as_bad_request()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-out",
            new
            {
                plantId = "P1", equipmentId = "EQ1", qty = 10m,
                defects = new object?[] { null }, carrierId = (string?)null,
                clientChannel = "MOBILE", deviceId = "PDA-07"
            });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lot_tracking_rejects_unknown_client_channel()
    {
        var res = await Client("pom:execute").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-in",
            new { plantId = "P1", equipmentId = "EQ1", clientChannel = "UNKNOWN" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HoldLot_success_returns_204()
    {
        var res = await Client("pom:execute").PostAsync("/api/v1/pom/lots/LOT1/hold", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task HoldLot_preserves_pop_channel_and_device_audit_context()
    {
        _lastHoldChannel = null;
        _lastHoldDeviceId = null;

        var res = await Client("pom:execute").PostAsync(
            "/api/v1/pom/lots/LOT1/hold?clientChannel=POP&deviceId=KIOSK-03",
            content: null);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _lastHoldChannel.Should().Be("POP");
        _lastHoldDeviceId.Should().Be("KIOSK-03");
    }

    [Fact]
    public async Task Hold_and_release_without_actor_claim_fail_closed()
    {
        var client = ClientWithoutActor("pom:execute");

        (await client.PostAsync("/api/v1/pom/lots/LOT1/hold", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/pom/lots/LOT1/release", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReleaseLot_not_found_maps_to_404()
    {
        var res = await Client("pom:execute").PostAsync("/api/v1/pom/lots/MISSING/release", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lot_execution_requires_execute_not_manage_permission()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/lots/LOT1/track-in",
            new { plantId = "P1", equipmentId = "EQ1", expectedVersion = 1, idempotencyKey = "track-1" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "관리 권한과 현장 실행 권한은 분리되어야 한다");
    }

    // ── LOT 라우팅 통제/예외 ──

    [Fact]
    public async Task Routing_context_requires_read_and_returns_current_next_route()
    {
        var res = await Client("pom:read").GetAsync("/api/v1/pom/lots/LOT1/routing-context");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<LotRoutingContextDto>();
        dto!.ControlMode.Should().Be("Flexible");
        dto.CurrentProcessId.Should().Be("CUT");
        dto.NextProcessId.Should().Be("ASSY");
    }

    [Fact]
    public async Task Strict_routing_evaluation_returns_structured_block_reason()
    {
        var res = await Client("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/lots/STRICT/routing/evaluate",
            new { plantId = "P1", deviationType = "Bypass", targetStepIndex = 1, reason = "설비 고장" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<RoutingPolicyDecisionDto>();
        dto!.Kind.Should().Be("Block");
        dto.Code.Should().Be("ROUTE_PREDECESSOR_REQUIRED");
        dto.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Flexible_exception_request_uses_request_permission_only()
    {
        var request = new
        {
            plantId = "P1", deviationType = "Bypass", targetStepIndex = 1,
            reason = "열처리 설비 고장", expectedVersion = 1,
            expiresAt = DateTime.UtcNow.AddMinutes(30), exceptionId = "REX-1",
            clientChannel = "MOBILE", deviceId = "PDA-07"
        };

        (await Client("pom:execute").PostAsJsonAsync(
            "/api/v1/pom/lots/FLEX/routing/exceptions", request)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        var allowed = await Client("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/lots/FLEX/routing/exceptions", request);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await allowed.Content.ReadFromJsonAsync<RouteExceptionDto>();
        dto!.Status.Should().Be("Requested");
        dto.ClientChannel.Should().Be("MOBILE");
    }

    [Fact]
    public async Task Exception_request_requires_client_stable_exception_id()
    {
        var res = await Client("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/lots/FLEX/routing/exceptions",
            new
            {
                plantId = "P1", deviationType = "Bypass", targetStepIndex = 1,
                reason = "설비 고장", expectedVersion = 1,
                expiresAt = DateTime.UtcNow.AddMinutes(30), exceptionId = "   ",
                clientChannel = "MOBILE"
            });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Routing_mutation_without_authenticated_actor_fails_closed()
    {
        var res = await ClientWithoutActor("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/lots/NOCONTROL/routing/deviations",
            new
            {
                plantId = "P1", deviationType = "Alternative", targetStepIndex = 1,
                reason = "병목 우회", expectedVersion = 1, idempotencyKey = "route-no-actor",
                clientChannel = "MES"
            });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exception_approval_is_separate_from_request_permission()
    {
        var requestOnly = await Client("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/routing/exceptions/REX-1/approve", new { reason = "검토 완료" });
        requestOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "예외 요청자는 승인 권한 없이 자신의 요청을 승인할 수 없어야 한다");

        _lastReviewChannel = null;
        _lastReviewDeviceId = null;
        var approved = await Client("pom:routing.approve").PostAsJsonAsync(
            "/api/v1/pom/routing/exceptions/REX-1/approve",
            new { reason = "검토 완료", clientChannel = "POP", deviceId = "KIOSK-03" });
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await approved.Content.ReadFromJsonAsync<RouteExceptionDto>();
        dto!.Status.Should().Be("Approved");
        dto.ClientChannel.Should().Be("MES",
            "requester channel remains the request provenance and must not be overwritten by review provenance");
        _lastReviewChannel.Should().Be("POP");
        _lastReviewDeviceId.Should().Be("KIOSK-03");
    }

    [Fact]
    public async Task NoControl_deviation_apply_preserves_audited_request_fields()
    {
        var res = await Client("pom:routing.request").PostAsJsonAsync(
            "/api/v1/pom/lots/NOCONTROL/routing/deviations",
            new
            {
                plantId = "P1", deviationType = "Alternative", targetStepIndex = 1,
                reason = "병목 우회", expectedVersion = 1, idempotencyKey = "route-apply-1",
                clientChannel = "POP", deviceId = "KIOSK-03"
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<LotDto>();
        dto!.CurrentStepIndex.Should().Be(1);
        dto.VersionNo.Should().Be(2);
    }

    // ── Lot Mixing(다중 애그리거트, DATA-3 원자화로 노출) ──

    [Fact]
    public async Task Mixing_without_pom_manage_is_forbidden()
    {
        var res = await Client("pom:read").PostAsJsonAsync("/api/v1/pom/lots/mixing/track-in-out",
            new { plantId = "P1", outputLotId = "OUT1", productId = "PROD01", equipmentId = "EQ1",
                  inputs = new[] { new { lotId = "IN1", inQty = 5m } } });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Mixing은 pom:manage 쓰기");
    }

    [Fact]
    public async Task Mixing_success_returns_output_lot()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/lots/mixing/track-in-out",
            new { plantId = "P1", outputLotId = "OUT1", productId = "PROD01", equipmentId = "EQ1",
                  outputRouteSteps = new[] { "CUT" },
                  inputs = new[] { new { lotId = "IN1", inQty = 5m }, new { lotId = "IN2", inQty = 3m } } });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<LotDto>();
        dto!.LotId.Should().Be("OUT1");
        dto.Qty.Should().Be(8m, "출력 수량 = 투입 합(가짜 브리지 규약)");
    }

    [Fact]
    public async Task Mixing_without_actor_claim_fails_closed()
    {
        var res = await ClientWithoutActor("pom:manage").PostAsJsonAsync(
            "/api/v1/pom/lots/mixing/track-in-out",
            new
            {
                plantId = "P1", outputLotId = "OUT-NO-ACTOR", productId = "PROD01", equipmentId = "EQ1",
                outputRouteSteps = new[] { "CUT" }, inputs = new[] { new { lotId = "IN1", inQty = 5m } }
            });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mixing_empty_inputs_maps_to_400()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/lots/mixing/track-in-out",
            new { plantId = "P1", outputLotId = "OUT1", productId = "PROD01", equipmentId = "EQ1" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "투입 없는 Mixing(Validation)은 400");
    }

    [Fact]
    public async Task Mixing_missing_input_lot_maps_to_404()
    {
        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/lots/mixing/track-in-out",
            new { plantId = "P1", outputLotId = "MISSING", productId = "PROD01", equipmentId = "EQ1",
                  inputs = new[] { new { lotId = "IN1", inQty = 5m } } });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 투입 Lot은 404");
    }
}
