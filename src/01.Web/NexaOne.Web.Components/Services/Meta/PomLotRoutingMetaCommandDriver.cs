using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

/// <summary>Designer와 MES/Mobile/POP가 공유하는 LOT 실행 및 라우팅 예외 명령 ID입니다.</summary>
public static class PomLotRoutingMetaCommands
{
    public const string TrackIn = "bridge:pom.lot.track-in";
    public const string TrackOut = "bridge:pom.lot.track-out";
    public const string Evaluate = "bridge:pom.route.evaluate";
    public const string ChangeControlMode = "bridge:pom.route.control-mode.change";
    public const string ApplyDeviation = "bridge:pom.route.deviation.apply";
    public const string RequestException = "bridge:pom.route.exception.request";
    public const string ApproveException = "bridge:pom.route.exception.approve";
    public const string RejectException = "bridge:pom.route.exception.reject";

    public static IReadOnlyList<string> All { get; } =
    [
        TrackIn, TrackOut, Evaluate, ChangeControlMode, ApplyDeviation,
        RequestException, ApproveException, RejectException,
    ];
}

/// <summary>
/// 선택/스캔된 LOT의 실행 명령을 typed POM API로 연결합니다. 이 드라이버의 availability는 빠른 UX 안내이며,
/// 라우팅 통제·권한·동시성·승인 사용 여부의 최종 판정은 서버가 수행합니다.
/// </summary>
public sealed class PomLotRoutingMetaCommandDriver : IMetaCommandDriver
{
    private const string ExecutePermission = "pom:execute";
    private const string RequestPermission = "pom:routing.request";
    private const string ApprovePermission = "pom:routing.approve";
    private const string ManagePermission = "pom:manage";

    private static readonly IReadOnlyDictionary<string, string> PermissionByCommand =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PomLotRoutingMetaCommands.TrackIn] = ExecutePermission,
            [PomLotRoutingMetaCommands.TrackOut] = ExecutePermission,
            [PomLotRoutingMetaCommands.Evaluate] = RequestPermission,
            [PomLotRoutingMetaCommands.ChangeControlMode] = ManagePermission,
            [PomLotRoutingMetaCommands.ApplyDeviation] = RequestPermission,
            [PomLotRoutingMetaCommands.RequestException] = RequestPermission,
            [PomLotRoutingMetaCommands.ApproveException] = ApprovePermission,
            [PomLotRoutingMetaCommands.RejectException] = ApprovePermission,
        };

    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        PomLotRoutingMetaCommands.All
            .Select(commandId => new MetaCommandDescriptor(
                commandId,
                PermissionByCommand[commandId],
                MetaCommandExecutionMode.PerRow,
                commandId.Equals(PomLotRoutingMetaCommands.Evaluate, StringComparison.OrdinalIgnoreCase)
                    ? MetaCommandEffect.NonMutating
                    : MetaCommandEffect.Mutating))
            .ToArray();

    private readonly IApiClient _api;

    public PomLotRoutingMetaCommandDriver(IApiClient api) => _api = api;

    public IReadOnlyCollection<string> CommandIds => PomLotRoutingMetaCommands.All;
    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    public string? GetRequiredPermission(string commandId)
        => PermissionByCommand.TryGetValue(commandId, out var permission) ? permission : null;

    /// <summary>필수 스캔값과 현재 행 상태를 검사해 버튼 옆에 즉시 이해할 수 있는 차단 사유를 제공합니다.</summary>
    public MetaCommandAvailability CanExecute(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        if (!PermissionByCommand.ContainsKey(commandId))
            return MetaCommandAvailability.Disabled($"지원하지 않는 LOT 라우팅 명령입니다: {commandId}");
        if (!IsSupportedChannel(context.ClientChannel))
            return MetaCommandAvailability.Disabled("실행 채널은 MES, MOBILE 또는 POP이어야 합니다.");

        if (IsReview(commandId))
        {
            if (string.IsNullOrWhiteSpace(Value(parameters, "EXCEPTION_ID", "exceptionId")))
                return MetaCommandAvailability.Disabled("검토할 라우팅 예외 요청을 선택하세요.");
            var status = Value(parameters, "STATUS", "status");
            if (!string.IsNullOrWhiteSpace(status)
                && !status.Equals("Requested", StringComparison.OrdinalIgnoreCase))
                return MetaCommandAvailability.Disabled("승인 대기(Requested) 상태의 예외만 검토할 수 있습니다.");
            if (commandId.Equals(PomLotRoutingMetaCommands.RejectException, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(Value(parameters, "REVIEW_REASON", "reviewReason", "REASON", "reason")))
                return MetaCommandAvailability.Disabled("반려 사유를 입력하세요.");
            return MetaCommandAvailability.Enabled;
        }

        if (string.IsNullOrWhiteSpace(Value(parameters, "LOT_ID", "lotId")))
            return MetaCommandAvailability.Disabled("LOT ID를 스캔하거나 목록에서 선택하세요.");
        if (string.IsNullOrWhiteSpace(Value(parameters, "PLANT_ID", "plantId")))
            return MetaCommandAvailability.Disabled("LOT의 공장 정보를 확인할 수 없습니다.");
        if (!TryInt(parameters, out var version, "VERSION_NO", "versionNo", "expectedVersion") || version < 1)
            return MetaCommandAvailability.Disabled("유효한 LOT 버전이 필요합니다. 목록을 새로고침하세요.");
        if (Bool(parameters, "IS_HOLD", "isHold"))
            return MetaCommandAvailability.Disabled("보류 중인 LOT은 실행하거나 라우팅을 변경할 수 없습니다.");

        if (commandId.Equals(PomLotRoutingMetaCommands.TrackIn, StringComparison.OrdinalIgnoreCase)
            || commandId.Equals(PomLotRoutingMetaCommands.TrackOut, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Value(parameters, "EQUIPMENT_ID", "equipmentId")))
                return MetaCommandAvailability.Disabled("Track-In/Track-Out 설비를 입력하세요.");
            if (commandId.Equals(PomLotRoutingMetaCommands.TrackOut, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryDecimal(parameters, out var qty, "QTY", "qty") || qty <= 0)
                    return MetaCommandAvailability.Disabled("Track-Out 수량은 0보다 커야 합니다.");
                if (!TryReadDefects(parameters, out var defects, out var defectError))
                    return MetaCommandAvailability.Disabled(defectError!);
                if (defects.Sum(defect => defect.DefectQty) > qty)
                    return MetaCommandAvailability.Disabled("불량 수량 합계는 Track-Out 수량을 초과할 수 없습니다.");
            }

            var processState = Value(parameters, "PROCESS_STATE", "processState");
            if (commandId.Equals(PomLotRoutingMetaCommands.TrackIn, StringComparison.OrdinalIgnoreCase)
                && processState?.Equals("Run", StringComparison.OrdinalIgnoreCase) == true)
                return MetaCommandAvailability.Disabled("이미 Track-In된 LOT입니다.");
            if (commandId.Equals(PomLotRoutingMetaCommands.TrackOut, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(processState)
                && !processState.Equals("Run", StringComparison.OrdinalIgnoreCase))
                return MetaCommandAvailability.Disabled("Track-In되어 실행 중인 LOT만 Track-Out할 수 있습니다.");
            return MetaCommandAvailability.Enabled;
        }

        if (commandId.Equals(PomLotRoutingMetaCommands.ChangeControlMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsControlMode(Value(parameters, "CONTROL_MODE_TARGET", "controlModeTarget", "controlMode")))
                return MetaCommandAvailability.Disabled("통제 모드는 Strict, Flexible 또는 NoControl이어야 합니다.");
            return string.IsNullOrWhiteSpace(Value(
                    parameters, "CONTROL_MODE_REASON", "controlModeReason", "REASON", "reason"))
                ? MetaCommandAvailability.Disabled("통제 모드 변경 사유를 입력하세요.")
                : MetaCommandAvailability.Enabled;
        }

        if (!IsDeviationType(Value(parameters, "DEVIATION_TYPE", "deviationType")))
            return MetaCommandAvailability.Disabled("예외 유형을 선택하세요: Bypass, Alternative, SequenceChange 또는 Rework.");
        if (!TryInt(parameters, out var targetStep, "TARGET_STEP_INDEX", "targetStepIndex") || targetStep < 0)
            return MetaCommandAvailability.Disabled("목표 공정 인덱스는 0 이상의 숫자여야 합니다.");

        return commandId.Equals(PomLotRoutingMetaCommands.Evaluate, StringComparison.OrdinalIgnoreCase)
            ? MetaCommandAvailability.Enabled
            : RequireReason(parameters, "라우팅 예외 사유를 입력하세요.");
    }

    /// <summary>서버 판정 결과에 따라 정상 실행, 즉시 편차 적용 또는 Flexible 승인 요청으로 분기합니다.</summary>
    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context,
        CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(availability.DisabledReason ?? "LOT 라우팅 명령을 실행할 수 없습니다.", 409);

        var channel = context.ClientChannel.Trim().ToUpperInvariant();
        var deviceId = Trim(context.DeviceId);
        if (IsReview(commandId))
            return await ReviewExceptionAsync(commandId, parameters, channel, deviceId, ct);

        var lotId = Value(parameters, "LOT_ID", "lotId")!;
        var plantId = Value(parameters, "PLANT_ID", "plantId")!;
        _ = TryInt(parameters, out var version, "VERSION_NO", "versionNo", "expectedVersion");

        if (commandId.Equals(PomLotRoutingMetaCommands.TrackIn, StringComparison.OrdinalIgnoreCase)
            || commandId.Equals(PomLotRoutingMetaCommands.TrackOut, StringComparison.OrdinalIgnoreCase))
            return await ExecuteTrackingAsync(commandId, parameters, lotId, plantId, version, channel, deviceId, ct);

        if (commandId.Equals(PomLotRoutingMetaCommands.ChangeControlMode, StringComparison.OrdinalIgnoreCase))
        {
            var mode = Value(parameters, "CONTROL_MODE_TARGET", "controlModeTarget", "controlMode")!;
            var reason = Value(parameters, "CONTROL_MODE_REASON", "controlModeReason", "REASON", "reason")!;
            var key = IdempotencyKey(parameters, channel, "control-mode", lotId, version, mode);
            var result = await _api.ChangePomLotRoutingControlModeAsync(
                lotId,
                new PomChangeRoutingControlModeRequest(
                    plantId, mode, reason, version, key, channel, deviceId),
                ct);
            return ToMetaResult(result, "라우팅 통제 모드 변경에 실패했습니다.");
        }

        var deviationType = Value(parameters, "DEVIATION_TYPE", "deviationType")!;
        _ = TryInt(parameters, out var targetStepIndex, "TARGET_STEP_INDEX", "targetStepIndex");
        var reasonText = Value(parameters, "REASON", "reason");
        // 새 편차 제출(RequestException)은 과거 승인 토큰을 재사용하지 않는다. 승인된 토큰은
        // 명시적인 ApplyDeviation 명령에서만 전달해 NoControl 즉시 적용과 Flexible permit 소비를 분리한다.
        var selectedExceptionId = Value(parameters, "EXCEPTION_ID", "exceptionId");
        var exceptionId = commandId.Equals(PomLotRoutingMetaCommands.ApplyDeviation, StringComparison.OrdinalIgnoreCase)
            ? selectedExceptionId
            : null;
        var decision = await _api.EvaluatePomLotRoutingAsync(
            lotId,
            new PomEvaluateRoutingRequest(
                plantId, deviationType, targetStepIndex, reasonText, exceptionId),
            ct);
        if (!decision.Success)
            return MetaCommandResult.Failed(decision.Error ?? "라우팅 정책 판정에 실패했습니다.", decision.StatusCode);
        if (commandId.Equals(PomLotRoutingMetaCommands.Evaluate, StringComparison.OrdinalIgnoreCase))
            return decision.Value!.IsAllowed
                ? MetaCommandResult.Succeeded(
                    decision.StatusCode,
                    decision.Value.Message,
                    decision.Value.Kind.Equals("AllowWithWarning", StringComparison.OrdinalIgnoreCase))
                : MetaCommandResult.Failed(decision.Value.Message, 409);

        if (decision.Value!.Kind.Equals("Block", StringComparison.OrdinalIgnoreCase))
            return MetaCommandResult.Failed(decision.Value.Message, 409);

        var applyNow = commandId.Equals(PomLotRoutingMetaCommands.ApplyDeviation, StringComparison.OrdinalIgnoreCase)
            || decision.Value.IsAllowed;
        if (applyNow)
        {
            var key = IdempotencyKey(
                parameters, channel, $"route-{deviationType}-{targetStepIndex}", lotId, version, reasonText);
            var applied = await _api.ApplyPomLotRouteDeviationAsync(
                lotId,
                new PomApplyRouteDeviationRequest(
                    plantId, deviationType, targetStepIndex, reasonText!, version,
                    key, exceptionId ?? decision.Value.ExceptionId, channel, deviceId),
                ct);
            return ToMetaResult(
                applied,
                decision.Value.Message,
                successMessage: decision.Value.Message,
                isWarning: decision.Value.Kind.Equals("AllowWithWarning", StringComparison.OrdinalIgnoreCase));
        }

        if (!decision.Value.Kind.Equals("ApprovalRequired", StringComparison.OrdinalIgnoreCase))
            return MetaCommandResult.Failed(decision.Value.Message, 409);

        var requestKey = IdempotencyKey(
            parameters, channel, $"request-{deviationType}-{targetStepIndex}", lotId, version, reasonText);
        var requestId = exceptionId ?? ExceptionIdFrom(requestKey);
        var requested = await _api.RequestPomLotRouteExceptionAsync(
            lotId,
            new PomRequestRouteExceptionRequest(
                plantId, deviationType, targetStepIndex, reasonText!, version,
                DateTime.UtcNow.AddMinutes(30), requestId, channel, deviceId),
            ct);
        return ToMetaResult(requested, "라우팅 예외 승인 요청에 실패했습니다.");
    }

    private async Task<MetaCommandResult> ExecuteTrackingAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        string lotId,
        string plantId,
        int version,
        string channel,
        string? deviceId,
        CancellationToken ct)
    {
        var equipmentId = Value(parameters, "EQUIPMENT_ID", "equipmentId")!;
        if (commandId.Equals(PomLotRoutingMetaCommands.TrackIn, StringComparison.OrdinalIgnoreCase))
        {
            _ = TryInt(parameters, out var currentStep, "CURRENT_STEP", "currentStep", "CURRENT_STEP_INDEX", "currentStepIndex");
            var decision = await _api.EvaluatePomLotRoutingAsync(
                lotId,
                new PomEvaluateRoutingRequest(plantId, "Normal", currentStep),
                ct);
            if (!decision.Success)
                return MetaCommandResult.Failed(decision.Error ?? "현재 공정 실행 가능 여부를 확인하지 못했습니다.", decision.StatusCode);
            if (!decision.Value!.IsAllowed)
                return MetaCommandResult.Failed(decision.Value.Message, 409);

            var key = IdempotencyKey(parameters, channel, "track-in", lotId, version, equipmentId);
            var result = await _api.ExecutePomLotTrackInAsync(
                lotId,
                new PomLotTrackInRequest(
                    plantId, equipmentId, version, key,
                    Value(parameters, "RECIPE_DEF_ID", "recipeDefId"),
                    NullableInt(parameters, "RECIPE_DEF_VERSION", "recipeDefVersion"),
                    channel, deviceId),
                ct);
            return ToMetaResult(result, "LOT Track-In에 실패했습니다.");
        }

        _ = TryDecimal(parameters, out var qty, "QTY", "qty");
        _ = TryReadDefects(parameters, out var defects, out _);
        var defectKeyPart = string.Join(",", defects
            .OrderBy(defect => defect.DefectCode, StringComparer.OrdinalIgnoreCase)
            .Select(defect => $"{defect.DefectCode}:{defect.DefectQty.ToString(CultureInfo.InvariantCulture)}"));
        var trackOutKey = IdempotencyKey(
            parameters, channel, "track-out", lotId, version,
            $"{qty.ToString(CultureInfo.InvariantCulture)}|{defectKeyPart}");
        var trackOut = await _api.ExecutePomLotTrackOutAsync(
            lotId,
            new PomLotTrackOutRequest(
                plantId, equipmentId, qty, version, trackOutKey,
                Value(parameters, "CARRIER_ID", "carrierId"), defects, channel, deviceId),
            ct);
        return ToMetaResult(trackOut, "LOT Track-Out에 실패했습니다.");
    }

    private async Task<MetaCommandResult> ReviewExceptionAsync(
        string commandId,
        IReadOnlyDictionary<string, object?> parameters,
        string channel,
        string? deviceId,
        CancellationToken ct)
    {
        var action = commandId.Equals(PomLotRoutingMetaCommands.ApproveException, StringComparison.OrdinalIgnoreCase)
            ? "approve"
            : "reject";
        var result = await _api.ReviewPomLotRouteExceptionAsync(
            action,
            Value(parameters, "EXCEPTION_ID", "exceptionId")!,
            new PomReviewRouteExceptionRequest(
                Value(parameters, "REVIEW_REASON", "reviewReason", "REASON", "reason"),
                channel,
                deviceId),
            ct);
        return ToMetaResult(result, $"라우팅 예외 {action} 처리에 실패했습니다.");
    }

    private static MetaCommandResult ToMetaResult<T>(
        PomRoutingApiResult<T> result,
        string fallback,
        string? successMessage = null,
        bool isWarning = false) where T : class
        => result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode, successMessage, isWarning)
            : MetaCommandResult.Failed(result.Error ?? fallback, result.StatusCode);

    private static MetaCommandAvailability RequireReason(
        IReadOnlyDictionary<string, object?> parameters,
        string message)
        => string.IsNullOrWhiteSpace(Value(parameters, "REASON", "reason"))
            ? MetaCommandAvailability.Disabled(message)
            : MetaCommandAvailability.Enabled;

    private static bool IsReview(string commandId)
        => commandId.Equals(PomLotRoutingMetaCommands.ApproveException, StringComparison.OrdinalIgnoreCase)
           || commandId.Equals(PomLotRoutingMetaCommands.RejectException, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedChannel(string? value)
        => value?.Trim().ToUpperInvariant() is "MES" or "MOBILE" or "POP";

    private static bool IsDeviationType(string? value)
        => value?.Trim().ToUpperInvariant() is "BYPASS" or "ALTERNATIVE" or "SEQUENCECHANGE" or "REWORK";

    private static bool IsControlMode(string? value)
        => value?.Trim().ToUpperInvariant() is "STRICT" or "FLEXIBLE" or "NOCONTROL";

    private static string IdempotencyKey(
        IReadOnlyDictionary<string, object?> parameters,
        string channel,
        string action,
        string lotId,
        int version,
        object? detail)
    {
        var supplied = Value(parameters, "IDEMPOTENCY_KEY", "idempotencyKey");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (supplied.Length <= 100) return supplied;
            var suppliedDigest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied))).ToLowerInvariant();
            return $"meta:provided:{suppliedDigest[..48]}";
        }

        var normalizedChannel = channel.Trim().ToUpperInvariant();
        var normalizedAction = action.Trim().ToLowerInvariant();
        var canonical = string.Join('|',
            normalizedChannel, normalizedAction,
            lotId.Trim().ToUpperInvariant(), version.ToString(CultureInfo.InvariantCulture),
            detail?.ToString()?.Trim() ?? "-");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var actionDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAction))).ToLowerInvariant();
        // POM execution persistence uses NVARCHAR(100). Fixed-size tokens keep every generated key below it,
        // including MOBILE + SequenceChange/target combinations, without weakening retry stability.
        return $"meta:{normalizedChannel.ToLowerInvariant()}:{actionDigest[..12]}:{digest[..48]}";
    }

    private static string ExceptionIdFrom(string idempotencyKey)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"REX-{digest[..32]}";
    }

    private static int? NullableInt(IReadOnlyDictionary<string, object?> values, params string[] keys)
        => TryInt(values, out var value, keys) ? value : null;

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

    private static bool TryReadDefects(
        IReadOnlyDictionary<string, object?> values,
        out IReadOnlyList<PomLotDefectInput> defects,
        out string? error)
    {
        defects = Array.Empty<PomLotDefectInput>();
        error = null;
        var raw = RawValue(values, "DEFECTS", "defects");
        if (raw is null) return true;
        if (raw is not IEnumerable<Dictionary<string, object?>> rows)
        {
            error = "불량 내역 형식이 올바르지 않습니다.";
            return false;
        }

        var parsed = new List<PomLotDefectInput>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = Value(row, "DEFECT_CODE", "defectCode");
            if (string.IsNullOrWhiteSpace(code))
            {
                error = "불량 코드를 입력하세요.";
                return false;
            }
            if (!TryDecimal(row, out var qty, "DEFECT_QTY", "defectQty") || qty <= 0)
            {
                error = $"불량 코드 {code}의 수량은 0보다 커야 합니다.";
                return false;
            }
            if (!codes.Add(code))
            {
                error = $"불량 코드가 중복되었습니다: {code}";
                return false;
            }
            parsed.Add(new PomLotDefectInput(code, qty));
        }

        defects = parsed;
        return true;
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        var value = Value(values, keys);
        return value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("y", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>DB UPPER_SNAKE와 Designer camelCase 키를 동일하게 읽습니다.</summary>
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

    private static object? RawValue(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var exact)) return exact;
            var normalized = Normalize(key);
            foreach (var pair in values)
                if (Normalize(pair.Key) == normalized) return pair.Value;
        }
        return null;
    }

    private static string Normalize(string value)
        => value.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
