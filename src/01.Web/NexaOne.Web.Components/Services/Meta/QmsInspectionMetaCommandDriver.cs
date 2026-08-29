using System.Globalization;
using System.Text.Json;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

/// <summary>QMS 검사 등록 화면과 Designer가 공유하는 typed bridge 명령 ID입니다.</summary>
public static class QmsInspectionMetaCommands
{
    public const string RecordIncoming = "bridge:qms.inspection.record-incoming";
    public const string RecordProcess = "bridge:qms.inspection.record-process";
    public const string RecordShipping = "bridge:qms.inspection.record-shipping";

    public static IReadOnlyList<string> All { get; } =
        [RecordIncoming, RecordProcess, RecordShipping];

    /// <summary>명령 ID에 고정된 검사 유형을 반환해 화면 값으로 유형이 바뀌지 않게 합니다.</summary>
    public static string? InspectionType(string commandId)
        => commandId switch
        {
            RecordIncoming => "Incoming",
            RecordProcess => "Process",
            RecordShipping => "Shipping",
            _ => null
        };
}

/// <summary>
/// collection/repeater 모델의 검사 항목들을 권위 v2 API로 전달합니다. 사용자 입력 검사 ID는 받지 않으며,
/// 화면이 유지하는 멱등키와 항목 배열을 사용해 네트워크 재시도에서도 중복 검사를 만들지 않습니다.
/// </summary>
public sealed class QmsInspectionMetaCommandDriver : IMetaCommandDriver
{
    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        QmsInspectionMetaCommands.All
            .Select(commandId => new MetaCommandDescriptor(
                commandId,
                RequiredPermission: "qms:manage",
                ExecutionMode: MetaCommandExecutionMode.PerRow,
                Effect: MetaCommandEffect.Mutating))
            .ToArray();

    private readonly IApiClient _api;

    public QmsInspectionMetaCommandDriver(IApiClient api) => _api = api;

    public IReadOnlyCollection<string> CommandIds => QmsInspectionMetaCommands.All;
    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    public string? GetRequiredPermission(string commandId)
        => QmsInspectionMetaCommands.InspectionType(commandId) is null ? null : "qms:manage";

    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        if (QmsInspectionMetaCommands.InspectionType(commandId) is null)
            return MetaCommandAvailability.Disabled($"지원하지 않는 QMS 검사 명령입니다: {commandId}");

        foreach (var (keys, label) in new[]
        {
            (new[] { "idempotencyKey", "IDEMPOTENCY_KEY" }, "멱등키"),
            (new[] { "lotId", "LOT_ID" }, "LOT"),
            (new[] { "equipmentId", "EQUIPMENT_ID" }, "설비")
        })
        {
            if (Value(parameters, keys) is null)
                return MetaCommandAvailability.Disabled($"{label}을(를) 입력하세요.");
        }

        if (!TryPositiveInt(Value(parameters, "lotQuantity", "LOT_QTY", "lotQty"), out var lotQuantity))
            return MetaCommandAvailability.Disabled("LOT 수량은 양의 정수여야 합니다.");
        if (!TryPositiveInt(Value(parameters, "sampleQuantity", "SAMPLE_QTY", "sampleQty"), out var sampleQuantity)
            || sampleQuantity > lotQuantity)
            return MetaCommandAvailability.Disabled("샘플 수량은 1 이상 LOT 수량 이하여야 합니다.");
        if (!TryNonNegativeInt(Value(parameters, "defectQuantity", "DEFECT_QTY", "defectQty"), out var defectQuantity)
            || defectQuantity > sampleQuantity)
            return MetaCommandAvailability.Disabled("불량 수량은 0 이상 샘플 수량 이하여야 합니다.");

        var relationType = Value(parameters, "relationType", "RELATION_TYPE") ?? "Original";
        if (!relationType.Equals("Original", StringComparison.OrdinalIgnoreCase)
            && !relationType.Equals("Correction", StringComparison.OrdinalIgnoreCase)
            && !relationType.Equals("Reinspection", StringComparison.OrdinalIgnoreCase))
            return MetaCommandAvailability.Disabled("관계 유형은 Original, Correction, Reinspection 중 하나여야 합니다.");
        if (!relationType.Equals("Original", StringComparison.OrdinalIgnoreCase)
            && Value(parameters, "parentInspectionId", "PARENT_INSPECTION_ID") is null)
            return MetaCommandAvailability.Disabled("정정 또는 재검사에는 이전 검사 ID가 필요합니다.");

        if (!TryReadItems(parameters, out var items, out var itemError))
            return MetaCommandAvailability.Disabled(itemError);
        if (items.Select(x => x.SpecId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
            return MetaCommandAvailability.Disabled("같은 검사 규격을 한 실행에 중복으로 추가할 수 없습니다.");
        if (items.Any(x => x.SampleQuantity > sampleQuantity || x.DefectQuantity > defectQuantity))
            return MetaCommandAvailability.Disabled("항목 수량은 실행의 샘플/불량 수량을 초과할 수 없습니다.");
        if (Value(parameters, "samplingPlanRevisionId", "SAMPLING_PLAN_REVISION_ID") is null
            && sampleQuantity != lotQuantity)
            return MetaCommandAvailability.Disabled("샘플링 계획이 없으면 LOT 전체를 검사해야 합니다.");

        return MetaCommandAvailability.Enabled;
    }

    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(
                availability.DisabledReason ?? "검사 실행을 등록할 수 없습니다.", 400);

        _ = TryPositiveInt(Value(parameters, "lotQuantity", "LOT_QTY", "lotQty"), out var lotQuantity);
        _ = TryPositiveInt(Value(parameters, "sampleQuantity", "SAMPLE_QTY", "sampleQty"), out var sampleQuantity);
        _ = TryNonNegativeInt(Value(parameters, "defectQuantity", "DEFECT_QTY", "defectQty"), out var defectQuantity);
        _ = TryReadItems(parameters, out var items, out _);

        var request = new RecordInspectionExecutionV2Request(
            Value(parameters, "idempotencyKey", "IDEMPOTENCY_KEY")!,
            QmsInspectionMetaCommands.InspectionType(commandId)!,
            Value(parameters, "lotId", "LOT_ID")!,
            Value(parameters, "equipmentId", "EQUIPMENT_ID")!,
            lotQuantity,
            sampleQuantity,
            defectQuantity,
            items,
            Value(parameters, "samplingPlanRevisionId", "SAMPLING_PLAN_REVISION_ID"),
            Value(parameters, "parentInspectionId", "PARENT_INSPECTION_ID"),
            Value(parameters, "relationType", "RELATION_TYPE") ?? "Original",
            Value(parameters, "remark", "REMARK"));

        var result = await _api.RecordInspectionExecutionV2Async(request, ct);
        return result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode)
            : MetaCommandResult.Failed(
                result.Error ?? "검사 등록에 실패했습니다. 멱등키·규격·LOT·설비·수량을 확인하세요.",
                result.StatusCode);
    }

    /// <summary>
    /// 검사 항목 컬렉션을 v2 입력 DTO로 변환합니다. 필드명은 camelCase·대문자 DB명과
    /// sampleQty/defectQty 단축 별칭을 함께 허용하며, 이름 비교 시 구분 문자를 정규화합니다.
    /// 각 항목은 measuredValue(계량)와 attributeResult(속성) 중 정확히 하나만 가져야 합니다.
    /// 항목 샘플 수량은 양수, 불량 수량은 0 이상 항목 샘플 이하이어야 하며, 변환 후 호출부가
    /// 항목 수량이 헤더의 실행 샘플/불량 수량을 초과하지 않는지도 추가 검증합니다.
    /// </summary>
    private static bool TryReadItems(
        IReadOnlyDictionary<string, object?> parameters,
        out IReadOnlyList<InspectionExecutionItemInputDto> items,
        out string error)
    {
        items = [];
        error = "검사 항목을 한 개 이상 입력하세요.";
        // 화면 메타데이터와 API 모델의 표기 차이를 흡수하기 위해 컬렉션 자체도 두 별칭을 허용합니다.
        var raw = RawValue(parameters, "items", "ITEMS");
        if (raw is null) return false;

        try
        {
            var element = raw switch
            {
                JsonElement json => json,
                string text => JsonDocument.Parse(text).RootElement.Clone(),
                _ => JsonSerializer.SerializeToElement(raw)
            };
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
                return false;

            var parsed = new List<InspectionExecutionItemInputDto>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "각 검사 항목은 객체 형식이어야 합니다.";
                    return false;
                }

                var specId = JsonString(item, "specId", "SPEC_ID");
                if (string.IsNullOrWhiteSpace(specId))
                {
                    error = "각 검사 항목에 검사 규격을 선택하세요.";
                    return false;
                }
                var measured = JsonDecimal(item, "measuredValue", "MEASURED_VALUE");
                var attribute = JsonString(item, "attributeResult", "ATTRIBUTE_RESULT");
                // 계량값과 속성 판정은 둘 다 비거나 둘 다 채워질 수 없는 XOR 입력입니다.
                if (measured is null == (attribute is null))
                {
                    error = "각 항목에는 계량형 측정값 또는 속성형 판정 중 하나만 입력하세요.";
                    return false;
                }
                if (attribute is not null
                    && !attribute.Equals("Pass", StringComparison.OrdinalIgnoreCase)
                    && !attribute.Equals("Fail", StringComparison.OrdinalIgnoreCase)
                    && !attribute.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    && !attribute.Equals("NG", StringComparison.OrdinalIgnoreCase))
                {
                    error = "속성형 판정은 Pass/Fail 또는 OK/NG여야 합니다.";
                    return false;
                }
                if (!JsonInt(item, out var itemSample, "sampleQuantity", "SAMPLE_QTY", "sampleQty")
                    || itemSample <= 0)
                {
                    error = "항목 샘플 수량은 양의 정수여야 합니다.";
                    return false;
                }
                if (!JsonInt(item, out var itemDefect, "defectQuantity", "DEFECT_QTY", "defectQty")
                    || itemDefect < 0 || itemDefect > itemSample)
                {
                    error = "항목 불량 수량은 0 이상 항목 샘플 수량 이하여야 합니다.";
                    return false;
                }

                parsed.Add(new InspectionExecutionItemInputDto(
                    specId.Trim(), measured, attribute?.Trim(), itemSample, itemDefect,
                    JsonString(item, "remark", "REMARK")));
            }

            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "검사 항목 배열의 JSON 형식이 올바르지 않습니다.";
            return false;
        }
    }

    private static decimal? JsonDecimal(JsonElement item, params string[] names)
    {
        if (!TryProperty(item, out var value, names) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        return TryDecimal(value.ToString(), out number) ? number : null;
    }

    private static bool JsonInt(JsonElement item, out int result, params string[] names)
    {
        result = 0;
        if (!TryProperty(item, out var value, names)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out result);
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static string? JsonString(JsonElement item, params string[] names)
        => TryProperty(item, out var value, names)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            && !string.IsNullOrWhiteSpace(value.ToString())
                ? value.ToString().Trim()
                : null;

    private static bool TryProperty(JsonElement item, out JsonElement value, params string[] names)
    {
        foreach (var property in item.EnumerateObject())
            if (names.Any(name => Normalize(name) == Normalize(property.Name)))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static bool TryPositiveInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
           && result > 0;

    private static bool TryNonNegativeInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
           && result >= 0;

    private static bool TryDecimal(string value, out decimal result)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result)
           || decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result);

    private static object? RawValue(
        IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var pair in values)
            if (keys.Any(key => Normalize(key) == Normalize(pair.Key))) return pair.Value;
        return null;
    }

    /// <summary>camelCase와 UPPER_SNAKE 차이를 제거해 Designer 폼 값을 같은 규칙으로 읽습니다.</summary>
    private static string? Value(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        var raw = RawValue(values, keys);
        return string.IsNullOrWhiteSpace(raw?.ToString()) ? null : raw!.ToString()!.Trim();
    }

    private static string Normalize(string value)
        => value.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
