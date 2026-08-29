using System.Text;

namespace NexaOne.Web.Services.Meta;

/// <summary>Auto 화면을 명시적 목적 전환 전에 구조적으로 분류하는 감사 그룹입니다.</summary>
public enum ScreenPurposeMigrationGroup
{
    StructurallyReadyReadOnly,
    StructurallyReadyEditable,
    StructurallyReadyExecute,
    ImplementationGap,
}

/// <summary>한 Auto 화면의 활성 surface 기반 목적 전환 감사 결과입니다.</summary>
public sealed record ScreenPurposeMigrationAuditItem(
    string UiId,
    string Title,
    ScreenPurposeMigrationGroup Group,
    ScreenCapabilitySnapshot Capabilities,
    string Reason);

/// <summary>
/// 화면 이름을 목적의 근거로 사용하지 않고 실제 활성 조회·입력·명령 surface로 Auto 화면을 분류합니다.
/// 업무 의미상 쓰기가 필요하다고 별도 검토된 화면은 <c>reviewedImplementationGapUiIds</c>로 전달해
/// 조회 구조만 있다는 이유로 Inquiry/Report 후보가 되는 것을 막습니다.
/// </summary>
public static class ScreenPurposeMigrationAudit
{
    /// <summary>Auto 화면을 UI ID 순으로 중복 없이 감사합니다.</summary>
    public static IReadOnlyList<ScreenPurposeMigrationAuditItem> InspectAuto(
        IEnumerable<ScreenDefinition> definitions,
        IMetaCommandDriverCatalog? commandCatalog = null,
        IEnumerable<string>? reviewedImplementationGapUiIds = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var reviewedGaps = new HashSet<string>(
            reviewedImplementationGapUiIds ?? [],
            StringComparer.OrdinalIgnoreCase);

        return definitions
            .Where(definition => definition.Purpose == ScreenPurpose.Auto)
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .Select(definition => Inspect(definition, commandCatalog, reviewedGaps.Contains(definition.UiId)))
            .ToArray();
    }

    /// <summary>그룹과 UI ID가 항상 같은 순서로 출력되는 검토용 텍스트를 만듭니다.</summary>
    public static string Format(IEnumerable<ScreenPurposeMigrationAuditItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items
            .OrderBy(item => item.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();

        foreach (var group in Enum.GetValues<ScreenPurposeMigrationGroup>())
        {
            var grouped = snapshot.Where(item => item.Group == group).ToArray();
            builder.Append('[').Append(group).Append("] ").AppendLine(grouped.Length.ToString());
            foreach (var item in grouped)
                builder.Append(item.UiId).Append(" | ").Append(item.Reason).Append(" | ").AppendLine(item.Title);
        }

        return builder.ToString().TrimEnd();
    }

    private static ScreenPurposeMigrationAuditItem Inspect(
        ScreenDefinition definition,
        IMetaCommandDriverCatalog? commandCatalog,
        bool isReviewedImplementationGap)
    {
        var capabilities = commandCatalog is null
            ? ScreenDefinitionCapabilityValidator.Inspect(definition)
            : ScreenDefinitionCapabilityValidator.Inspect(definition, commandCatalog);

        if (isReviewedImplementationGap)
            return NewItem(definition, capabilities, ScreenPurposeMigrationGroup.ImplementationGap,
                "reviewed-write-intent-without-implementation");

        if (capabilities.HasEditableInput && capabilities.HasCreateOrUpdatePath)
            return NewItem(definition, capabilities, ScreenPurposeMigrationGroup.StructurallyReadyEditable,
                "editable-input+save");

        if (capabilities.HasAnyWritePath)
            return NewItem(definition, capabilities, ScreenPurposeMigrationGroup.StructurallyReadyExecute,
                "active-mutation-path");

        if (capabilities.HasReadPath && !capabilities.HasEditableInput)
            return NewItem(definition, capabilities, ScreenPurposeMigrationGroup.StructurallyReadyReadOnly,
                "primary-read-only");

        return NewItem(definition, capabilities, ScreenPurposeMigrationGroup.ImplementationGap,
            "no-complete-purpose-contract");
    }

    private static ScreenPurposeMigrationAuditItem NewItem(
        ScreenDefinition definition,
        ScreenCapabilitySnapshot capabilities,
        ScreenPurposeMigrationGroup group,
        string reason)
        => new(definition.UiId, definition.Title, group, capabilities, reason);
}
