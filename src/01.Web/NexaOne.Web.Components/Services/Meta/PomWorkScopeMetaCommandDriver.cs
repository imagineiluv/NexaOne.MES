using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

/// <summary>설비 작업 관리 화면에서 사용하는 POM 작업 범위 bridge 명령의 단일 출처입니다.</summary>
public static class PomWorkScopeMetaCommands
{
    public const string Create = "bridge:pom.work-scope.create";
    public const string Release = "bridge:pom.work-scope.release";
    public const string Start = "bridge:pom.work-scope.start";
    public const string Report = "bridge:pom.work-scope.report";
    public const string Hold = "bridge:pom.work-scope.hold";
    public const string ReleaseHold = "bridge:pom.work-scope.release-hold";
    public const string Complete = "bridge:pom.work-scope.complete";
    public const string Cancel = "bridge:pom.work-scope.cancel";

    public static IReadOnlyList<string> All { get; } =
        [Create, Release, Start, Report, Hold, ReleaseHold, Complete, Cancel];

    public static IReadOnlyList<string> Lifecycle { get; } =
        [Release, Start, Report, Hold, ReleaseHold, Complete, Cancel];
}

/// <summary>
/// 작업 범위 등록 폼과 POM.WorkScopeList 선택 행을 typed POM REST API로 연결합니다.
/// 서버가 JWT 작업자, 상태 전이, 낙관적 버전, 멱등키와 실행 이력을 최종 검증합니다.
/// </summary>
public sealed class PomWorkScopeMetaCommandDriver : IMetaCommandDriver
{
    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        PomWorkScopeMetaCommands.All
            .Select(commandId => new MetaCommandDescriptor(
                commandId,
                RequiredPermission: IsManagementCommand(commandId) ? "pom:manage" : "pom:execute",
                ExecutionMode: MetaCommandExecutionMode.PerRow,
                Effect: MetaCommandEffect.Mutating))
            .ToArray();

    private readonly IApiClient _api;

    public PomWorkScopeMetaCommandDriver(IApiClient api)
        => _api = api ?? throw new ArgumentNullException(nameof(api));

    public IReadOnlyCollection<string> CommandIds => PomWorkScopeMetaCommands.All;

    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    public string? GetRequiredPermission(string commandId)
        => PomWorkScopeMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase)
            ? IsManagementCommand(commandId) ? "pom:manage" : "pom:execute"
            : null;

    /// <summary>
    /// 빠른 UX 안내용 상태 가드입니다. 서버의 권한·동시성·도메인 검증을 대체하지 않습니다.
    /// </summary>
    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        if (!PomWorkScopeMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase))
            return MetaCommandAvailability.Disabled($"지원하지 않는 작업 범위 명령입니다: {commandId}");

        if (commandId.Equals(PomWorkScopeMetaCommands.Create, StringComparison.OrdinalIgnoreCase))
            return ValidateCreate(parameters);

        if (string.IsNullOrWhiteSpace(Value(parameters, "WORK_SCOPE_ID", "workScopeId")))
            return MetaCommandAvailability.Disabled("작업 범위를 선택하세요.");
        if (!TryInt(parameters, out var version, "VERSION_NO", "versionNo", "expectedVersion") || version < 1)
            return MetaCommandAvailability.Disabled("유효한 작업 범위 버전이 필요합니다. 목록을 새로고침하세요.");
        var suppliedIdempotencyKey = Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey");
        if (suppliedIdempotencyKey?.Length > 100)
            return MetaCommandAvailability.Disabled("멱등키는 100자 이하여야 합니다.");
        if (!IsSupportedChannel(context.ClientChannel))
            return MetaCommandAvailability.Disabled("실행 채널은 MES, MOBILE 또는 POP이어야 합니다.");

        var status = Value(parameters, "STATUS", "status");
        if (string.IsNullOrWhiteSpace(status))
            return MetaCommandAvailability.Disabled("작업 범위 상태를 확인할 수 없습니다.");
        var isHold = Bool(parameters, "IS_HOLD", "isHold");

        if (commandId.Equals(PomWorkScopeMetaCommands.Release, StringComparison.OrdinalIgnoreCase))
            return status.Equals("Created", StringComparison.OrdinalIgnoreCase)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("Created 상태의 작업 범위만 릴리즈할 수 있습니다.");

        if (commandId.Equals(PomWorkScopeMetaCommands.Start, StringComparison.OrdinalIgnoreCase))
            return status.Equals("Released", StringComparison.OrdinalIgnoreCase) && !isHold
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("Released 상태이며 보류되지 않은 작업 범위만 시작할 수 있습니다.");

        if (commandId.Equals(PomWorkScopeMetaCommands.ReleaseHold, StringComparison.OrdinalIgnoreCase))
            return isHold && !IsTerminal(status)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("보류 중인 비종료 작업 범위만 보류 해제할 수 있습니다.");

        if (commandId.Equals(PomWorkScopeMetaCommands.Hold, StringComparison.OrdinalIgnoreCase))
            return !IsTerminal(status) && !isHold
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("종료되지 않은 비보류 작업 범위만 보류할 수 있습니다.");

        if (commandId.Equals(PomWorkScopeMetaCommands.Cancel, StringComparison.OrdinalIgnoreCase))
            return !IsTerminal(status)
                ? MetaCommandAvailability.Enabled
                : MetaCommandAvailability.Disabled("완료 또는 취소된 작업 범위는 취소할 수 없습니다.");

        // Report/Complete는 도메인에서 Started + 비보류만 허용한다.
        if (!status.Equals("Started", StringComparison.OrdinalIgnoreCase) || isHold)
            return MetaCommandAvailability.Disabled("Started 상태이며 보류되지 않은 작업 범위에서만 실적을 처리할 수 있습니다.");

        return ValidateQuantities(parameters, commandId);
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
                availability.DisabledReason ?? "실행할 수 없는 작업 범위 명령입니다.",
                commandId.Equals(PomWorkScopeMetaCommands.Create, StringComparison.OrdinalIgnoreCase)
                    || IsQuantityValidationFailure(availability.DisabledReason)
                    ? 400
                    : 409);

        if (commandId.Equals(PomWorkScopeMetaCommands.Create, StringComparison.OrdinalIgnoreCase))
        {
            var scopeType = NormalizeScopeType(Value(parameters, "SCOPE_TYPE", "scopeType"));
            _ = TryDecimal(parameters, out var planQty, "PLAN_QTY", "planQty");
            int? recipeVersion = TryInt(parameters, out var parsedRecipeVersion, "RECIPE_VERSION", "recipeVersion")
                ? parsedRecipeVersion
                : null;
            var createRequest = new PomWorkScopeCreateRequest(
                WorkScopeId: Value(parameters, "WORK_SCOPE_ID", "workScopeId")!,
                PlantId: Value(parameters, "PLANT_ID", "plantId")!,
                ScopeType: scopeType!,
                TargetId: Value(parameters, "TARGET_ID", "targetId")!,
                Name: Value(parameters, "NAME", "name")!,
                ParentScopeId: Value(parameters, "PARENT_SCOPE_ID", "parentScopeId"),
                EquipmentId: Value(parameters, "EQUIPMENT_ID", "equipmentId"),
                ProductId: Value(parameters, "PRODUCT_ID", "productId"),
                ProcessId: Value(parameters, "PROCESS_ID", "processId"),
                RecipeId: Value(parameters, "RECIPE_ID", "recipeId"),
                RecipeVersion: recipeVersion,
                PlanQty: planQty > 0 ? planQty : null,
                OwnerId: Value(parameters, "OWNER_ID", "ownerId"),
                Description: Value(parameters, "DESCRIPTION", "description"),
                CarrierId: Value(parameters, "CARRIER_ID", "carrierId"),
                WorkOrderId: Value(parameters, "WORK_ORDER_ID", "workOrderId"),
                IdempotencyKey: Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey")
                    ?? StableCreateIdempotencyKey(parameters));
            var created = await _api.CreatePomWorkScopeAsync(createRequest, ct);
            return created.Success
                ? MetaCommandResult.Succeeded(created.StatusCode, "작업 범위가 등록되었습니다.")
                : MetaCommandResult.Failed(created.Error ?? "작업 범위 등록에 실패했습니다.", created.StatusCode);
        }

        var workScopeId = Value(parameters, "WORK_SCOPE_ID", "workScopeId")!;
        _ = TryInt(parameters, out var expectedVersion, "VERSION_NO", "versionNo", "expectedVersion");
        var action = ActionSegment(commandId);

        decimal? goodQty = null;
        decimal? defectQty = null;
        if (action is "report" or "complete")
        {
            _ = TryDecimal(parameters, out var good, "GOOD_QTY", "goodQty", "COMPLETE_QTY", "completeQty");
            _ = TryDecimal(parameters, out var defect, "DEFECT_QTY", "defectQty", "SCRAP_QTY", "scrapQty");
            goodQty = good;
            defectQty = defect;
        }

        var channel = context.ClientChannel.Trim().ToUpperInvariant();
        var carrierId = Trim(Value(parameters, "CARRIER_ID", "carrierId"));
        var resultCode = Trim(Value(parameters, "RESULT_CODE", "resultCode", "PASS_FAIL", "passFail"));
        var resultMetadataJson = Trim(Value(parameters, "RESULT_METADATA_JSON", "resultMetadataJson"));
        var idempotencyKey = Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey")
            ?? StableIdempotencyKey(
                channel, action, workScopeId, expectedVersion, goodQty, defectQty,
                carrierId, resultCode, resultMetadataJson);
        var actionRequest = new PomWorkScopeActionRequest(
            ExpectedVersion: expectedVersion,
            IdempotencyKey: idempotencyKey,
            ClientChannel: channel,
            DeviceId: Trim(context.DeviceId),
            Remark: Value(parameters, "REMARK", "remark"),
            GoodQty: goodQty,
            DefectQty: defectQty,
            CarrierId: carrierId,
            ResultCode: resultCode,
            ResultMetadataJson: resultMetadataJson);
        var result = await _api.ExecutePomWorkScopeActionAsync(action, workScopeId, actionRequest, ct);
        return result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode)
            : MetaCommandResult.Failed(result.Error ?? "작업 범위 명령 실행에 실패했습니다.", result.StatusCode);
    }

    private static MetaCommandAvailability ValidateCreate(IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (key, label) in new[]
        {
            ("workScopeId", "작업 범위 ID"),
            ("plantId", "공장 ID"),
            ("scopeType", "범위 유형"),
            ("targetId", "대상 ID"),
            ("name", "작업명"),
        })
        {
            if (string.IsNullOrWhiteSpace(Value(parameters, key)))
                return MetaCommandAvailability.Disabled($"{label}을(를) 입력하세요.");
        }

        var scopeType = NormalizeScopeType(Value(parameters, "SCOPE_TYPE", "scopeType"));
        if (scopeType is null)
            return MetaCommandAvailability.Disabled("범위 유형은 Batch, Campaign, Carrier, Lot, Equipment 또는 Other여야 합니다.");

        if (TryDecimal(parameters, out var planQty, "PLAN_QTY", "planQty") && planQty <= 0)
            return MetaCommandAvailability.Disabled("계획 수량은 입력할 경우 0보다 커야 합니다.");
        if (TryInt(parameters, out var recipeVersion, "RECIPE_VERSION", "recipeVersion") && recipeVersion <= 0)
            return MetaCommandAvailability.Disabled("레시피 버전은 입력할 경우 1 이상이어야 합니다.");
        var suppliedIdempotencyKey = Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey");
        if (suppliedIdempotencyKey?.Length > 100)
            return MetaCommandAvailability.Disabled("멱등키는 100자 이하여야 합니다.");

        var parentId = Value(parameters, "PARENT_SCOPE_ID", "parentScopeId");
        if (scopeType == "Campaign" && parentId is not null)
            return MetaCommandAvailability.Disabled("Campaign은 최상위 작업 범위이므로 상위 범위를 비워 주세요.");

        var targetId = Value(parameters, "TARGET_ID", "targetId");
        var carrierId = Value(parameters, "CARRIER_ID", "carrierId");
        if (scopeType == "Carrier"
            && carrierId is not null
            && !carrierId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            return MetaCommandAvailability.Disabled("Carrier 범위의 Carrier ID는 대상 ID와 같아야 합니다.");

        var equipmentId = Value(parameters, "EQUIPMENT_ID", "equipmentId");
        if (scopeType == "Equipment"
            && equipmentId is not null
            && !equipmentId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            return MetaCommandAvailability.Disabled("설비 범위의 설비 ID는 대상 ID와 같아야 합니다.");

        return MetaCommandAvailability.Enabled;
    }

    private static MetaCommandAvailability ValidateQuantities(
        IReadOnlyDictionary<string, object?> parameters,
        string commandId)
    {
        if (!TryDecimal(parameters, out var good, "GOOD_QTY", "goodQty", "COMPLETE_QTY", "completeQty") || good < 0)
            return MetaCommandAvailability.Disabled("양품 누계는 0 이상의 숫자여야 합니다.");
        if (!TryDecimal(parameters, out var defect, "DEFECT_QTY", "defectQty", "SCRAP_QTY", "scrapQty") || defect < 0)
            return MetaCommandAvailability.Disabled("이상 누계는 0 이상의 숫자여야 합니다.");
        if (commandId.Equals(PomWorkScopeMetaCommands.Complete, StringComparison.OrdinalIgnoreCase)
            && good + defect <= 0)
            return MetaCommandAvailability.Disabled("완료하려면 양품 또는 이상 누계가 1개 이상이어야 합니다.");
        if (TryDecimal(parameters, out var upper, "START_QTY", "startQty", "PLAN_QTY", "planQty")
            && upper > 0 && good + defect > upper)
            return MetaCommandAvailability.Disabled("양품과 이상 누계 합계는 시작 수량을 초과할 수 없습니다.");
        return MetaCommandAvailability.Enabled;
    }

    private static string? NormalizeScopeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToUpperInvariant() switch
        {
            "BATCH" => "Batch",
            "CAMPAIGN" => "Campaign",
            "CARRIER" => "Carrier",
            "LOT" => "Lot",
            "EQUIPMENT" => "Equipment",
            "OTHER" => "Other",
            _ => null,
        };
    }

    private static string ActionSegment(string commandId)
        => commandId.Equals(PomWorkScopeMetaCommands.ReleaseHold, StringComparison.OrdinalIgnoreCase)
            ? "release-hold"
            : commandId[(commandId.LastIndexOf('.') + 1)..].ToLowerInvariant();

    private static bool IsSupportedChannel(string? value)
        => value?.Trim().ToUpperInvariant() is "MES" or "MOBILE" or "POP";

    private static bool IsManagementCommand(string commandId)
        => commandId.Equals(PomWorkScopeMetaCommands.Create, StringComparison.OrdinalIgnoreCase)
           || commandId.Equals(PomWorkScopeMetaCommands.Release, StringComparison.OrdinalIgnoreCase)
           || commandId.Equals(PomWorkScopeMetaCommands.Cancel, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(string status)
        => status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
           || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuantityValidationFailure(string? reason)
        => reason is not null
            && (reason.Contains("누계", StringComparison.Ordinal)
                || reason.Contains("수량", StringComparison.Ordinal));

    /// <summary>응답 유실 후 같은 버전/누계로 재시도하면 서버 멱등 원장을 재생하도록 만든다.</summary>
    internal static string StableIdempotencyKey(
        string channel,
        string action,
        string workScopeId,
        int expectedVersion,
        decimal? goodQty,
        decimal? defectQty,
        string? carrierId = null,
        string? resultCode = null,
        string? resultMetadataJson = null)
    {
        var canonical = string.Join('|',
            channel.Trim().ToUpperInvariant(),
            action.Trim().ToLowerInvariant(),
            workScopeId.Trim().ToUpperInvariant(),
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            goodQty?.ToString("G29", CultureInfo.InvariantCulture) ?? "-",
            defectQty?.ToString("G29", CultureInfo.InvariantCulture) ?? "-",
            Canonical(carrierId),
            Canonical(resultCode),
            Canonical(resultMetadataJson));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"meta:{channel.Trim().ToLowerInvariant()}:{action.Trim().ToLowerInvariant()}:{digest}";
    }

    /// <summary>
    /// 생성 재시도에서 동일 요청만 서버 멱등 원장으로 재생되도록 입력의 업무 식별 필드를
    /// 정규화해 키를 만든다. 화면이 매번 새 GUID를 만들지 않아 응답 유실에도 안전하다.
    /// </summary>
    internal static string StableCreateIdempotencyKey(IReadOnlyDictionary<string, object?> parameters)
    {
        _ = TryDecimal(parameters, out var planQty, "PLAN_QTY", "planQty");
        var canonical = string.Join('|',
            Canonical(Value(parameters, "WORK_SCOPE_ID", "workScopeId")),
            Canonical(Value(parameters, "PLANT_ID", "plantId")),
            Canonical(Value(parameters, "SCOPE_TYPE", "scopeType")),
            Canonical(Value(parameters, "TARGET_ID", "targetId")),
            Canonical(Value(parameters, "NAME", "name")),
            Canonical(Value(parameters, "PARENT_SCOPE_ID", "parentScopeId")),
            Canonical(Value(parameters, "EQUIPMENT_ID", "equipmentId")),
            Canonical(Value(parameters, "PRODUCT_ID", "productId")),
            Canonical(Value(parameters, "PROCESS_ID", "processId")),
            Canonical(Value(parameters, "RECIPE_ID", "recipeId")),
            TryInt(parameters, out var recipeVersion, "RECIPE_VERSION", "recipeVersion")
                ? recipeVersion.ToString(CultureInfo.InvariantCulture)
                : "-",
            planQty.ToString("G29", CultureInfo.InvariantCulture),
            Canonical(Value(parameters, "OWNER_ID", "ownerId")),
            Canonical(Value(parameters, "DESCRIPTION", "description")),
            Canonical(Value(parameters, "CARRIER_ID", "carrierId")),
            Canonical(Value(parameters, "WORK_ORDER_ID", "workOrderId")));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"meta:create:{digest}";
    }

    private static string Canonical(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

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

    /// <summary>DB UPPER_SNAKE와 폼 camelCase를 같은 키로 읽는다.</summary>
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
