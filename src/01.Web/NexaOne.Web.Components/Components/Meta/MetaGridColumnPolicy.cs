using NexaOne.Web.Services.Meta;

namespace NexaOne.Web.Components.Meta;

/// <summary>
/// 화면별 컬럼 선언 순서와 별개로 카드 보기의 정보 위계를 결정하는 공통 정책입니다.
/// 메타 계약에 새 속성을 추가하지 않고도 모든 관리 화면에서 상태·업무 식별자·일정·수량 순으로
/// 핵심 정보를 보여 주며, 같은 우선순위에서는 디자이너가 선언한 순서를 그대로 보존합니다.
/// </summary>
public static class MetaGridColumnPolicy
{
    public const int DefaultCardFieldCount = 6;

    /// <summary>카드 제목에는 업무 번호를 우선하고 ID·코드·명칭 순으로 안전하게 대체합니다.</summary>
    public static GridColumnDefinition? CardPrimary(IReadOnlyList<GridColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        string[] suffixes = ["_NO", "_ID", "_CODE", "_NAME"];
        foreach (var suffix in suffixes)
        {
            var match = columns.FirstOrDefault(column =>
                column.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return columns.FirstOrDefault();
    }

    /// <summary>
    /// 카드에 노출할 요약 컬럼을 고릅니다. 상태/판정과 Hold 같은 즉시 판단 정보가 먼저 오고,
    /// 명칭·관계 식별자, 납기/일시, 수량/비율 순으로 배치한 뒤 나머지는 선언 순서를 사용합니다.
    /// </summary>
    public static IReadOnlyList<GridColumnDefinition> CardSummary(
        IReadOnlyList<GridColumnDefinition> columns,
        GridColumnDefinition? primary,
        int maxFields = DefaultCardFieldCount)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (maxFields <= 0) return Array.Empty<GridColumnDefinition>();

        return columns
            .Select((column, index) => new { Column = column, Index = index })
            .Where(item => primary is null ||
                !string.Equals(item.Column.Key, primary.Key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => SummaryPriority(item.Column.Key))
            .ThenBy(item => item.Index)
            .Take(maxFields)
            .Select(item => item.Column)
            .ToArray();
    }

    private static int SummaryPriority(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();

        if (HasPart(normalized, "STATUS", "STATE", "RESULT", "GRADE", "SEVERITY")) return 0;
        if (HasPart(normalized, "HOLD", "ACTIVE", "VALID", "CANCELLED", "CONFIRMED", "SUPERSEDED")) return 5;
        if (normalized.EndsWith("_NAME", StringComparison.Ordinal) || normalized == "NAME") return 10;
        if (HasPart(normalized, "CUSTOMER", "VENDOR", "SUPPLIER", "PRODUCT", "ITEM", "LOT",
                "EQUIPMENT", "PROCESS", "SEGMENT", "PLANT", "AREA", "WORKER")
            || normalized.Contains("WORK_CENTER", StringComparison.Ordinal)) return 20;
        if (HasPart(normalized, "DUE", "DATE", "AT", "TIME", "START", "END", "SCHEDULE", "SCHEDULED")) return 30;
        if (HasPart(normalized, "QTY", "QUANTITY", "COUNT", "AMOUNT", "RATE", "PERCENT", "VALUE", "SCORE")) return 40;
        if (HasPart(normalized, "TYPE", "CLASS", "CATEGORY")) return 50;
        return 100;
    }

    private static bool HasPart(string key, params string[] candidates)
    {
        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Any(part => candidates.Contains(part, StringComparer.Ordinal));
    }
}
