using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services.Meta;

public static class PrcPurchaseOrderMetaCommands
{
    public const string Order = "bridge:prc.purchase-order.order";
    public const string Close = "bridge:prc.purchase-order.close";

    public static IReadOnlyList<string> All { get; } = [Order, Close];
}

/// <summary>Selected purchase-order rows call PRC-owned status commands, not host SQL.</summary>
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
        var isOrder = commandId.Equals(PrcPurchaseOrderMetaCommands.Order, StringComparison.OrdinalIgnoreCase);
        var isClose = commandId.Equals(PrcPurchaseOrderMetaCommands.Close, StringComparison.OrdinalIgnoreCase);
        if (!isOrder && !isClose)
            return MetaCommandAvailability.Disabled("지원하지 않는 발주 명령입니다.");
        if (string.IsNullOrWhiteSpace(Value(parameters, "purchaseOrderId", "PURCHASE_ORDER_ID")))
            return MetaCommandAvailability.Disabled("발주 ID가 필요합니다.");

        var status = Value(parameters, "status", "STATUS");
        var held = Value(parameters, "isHold", "IS_HOLD") is "Y" or "y" or "true" or "True";
        if (held || status is not null && status != (isOrder ? "Draft" : "Incoming"))
            return MetaCommandAvailability.Disabled("현재 상태 또는 보류 상태에서는 실행할 수 없습니다.");
        return MetaCommandAvailability.Enabled;
    }

    public async Task<MetaCommandResult> ExecuteAsync(
        string commandId, IReadOnlyDictionary<string, object?> parameters,
        MetaCommandExecutionContext context, CancellationToken ct = default)
    {
        var availability = CanExecute(commandId, parameters, context);
        if (!availability.CanExecute)
            return MetaCommandResult.Failed(availability.DisabledReason ?? "발주 명령을 실행할 수 없습니다.", 400);

        var action = commandId.Equals(PrcPurchaseOrderMetaCommands.Order, StringComparison.OrdinalIgnoreCase)
            ? "order" : "close";
        var result = await api.ExecutePrcPurchaseOrderActionAsync(
            action, Value(parameters, "purchaseOrderId", "PURCHASE_ORDER_ID")!.Trim(), ct);
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
}
