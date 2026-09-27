using System.Globalization;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

public static class SlsSalesOrderMetaCommands
{
    public const string Save = "bridge:sls.sales-order.save";
    public const string Delete = "bridge:sls.sales-order.delete";
    public const string Confirm = "bridge:sls.sales-order.confirm";
    public const string Close = "bridge:sls.sales-order.close";

    public static IReadOnlyList<string> All { get; } = [Save, Delete, Confirm, Close];
}

/// <summary>수주 메타 화면의 모델과 선택 행을 SLS 소유 REST 명령으로 변환합니다.</summary>
public sealed class SlsSalesOrderMetaCommandDriver(IApiClient api) : IMetaCommandDriver
{
    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        SlsSalesOrderMetaCommands.All
            .Select(id => new MetaCommandDescriptor(id, "sls:manage"))
            .ToArray();

    public IReadOnlyCollection<string> CommandIds => SlsSalesOrderMetaCommands.All;
    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    public string? GetRequiredPermission(string commandId)
        => SlsSalesOrderMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase)
            ? "sls:manage" : null;

    public MetaCommandAvailability CanExecute(
        string commandId, IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        var canonical = SlsSalesOrderMetaCommands.All.FirstOrDefault(
            id => id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return MetaCommandAvailability.Disabled("지원하지 않는 수주 명령입니다.");
        var id = Value(parameters, "salesOrderId", "SALES_ORDER_ID");
        if (string.IsNullOrWhiteSpace(id))
            return MetaCommandAvailability.Disabled("수주 번호가 필요합니다.");

        var status = Value(parameters, "status", "STATUS");
        var held = Value(parameters, "isHold", "IS_HOLD") is "Y" or "y" or "true" or "True";
        if (status is not null || held)
        {
            var allowed = canonical switch
            {
                SlsSalesOrderMetaCommands.Save or SlsSalesOrderMetaCommands.Delete
                    => status is null or "Draft" && !held,
                SlsSalesOrderMetaCommands.Confirm => status is null or "Draft" && !held,
                SlsSalesOrderMetaCommands.Close
                    => status is null or "Producing" or "Delivered" && !held,
                _ => false,
            };
            if (!allowed)
                return MetaCommandAvailability.Disabled("현재 상태 또는 보류 상태에서는 실행할 수 없습니다.");
        }

        if (canonical != SlsSalesOrderMetaCommands.Save)
            return MetaCommandAvailability.Enabled;
        foreach (var key in new[] { "plantId", "customerId", "productId" })
            if (string.IsNullOrWhiteSpace(Value(parameters, key, ToUpperSnake(key))))
                return MetaCommandAvailability.Disabled("공장·고객·품목을 입력하세요.");
        if (!TryDecimal(Value(parameters, "planQty", "PLAN_QTY"), out var qty) || qty <= 0)
            return MetaCommandAvailability.Disabled("계획 수량은 0보다 커야 합니다.");
        if (!TryDate(Value(parameters, "planEndDate", "PLAN_END_DATE"), out var due))
            return MetaCommandAvailability.Disabled("납기 예정일을 입력하세요.");
        var startValue = Value(parameters, "planStartDate", "PLAN_START_DATE");
        if (!string.IsNullOrWhiteSpace(startValue)
            && (!TryDate(startValue, out var start) || start > due))
            return MetaCommandAvailability.Disabled("계획 기간을 확인하세요.");
        return MetaCommandAvailability.Enabled;
    }

    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId, IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context, CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(availability.DisabledReason ?? "수주 명령을 실행할 수 없습니다.", 400);

        SlsSalesOrderActionResult result;
        if (commandId.Equals(SlsSalesOrderMetaCommands.Save, StringComparison.OrdinalIgnoreCase))
        {
            _ = TryDecimal(Value(parameters, "planQty", "PLAN_QTY"), out var qty);
            _ = TryDate(Value(parameters, "planEndDate", "PLAN_END_DATE"), out var due);
            var startValue = Value(parameters, "planStartDate", "PLAN_START_DATE");
            DateTime? start = TryDate(startValue, out var parsedStart) ? parsedStart : null;
            result = await api.SaveSlsSalesOrderAsync(new SlsSalesOrderDraftRequest(
                Value(parameters, "salesOrderId", "SALES_ORDER_ID")!.Trim(),
                Value(parameters, "salesOrderName", "SALES_ORDER_NAME"),
                Value(parameters, "plantId", "PLANT_ID")!.Trim(),
                Value(parameters, "customerId", "CUSTOMER_ID")!.Trim(),
                Value(parameters, "productId", "PRODUCT_ID")!.Trim(),
                start, due, qty), ct);
        }
        else
        {
            var action = commandId.Equals(SlsSalesOrderMetaCommands.Delete, StringComparison.OrdinalIgnoreCase)
                ? "delete"
                : commandId.Equals(SlsSalesOrderMetaCommands.Confirm, StringComparison.OrdinalIgnoreCase)
                    ? "confirm" : "close";
            result = await api.ExecuteSlsSalesOrderActionAsync(
                action, Value(parameters, "salesOrderId", "SALES_ORDER_ID")!.Trim(), ct);
        }
        return result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode)
            : MetaCommandResult.Failed(result.Error ?? "수주 명령에 실패했습니다.", result.StatusCode);
    }

    private static string? Value(IReadOnlyDictionary<string, object?> parameters, params string[] keys)
    {
        foreach (var key in keys)
            if (parameters.TryGetValue(key, out var value) && value is not null)
                return value.ToString();
        return null;
    }

    private static bool TryDecimal(string? value, out decimal parsed)
        => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
               CultureInfo.CurrentCulture, out parsed)
           || decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
               CultureInfo.InvariantCulture, out parsed);

    private static bool TryDate(string? value, out DateTime parsed)
        => DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed)
           || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);

    private static string ToUpperSnake(string camel)
        => string.Concat(camel.Select(ch =>
            char.IsUpper(ch) ? $"_{ch}" : ch.ToString())).ToUpperInvariant();
}
