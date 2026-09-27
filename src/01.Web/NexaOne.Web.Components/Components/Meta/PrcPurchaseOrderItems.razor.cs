using System.Globalization;
using Microsoft.AspNetCore.Components;
using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Components.Meta;

public partial class PrcPurchaseOrderItems : IDisposable
{
    private const decimal MaximumQuantity = 99999999999999.9999m;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _loadedOrderId;
    private List<PrcPurchaseOrderItemDto>? _items;
    private string _productId = string.Empty;
    private string _quantityText = string.Empty;
    private string? _deleteProductId;
    private string? _fieldError;
    private string? _loadError;
    private string? _actionError;
    private bool _editingExisting;
    private bool _loading;
    private bool _busy;
    private bool _disposed;

    [Inject] public IApiClient Api { get; set; } = default!;
    [Parameter] public string? PurchaseOrderId { get; set; }
    [Parameter] public string? OrderStatus { get; set; }
    [Parameter] public bool IsHeld { get; set; }
    [Parameter] public bool CanManage { get; set; }
    [Parameter] public string? UserId { get; set; }
    [Parameter] public EventCallback<decimal> OnChanged { get; set; }

    private bool CanEdit => CanManage && OrderStatus == "Draft" && !IsHeld
        && !string.IsNullOrWhiteSpace(UserId);

    private string ReadOnlyReason => !CanManage
        ? "품목은 읽기 전용입니다. 편집하려면 구매 관리 권한이 필요합니다."
        : OrderStatus != "Draft"
            ? "Draft 발주에서만 품목을 편집할 수 있습니다."
            : IsHeld
                ? "보류된 발주의 품목은 편집할 수 없습니다."
                : "사용자 신원을 확인할 수 없어 품목을 편집할 수 없습니다.";

    private string Path => $"api/v1/prc/purchase-orders/{Uri.EscapeDataString(PurchaseOrderId!)}/items";

    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(_loadedOrderId, PurchaseOrderId, StringComparison.Ordinal)) return;
        _loadedOrderId = PurchaseOrderId;
        _items = null;
        _loadError = null;
        _actionError = null;
        CancelEdit();
        CancelDelete();
        if (!string.IsNullOrWhiteSpace(PurchaseOrderId)) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(PurchaseOrderId)) return;
        _loading = true;
        _loadError = null;
        try
        {
            var result = await Api.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                Path, _lifetime.Token);
            if (_disposed) return;
            if (result.Value is null)
            {
                _items = null;
                _loadError = $"품목을 조회하지 못했습니다. {result.Error ?? $"HTTP {result.StatusCode}"}";
            }
            else
            {
                _items = result.Value;
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _loading = false; }
    }

    private void BeginEdit(PrcPurchaseOrderItemDto item)
    {
        if (!CanEdit || item.IncomingQuantity != 0 || _busy) return;
        _productId = item.ProductId;
        _quantityText = item.OrderQuantity.ToString("0.####", CultureInfo.InvariantCulture);
        _editingExisting = true;
        _fieldError = null;
        CancelDelete();
    }

    private void CancelEdit()
    {
        _productId = string.Empty;
        _quantityText = string.Empty;
        _editingExisting = false;
        _fieldError = null;
    }

    private void RequestDelete(string productId)
    {
        if (!CanEdit || _busy) return;
        _deleteProductId = productId;
        CancelEdit();
    }

    private void CancelDelete() => _deleteProductId = null;

    private async Task SaveAsync()
    {
        if (!CanEdit || _busy || _loading) return;
        var productId = _productId.Trim();
        if (productId.Length is 0 or > 50 || productId.Any(char.IsControl)
            || !decimal.TryParse(_quantityText, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0 || quantity > MaximumQuantity || decimal.Round(quantity, 4) != quantity)
        {
            _fieldError = "품목 ID와 0보다 큰 발주수량(소수점 4자리 이하)을 확인하세요.";
            return;
        }

        _busy = true;
        _fieldError = null;
        _actionError = null;
        try
        {
            var result = await Api.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Put, $"{Path}/{Uri.EscapeDataString(productId)}",
                new { quantity }, UserId!, _lifetime.Token);
            if (_disposed) return;
            if (result.Value is null)
                _actionError = $"품목 저장 결과를 확인하세요. {result.Error ?? $"HTTP {result.StatusCode}"} 아래 목록을 다시 조회했습니다. 확인 전에는 같은 변경을 반복하지 마세요.";
            else
                CancelEdit();
            await LoadAsync();
            if (result.Value is not null) await OnChanged.InvokeAsync(result.Value.OrderQuantity);
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    private async Task DeleteAsync()
    {
        if (!CanEdit || _busy || _deleteProductId is null) return;
        var productId = _deleteProductId;
        _busy = true;
        _actionError = null;
        try
        {
            var result = await Api.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Delete, $"{Path}/{Uri.EscapeDataString(productId)}",
                new { }, UserId!, _lifetime.Token);
            if (_disposed) return;
            if (result.Value is null)
                _actionError = $"품목 삭제 결과를 확인하세요. {result.Error ?? $"HTTP {result.StatusCode}"} 아래 목록을 다시 조회했습니다. 확인 전에는 같은 변경을 반복하지 마세요.";
            else
                CancelDelete();
            await LoadAsync();
            if (result.Value is not null) await OnChanged.InvokeAsync(result.Value.OrderQuantity);
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record PrcPurchaseOrderItemDto(string ProductId, decimal OrderQuantity, decimal IncomingQuantity);
public sealed record PrcPurchaseOrderItemTotalDto(string PurchaseOrderId, decimal OrderQuantity);
