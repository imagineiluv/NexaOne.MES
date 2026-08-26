using NexaOne.Application.Query;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>카탈로그를 조회해야만 판단할 수 있는 화면 바인딩 진단 결과입니다.</summary>
public sealed record ScreenBindingDiagnostic(
    string UiId,
    string Code,
    ScreenCapabilityDiagnosticSeverity Severity,
    string BindingPath,
    string BindingId,
    string Message);

/// <summary>
/// 화면 정의가 참조하는 named query와 typed bridge command의 존재, 종류, 권한 계약을 검사합니다.
/// 목적/입력/쓰기 경로의 구조 일관성은 <see cref="ScreenDefinitionCapabilityValidator"/>가 담당하고,
/// 이 검증기는 런타임 카탈로그와 대조해야 하는 contextual 규칙과 런타임 안전에 필요한
/// 컬렉션 노드의 최소 구조 불변식을 담당합니다.
/// </summary>
public interface IScreenDefinitionBindingValidator
{
    IReadOnlyList<ScreenBindingDiagnostic> Validate(ScreenDefinition definition);
}

public sealed class ScreenDefinitionBindingValidator : IScreenDefinitionBindingValidator
{
    public const string BindingIdNotCanonical = "META-BIND-001";
    public const string ReadBindingMissing = "META-BIND-101";
    public const string ReadBindingUsesWrite = "META-BIND-102";
    public const string ReadBindingPermissionMissing = "META-BIND-103";
    public const string WriteBindingMissing = "META-BIND-201";
    public const string WriteBindingUsesRead = "META-BIND-202";
    public const string WriteBindingPermissionMissing = "META-BIND-203";
    public const string BindingPermissionMismatch = "META-BIND-204";
    public const string BridgeCommandMissing = "META-BIND-205";
    public const string ButtonPermissionMissing = "META-BIND-206";
    public const string BridgeCommandExecutionModeMismatch = "META-BIND-207";
    public const string BridgeCommandEffectMismatch = "META-BIND-208";
    public const string CollectionKeyMissing = "META-LAYOUT-101";
    public const string CollectionMinimumInvalid = "META-LAYOUT-102";
    public const string CollectionMaximumInvalid = "META-LAYOUT-103";

    private readonly IQueryRegistry _queries;
    private readonly IMetaCommandDriverCatalog _commands;

    public ScreenDefinitionBindingValidator(
        IQueryRegistry queries,
        IMetaCommandDriverCatalog commands)
    {
        _queries = queries;
        _commands = commands;
    }

    /// <summary>화면의 최상위, 필드 옵션, 검색 옵션과 중첩 layout 바인딩을 모두 감사합니다.</summary>
    public IReadOnlyList<ScreenBindingDiagnostic> Validate(ScreenDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var diagnostics = new List<ScreenBindingDiagnostic>();

        // 검색 조건은 flat/layout 어느 렌더링 모드에서도 실제로 노출되고 옵션 쿼리를 실행한다.
        ValidateFieldOptions(definition, definition.SearchFields, "searchFields", diagnostics);

        if (definition.Layout is null)
        {
            // flat 폼은 필드가 있어야 렌더링되므로 필드 옵션과 저장 경로도 그때만 활성 surface다.
            if (definition.Fields.Count > 0)
            {
                ValidateFieldOptions(definition, definition.Fields, "fields", diagnostics);
                ValidateWrite(
                    definition, definition.SaveQueryId, "saveQueryId", definition.SaveRequiredPermission,
                    CommandBindingSurface.FormSave, diagnostics);
            }

            // flat 그리드는 컬럼과 데이터 쿼리가 모두 있어야 행과 CRUD/일괄 툴바를 활성화한다.
            var hasFlatGrid = definition.Columns is { Count: > 0 }
                && !string.IsNullOrWhiteSpace(definition.QueryId);
            if (!hasFlatGrid) return diagnostics;

            ValidateRead(
                definition,
                definition.QueryId,
                "queryId",
                definition.ReadRequiredPermission,
                diagnostics);
            ValidateRead(definition, definition.CountQueryId, "countQueryId", null, diagnostics);
            ValidateWrite(
                definition, definition.DeleteQueryId, "deleteQueryId", definition.DeleteRequiredPermission,
                CommandBindingSurface.Delete, diagnostics);
            ValidateBulkCommands(definition, diagnostics);
            return diagnostics;
        }

        // layout 모드에서는 트리가 폼/조회/명령 surface의 유일한 본문이다. top-level flat 바인딩은 무시한다.
        ValidateLayout(definition, definition.Layout, "layout", diagnostics);

        // top-level 삭제/일괄 명령은 LayoutRenderer가 GridWidget에 전달하므로 행을 조회하는 데이터 그리드가 있을 때만 활성이다.
        if (ContainsDataGrid(definition.Layout))
        {
            ValidateWrite(
                definition, definition.DeleteQueryId, "deleteQueryId", definition.DeleteRequiredPermission,
                CommandBindingSurface.Delete, diagnostics);
            ValidateBulkCommands(definition, diagnostics);
        }

        return diagnostics;
    }

    private void ValidateBulkCommands(
        ScreenDefinition definition,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (definition.BulkCommands is null) return;
        for (var index = 0; index < definition.BulkCommands.Count; index++)
        {
            ValidateWrite(
                definition,
                definition.BulkCommands[index].CommandQueryId,
                $"bulkCommands[{index}].commandQueryId",
                definition.BulkCommands[index].RequiredPermission,
                CommandBindingSurface.Bulk,
                diagnostics);
        }
    }

    /// <summary>레이아웃 트리에 top-level 삭제·일괄 명령을 받을 데이터 그리드가 있는지 확인합니다.</summary>
    private static bool ContainsDataGrid(LayoutNode node)
        => node switch
        {
            GridWidget grid => !string.IsNullOrWhiteSpace(grid.QueryId),
            SectionNode section => ContainsDataGrid(section.Children),
            RowNode row => ContainsDataGrid(row.Children),
            ColumnNode column => ContainsDataGrid(column.Children),
            _ => false,
        };

    private static bool ContainsDataGrid(IReadOnlyList<LayoutNode>? children)
        => children?.Any(ContainsDataGrid) == true;

    private void ValidateFieldOptions(
        ScreenDefinition definition,
        IReadOnlyList<FieldDefinition>? fields,
        string path,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (fields is null) return;
        for (var index = 0; index < fields.Count; index++)
        {
            ValidateRead(
                definition,
                fields[index].OptionsQueryId,
                $"{path}[{index}].optionsQueryId",
                declaredPermission: null,
                diagnostics);
        }
    }

    private void ValidateLayout(
        ScreenDefinition definition,
        LayoutNode? node,
        string path,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (node is null) return;

        switch (node)
        {
            case GridWidget grid:
                ValidateRead(definition, grid.QueryId, $"{path}.queryId", grid.RequiredPermission, diagnostics);
                break;
            case KpiWidget kpi:
                ValidateRead(definition, kpi.QueryId, $"{path}.queryId", kpi.RequiredPermission, diagnostics);
                break;
            case BadgeWidget badge:
                ValidateRead(definition, badge.QueryId, $"{path}.queryId", badge.RequiredPermission, diagnostics);
                break;
            case TrendChartWidget trend:
                ValidateRead(definition, trend.QueryId, $"{path}.queryId", trend.RequiredPermission, diagnostics);
                break;
            case FormWidget form:
                ValidateWrite(
                    definition,
                    form.SaveQueryId,
                    $"{path}.saveQueryId",
                    form.RequiredPermission,
                    CommandBindingSurface.FormSave,
                    diagnostics);
                if (form.Fields is not null)
                {
                    for (var index = 0; index < form.Fields.Count; index++)
                        ValidateLayout(definition, form.Fields[index], $"{path}.fields[{index}]", diagnostics);
                }
                break;
            case FieldWidget field:
                ValidateRead(
                    definition,
                    field.Field?.OptionsQueryId,
                    $"{path}.field.optionsQueryId",
                    field.RequiredPermission,
                    diagnostics);
                break;
            case CollectionWidget collection:
                ValidateCollectionStructure(definition, collection, path, diagnostics);
                if (collection.Fields is not null)
                {
                    for (var index = 0; index < collection.Fields.Count; index++)
                        ValidateLayout(definition, collection.Fields[index], $"{path}.fields[{index}]", diagnostics);
                }
                break;
            case ButtonWidget button:
                ValidateWrite(
                    definition,
                    button.Command,
                    $"{path}.command",
                    button.RequiredPermission,
                    CommandBindingSurface.Button,
                    diagnostics);
                break;
            case SectionNode section:
                ValidateChildren(definition, section.Children, path, diagnostics);
                break;
            case RowNode row:
                ValidateChildren(definition, row.Children, path, diagnostics);
                break;
            case ColumnNode column:
                ValidateChildren(definition, column.Children, path, diagnostics);
                break;
        }
    }

    /// <summary>
    /// Designer가 저장한 반복 입력 구조를 런타임에 넘기기 전에 검증합니다. 잘못된 범위는
    /// MetaCollectionEditor 예외나 명령 모델 누락으로 이어지므로 binding 오류와 같은 Error로 거부합니다.
    /// </summary>
    private static void ValidateCollectionStructure(
        ScreenDefinition definition,
        CollectionWidget collection,
        string path,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(collection.CollectionKey))
        {
            diagnostics.Add(Error(
                definition,
                CollectionKeyMissing,
                $"{path}.collectionKey",
                collection.CollectionKey ?? string.Empty,
                "CollectionWidget.CollectionKey must not be empty or whitespace."));
        }

        if (collection.MinItems < 0)
        {
            diagnostics.Add(Error(
                definition,
                CollectionMinimumInvalid,
                $"{path}.minItems",
                collection.MinItems.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "CollectionWidget.MinItems must be zero or greater."));
        }

        if (collection.MaxItems is int maximum
            && (maximum < 0 || maximum < collection.MinItems))
        {
            diagnostics.Add(Error(
                definition,
                CollectionMaximumInvalid,
                $"{path}.maxItems",
                maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "CollectionWidget.MaxItems must be zero or greater and not less than MinItems."));
        }
    }

    private void ValidateChildren(
        ScreenDefinition definition,
        IReadOnlyList<LayoutNode>? children,
        string path,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (children is null) return;
        for (var index = 0; index < children.Count; index++)
            ValidateLayout(definition, children[index], $"{path}.children[{index}]", diagnostics);
    }

    private void ValidateRead(
        ScreenDefinition definition,
        string? bindingId,
        string bindingPath,
        string? declaredPermission,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (!TryGetCanonicalBindingId(
                definition, bindingId, bindingPath, diagnostics, out var id))
            return;

        if (!_queries.TryGet(id, out var query) || query is null)
        {
            diagnostics.Add(Error(
                definition, ReadBindingMissing, bindingPath, id,
                $"Read binding '{id}' is not registered in the named-query catalog."));
            return;
        }

        if (query.IsWrite)
        {
            diagnostics.Add(Error(
                definition, ReadBindingUsesWrite, bindingPath, id,
                $"Read binding '{id}' points to a write query."));
            return;
        }

        if (!query.IsPublic && string.IsNullOrWhiteSpace(query.RequiredPermission))
        {
            diagnostics.Add(Error(
                definition, ReadBindingPermissionMissing, bindingPath, id,
                $"Read binding '{id}' has neither requiredPermission nor access=public."));
            return;
        }

        ValidatePermission(
            definition, bindingPath, id, declaredPermission, query.RequiredPermission, diagnostics);
    }

    private void ValidateWrite(
        ScreenDefinition definition,
        string? bindingId,
        string bindingPath,
        string? declaredPermission,
        CommandBindingSurface surface,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (!TryGetCanonicalBindingId(
                definition, bindingId, bindingPath, diagnostics, out var id))
            return;

        string? catalogPermission;

        if (id.StartsWith("bridge:", StringComparison.OrdinalIgnoreCase))
        {
            if (!_commands.TryGetDescriptor(id, out var descriptor) || descriptor is null)
            {
                diagnostics.Add(Error(
                    definition, BridgeCommandMissing, bindingPath, id,
                    $"Bridge command '{id}' is not registered in IMetaCommandDriverCatalog."));
                return;
            }

            if (descriptor.ExecutionMode == MetaCommandExecutionMode.HostRequiredAggregate
                && surface != CommandBindingSurface.Bulk)
            {
                diagnostics.Add(Error(
                    definition, BridgeCommandExecutionModeMismatch, bindingPath, id,
                    $"Bridge command '{id}' requires an aggregate host and can only be bound as a bulk command."));
                return;
            }

            if (descriptor.Effect == MetaCommandEffect.NonMutating
                && surface is CommandBindingSurface.FormSave or CommandBindingSurface.Delete)
            {
                diagnostics.Add(Error(
                    definition, BridgeCommandEffectMismatch, bindingPath, id,
                    $"Non-mutating bridge command '{id}' cannot be used as a save or delete binding."));
                return;
            }

            catalogPermission = descriptor.RequiredPermission;
        }
        else
        {
            if (!_queries.TryGet(id, out var query) || query is null)
            {
                diagnostics.Add(Error(
                    definition, WriteBindingMissing, bindingPath, id,
                    $"Write binding '{id}' is not registered in the named-query catalog."));
                return;
            }
            if (!query.IsWrite)
            {
                diagnostics.Add(Error(
                    definition, WriteBindingUsesRead, bindingPath, id,
                    $"Write binding '{id}' points to a read query."));
                return;
            }
            catalogPermission = query.RequiredPermission;
        }

        if (string.IsNullOrWhiteSpace(catalogPermission))
        {
            diagnostics.Add(Error(
                definition, WriteBindingPermissionMissing, bindingPath, id,
                $"Write binding '{id}' does not declare a server requiredPermission."));
            return;
        }

        if (surface == CommandBindingSurface.Button && string.IsNullOrWhiteSpace(declaredPermission))
        {
            diagnostics.Add(Error(
                definition, ButtonPermissionMissing, bindingPath, id,
                $"Button command '{id}' must expose RequiredPermission '{catalogPermission}' " +
                "to keep Designer permission metadata consistent with the command catalog."));
            return;
        }

        ValidatePermission(
            definition, bindingPath, id, declaredPermission, catalogPermission, diagnostics);
    }

    /// <summary>
    /// 런타임은 작성된 바인딩 ID 원문으로 명령을 찾습니다. 런타임에서 실행할 수 없는 trim 별칭을
    /// 검증 단계에서 허용하지 않도록 앞뒤 공백을 명시적으로 거부합니다.
    /// </summary>
    private static bool TryGetCanonicalBindingId(
        ScreenDefinition definition,
        string? bindingId,
        string bindingPath,
        List<ScreenBindingDiagnostic> diagnostics,
        out string id)
    {
        id = string.Empty;
        if (bindingId is null || bindingId.Length == 0) return false;

        if (string.IsNullOrWhiteSpace(bindingId)
            || !string.Equals(bindingId, bindingId.Trim(), StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                definition,
                BindingIdNotCanonical,
                bindingPath,
                bindingId,
                $"Binding ID '{bindingId}' must not contain leading or trailing whitespace."));
            return false;
        }

        id = bindingId;
        return true;
    }

    private static void ValidatePermission(
        ScreenDefinition definition,
        string bindingPath,
        string bindingId,
        string? declaredPermission,
        string? catalogPermission,
        List<ScreenBindingDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(declaredPermission)) return;
        if (string.Equals(
                declaredPermission.Trim(), catalogPermission?.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        diagnostics.Add(Error(
            definition,
            BindingPermissionMismatch,
            bindingPath,
            bindingId,
            $"Binding RequiredPermission '{declaredPermission}' does not match catalog permission " +
            $"'{catalogPermission ?? "<public>"}'."));
    }

    private static ScreenBindingDiagnostic Error(
        ScreenDefinition definition,
        string code,
        string bindingPath,
        string bindingId,
        string message)
        => new(
            definition.UiId,
            code,
            ScreenCapabilityDiagnosticSeverity.Error,
            bindingPath,
            bindingId,
            message);

    /// <summary>descriptor 실행 방식과 화면 바인딩 위치를 대조하기 위한 내부 surface 구분입니다.</summary>
    private enum CommandBindingSurface
    {
        FormSave,
        Delete,
        Button,
        Bulk,
    }
}
