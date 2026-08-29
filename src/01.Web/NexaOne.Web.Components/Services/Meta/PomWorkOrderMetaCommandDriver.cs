using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

/// <summary>Designer에서 사용하는 POM 작업지시 브리지 액션 ID의 단일 출처입니다.</summary>
public static class PomWorkOrderMetaCommands
{
    public const string Create = "bridge:pom.work-order.create";
    public const string Release = "bridge:pom.work-order.release";
    public const string Cancel = "bridge:pom.work-order.cancel";
    public const string Start = "bridge:pom.work-order.start";
    public const string Report = "bridge:pom.work-order.report";
    public const string Hold = "bridge:pom.work-order.hold";
    public const string ReleaseHold = "bridge:pom.work-order.release-hold";
    public const string Complete = "bridge:pom.work-order.complete";

    public static IReadOnlyList<string> All { get; } =
        [Create, Release, Cancel, Start, Report, Hold, ReleaseHold, Complete];
}

/// <summary>
/// MetaScreen 모델/그리드 행을 POM 작업지시 typed REST 요청으로 변환합니다.
/// SQL UPDATE를 우회하므로 서버의 JWT 권한, 낙관적 버전, 멱등성 및 실행 이력 저장이 그대로 적용됩니다.
/// </summary>
public sealed class PomWorkOrderMetaCommandDriver : IMetaCommandDriver
{
    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        PomWorkOrderMetaCommands.All
            .Select(commandId => new MetaCommandDescriptor(
                commandId,
                RequiredPermission: IsManagementCommand(commandId) ? "pom:manage" : "pom:execute",
                ExecutionMode: MetaCommandExecutionMode.PerRow,
                Effect: MetaCommandEffect.Mutating))
            .ToArray();

    private readonly IApiClient _api;

    public PomWorkOrderMetaCommandDriver(IApiClient api) => _api = api;

    public IReadOnlyCollection<string> CommandIds => PomWorkOrderMetaCommands.All;

    /// <summary>작업지시 액션은 현재 행/폼 모델을 바꾸는 일반 변경 명령입니다.</summary>
    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    // Designer의 표시/사전 안내용 힌트다. 최종 권한 판정은 PomWorkOrderController의 RequirePermission이 수행한다.
    public string? GetRequiredPermission(string commandId)
        => PomWorkOrderMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase)
            ? IsManagementCommand(commandId)
                ? "pom:manage"
                : "pom:execute"
            : null;

    /// <summary>현재 작업지시 상태와 보류 여부를 기준으로 잘못된 액션을 UI와 실행 경계에서 미리 차단합니다.</summary>
    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        if (!PomWorkOrderMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase))
            return MetaCommandAvailability.Disabled($"지원하지 않는 작업지시 명령입니다: {commandId}");
        if (commandId.Equals(PomWorkOrderMetaCommands.Create, StringComparison.OrdinalIgnoreCase))
            return ValidateCreate(parameters);
        if (string.IsNullOrWhiteSpace(Value(parameters, "WORK_ORDER_ID", "workOrderId")))
            return MetaCommandAvailability.Disabled("작업지시를 선택하세요.");
        if (!TryInt(parameters, out var version, "VERSION_NO", "expectedVersion") || version < 1)
            return MetaCommandAvailability.Disabled("유효한 작업지시 버전이 필요합니다. 목록을 새로고침하세요.");
        if (!IsSupportedChannel(context.ClientChannel))
            return MetaCommandAvailability.Disabled("실행 채널은 MES, MOBILE 또는 POP이어야 합니다.");

        var status = Value(parameters, "STATUS", "status");
        var isHold = Bool(parameters, "IS_HOLD", "isHold");
        if (string.IsNullOrWhiteSpace(status))
            return MetaCommandAvailability.Disabled("작업지시 상태를 확인할 수 없습니다.");

        if (commandId.Equals(PomWorkOrderMetaCommands.Release, StringComparison.OrdinalIgnoreCase))
            return status.Equals("Created", StringComparison.OrdinalIgnoreCase)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("Created 상태의 작업지시만 릴리즈할 수 있습니다.");

        if (commandId.Equals(PomWorkOrderMetaCommands.Cancel, StringComparison.OrdinalIgnoreCase))
            return status.Equals("Created", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("Released", StringComparison.OrdinalIgnoreCase)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("Created 또는 Released 상태의 작업지시만 취소할 수 있습니다.");

        // 라우팅 연결 작업지시는 LOT 단위 Track-In/Track-Out이 공정 선후행과 추적성의 기준이다.
        // 직접 시작·실적·완료를 허용하면 LOT 흐름과 작업지시 누계가 분리되므로 UI에서도 먼저 차단한다.
        // 서버 정책이 최종 경계이며, 보류/보류해제는 안전 조치이므로 이 선행 차단에서 제외한다.
        var routingScope = Value(parameters, "ROUTING_SCOPE", "routingScope");
        var routeBound = (routingScope is not null
                && !routingScope.Equals("Unbound", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrWhiteSpace(Value(parameters, "ROUTING_ID", "routingId"))
            || TryInt(parameters, out _, "ROUTING_STEP_NO", "routingStepNo");
        var isSafetyHoldAction = commandId.Equals(PomWorkOrderMetaCommands.Hold, StringComparison.OrdinalIgnoreCase)
                                 || commandId.Equals(PomWorkOrderMetaCommands.ReleaseHold, StringComparison.OrdinalIgnoreCase);
        if (routeBound && !isSafetyHoldAction)
            return MetaCommandAvailability.Disabled(
                routingScope?.Equals("SerialRoute", StringComparison.OrdinalIgnoreCase) == true
                    ? "전체 라우팅 작업지시는 직접 실행할 수 없습니다. LOT을 스캔한 뒤 첫 공정부터 마지막 공정까지 Track-In/Track-Out을 사용하세요."
                    : "라우팅 연결 작업지시는 직접 실행할 수 없습니다. LOT을 스캔한 뒤 공정 Track-In/Track-Out을 사용하세요.");

        if (commandId.Equals(PomWorkOrderMetaCommands.Start, StringComparison.OrdinalIgnoreCase))
            return status.Equals("Released", StringComparison.OrdinalIgnoreCase) && !isHold
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("Released 상태이며 보류되지 않은 작업지시만 시작할 수 있습니다.");

        if (commandId.Equals(PomWorkOrderMetaCommands.ReleaseHold, StringComparison.OrdinalIgnoreCase))
            return isHold && !IsTerminal(status)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("보류 중인 작업지시만 보류 해제할 수 있습니다.");

        // 현장 실행 정책: 실적·보류·완료는 시작된 비보류 작업지시에만 허용한다.
        return status.Equals("Started", StringComparison.OrdinalIgnoreCase) && !isHold
            ? MetaCommandAvailability.Enabled
            : MetaCommandAvailability.Disabled("Started 상태이며 보류되지 않은 작업지시에서만 실행할 수 있습니다.");
    }

    /// <summary>누계 수량과 버전/멱등/채널 정보를 typed 요청으로 만들어 작업지시 REST API를 호출합니다.</summary>
    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(
                availability.DisabledReason ?? "실행할 수 없는 작업지시 명령입니다.",
                commandId.Equals(PomWorkOrderMetaCommands.Create, StringComparison.OrdinalIgnoreCase) ? 400 : 409);

        if (commandId.Equals(PomWorkOrderMetaCommands.Create, StringComparison.OrdinalIgnoreCase))
        {
            var scope = ResolveRoutingScope(parameters);
            _ = TryDecimal(parameters, out var planQty, "PLAN_QTY", "planQty");
            int? routingStepNo = TryInt(parameters, out var stepNo, "ROUTING_STEP_NO", "routingStepNo")
                ? stepNo
                : null;
            var createRequest = new PomWorkOrderCreateRequest(
                Value(parameters, "WORK_ORDER_ID", "workOrderId")!,
                Value(parameters, "PRODUCTION_ORDER_ID", "productionOrderId")!,
                Value(parameters, "PLANT_ID", "plantId")!,
                Value(parameters, "WORK_ORDER_NAME", "workOrderName")!,
                Value(parameters, "PRODUCT_ID", "productId")!,
                planQty,
                DateValue(parameters, "PLAN_START_DATE", "planStartDate"),
                DateValue(parameters, "PLAN_END_DATE", "planEndDate"),
                Value(parameters, "PROCESS_ID", "processId"),
                Value(parameters, "EQUIPMENT_ID", "equipmentId"),
                Value(parameters, "OWNER_ID", "ownerId"),
                Value(parameters, "ROUTING_ID", "routingId"),
                routingStepNo,
                Value(parameters, "WORK_CENTER_ID", "workCenterId"),
                Value(parameters, "AREA_ID", "areaId"),
                Value(parameters, "WORK_ORDER_TYPE", "workOrderType"),
                Value(parameters, "SALES_ORDER_ID", "salesOrderId"),
                Value(parameters, "DESCRIPTION", "description"),
                scope);
            var created = await _api.CreatePomWorkOrderAsync(createRequest, ct);
            return created.Success
                ? MetaCommandResult.Succeeded(created.StatusCode, "작업지시가 등록되었습니다.")
                : MetaCommandResult.Failed(created.Error ?? "작업지시 등록에 실패했습니다.", created.StatusCode);
        }

        var workOrderId = Value(parameters, "WORK_ORDER_ID", "workOrderId")!;
        _ = TryInt(parameters, out var expectedVersion, "VERSION_NO", "expectedVersion");
        var action = ActionSegment(commandId);

        decimal? goodQty = null;
        decimal? defectQty = null;
        if (action is "report" or "complete")
        {
            if (!TryDecimal(parameters, out var good, "GOOD_QTY", "goodQty", "COMPLETE_QTY", "completeQty") || good < 0)
                return MetaCommandResult.Failed("양품 누계는 0 이상의 숫자여야 합니다.", 400);
            if (!TryDecimal(parameters, out var defect, "DEFECT_QTY", "defectQty", "SCRAP_QTY", "scrapQty") || defect < 0)
                return MetaCommandResult.Failed("불량 누계는 0 이상의 숫자여야 합니다.", 400);
            if (action == "complete" && good + defect <= 0)
                return MetaCommandResult.Failed("완료하려면 양품 또는 불량 누계가 1개 이상이어야 합니다.", 400);
            if (TryDecimal(parameters, out var upper, "START_QTY", "startQty", "PLAN_QTY", "planQty")
                && upper > 0 && good + defect > upper)
                return MetaCommandResult.Failed("양품과 불량 누계 합계는 시작 수량을 초과할 수 없습니다.", 400);
            goodQty = good;
            defectQty = defect;
        }

        var channel = context.ClientChannel.Trim().ToUpperInvariant();
        var idempotencyKey = Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey")
            ?? StableIdempotencyKey(channel, action, workOrderId, expectedVersion, goodQty, defectQty);
        var request = new PomWorkOrderActionRequest(
            expectedVersion,
            idempotencyKey,
            channel,
            Trim(context.DeviceId),
            Value(parameters, "REMARK", "remark"),
            goodQty,
            defectQty);

        var result = await _api.ExecutePomWorkOrderActionAsync(action, workOrderId, request, ct);
        return result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode)
            : MetaCommandResult.Failed(result.Error ?? "작업지시 명령 실행에 실패했습니다.", result.StatusCode);
    }

    private static string ActionSegment(string commandId)
        => commandId.Equals(PomWorkOrderMetaCommands.ReleaseHold, StringComparison.OrdinalIgnoreCase)
            ? "release-hold"
            : commandId[(commandId.LastIndexOf('.') + 1)..].ToLowerInvariant();

    private static bool IsSupportedChannel(string? value)
        => value?.Trim().ToUpperInvariant() is "MES" or "MOBILE" or "POP";

    private static bool IsManagementCommand(string commandId)
        => commandId.Equals(PomWorkOrderMetaCommands.Create, StringComparison.OrdinalIgnoreCase)
           || commandId.Equals(PomWorkOrderMetaCommands.Release, StringComparison.OrdinalIgnoreCase)
           || commandId.Equals(PomWorkOrderMetaCommands.Cancel, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(string status)
        => status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
           || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>등록 폼의 공통 필드와 라우팅 실행 범위별 필수·금지 조합을 API 호출 전에 안내합니다.</summary>
    private static MetaCommandAvailability ValidateCreate(IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (key, label) in new[]
                 {
                     ("workOrderId", "작업지시 ID"),
                     ("productionOrderId", "생산관리오더 ID"),
                     ("plantId", "공장 ID"),
                     ("workOrderName", "작업지시명"),
                     ("productId", "품목 ID"),
                 })
        {
            if (string.IsNullOrWhiteSpace(Value(parameters, key)))
                return MetaCommandAvailability.Disabled($"{label}을(를) 입력하세요.");
        }

        if (!TryDecimal(parameters, out var planQty, "PLAN_QTY", "planQty") || planQty <= 0)
            return MetaCommandAvailability.Disabled("계획 수량은 0보다 커야 합니다.");

        var scope = ResolveRoutingScope(parameters);
        if (scope.Length == 0)
            return MetaCommandAvailability.Disabled("라우팅 실행 범위는 Unbound, Operation 또는 SerialRoute여야 합니다.");

        var routingId = Value(parameters, "ROUTING_ID", "routingId");
        var processId = Value(parameters, "PROCESS_ID", "processId");
        var hasStep = TryInt(parameters, out var stepNo, "ROUTING_STEP_NO", "routingStepNo");
        return scope switch
        {
            "Unbound" when !string.IsNullOrWhiteSpace(routingId) || hasStep
                => MetaCommandAvailability.Disabled("미연결 작업지시는 라우팅 ID와 공정 순번을 비워 주세요."),
            "Operation" when string.IsNullOrWhiteSpace(routingId)
                => MetaCommandAvailability.Disabled("공정 단위 작업지시는 라우팅 ID가 필요합니다."),
            "Operation" when !hasStep || stepNo <= 0
                => MetaCommandAvailability.Disabled("공정 단위 작업지시는 1 이상의 공정 순번이 필요합니다."),
            "Operation" when string.IsNullOrWhiteSpace(processId)
                => MetaCommandAvailability.Disabled("공정 단위 작업지시는 공정 ID가 필요합니다."),
            "SerialRoute" when string.IsNullOrWhiteSpace(routingId)
                => MetaCommandAvailability.Disabled("전체 라우팅 작업지시는 제품 라우팅 ID가 필요합니다."),
            "SerialRoute" when hasStep || !string.IsNullOrWhiteSpace(processId)
                => MetaCommandAvailability.Disabled("전체 라우팅 작업지시는 공정 순번과 공정 ID를 비워 주세요. 라우팅 마스터의 전 공정을 순차 사용합니다."),
            _ => MetaCommandAvailability.Enabled,
        };
    }

    /// <summary>구 클라이언트는 명시 범위가 없어도 라우팅 ID/공정 순번 조합으로 같은 의미를 추론합니다.</summary>
    private static string ResolveRoutingScope(IReadOnlyDictionary<string, object?> parameters)
    {
        var explicitScope = Value(parameters, "ROUTING_SCOPE", "routingScope");
        if (!string.IsNullOrWhiteSpace(explicitScope))
        {
            if (explicitScope.Equals("Unbound", StringComparison.OrdinalIgnoreCase)) return "Unbound";
            if (explicitScope.Equals("Operation", StringComparison.OrdinalIgnoreCase)) return "Operation";
            if (explicitScope.Equals("SerialRoute", StringComparison.OrdinalIgnoreCase)) return "SerialRoute";
            return string.Empty;
        }

        var routingId = Value(parameters, "ROUTING_ID", "routingId");
        return string.IsNullOrWhiteSpace(routingId)
            ? "Unbound"
            : TryInt(parameters, out _, "ROUTING_STEP_NO", "routingStepNo")
                ? "Operation"
                : "SerialRoute";
    }

    private static DateTime? DateValue(
        IReadOnlyDictionary<string, object?> values,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in values)
            {
                if (Normalize(pair.Key) != Normalize(key) || pair.Value is null) continue;
                if (pair.Value is DateTime date) return date;
                if (pair.Value is DateTimeOffset offset) return offset.UtcDateTime;
                if (DateTime.TryParse(pair.Value.ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed;
                if (DateTime.TryParse(pair.Value.ToString(), CultureInfo.CurrentCulture,
                        DateTimeStyles.AllowWhiteSpaces, out parsed))
                    return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// 화면 응답이 유실되어 같은 버전/누계로 다시 누르면 같은 서버 실행을 재생하고,
    /// 성공 후 VERSION_NO 또는 누계가 바뀌면 새 실행이 되도록 안정 멱등키를 만듭니다.
    /// </summary>
    internal static string StableIdempotencyKey(
        string channel,
        string action,
        string workOrderId,
        int expectedVersion,
        decimal? goodQty,
        decimal? defectQty)
    {
        var canonical = string.Join('|',
            channel.Trim().ToUpperInvariant(),
            action.Trim().ToLowerInvariant(),
            workOrderId.Trim().ToUpperInvariant(),
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            goodQty?.ToString("G29", CultureInfo.InvariantCulture) ?? "-",
            defectQty?.ToString("G29", CultureInfo.InvariantCulture) ?? "-");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"meta:{channel.Trim().ToLowerInvariant()}:{action.Trim().ToLowerInvariant()}:{digest}";
    }

    private static bool TryInt(
        IReadOnlyDictionary<string, object?> values,
        out int result,
        params string[] keys)
        => int.TryParse(Value(values, keys), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryDecimal(
        IReadOnlyDictionary<string, object?> values,
        out decimal result,
        params string[] keys)
        => decimal.TryParse(Value(values, keys), NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    private static bool Bool(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        var value = Value(values, keys);
        return value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("y", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>UPPER_SNAKE/camelCase 차이를 제거해 Designer 폼과 DB 조회 행을 같은 방식으로 읽습니다.</summary>
    private static string? Value(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var exact) && !string.IsNullOrWhiteSpace(exact?.ToString()))
                return exact!.ToString()!.Trim();

            var normalized = Normalize(key);
            foreach (var pair in values)
                if (Normalize(pair.Key) == normalized && !string.IsNullOrWhiteSpace(pair.Value?.ToString()))
                    return pair.Value!.ToString()!.Trim();
        }
        return null;
    }

    private static string Normalize(string value)
        => value.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
