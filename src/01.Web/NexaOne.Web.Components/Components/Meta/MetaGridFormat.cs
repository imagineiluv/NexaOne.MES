namespace NexaOne.Web.Components.Meta;

/// <summary>메타 그리드 셀 표현 순수 헬퍼(무상태 정적) — MetaGridRenderer.razor에서 추출(파일 894줄
/// 비대화 해소). 렌더/상태와 무관한 값 변환만: 종류 추론(스키마리스 문자열 샘플), 폭/배지/포맷, CSV 필드.
/// razor는 @using static으로 무접두 사용(추출 전 호출부 불변), 단위테스트는 직접 참조한다.</summary>
public static class MetaGridFormat
{
    /// <summary>컬럼 종류 — 값 표현/폭/정렬을 결정한다. 스키마리스(문자열) 데이터라 값 샘플로 추론한다.</summary>
    public enum ColumnKind { Text, Numeric, DateTime, Status, Boolean, Empty }

    /// <summary>셀 값 조회(문자열). ExpandoObject를 IDictionary로 보고 Key로 찾는다.</summary>
    public static string Cell(System.Dynamic.ExpandoObject row, string key)
        => ((IDictionary<string, object?>)row).TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    /// <summary>타임스탬프 포맷(P1) — ISO 등으로 파싱되는 DateTime은 "yyyy-MM-dd HH:mm:ss"(로컬)로. 그 외는 원문.</summary>
    public static string FormatCell(string raw)
        => DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
           ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
           : raw;

    /// <summary>컬럼 값 목록 → 종류 추론. 우선순위: 빈 > 상태(전부 화이트리스트) > 불리언 > 시각 > 숫자 > 텍스트.
    /// '전부' 규칙이라 혼합 컬럼은 텍스트로 안전 폴백(오분류로 정렬/폭 깨짐 방지).</summary>
    public static ColumnKind InferKind(IReadOnlyList<string> values)
    {
        var nonEmpty = values.Where(v => v.Length > 0).ToList();
        if (nonEmpty.Count == 0) return ColumnKind.Empty;
        if (nonEmpty.All(v => SeverityOf(v) is not null)) return ColumnKind.Status;
        if (nonEmpty.All(IsBooleanish)) return ColumnKind.Boolean;
        if (nonEmpty.All(v => DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _))) return ColumnKind.DateTime;
        if (nonEmpty.All(v => decimal.TryParse(v, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))) return ColumnKind.Numeric;
        return ColumnKind.Text;
    }

    /// <summary>종류별 최소 폭(px)을 정하고 현재 언어의 헤더 길이에 맞춰 제한적으로 확장한다.
    /// 여러 열의 합이 화면보다 넓을 때는 표가 data viewport 안에서 가로 스크롤되므로 문서 폭은 늘어나지 않는다.</summary>
    public static string WidthFor(ColumnKind kind, string key, string? caption = null)
    {
        var baseWidth = kind switch
        {
            ColumnKind.Numeric => 108,
            ColumnKind.DateTime => 168,
            ColumnKind.Status => 116,
            ColumnKind.Boolean => 76,
            ColumnKind.Empty => 72,
            ColumnKind.Text when IsIdentifierKey(key) => 160,
            _ => 140,
        };

        // 헤더 버튼의 정렬 상태와 좌우 여백까지 확보한다. 언어에 따라 긴 캡션은 최대 240px까지만
        // 표 내부 폭을 늘려, 라벨 잘림과 Radzen 루트의 페이지 바깥 확장을 함께 막는다.
        var captionWidth = EstimateCaptionWidth(caption);
        return $"{Math.Max(baseWidth, captionWidth)}px";
    }

    private static int EstimateCaptionWidth(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 0;

        var textWidth = 0;
        foreach (var ch in caption)
            textWidth += ch > 0x7f ? 14 : ch == ' ' ? 4 : 7;

        var withControls = textWidth + 48;
        return Math.Clamp(((withControls + 7) / 8) * 8, 0, 240);
    }

    // 식별자성 컬럼 키 — 대개 짧은 코드라 유연 분배 시 잘리기 쉽다. _ID/ID/_CODE/CODE 접미.
    private static bool IsIdentifierKey(string key)
        => key.EndsWith("_ID", StringComparison.OrdinalIgnoreCase) || key.Equals("ID", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_CODE", StringComparison.OrdinalIgnoreCase) || key.EndsWith("CODE", StringComparison.OrdinalIgnoreCase);

    public static bool IsBooleanish(string v) => v.Trim() switch
    {
        "0" or "1" or "true" or "false" or "True" or "False" or "Y" or "N" or "Yes" or "No" or "y" or "n" => true,
        _ => false,
    };

    public static bool IsTruthy(string v) => v.Trim() switch
    {
        "1" or "true" or "True" or "Y" or "Yes" or "y" => true,
        _ => false,
    };

    /// <summary>심각도 배지(P1) — 알려진 상태 단어만 색 배지로(오탐 방지). 그 외는 null(평문).</summary>
    public static Radzen.BadgeStyle? SeverityOf(string raw) => raw.Trim() switch
    {
        "Error" or "Critical" or "Fatal" or "Fail" or "Failed" or "Danger" or "Down" or "Stopped" or "Interlock"
            => Radzen.BadgeStyle.Danger,
        "Warning" or "Warn" or "Pending" or "Idle" or "Hold" => Radzen.BadgeStyle.Warning,
        "Success" or "OK" or "Normal" or "Active" or "Running" or "Completed" or "Done" or "Valid"
            => Radzen.BadgeStyle.Success,
        "Information" or "Info" or "Draft" or "Confirmed" or "Issued" or "Planned" => Radzen.BadgeStyle.Info,
        _ => null,
    };

    public static string CsvField(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    public static string RawCell(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    public static decimal ParseDec(string s) => decimal.TryParse(s, out var d) ? d : decimal.MinValue;
}
