using System.Globalization;

namespace NexaOne.Web.Components.Shared;

/// <summary>
/// 조건 저장 값(문자열) ↔ 컨트롤 타입 변환 어댑터 (설계서 20.8).
/// 기간 조건(DateTime) 등 특수 컨트롤 값은 이 어댑터를 통해 문화권 중립으로 직렬화/복원한다.
/// </summary>
public static class ConditionValueAdapter
{
    public static string? From(DateTime? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    public static string? From(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    public static string? From(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    public static DateTime? ToDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    public static int? ToInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : null;

    public static decimal? ToDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : null;
}
