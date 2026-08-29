using NexaOne.Web.Services;
using Radzen;

namespace NexaOne.Web.Components.Meta;

/// <summary>
/// 메타 폼과 그리드가 공유하는 업무 상태 표시 규칙입니다.
/// 저장·조회 계약 값은 바꾸지 않고, 현재 언어의 표시 라벨과 의미 색상만 제공합니다.
/// </summary>
internal static class MetaStatusPresentation
{
    /// <summary>상태 의미가 명시된 필드/컬럼 키인지 판별합니다.</summary>
    internal static bool IsStatusKey(string key)
        => key.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("STATE", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_STATUS", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_STATE", StringComparison.OrdinalIgnoreCase)
            || HasCamelCaseSuffix(key, "Status")
            || HasCamelCaseSuffix(key, "State");

    /// <summary>공통 사전에 등록된 상태 계약 값인지 확인합니다.</summary>
    internal static bool IsKnown(string raw) => StyleOf(raw) is not null;

    /// <summary>상태 계약 값을 성공·주의·오류·정보·중성 tone으로 매핑합니다.</summary>
    internal static BadgeStyle? StyleOf(string raw) => Normalize(raw) switch
    {
        "ERROR" or "CRITICAL" or "FATAL" or "FAIL" or "FAILED" or "DANGER" or "DOWN" or "STOPPED" or "INTERLOCK"
            => BadgeStyle.Danger,
        "WARNING" or "WARN" or "PENDING" or "IDLE" or "HOLD"
            => BadgeStyle.Warning,
        "SUCCESS" or "OK" or "NORMAL" or "ACTIVE" or "RUNNING" or "STARTED" or "PRODUCING"
            or "COMPLETED" or "DONE" or "DELIVERED" or "VALID" or "INSTOCK" or "AVAILABLE"
            or "APPROVED" or "PASS" or "PASSED"
            => BadgeStyle.Success,
        "OUTOFSTOCK" or "CANCELLED" or "CANCELED" or "REJECTED" => BadgeStyle.Danger,
        "INFORMATION" or "INFO" or "DRAFT" or "CONFIRMED" or "ISSUED" or "PLANNED"
            => BadgeStyle.Info,
        "CLOSED" => BadgeStyle.Light,
        _ => null,
    };

    /// <summary>
    /// 상태 표시 라벨을 반환합니다. EnUs 리소스가 아직 동기화되지 않은 실행 환경에서도 영문 화면에
    /// 한국어가 섞이지 않도록 언어별 안전 폴백을 사용하며, 알려지지 않은 값은 원문을 보존합니다.
    /// </summary>
    internal static string LabelOf(string raw, UiTextService? uiText)
    {
        var normalized = Normalize(raw);
        var labels = normalized switch
        {
            "DRAFT" => (Ko: "초안", En: "Draft"),
            "CONFIRMED" => (Ko: "확정", En: "Confirmed"),
            "PRODUCING" => (Ko: "생산 중", En: "Producing"),
            "DELIVERED" => (Ko: "납품 완료", En: "Delivered"),
            "CLOSED" => (Ko: "마감", En: "Closed"),
            "ISSUED" => (Ko: "발행", En: "Issued"),
            "PLANNED" => (Ko: "계획", En: "Planned"),
            "STARTED" => (Ko: "시작", En: "Started"),
            "RUNNING" => (Ko: "가동 중", En: "Running"),
            "COMPLETED" or "DONE" => (Ko: "완료", En: "Completed"),
            "PENDING" => (Ko: "대기", En: "Pending"),
            "HOLD" => (Ko: "보류", En: "On Hold"),
            "IDLE" => (Ko: "유휴", En: "Idle"),
            "STOPPED" => (Ko: "정지", En: "Stopped"),
            "FAILED" or "FAIL" => (Ko: "실패", En: "Failed"),
            "ERROR" => (Ko: "오류", En: "Error"),
            "SUCCESS" => (Ko: "성공", En: "Success"),
            "OK" or "NORMAL" => (Ko: "정상", En: "Normal"),
            "ACTIVE" => (Ko: "활성", En: "Active"),
            "INSTOCK" or "AVAILABLE" => (Ko: "재고 있음", En: "In Stock"),
            "OUTOFSTOCK" => (Ko: "재고 없음", En: "Out of Stock"),
            "APPROVED" => (Ko: "승인", En: "Approved"),
            "REJECTED" => (Ko: "반려", En: "Rejected"),
            "PASS" or "PASSED" => (Ko: "합격", En: "Passed"),
            "CANCELLED" or "CANCELED" => (Ko: "취소", En: "Canceled"),
            _ => default,
        };

        if (labels.Ko is null)
            return uiText?.T($"status.{normalized.ToLowerInvariant()}", raw) ?? raw;

        var fallback = string.Equals(uiText?.Language, "EnUs", StringComparison.OrdinalIgnoreCase)
            ? labels.En
            : labels.Ko;
        return uiText?.T($"status.{normalized.ToLowerInvariant()}", fallback) ?? fallback;
    }

    private static bool HasCamelCaseSuffix(string key, string suffix)
    {
        if (!key.EndsWith(suffix, StringComparison.Ordinal) || key.Length == suffix.Length) return false;
        var suffixStart = key.Length - suffix.Length;
        return char.IsLower(key[suffixStart - 1]) && char.IsUpper(key[suffixStart]);
    }

    private static string Normalize(string raw) => raw.Trim().ToUpperInvariant();
}
