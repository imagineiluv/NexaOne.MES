using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Est;

/// <summary>
/// ErrorType→HTTP 상태 매핑의 end-to-end 검증 — 존재하지 않는 알람 해제는 서비스가 Error.NotFound를
/// 돌려주므로(EquipmentAlarmService.ClearAlarmAsync) 응답이 404여야 한다. 이전엔 모든 실패가 400으로
/// 평탄화됐다(ToActionResult 도입 전). 본문은 ProblemDetails가 아니라 { Code, Description }를 유지한다
/// (웹 ApiClient.ReadErrorAsync 계약). 여기서 Code는 2-인자 팩터리라 "EquipmentAlarm"(필드명) — 즉
/// 상태 매핑이 Code 접두어가 아니라 Error.Type으로 이뤄짐을 함께 실증한다.
/// </summary>
public sealed class EstAlarmNotFoundIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public EstAlarmNotFoundIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ClearAlarm_for_unknown_alarm_returns_404_with_error_body()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:est:manage 통과

        var resp = await client.PutAsJsonAsync(
            "/api/v1/est/alarms/ALM-DOES-NOT-EXIST/clear", new { clearedAt = DateTime.UtcNow });

        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"미존재 알람 해제는 Error.NotFound → 404여야 한다(400 평탄화 회귀 방지). 응답 본문: {body}");

        // 본문은 { Code, Description } 형태를 유지해야 한다(ProblemDetails로 전환 금지).
        var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>();
        error.Should().NotBeNull();
        error!.Code.Should().NotBeNullOrEmpty("Error.Code가 직렬화돼야 한다");
        error.Description.Should().NotBeNullOrEmpty("Error.Description이 직렬화돼야 한다");
    }

    private sealed record ApiErrorPayload(string? Code, string? Description);
}
