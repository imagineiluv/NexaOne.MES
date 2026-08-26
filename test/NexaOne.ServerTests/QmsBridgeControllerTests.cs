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
using NexaOne.ServiceContracts.Qms;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>QMS 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008) — modules OFF + 가짜 IQmsBridge 주입으로
/// 읽기 200·쓰기 권한(403/204)·상태전이(Conflict→409·Validation→400)를 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class QmsBridgeControllerTests : IClassFixture<QmsBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-qms-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-qms-bridge-test";
    private readonly BridgeFactory _factory;
    public QmsBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-qms-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s => s.AddSingleton<IQmsBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed class FakeBridge : IQmsBridge
    {
        public Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DefectDto>>(
                new[] { new DefectDto("DF1", lotId, "EQ1", "DC1", 3, 0.05m, DateTime.UtcNow, "insp1", null, false, null) });

        public Task<Result<DefectDto>> RecordDefectAsync(string id, string lotId, string equipmentId,
            string defectClassId, int defectCount, decimal defectRate, string inspectorId,
            string? remark, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new DefectDto(id, lotId, equipmentId, defectClassId,
                defectCount, defectRate, DateTime.UtcNow, inspectorId, remark, false, null)));

        public Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
            => Task.FromResult(defectId == "CONFLICT"
                ? Result.Failure(Error.Conflict("Defect is already confirmed."))
                : Result.Success());

        public Task<Result> UpdateControlLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
            => Task.FromResult(ucl <= lcl
                ? Result.Failure(Error.Validation(nameof(ucl), "UCL must be greater than LCL."))
                : Result.Success());

        public Task<IReadOnlyList<DefectClassDto>> GetDefectClassesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DefectClassDto>>([new("DC1", "Scratch", "", "Minor", true)]);
        public Task<Result<DefectClassDto>> CreateDefectClassAsync(string id, string name, string description,
            string severity, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new DefectClassDto(id, name, description, severity, true)));
        public Task<IReadOnlyList<InspectionSpecDto>> GetInspectionSpecsAsync(string? processId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InspectionSpecDto>>([]);
        public Task<Result<InspectionSpecDto>> CreateInspectionSpecAsync(string id, string name, string processId,
            string itemName, string measureType, decimal? nominalValue, decimal? tolerancePlus,
            decimal? toleranceMinus, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new InspectionSpecDto(id, name, processId, itemName,
                measureType, nominalValue, tolerancePlus, toleranceMinus, true)));
        public Task<IReadOnlyList<InspectionResultDto>> GetInspectionResultsByLotAsync(string lotId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InspectionResultDto>>([]);
        public Task<Result<InspectionResultDto>> RecordInspectionResultAsync(string id, string specId,
            string lotId, string equipmentId, string inspectorId, decimal? measuredValue,
            string? attributeResult, bool? isPass, string? remark, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new InspectionResultDto(id, id, specId, lotId, equipmentId,
                measuredValue, attributeResult, DateTime.UtcNow, inspectorId, isPass ?? true, remark)));
        public Task<Result<InspectionResultDto>> RecordInspectionExecutionAsync(string inspectionType,
            string id, string specId, string lotId, string equipmentId, string inspectorId,
            decimal? measuredValue, string? attributeResult, bool? isPass, string? remark,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(new InspectionResultDto(id, id, specId, lotId, equipmentId,
                measuredValue, attributeResult, DateTime.UtcNow, inspectorId, isPass ?? true,
                inspectionType)));
        public Task<LotInspectionStatusDto> GetLotInspectionStatusAsync(string lotId, CancellationToken ct = default)
            => Task.FromResult(new LotInspectionStatusDto(lotId, true, true, 1, 0, DateTime.UtcNow));
        public Task<Result<InspectionExecutionV2Dto>> RecordInspectionExecutionV2Async(
            RecordInspectionExecutionV2Dto request, string actorId, CancellationToken ct = default)
            => Task.FromResult(request.IdempotencyKey == "CONFLICT"
                ? Result.Failure<InspectionExecutionV2Dto>(Error.Conflict(
                    "The idempotency key was already used for another request."))
                : Result.Success(V2Dto(request.IdempotencyKey, request.InspectionType,
                    actorId, request.IdempotencyKey == "REPLAY")));
        public Task<Result<InspectionExecutionV2Dto>> GetInspectionExecutionV2Async(
            string inspectionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(V2Dto("GET-KEY", "Process", "qa-reader", false)));
        public Task<Result<InspectionExecutionV2Dto>> CancelInspectionExecutionV2Async(
            string inspectionId, string idempotencyKey, string reason, string actorId,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(V2Dto(idempotencyKey, "Process", actorId, false)
                with { IsCancelled = true }));

        private static InspectionExecutionV2Dto V2Dto(
            string key, string inspectionType, string actor, bool replay)
            => new(
                "QMSI-SERVER", inspectionType, "Original", "QMSI-SERVER", null,
                "LOT1", "EQ1", 10, 10, 0, key, new string('a', 64), DateTime.UtcNow,
                actor, true, false, replay, null, null,
                [new("QMSR-SERVER", "SPEC1", 10m, null, 10, 0, true, null)],
                [], []);
        public Task<IReadOnlyList<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpcParamDto>>([]);
        public Task<Result<SpcParamDto>> CreateSpcParamAsync(string id, string name, string equipmentId,
            string processId, decimal mean, decimal ucl, decimal lcl, int sampleSize,
            decimal? usl, decimal? lsl, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new SpcParamDto(id, name, equipmentId, processId,
                mean, ucl, lcl, usl, lsl, sampleSize, true)));

        public Task<Result<SpcLimitRevisionDto>> AddSpcLimitRevisionAsync(string id, string paramId,
            int revisionNo, string chartType, decimal centerLine, decimal ucl, decimal lcl,
            DateTime effectiveFrom, string reason, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new SpcLimitRevisionDto(id, paramId, revisionNo,
                chartType, centerLine, ucl, lcl, effectiveFrom, reason)));
        public Task<Result<SpcSubgroupEvaluationDto>> EvaluateSpcSubgroupAsync(string subgroupId,
            string idempotencyKey, string limitRevisionId, DateTime observedAt,
            IReadOnlyList<decimal> values, string sourceType, string actorId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new SpcSubgroupEvaluationDto(subgroupId, "P1",
                limitRevisionId, "IndividualsMovingRange", observedAt, values, [], false)));
        public Task<IReadOnlyList<SpcRuleViolationDto>> GetSpcViolationsAsync(string? paramId,
            string? subgroupId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpcRuleViolationDto>>([]);
        public Task<Result<SamplingPlanRevisionDto>> AddSamplingPlanRevisionAsync(string id,
            string planId, int revisionNo, string mode, int lotSizeMin, int? lotSizeMax,
            int? sampleSize, int acceptanceNumber, int rejectionNumber, decimal aql,
            string standardName, string standardVersion, DateTime effectiveFrom, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new SamplingPlanRevisionDto(id, planId, revisionNo,
                mode, lotSizeMin, lotSizeMax, sampleSize, acceptanceNumber, rejectionNumber,
                aql, standardName, standardVersion, effectiveFrom)));
        public Task<Result<SamplingPlanRevisionDto>> SelectSamplingPlanAsync(int lotSize,
            DateTime effectiveAt, CancellationToken ct = default)
            => AddSamplingPlanRevisionAsync("PR1", "PLAN1", 1, "Sampling", 1, 1000,
                80, 2, 3, 1m, "ISO 2859-1", "2026", effectiveAt, ct);
        public async Task<Result<SamplingEvaluationDto>> EvaluateSamplingAsync(int lotSize,
            int inspectedQuantity, int defectQuantity, DateTime effectiveAt, CancellationToken ct = default)
        {
            var plan = await SelectSamplingPlanAsync(lotSize, effectiveAt, ct);
            return Result.Success(new SamplingEvaluationDto(plan.Value,
                new SamplingDecisionDto(defectQuantity <= 2 ? "Accept" : "Reject", 80,
                    inspectedQuantity, defectQuantity, "fake")));
        }
        public Task<Result<AiModelVersionDto>> RegisterAiModelVersionAsync(string id, string modelId,
            int versionNo, string artifactUri, string artifactSha256, decimal confidenceThreshold,
            DateTime effectiveFrom, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new AiModelVersionDto(id, modelId, versionNo,
                artifactUri, artifactSha256, confidenceThreshold, effectiveFrom)));
        public Task<Result<AiInferenceDto>> RecordAiInferenceAsync(string id, string idempotencyKey,
            string modelVersionId, string inspectionId, string imageUri, string imageSha256,
            string rawVerdict, decimal confidence, DateTime inferredAt, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new AiInferenceDto(id, idempotencyKey, modelVersionId,
                inspectionId, imageUri, imageSha256, rawVerdict, confidence, .9m,
                inferredAt, confidence < .9m)));
        public Task<Result<AiInferenceDto>> GetAiInferenceAsync(string inferenceId, CancellationToken ct = default)
            => RecordAiInferenceAsync(inferenceId, "KEY1", "MV1", "INSP1",
                "https://images.local/i.png", new string('a', 64), "Pass", .95m, DateTime.UtcNow, ct);
        public Task<IReadOnlyList<AiReviewDto>> GetAiReviewsAsync(string inferenceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AiReviewDto>>([]);
        public Task<Result<AiReviewDto>> ReviewAiInferenceAsync(string reviewId, string inferenceId,
            string reviewerId, string verdict, string reason, DateTime reviewedAt, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new AiReviewDto(reviewId, inferenceId, 1,
                reviewerId, verdict, reason, reviewedAt)));
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "qms-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task GetDefects_returns_rows_for_qms_reader()
    {
        var res = await Client("qms:read").GetAsync("/api/v1/qms/defects?lotId=LOT1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<DefectDto>>();
        rows!.Should().ContainSingle(d => d.Id == "DF1" && d.LotId == "LOT1");
    }

    [Fact]
    public async Task ConfirmDefect_without_qms_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/qms/defects/DF1/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "qms:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task ConfirmDefect_success_returns_204()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/defects/DF1/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task ConfirmDefect_conflict_maps_to_409()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/defects/CONFLICT/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "재확정(Conflict)은 409로 매핑");
    }

    [Fact]
    public async Task UpdateControlLimits_without_qms_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 12.0m, lcl = 8.0m });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "qms:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task UpdateControlLimits_success_returns_204()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 12.0m, lcl = 8.0m });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 갱신은 204");
    }

    [Fact]
    public async Task RecordDefect_uses_authenticated_actor_and_requires_manage_permission()
    {
        var body = new { id = "DF2", lotId = "LOT1", equipmentId = "EQ1", defectClassId = "DC1", defectCount = 1, defectRate = .1m };
        (await Client().PostAsJsonAsync("/api/v1/qms/defects", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var response = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/defects", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<DefectDto>())!.InspectorId.Should().Be("qms-bridge-tester");
    }

    [Fact]
    public async Task RecordInspectionResult_routes_explicit_type_to_typed_bridge_method()
    {
        var response = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/inspection-results", new
        {
            id = "IR-INCOMING",
            specId = "SPEC1",
            lotId = "LOT1",
            equipmentId = "EQ1",
            measuredValue = 10m,
            attributeResult = (string?)null,
            isPass = (bool?)null,
            remark = "legacy-path",
            inspectionType = "Incoming"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<InspectionResultDto>();
        result!.Remark.Should().Be("Incoming");
        result.InspectorId.Should().Be("qms-bridge-tester");
    }

    [Fact]
    public async Task V2_execution_prefers_idempotency_header_and_uses_JWT_actor()
    {
        var client = Client("qms:manage");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/qms/inspection-executions")
        {
            Content = JsonContent.Create(V2Request("BODY-KEY"))
        };
        request.Headers.Add("Idempotency-Key", "REPLAY");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "동일 요청 재생은 새 리소스를 만들지 않는다");
        var dto = await response.Content.ReadFromJsonAsync<InspectionExecutionV2Dto>();
        dto!.IdempotencyKey.Should().Be("REPLAY");
        dto.IsReplay.Should().BeTrue();
        dto.InspectorId.Should().Be("qms-bridge-tester");
    }

    [Fact]
    public async Task V2_new_execution_returns_201_and_reused_key_conflict_returns_409()
    {
        var client = Client("qms:manage");
        (await client.PostAsJsonAsync("/api/v2/qms/inspection-executions", V2Request("NEW-KEY")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/v2/qms/inspection-executions", V2Request("CONFLICT")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task V2_execution_details_require_qms_read_permission()
    {
        (await Client().GetAsync("/api/v2/qms/inspection-executions/QMSI-SERVER"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("fdc:read").GetAsync("/api/v2/qms/inspection-executions/QMSI-SERVER"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var allowed = await Client("qms:read")
            .GetAsync("/api/v2/qms/inspection-executions/QMSI-SERVER");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<InspectionExecutionV2Dto>())!
            .AiEvidence.Should().NotBeNull();
    }

    private static RecordInspectionExecutionV2Dto V2Request(string key)
        => new(
            key, "Process", "LOT1", "EQ1", 10, 10, 0,
            [new("SPEC1", 10m, null, 10, 0)],
            Remark: "controller-test");

    [Theory]
    [InlineData("GET", "/api/v1/qms/defects?lotId=LOT1")]
    [InlineData("GET", "/api/v1/qms/defect-classes")]
    [InlineData("GET", "/api/v1/qms/inspection-specs")]
    [InlineData("GET", "/api/v1/qms/inspection-results?lotId=LOT1")]
    [InlineData("GET", "/api/v1/qms/lots/LOT1/inspection-status")]
    [InlineData("GET", "/api/v2/qms/inspection-executions/QMSI-SERVER")]
    [InlineData("GET", "/api/v1/qms/spc-params?equipmentId=EQ1")]
    [InlineData("GET", "/api/v1/qms/spc/violations?paramId=P1")]
    [InlineData("GET", "/api/v1/qms/sampling-plans/select?lotSize=100")]
    [InlineData("POST", "/api/v1/qms/sampling-plans/evaluate")]
    [InlineData("GET", "/api/v1/qms/ai/inferences/I1")]
    [InlineData("GET", "/api/v1/qms/ai/inferences/I1/reviews")]
    public async Task Qms_read_endpoints_require_qms_read_permission(string method, string path)
    {
        var denied = await Client().SendAsync(CreateQmsReadRequest(method, path));
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowed = await Client("qms:read").SendAsync(CreateQmsReadRequest(method, path));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpRequestMessage CreateQmsReadRequest(string method, string path)
        => new(new HttpMethod(method), path)
        {
            Content = method == "POST"
                ? JsonContent.Create(new
                {
                    lotSize = 100,
                    inspectedQuantity = 80,
                    defectQuantity = 1,
                    effectiveAt = (DateTime?)null
                })
                : null
        };

    [Fact]
    public async Task Web_client_routes_are_available()
    {
        var client = Client("qms:read");
        (await client.GetAsync("/api/v1/qms/defect-classes")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/qms/inspection-specs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/qms/inspection-results?lotId=LOT1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/qms/spc-params?equipmentId=EQ1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/qms/lots/LOT1/inspection-status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Advanced_QMS_writes_require_manage_permission()
    {
        var body = new { id = "MV1", modelId = "M1", versionNo = 1, artifactUri = "https://models.local/m.onnx",
            artifactSha256 = new string('a', 64), confidenceThreshold = .9m, effectiveFrom = DateTime.UtcNow };
        (await Client().PostAsJsonAsync("/api/v1/qms/ai/models/versions", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/ai/models/versions", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AI_inference_prefers_idempotency_header_and_review_uses_JWT_actor()
    {
        var client = Client("qms:manage");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "HEADER-KEY");
        var inference = await client.PostAsJsonAsync("/api/v1/qms/ai/inferences", new
        {
            id = "I1", idempotencyKey = "BODY-KEY", modelVersionId = "MV1", inspectionId = "INSP1",
            imageUri = "https://images.local/i.png", imageSha256 = new string('a', 64),
            rawVerdict = "Pass", confidence = .95m, inferredAt = DateTime.UtcNow
        });
        (await inference.Content.ReadFromJsonAsync<AiInferenceDto>())!.IdempotencyKey.Should().Be("HEADER-KEY");

        var review = await client.PostAsJsonAsync("/api/v1/qms/ai/inferences/I1/reviews",
            new { id = "R1", verdict = "Pass", reason = "manual review" });
        (await review.Content.ReadFromJsonAsync<AiReviewDto>())!.ReviewerId.Should().Be("qms-bridge-tester");
    }

    [Fact]
    public async Task AI_evidence_reads_return_content_for_qms_reader()
    {
        var reader = Client("qms:read");
        var inference = await reader.GetAsync("/api/v1/qms/ai/inferences/I1");
        inference.StatusCode.Should().Be(HttpStatusCode.OK);
        (await inference.Content.ReadFromJsonAsync<AiInferenceDto>())!.Id.Should().Be("I1");

        var reviews = await reader.GetAsync("/api/v1/qms/ai/inferences/I1/reviews");
        reviews.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reviews.Content.ReadFromJsonAsync<List<AiReviewDto>>()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateControlLimits_invalid_limits_maps_to_400()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 8.0m, lcl = 12.0m });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "UCL<=LCL 불변식 위반(Validation)은 400으로 매핑");
    }
}
