using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using Moq;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;

namespace NexaOne.UnitTests.Web;

/// <summary>명명 command의 전송 성공과 실제 DB 반영 성공을 구분하는 공통 API 경계 회귀 테스트.</summary>
public sealed class ApiClientCommandResultTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task Standard_affected_response_reports_actual_application(int affected, bool expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { affected }),
        };

        (await ApiClient.IsCommandAppliedAsync(response)).Should().Be(expected);
    }

    [Fact]
    public async Task Non_success_http_response_is_not_applied()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        (await ApiClient.IsCommandAppliedAsync(response)).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_empty_success_response_keeps_compatibility()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        };

        (await ApiClient.IsCommandAppliedAsync(response)).Should().BeTrue();
    }

    [Fact]
    public async Task Qms_v2_request_transmits_idempotency_header_and_preserves_conflict_details()
    {
        HttpRequestMessage? captured = null;
        var handler = new CaptureHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { code = "Conflict", description = "key collision" })
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nexaone.local/") };
        var storage = new ProtectedSessionStorage(
            Mock.Of<IJSRuntime>(), new EphemeralDataProtectionProvider());
        var tokens = new AuthTokenService(storage);
        var client = new ApiClient(http, tokens, new JwtAuthStateProvider(tokens),
            new ApiNotificationService(), new UiTextService());
        var request = new RecordInspectionExecutionV2Request(
            "QMS-IDEMPOTENCY-1", "Process", "LOT-1", "EQ-1", 10, 10, 0,
            [new InspectionExecutionItemInputDto("SPEC-1", 10m, null, 10, 0)]);

        var result = await client.RecordInspectionExecutionV2Async(request);

        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/api/v2/qms/inspection-executions");
        captured.Headers.GetValues("Idempotency-Key").Should().ContainSingle("QMS-IDEMPOTENCY-1");
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("key collision");
    }

    [Fact]
    public async Task Paged_query_capability_rejection_falls_back_without_error_notification()
    {
        HttpRequestMessage? captured = null;
        var handler = new CaptureHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = JsonContent.Create(new
                {
                    code = "PAGED_QUERY_UNSUPPORTED",
                    description = "query declares its own limit",
                }),
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nexaone.local/") };
        var storage = new ProtectedSessionStorage(
            Mock.Of<IJSRuntime>(), new EphemeralDataProtectionProvider());
        var tokens = new AuthTokenService(storage);
        var notifications = new ApiNotificationService();
        var surfaced = new List<ApiNotification>();
        notifications.OnNotify += surfaced.Add;
        var client = new ApiClient(http, tokens, new JwtAuthStateProvider(tokens),
            notifications, new UiTextService());

        var result = await client.ExecuteQueryPagedAsync("SYS.AppLogList", limit: 500, offset: 0);

        result.Should().BeNull("422는 기존 전량 조회로 전환하라는 capability 신호다");
        surfaced.Should().BeEmpty("정상 폴백을 사용자 오류 토스트로 노출하면 안 된다");
        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/api/v1/query/SYS.AppLogList/paged");
    }

    [Fact]
    public async Task Pom_routing_client_escapes_lot_path_and_preserves_interlock_conflict_reason()
    {
        HttpRequestMessage? captured = null;
        var handler = new CaptureHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    code = "POM_ROUTE_PREDECESSOR_INCOMPLETE",
                    description = "이전 공정이 완료되지 않았습니다.",
                }),
            };
        });
        var client = CreateClient(handler);

        var result = await client.EvaluatePomLotRoutingAsync(
            "LOT A/01",
            new PomEvaluateRoutingRequest("P1", "Normal", 0));

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/pom/lots/LOT%20A%2F01/routing/evaluate");
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("이전 공정이 완료되지 않았습니다.");
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Pom_routing_review_rejects_unsupported_action_without_http_call()
    {
        var calls = 0;
        var client = CreateClient(new CaptureHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await client.ReviewPomLotRouteExceptionAsync(
            "apply", "REX-1", new PomReviewRouteExceptionRequest());

        result.StatusCode.Should().Be(400);
        result.Error.Should().Contain("지원하지 않는");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Pom_lot_hold_client_preserves_mobile_audit_context_in_query_contract()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(new CaptureHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        var applied = await client.HoldLotAsync(
            "LOT A/01",
            new PomLotHoldRequest(
                ExpectedVersion: 7,
                IdempotencyKey: "hold key/01",
                Reason: "긴급 품질 확인",
                ClientChannel: "MOBILE",
                DeviceId: "PDA 07"));

        applied.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/api/v1/pom/lots/LOT%20A%2F01/hold");
        captured.RequestUri.Query.Should().Contain("clientChannel=MOBILE")
            .And.Contain("expectedVersion=7")
            .And.Contain("idempotencyKey=hold%20key%2F01")
            .And.Contain("reason=%EA%B8%B4%EA%B8%89%20%ED%92%88%EC%A7%88%20%ED%99%95%EC%9D%B8")
            .And.Contain("deviceId=PDA%2007");
    }

    [Theory]
    [InlineData("release")]
    [InlineData("cancel")]
    public async Task Pom_work_order_management_action_uses_guarded_typed_route(string action)
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(new CaptureHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(WorkOrderDto(version: 8)),
            };
        }));
        var request = new PomWorkOrderActionRequest(
            ExpectedVersion: 7,
            IdempotencyKey: $"meta:mes:{action}:1",
            ClientChannel: "MES");

        var result = await client.ExecutePomWorkOrderActionAsync(action, "WO A/01", request);

        result.Success.Should().BeTrue();
        result.WorkOrder!.VersionNo.Should().Be(8);
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/v1/pom/work-orders/WO%20A%2F01/{action}");
    }

    [Fact]
    public async Task Pom_work_order_client_rejects_unknown_action_without_http_call()
    {
        var calls = 0;
        var client = CreateClient(new CaptureHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await client.ExecutePomWorkOrderActionAsync(
            "archive", "WO-1", new PomWorkOrderActionRequest(1, "key-1", "MES"));

        result.StatusCode.Should().Be(400);
        result.Error.Should().Contain("지원하지 않는");
        calls.Should().Be(0);
    }

    private static PomWorkOrderDto WorkOrderDto(int version)
        => new(
            "WO-100", "PO-100", "P1", "작업 100", "ITEM-1",
            10m, 0m, 0m, 0m, "Released", false,
            null, null, "worker", null, null, null, null,
            null, null, null, null, null, null, null, version);

    private static ApiClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nexaone.local/") };
        var storage = new ProtectedSessionStorage(
            Mock.Of<IJSRuntime>(), new EphemeralDataProtectionProvider());
        var tokens = new AuthTokenService(storage);
        return new ApiClient(http, tokens, new JwtAuthStateProvider(tokens),
            new ApiNotificationService(), new UiTextService());
    }

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
