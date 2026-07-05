using NexaOne.Web.Components;
using NexaOne.Web.Services;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 전역 API 토스트 브릿지(ApiToastHost) — 실제 표시는 Radzen 알림(NotificationService)이 맡고
/// 이 컴포넌트는 채널 구독→전달만 한다. 우리가 소유한 것은 API 심각도→Radzen 심각도 매핑뿐이라
/// 그 순수 매핑을 검증한다(렌더는 라이브러리 책임).
/// </summary>
public sealed class ApiToastHostTests
{
    [Theory]
    [InlineData(ApiNotificationSeverity.Error, NotificationSeverity.Error)]
    [InlineData(ApiNotificationSeverity.Warning, NotificationSeverity.Warning)]
    [InlineData(ApiNotificationSeverity.Info, NotificationSeverity.Info)]
    public void SeverityOf_maps_api_severity_to_radzen_notification_severity(
        ApiNotificationSeverity input, NotificationSeverity expected)
        => ApiToastHost.SeverityOf(input).Should().Be(expected,
            "API 통지 심각도는 대응하는 Radzen 알림 심각도(색/아이콘)로 매핑돼야 한다");
}
