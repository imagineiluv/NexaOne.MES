namespace NexaOne.Web.Services.Meta;

/// <summary>
/// 메타 등록 모델의 자동 생성값과 반복 항목 최소 개수를 준비하는 공통 도우미입니다.
/// 같은 모델에 여러 번 적용해도 이미 채워진 값은 바꾸지 않으므로 실패 재시도에서 멱등키가 유지됩니다.
/// </summary>
public static class MetaModelDefaults
{
    /// <summary>비어 있는 자동 생성 필드만 채웁니다.</summary>
    public static void EnsureGeneratedValues(
        IEnumerable<FieldDefinition> fields,
        IDictionary<string, object?> model)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(model);

        foreach (var field in fields)
        {
            if (field.ValueGenerator == FieldValueGenerator.None
                || (!string.IsNullOrWhiteSpace(field.Key)
                    && model.TryGetValue(field.Key, out var current)
                    && !IsEmpty(current)))
                continue;

            if (string.IsNullOrWhiteSpace(field.Key))
                continue;

            model[field.Key] = field.ValueGenerator switch
            {
                FieldValueGenerator.UuidV4 => Guid.NewGuid().ToString("D"),
                _ => null,
            };
        }
    }

    /// <summary>새 반복 항목 모델을 만들고 항목 스키마의 자동 생성값을 한 번 채웁니다.</summary>
    public static Dictionary<string, object?> CreateItem(IEnumerable<FieldDefinition> fields)
    {
        var item = new Dictionary<string, object?>(StringComparer.Ordinal);
        EnsureGeneratedValues(fields, item);
        return item;
    }

    /// <summary>
    /// 레이아웃의 공유 폼과 컬렉션을 순회하며 신규 모델 기본값을 준비합니다.
    /// 권한 판정기를 전달하면 차단된 부모와 그 하위 스키마는 모델에도 만들지 않습니다.
    /// </summary>
    public static void EnsureLayoutDefaults(
        LayoutNode node,
        Dictionary<string, object?> model,
        Func<LayoutNode, bool>? canAccess = null,
        bool skipIsolatedForms = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(model);
        if (canAccess is not null && !canAccess(node)) return;

        switch (node)
        {
            case FieldWidget field:
                EnsureGeneratedValues([ToField(field)], model);
                break;
            case FormWidget form:
                if (skipIsolatedForms
                    && (form.Isolated || !string.IsNullOrWhiteSpace(form.BindingScope))) break;
                foreach (var field in form.Fields ?? [])
                    EnsureLayoutDefaults(field, model, canAccess, skipIsolatedForms);
                break;
            case CollectionWidget collection:
                if (skipIsolatedForms && !string.IsNullOrWhiteSpace(collection.BindingScope)) break;
                EnsureCollectionDefaults(collection, model, canAccess);
                break;
            case SectionNode section:
                foreach (var child in section.Children ?? [])
                    EnsureLayoutDefaults(child, model, canAccess, skipIsolatedForms);
                break;
            case RowNode row:
                foreach (var child in row.Children ?? [])
                    EnsureLayoutDefaults(child, model, canAccess, skipIsolatedForms);
                break;
            case ColumnNode column:
                foreach (var child in column.Children ?? [])
                    EnsureLayoutDefaults(child, model, canAccess, skipIsolatedForms);
                break;
        }
    }

    /// <summary>공유 모델에서 컬렉션 항목을 읽습니다. 형식이 다르면 빈 목록을 반환합니다.</summary>
    public static IReadOnlyList<Dictionary<string, object?>> GetCollectionItems(
        IReadOnlyDictionary<string, object?> model,
        string collectionKey)
    {
        if (string.IsNullOrWhiteSpace(collectionKey)
            || !model.TryGetValue(collectionKey, out var raw)
            || raw is null)
            return Array.Empty<Dictionary<string, object?>>();

        return raw switch
        {
            List<Dictionary<string, object?>> list => list,
            IReadOnlyList<Dictionary<string, object?>> list => list,
            IEnumerable<Dictionary<string, object?>> sequence => sequence.ToList(),
            _ => Array.Empty<Dictionary<string, object?>>(),
        };
    }

    private static void EnsureCollectionDefaults(
        CollectionWidget collection,
        Dictionary<string, object?> model,
        Func<LayoutNode, bool>? canAccess)
    {
        if (string.IsNullOrWhiteSpace(collection.CollectionKey)) return;

        var fields = (collection.Fields ?? [])
            .Where(field => canAccess is null || canAccess(field))
            .Select(ToField)
            .ToArray();
        var existing = GetCollectionItems(model, collection.CollectionKey);
        var items = existing is List<Dictionary<string, object?>> list
            ? list
            : existing.Select(Clone).ToList();

        foreach (var item in items)
            EnsureGeneratedValues(fields, item);

        var minimum = Math.Max(0, collection.MinItems);
        while (items.Count < minimum)
            items.Add(CreateItem(fields));

        model[collection.CollectionKey] = items;
    }

    private static FieldDefinition ToField(FieldWidget widget)
        => widget.Field
            ?? new FieldDefinition(widget.FieldKey ?? string.Empty, widget.FieldKey ?? string.Empty);

    private static Dictionary<string, object?> Clone(Dictionary<string, object?> source)
        => new(source, source.Comparer);

    private static bool IsEmpty(object? value)
        => value is null || value is string text && string.IsNullOrWhiteSpace(text);
}
