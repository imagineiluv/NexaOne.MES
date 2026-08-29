namespace NexaOne.Web.Services.Meta;

/// <summary>화면 목적과 실제 입출력 기능의 불일치 심각도.</summary>
public enum ScreenCapabilityDiagnosticSeverity
{
    /// <summary>기존 호환성을 위해 실행은 허용하되 명시적 목적 전환 후보로 노출한다.</summary>
    Advisory,

    /// <summary>명시한 화면 목적과 기능이 모순된다.</summary>
    Error,
}

/// <summary>화면 목적과 기능 계약을 감사한 단일 진단 결과.</summary>
public sealed record ScreenCapabilityDiagnostic(
    string UiId,
    ScreenPurpose Purpose,
    string Code,
    ScreenCapabilityDiagnosticSeverity Severity,
    string Message);

/// <summary>
/// 화면 정의에서 조회·등록·변경 기능을 추출한 불변 스냅샷입니다.
/// 평면 메타와 Designer 레이아웃 메타를 같은 규칙으로 판단하기 위해 공개합니다.
/// </summary>
public sealed record ScreenCapabilitySnapshot(
    bool HasEditableInput,
    bool HasReadPath,
    bool HasContextualReadPath,
    bool HasSavePath,
    bool HasDeletePath,
    bool HasBulkMutationPath,
    bool HasLayoutCommandPath,
    bool HasNonMutatingCommandPath = false)
{
    /// <summary>본문 데이터가 아닌 페이징 건수·선택 옵션을 읽는 보조 조회 경로.</summary>
    public bool HasAnyReadPath => HasReadPath || HasContextualReadPath;

    /// <summary>등록·관리 화면이 데이터를 생성하거나 수정하는 명시적 저장 경로.</summary>
    public bool HasCreateOrUpdatePath => HasSavePath;

    /// <summary>기존 호출자를 위한 등록 쓰기 경로 별칭. generic 버튼 명령은 생성·수정 경로로 간주하지 않는다.</summary>
    public bool HasRegistrationWritePath => HasCreateOrUpdatePath;

    /// <summary>저장·삭제·일괄 전이·레이아웃 명령 중 하나라도 존재하는 실행 가능한 쓰기 경로.</summary>
    public bool HasAnyWritePath => HasSavePath || HasDeletePath || HasBulkMutationPath || HasLayoutCommandPath;
}

/// <summary>
/// <see cref="ScreenPurpose"/>와 화면의 실제 capability가 일치하는지 검사합니다.
/// 런타임 등록을 차단하지 않는 순수 검증기이므로 Designer, 시드, DB 정의 감사에 함께 사용할 수 있습니다.
/// </summary>
public static class ScreenDefinitionCapabilityValidator
{
    /// <summary>Auto 목적을 명시적 목적으로 전환하도록 안내하는 하위 호환 진단.</summary>
    public const string AutoPurposeAdvisory = "META-CAP-000";

    /// <summary>등록·관리 목적에 편집 가능한 입력이 없음을 나타내는 오류.</summary>
    public const string EditablePurposeMissingInput = "META-CAP-101";

    /// <summary>등록·관리 목적에 명시적 SaveQueryId가 없음을 나타내는 오류.</summary>
    public const string EditablePurposeMissingWritePath = "META-CAP-102";

    /// <summary>조회 전용 목적에 저장 경로가 섞여 있음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeHasSavePath = "META-CAP-201";

    /// <summary>조회 전용 목적에 삭제 경로가 섞여 있음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeHasDeletePath = "META-CAP-202";

    /// <summary>조회 전용 목적에 일괄 변경 경로가 섞여 있음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeHasBulkMutation = "META-CAP-203";

    /// <summary>조회 전용 목적에 레이아웃 변경 명령이 섞여 있음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeHasLayoutCommand = "META-CAP-204";

    /// <summary>조회·현황 목적에 본문 데이터를 읽는 primary query binding이 없음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeMissingReadPath = "META-CAP-205";

    /// <summary>조회·현황 목적에 사용자가 변경할 수 있는 입력 필드가 섞여 있음을 나타내는 오류.</summary>
    public const string ReadOnlyPurposeHasEditableInput = "META-CAP-206";

    /// <summary>작업 실행 목적에 실행 가능한 command/write 경로가 없음을 나타내는 오류.</summary>
    public const string ExecutePurposeMissingExecutionPath = "META-CAP-301";

    /// <summary>
    /// 런타임이 실제 렌더하는 surface만 조회·입력·명령 capability로 계산합니다.
    /// 평면 모드는 Fields/Columns와 결합된 binding만, 레이아웃 모드는 layout binding만 활성으로 보며
    /// SearchFields 옵션 조회는 두 모드에서 공통으로 활성입니다.
    /// </summary>
    public static ScreenCapabilitySnapshot Inspect(ScreenDefinition definition)
        => Inspect(definition, static _ => null);

    /// <summary>
    /// 등록된 명령 descriptor를 기준으로 변경 명령과 내보내기 같은 비변경 명령을 구분합니다.
    /// 카탈로그에 없는 명령은 기존 호환과 안전을 위해 변경 명령으로 간주합니다.
    /// </summary>
    public static ScreenCapabilitySnapshot Inspect(
        ScreenDefinition definition,
        IMetaCommandDriverCatalog commandCatalog)
    {
        ArgumentNullException.ThrowIfNull(commandCatalog);
        return Inspect(definition, commandId =>
            commandCatalog.TryGetDescriptor(commandId, out var descriptor) ? descriptor : null);
    }

    /// <summary>명령 descriptor resolver를 사용해 화면 capability를 계산합니다.</summary>
    public static ScreenCapabilitySnapshot Inspect(
        ScreenDefinition definition,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptorResolver);

        var layout = InspectLayout(definition.Layout, descriptorResolver);
        var hasSearchOptionsQuery = definition.SearchFields?.Any(HasOptionsQuery) == true;
        var bulkCommands = definition.BulkCommands?
            .Where(command => HasQuery(command.CommandQueryId))
            .Select(command => ResolveEffect(command.CommandQueryId, descriptorResolver))
            .ToArray() ?? [];
        var hasBulkMutation = bulkCommands.Contains(MetaCommandEffect.Mutating);
        var hasBulkNonMutation = bulkCommands.Contains(MetaCommandEffect.NonMutating);

        if (definition.Layout is null)
        {
            var hasFields = definition.Fields.Count > 0;
            var hasGrid = definition.Columns is { Count: > 0 };
            var hasPrimaryRead = hasGrid && HasQuery(definition.QueryId);
            var hasMutatingSave = hasFields && IsMutatingCommand(definition.SaveQueryId, descriptorResolver);
            var hasMutatingDelete = hasPrimaryRead && IsMutatingCommand(definition.DeleteQueryId, descriptorResolver);
            var hasNonMutatingSave = hasFields && IsNonMutatingCommand(definition.SaveQueryId, descriptorResolver);
            var hasNonMutatingDelete = hasPrimaryRead && IsNonMutatingCommand(definition.DeleteQueryId, descriptorResolver);
            // 삭제/일괄 명령은 조회 결과의 선택 행을 입력으로 사용하므로 컬럼만 있고 QueryId가 없으면 실행 불가능하다.
            return new ScreenCapabilitySnapshot(
                HasEditableInput: hasFields && definition.Fields.Any(IsEditable),
                HasReadPath: hasPrimaryRead,
                HasContextualReadPath: hasSearchOptionsQuery
                    || (hasFields && definition.Fields.Any(HasOptionsQuery))
                    || (hasPrimaryRead && HasQuery(definition.CountQueryId)),
                HasSavePath: hasMutatingSave,
                HasDeletePath: hasMutatingDelete,
                HasBulkMutationPath: hasPrimaryRead && hasBulkMutation,
                HasLayoutCommandPath: false,
                HasNonMutatingCommandPath: hasNonMutatingSave
                    || hasNonMutatingDelete
                    || (hasPrimaryRead && hasBulkNonMutation));
        }

        return new ScreenCapabilitySnapshot(
            HasEditableInput: layout.HasEditableInput,
            HasReadPath: layout.HasReadPath,
            HasContextualReadPath: hasSearchOptionsQuery || layout.HasContextualReadPath,
            HasSavePath: layout.HasSavePath,
            // LayoutRenderer도 QueryId가 있는 GridWidget에서만 선택 행을 만들 수 있다.
            HasDeletePath: layout.HasDataGrid
                && IsMutatingCommand(definition.DeleteQueryId, descriptorResolver),
            HasBulkMutationPath: layout.HasBulkMutationPath
                || (layout.HasDataGrid && hasBulkMutation),
            HasLayoutCommandPath: layout.HasCommandPath,
            HasNonMutatingCommandPath: layout.HasNonMutatingCommandPath
                || (layout.HasDataGrid && IsNonMutatingCommand(definition.DeleteQueryId, descriptorResolver))
                || layout.HasNonMutatingBulkPath
                || (layout.HasDataGrid && hasBulkNonMutation));
    }

    /// <summary>
    /// 명시적 목적은 모순을 오류로 반환합니다. Auto는 하위 호환을 위해 오류로 만들지 않고
    /// capability 요약을 Advisory 한 건으로 반환합니다. 명시적 목적은 업무에 필요한 최소 read/write 계약도 검사합니다.
    /// </summary>
    public static IReadOnlyList<ScreenCapabilityDiagnostic> Validate(ScreenDefinition definition)
        => Validate(definition, static _ => null);

    /// <summary>카탈로그 descriptor를 적용해 비변경 명령을 조회/보고서 화면에 허용합니다.</summary>
    public static IReadOnlyList<ScreenCapabilityDiagnostic> Validate(
        ScreenDefinition definition,
        IMetaCommandDriverCatalog commandCatalog)
    {
        ArgumentNullException.ThrowIfNull(commandCatalog);
        return Validate(definition, commandId =>
            commandCatalog.TryGetDescriptor(commandId, out var descriptor) ? descriptor : null);
    }

    /// <summary>descriptor resolver를 적용해 화면 목적 계약을 검증합니다.</summary>
    public static IReadOnlyList<ScreenCapabilityDiagnostic> Validate(
        ScreenDefinition definition,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptorResolver);
        var capabilities = Inspect(definition, descriptorResolver);
        var diagnostics = new List<ScreenCapabilityDiagnostic>();

        if (definition.Purpose == ScreenPurpose.Auto)
        {
            diagnostics.Add(NewDiagnostic(
                definition,
                AutoPurposeAdvisory,
                ScreenCapabilityDiagnosticSeverity.Advisory,
                $"Purpose가 Auto입니다. 명시적 목적 전환 후보입니다 " +
                $"(본문조회={capabilities.HasReadPath}, 보조조회={capabilities.HasContextualReadPath}, " +
                $"입력={capabilities.HasEditableInput}, 저장={capabilities.HasSavePath}, " +
                $"삭제={capabilities.HasDeletePath}, 일괄변경={capabilities.HasBulkMutationPath}, " +
                $"레이아웃명령={capabilities.HasLayoutCommandPath}, " +
                $"비변경명령={capabilities.HasNonMutatingCommandPath})."));
            return diagnostics;
        }

        if (definition.Purpose is ScreenPurpose.Register or ScreenPurpose.Manage)
        {
            if (!capabilities.HasEditableInput)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    EditablePurposeMissingInput,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 편집 가능한 입력 필드가 있어야 합니다."));
            }

            if (!capabilities.HasCreateOrUpdatePath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    EditablePurposeMissingWritePath,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 생성·수정을 위한 SaveQueryId가 있어야 합니다."));
            }

            return diagnostics;
        }

        if (definition.Purpose is ScreenPurpose.Inquiry or ScreenPurpose.Report)
        {
            if (!capabilities.HasReadPath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeMissingReadPath,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 본문 데이터를 읽는 QueryId 또는 조회 위젯 binding이 있어야 합니다."));
            }

            if (capabilities.HasEditableInput)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeHasEditableInput,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 사용자가 변경할 수 있는 입력 필드를 둘 수 없습니다."));
            }

            if (capabilities.HasSavePath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeHasSavePath,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 저장 경로를 둘 수 없습니다."));
            }

            if (capabilities.HasDeletePath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeHasDeletePath,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 삭제 경로를 둘 수 없습니다."));
            }

            if (capabilities.HasBulkMutationPath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeHasBulkMutation,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 일괄 변경 명령을 둘 수 없습니다."));
            }

            if (capabilities.HasLayoutCommandPath)
            {
                diagnostics.Add(NewDiagnostic(
                    definition,
                    ReadOnlyPurposeHasLayoutCommand,
                    ScreenCapabilityDiagnosticSeverity.Error,
                    $"{definition.Purpose} 화면에는 변경 명령 버튼을 둘 수 없습니다."));
            }
        }

        if (definition.Purpose == ScreenPurpose.Execute && !capabilities.HasAnyWritePath)
        {
            diagnostics.Add(NewDiagnostic(
                definition,
                ExecutePurposeMissingExecutionPath,
                ScreenCapabilityDiagnosticSeverity.Error,
                "Execute 화면에는 작업을 실행할 command 또는 쓰기 경로가 있어야 합니다."));
        }

        return diagnostics;
    }

    /// <summary>여러 화면 정의를 한 번에 감사합니다. Auto Advisory와 명시적 목적 Error를 함께 반환합니다.</summary>
    public static IReadOnlyList<ScreenCapabilityDiagnostic> Audit(IEnumerable<ScreenDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return definitions.SelectMany(Validate).ToArray();
    }

    /// <summary>카탈로그 descriptor를 적용해 여러 화면 정의를 한 번에 감사합니다.</summary>
    public static IReadOnlyList<ScreenCapabilityDiagnostic> Audit(
        IEnumerable<ScreenDefinition> definitions,
        IMetaCommandDriverCatalog commandCatalog)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(commandCatalog);
        return definitions.SelectMany(definition => Validate(definition, commandCatalog)).ToArray();
    }

    private static ScreenCapabilityDiagnostic NewDiagnostic(
        ScreenDefinition definition,
        string code,
        ScreenCapabilityDiagnosticSeverity severity,
        string message)
        => new(definition.UiId, definition.Purpose, code, severity, message);

    private static bool IsEditable(FieldDefinition field)
        => !field.ReadOnly && !field.Hidden && !string.IsNullOrWhiteSpace(field.Key);

    private static bool HasOptionsQuery(FieldDefinition field)
        => HasQuery(field.OptionsQueryId);

    private static bool HasQuery(string? queryId)
        => !string.IsNullOrWhiteSpace(queryId);

    private static MetaCommandEffect ResolveEffect(
        string commandId,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
        => descriptorResolver(commandId)?.Effect ?? MetaCommandEffect.Mutating;

    private static bool IsMutatingCommand(
        string? commandId,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
        => HasQuery(commandId)
           && ResolveEffect(commandId!, descriptorResolver) == MetaCommandEffect.Mutating;

    private static bool IsNonMutatingCommand(
        string? commandId,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
        => HasQuery(commandId)
           && ResolveEffect(commandId!, descriptorResolver) == MetaCommandEffect.NonMutating;

    private static LayoutCapabilities InspectLayout(
        LayoutNode? node,
        Func<string, MetaCommandDescriptor?> descriptorResolver)
    {
        if (node is null) return default;

        return node switch
        {
            FieldWidget field => new LayoutCapabilities(
                HasEditableInput: field.Field is null
                    ? !string.IsNullOrWhiteSpace(field.FieldKey)
                    : IsEditable(field.Field),
                HasReadPath: false,
                HasContextualReadPath: field.Field is not null && HasOptionsQuery(field.Field),
                HasSavePath: false,
                HasCommandPath: false,
                HasNonMutatingCommandPath: false,
                HasDataGrid: false),
            FormWidget form => Merge(
                new LayoutCapabilities(
                    HasEditableInput: false,
                    HasReadPath: false,
                    HasContextualReadPath: false,
                    HasSavePath: IsMutatingCommand(form.SaveQueryId, descriptorResolver),
                    HasCommandPath: false,
                    HasNonMutatingCommandPath: IsNonMutatingCommand(form.SaveQueryId, descriptorResolver),
                    HasDataGrid: false),
                form.Fields?.Select(field => InspectLayout(field, descriptorResolver))),
            // 반복 항목 스키마는 공유 루트 필드와 별개지만 입력/옵션 capability에는 포함한다.
            CollectionWidget collection => Merge(
                default,
                collection.Fields?.Select(field => InspectLayout(field, descriptorResolver))),
            ButtonWidget button => new LayoutCapabilities(
                HasEditableInput: false,
                HasReadPath: false,
                HasContextualReadPath: false,
                HasSavePath: false,
                HasCommandPath: HasQuery(button.Command)
                    && ResolveEffect(button.Command!, descriptorResolver) == MetaCommandEffect.Mutating,
                HasNonMutatingCommandPath: HasQuery(button.Command)
                    && ResolveEffect(button.Command!, descriptorResolver) == MetaCommandEffect.NonMutating,
                HasDataGrid: false),
            GridWidget grid => ReadBinding(
                grid.QueryId,
                hasDataGrid: HasQuery(grid.QueryId),
                bulkCommands: grid.BulkCommands,
                descriptorResolver: descriptorResolver),
            KpiWidget kpi => ReadBinding(kpi.QueryId),
            BadgeWidget badge => ReadBinding(badge.QueryId),
            TrendChartWidget trend => ReadBinding(trend.QueryId),
            SectionNode section => Merge(default, section.Children?.Select(child => InspectLayout(child, descriptorResolver))),
            RowNode row => Merge(default, row.Children?.Select(child => InspectLayout(child, descriptorResolver))),
            ColumnNode column => Merge(default, column.Children?.Select(child => InspectLayout(child, descriptorResolver))),
            _ => default,
        };
    }

    private static LayoutCapabilities Merge(
        LayoutCapabilities seed,
        IEnumerable<LayoutCapabilities>? children)
    {
        if (children is null) return seed;

        return children.Aggregate(seed, static (current, child) => new LayoutCapabilities(
            current.HasEditableInput || child.HasEditableInput,
            current.HasReadPath || child.HasReadPath,
            current.HasContextualReadPath || child.HasContextualReadPath,
            current.HasSavePath || child.HasSavePath,
            current.HasCommandPath || child.HasCommandPath,
            current.HasNonMutatingCommandPath || child.HasNonMutatingCommandPath,
            current.HasDataGrid || child.HasDataGrid,
            current.HasBulkMutationPath || child.HasBulkMutationPath,
            current.HasNonMutatingBulkPath || child.HasNonMutatingBulkPath));
    }

    private static LayoutCapabilities ReadBinding(
        string? queryId,
        bool hasDataGrid = false,
        IReadOnlyList<BulkCommandDefinition>? bulkCommands = null,
        Func<string, MetaCommandDescriptor?>? descriptorResolver = null)
        => new(
            HasEditableInput: false,
            HasReadPath: HasQuery(queryId),
            HasContextualReadPath: false,
            HasSavePath: false,
            HasCommandPath: false,
            HasNonMutatingCommandPath: false,
            HasDataGrid: hasDataGrid,
            HasBulkMutationPath: bulkCommands?.Any(command =>
                HasQuery(command.CommandQueryId)
                && ResolveEffect(command.CommandQueryId, descriptorResolver ?? (_ => null)) == MetaCommandEffect.Mutating) == true,
            HasNonMutatingBulkPath: bulkCommands?.Any(command =>
                HasQuery(command.CommandQueryId)
                && ResolveEffect(command.CommandQueryId, descriptorResolver ?? (_ => null)) == MetaCommandEffect.NonMutating) == true);

    private readonly record struct LayoutCapabilities(
        bool HasEditableInput,
        bool HasReadPath,
        bool HasContextualReadPath,
        bool HasSavePath,
        bool HasCommandPath,
        bool HasNonMutatingCommandPath,
        bool HasDataGrid,
        bool HasBulkMutationPath = false,
        bool HasNonMutatingBulkPath = false);
}
