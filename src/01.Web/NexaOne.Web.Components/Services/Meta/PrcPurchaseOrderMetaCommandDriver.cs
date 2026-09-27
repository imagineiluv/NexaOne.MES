using System.Globalization;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

public static class PrcPurchaseOrderMetaCommands
{
    public const string Save = "bridge:prc.purchase-order.save";
    public const string Delete = "bridge:prc.purchase-order.delete";
    public const string Order = "bridge:prc.purchase-order.order";
    public const string Close = "bridge:prc.purchase-order.close";

    public static IReadOnlyList<string> All { get; } = [Save, Delete, Order, Close];
}

/// <summary>Purchase-order forms and selected rows call PRC-owned commands, not host SQL.</summary>
public sealed class PrcPurchaseOrderMetaCommandDriver(IApiClient api) : IMetaCommandDriver
{
    private static readonly IReadOnlyCollection<MetaCommandDescriptor> Descriptors =
        PrcPurchaseOrderMetaCommands.All
            .Select(id => new MetaCommandDescriptor(id, "prc:manage"))
            .ToArray();

    public IReadOnlyCollection<string> CommandIds => PrcPurchaseOrderMetaCommands.All;
    public IReadOnlyCollection<MetaCommandDescriptor> Commands => Descriptors;

    public string? GetRequiredPermission(string commandId) =>
        PrcPurchaseOrderMetaCommands.All.Contains(commandId, StringComparer.OrdinalIgnoreCase)
            ? "prc:manage" : null;

    public MetaCommandAvailability CanExecute(
        string commandId, IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context)
    {
        var canonical = PrcPurchaseOrderMetaCommands.All.FirstOrDefault(
            id => id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return MetaCommandAvailability.Disabled("지원하지 않는 발주 명령입니다.");
        if (string.IsNullOrWhiteSpace(Value(parameters, "purchaseOrderId", "PURCHASE_ORDER_ID")))
            return MetaCommandAvailability.Disabled("발주 ID가 필요합니다.");

        var status = Value(parameters, "status", "STATUS");
        var held = Value(parameters, "isHold", "IS_HOLD") is "Y" or "y" or "true" or "True";
        var expectedStatus = canonical == PrcPurchaseOrderMetaCommands.Close ? "Incoming" : "Draft";
        if (held || status is not null && status != expectedStatus)
            return MetaCommandAvailability.Disabled("현재 상태 또는 보류 상태에서는 실행할 수 없습니다.");
        if (canonical == PrcPurchaseOrderMetaCommands.Save)
        {
            if (string.IsNullOrWhiteSpace(Value(parameters, "plantId", "PLANT_ID")))
                return MetaCommandAvailability.Disabled("공장을 입력하세요.");
            if (!TryDecimal(Value(parameters, "orderQty", "ORDER_QTY"), out var quantity)
                || quantity < 0)
                return MetaCommandAvailability.Disabled("발주 수량을 확인하세요.");
        }
        return MetaCommandAvailability.Enabled;
    }

    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId, IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context, CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(availability.DisabledReason ?? "발주 명령을 실행할 수 없습니다.", 400);

        PrcPurchaseOrderActionResult result;
        if (commandId.Equals(PrcPurchaseOrderMetaCommands.Save, StringComparison.OrdinalIgnoreCase))
        {
            _ = TryDecimal(Value(parameters, "orderQty", "ORDER_QTY"), out var quantity);
            result = await api.SavePrcPurchaseOrderDraftAsync(new PrcPurchaseOrderDraftRequest(
                Value(parameters, "purchaseOrderId", "PURCHASE_ORDER_ID")!.Trim(),
                Value(parameters, "plantId", "PLANT_ID")!.Trim(),
                Value(parameters, "purchaseOrderName", "PURCHASE_ORDER_NAME"),
                Value(parameters, "vendorId", "VENDOR_ID"), quantity), ct);
        }
        else
        {
            var action = commandId.Equals(PrcPurchaseOrderMetaCommands.Delete, StringComparison.OrdinalIgnoreCase)
                ? "delete"
                : commandId.Equals(PrcPurchaseOrderMetaCommands.Order, StringComparison.OrdinalIgnoreCase)
                    ? "order" : "close";
            result = await api.ExecutePrcPurchaseOrderActionAsync(
                action, Value(parameters, "purchaseOrderId", "PURCHASE_ORDER_ID")!.Trim(), ct);
        }
        return result.Success
            ? MetaCommandResult.Succeeded(result.StatusCode)
            : MetaCommandResult.Failed(result.Error ?? "발주 명령에 실패했습니다.", result.StatusCode);
    }

    private static string? Value(IReadOnlyDictionary<string, object?> parameters, params string[] keys)
    {
        foreach (var key in keys)
            if (parameters.TryGetValue(key, out var value) && value is not null)
                return value.ToString();
        return null;
    }

    private static bool TryDecimal(string? value, out decimal parsed) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.CurrentCulture, out parsed)
        || decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out parsed);
}
