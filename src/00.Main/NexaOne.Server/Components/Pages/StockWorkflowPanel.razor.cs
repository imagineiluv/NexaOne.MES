using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;

namespace NexaOne.Server.Components.Pages;

public partial class StockWorkflowPanel : IDisposable
{
    [Inject] public IApiClient Api { get; set; } = null!;
    [Inject] public UiTextService Ui { get; set; } = null!;
    [Inject] public ProtectedSessionStorage Storage { get; set; } = null!;
    [Parameter, EditorRequired] public InventoryAccessScope Scope { get; set; } = null!;
    [Parameter] public string? UserId { get; set; }
    [Parameter] public IReadOnlyList<Warehouse>? Warehouses { get; set; }
    [Parameter] public EventCallback Saved { get; set; }
    [Parameter] public EventCallback<ProductVariant?> VariantChanged { get; set; }

    // A browser recovery envelope for stock writes, not a second stock business model.
    // It is stored under its own key prefix so an equipment intent in the same tab is never mixed with it.
    // OperationId carries the reservation operation a consumption must be confirmed against after a reload.
    public sealed record PendingWrite(int Schema, string UserId, Guid BusinessUserId, Guid TenantId,
        Guid OrganizationId, string Kind, Guid Id, JsonElement Payload, DateTimeOffset CreatedAt,
        bool Confirmed = false, Guid? ResultId = null, Guid? OperationId = null);

    private enum Editor { None, Posting, Reserve, Warehouse }
    private enum Focus { None, Warehouse, Movement, Reservation }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _storageKey;
    private int _generation;
    private bool _interactive, _loaded, _loading, _busy, _storageError, _acknowledged, _disposed;
    private CancellationTokenSource? _request;
    private PendingWrite? _pending;
    private Product? _product;
    private ProductVariant? _variant;
    private StockMovement? _movement;
    private StockReservation? _reservation;
    private Warehouse? _warehouse;
    private Warehouse? _observedWarehouse;
    private StockMovement? _observedMovement;
    private StockReservation? _observedReservation;
    private Editor _editor;
    private Focus _focus;
    private string _warehouseCode = "", _warehouseName = "";
    private string _kind = nameof(StockMovementKind.Receipt), _from = "", _to = "", _quantity = "", _reference = "", _reversalReference = "";
    private string _reservationWarehouse = "", _reservationQuantity = "", _reservationReference = "", _reservationId = "";
    private string? _error, _message;
    private ElementReference _heading;

    private bool Has(string grant) => Scope?.Membership.Permissions.Contains(grant, StringComparer.Ordinal) == true;
    private bool OwnerValid => Scope is not null && !string.IsNullOrWhiteSpace(UserId)
        && string.Equals(Scope.Membership.UserId, UserId, StringComparison.Ordinal)
        && Scope.Membership.IsActive && Scope.Membership.Version > 0 && Scope.Membership.BusinessUserId != Guid.Empty
        && Scope.Membership.TenantId != Guid.Empty && Scope.Membership.OrganizationId != Guid.Empty;
    private bool CanStart => OwnerValid && _loaded && !_busy && !_loading && !_storageError && _pending is null;
    private bool CanWriteWarehouse => Has("stock.warehouse.write");
    private bool CanPost => Has("stock.post") && Has("stock.read") && Has("stock.warehouse.read") && _variant is { Active: true };
    private bool CanReverse => Has("stock.reverse") && _movement is { ReversedById: null, ReversalOfId: null };
    private bool CanReserve => Has("stock.reserve") && Has("stock.read") && Has("stock.warehouse.read") && _variant is { Active: true };
    private bool CanRelease => Has("stock.release") && _reservation is { State: StockReservationState.Active };
    private bool CanConsume => Has("stock.consume") && _reservation is { State: StockReservationState.Active };
    private string Root => $"api/v1/ivt/stock/{Scope.Membership.TenantId:D}/{Scope.Membership.OrganizationId:D}";
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);

    protected override void OnInitialized() => Ui.Changed += LanguageChanged;

    protected override void OnParametersSet()
    {
        var key = OwnerValid ? StorageKey(UserId!, Scope) : null;
        if (key == _storageKey) return;
        ++_generation;
        _request?.Cancel();
        _storageKey = key;
        _pending = null; _product = null; _variant = null; _movement = null; _reservation = null; _warehouse = null; _editor = Editor.None;
        _observedWarehouse = null; _observedMovement = null; _observedReservation = null; _focus = Focus.None;
        _loaded = false; _loading = false; _busy = false; _storageError = false;
        _error = null; _message = null; _acknowledged = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _interactive = true;
        if (!_loaded && !_loading && !_storageError && _storageKey is not null)
            await LoadRecoveryAsync();
    }

    private static string StorageKey(string userId, InventoryAccessScope scope)
    {
        var owner = JsonSerializer.Serialize(new[] { userId, scope.Membership.BusinessUserId.ToString("D"),
            scope.Membership.TenantId.ToString("D"), scope.Membership.OrganizationId.ToString("D") });
        return "nexaone_stock_write_v1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)));
    }

    private bool Current(int generation, string key) => !_disposed && generation == _generation && key == _storageKey;

    private async Task LoadRecoveryAsync()
    {
        if (!_interactive || _storageKey is not { } key || _busy || _loading) return;
        var generation = _generation;
        _loading = true; _error = null;
        try
        {
            var stored = await Storage.GetAsync<PendingWrite>(key);
            if (!Current(generation, key)) return;
            if (stored.Success && stored.Value is not null)
            {
                if (!ValidIntent(stored.Value)) throw new JsonException("Invalid recovery envelope.");
                _pending = stored.Value;
            }
            else _pending = null;
            _loaded = true; _storageError = false; _acknowledged = false;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            if (Current(generation, key)) { _storageError = true; _error = StorageMessage(); }
        }
        finally
        {
            if (Current(generation, key)) { _loading = false; StateHasChanged(); }
        }
    }

    private bool ValidIntent(PendingWrite value) => value.Schema == 1 && value.UserId == UserId
        && value.BusinessUserId == Scope.Membership.BusinessUserId && value.TenantId == Scope.Membership.TenantId
        && value.OrganizationId == Scope.Membership.OrganizationId && value.Id != Guid.Empty
        && value.CreatedAt != default && value.Payload.ValueKind == JsonValueKind.Object
        && Permission(value.Kind) is not null && (!value.Confirmed || value.ResultId is { } id && id != Guid.Empty)
        && (value.Kind != "consume" || value.OperationId is { } operation && operation != Guid.Empty)
        && RequestBody(value) is not null;

    private static string? Permission(string kind) => kind switch
    {
        "warehouse-create" or "warehouse-update" or "warehouse-active" => "stock.warehouse.write",
        "post" => "stock.post",
        "reverse" => "stock.reverse",
        "reserve" => "stock.reserve", "release" => "stock.release", "consume" => "stock.consume",
        _ => null
    };

    private static object? RequestBody(PendingWrite value)
    {
        // Rehydrate only existing Framework/controller contracts. A stored URL or method is never trusted.
        return value.Kind switch
        {
            "warehouse-create" => value.Payload.Deserialize<StockController.WarehouseCreate>(Json) is { } create
                && create.OperationId == value.Id && ValidWarehouseInput(create.Code, create.Name) ? create : null,
            "warehouse-update" => value.Payload.Deserialize<StockController.WarehouseChange>(Json) is { } change
                && change.Version != Guid.Empty && ValidWarehouseInput(change.Code, change.Name) ? change : null,
            "warehouse-active" => value.Payload.Deserialize<StockController.ActiveChange>(Json) is { Version: var version } active
                && version != Guid.Empty ? active : null,
            "post" => value.Payload.Deserialize<StockPosting>(Json) is { } posting
                && posting.OperationId == value.Id && ValidPosting(posting) ? posting : null,
            "reverse" => value.Payload.Deserialize<StockController.ReversalCommand>(Json) is { } reversal
                && reversal.Version != Guid.Empty && reversal.OperationId != Guid.Empty && reversal.OperationId != value.Id
                && ValidReference(reversal.Reference) ? reversal : null,
            "reserve" => value.Payload.Deserialize<StockController.ReservationCommand>(Json) is { } reserve
                && reserve.OperationId == value.Id && reserve.VariantId != Guid.Empty && reserve.WarehouseId != Guid.Empty
                && ValidQuantity(reserve.Quantity, false) && ValidReference(reserve.Reference) ? reserve : null,
            "release" or "consume" => value.Payload.Deserialize<StockController.VersionedCommand>(Json) is { } command
                && command.Version != Guid.Empty ? command : null,
            _ => null
        };
    }

    private static bool ValidQuantity(decimal value, bool signed)
        => (signed ? value != 0 : value > 0) && value is >= -1_000_000_000_000m and <= 1_000_000_000_000m && decimal.Round(value, 6) == value;

    private static bool ValidWarehouseInput(string? code, string? name)
        => !string.IsNullOrWhiteSpace(code) && code.Length <= 80 && code == code.Trim()
            && !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name == name.Trim();

    private static bool ValidReference(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 255;

    private static bool ValidPosting(StockPosting posting) => posting.OperationId != Guid.Empty && posting.VariantId != Guid.Empty
        && ValidReference(posting.Reference) && ValidQuantity(posting.Quantity, posting.Kind == StockMovementKind.Adjustment)
        && posting.Kind switch
        {
            StockMovementKind.Receipt or StockMovementKind.Adjustment => posting.FromWarehouseId is null && posting.ToWarehouseId is { } to && to != Guid.Empty,
            StockMovementKind.Issue => posting.FromWarehouseId is { } from && from != Guid.Empty && posting.ToWarehouseId is null,
            StockMovementKind.Transfer => posting.FromWarehouseId is { } source && source != Guid.Empty
                && posting.ToWarehouseId is { } target && target != Guid.Empty && source != target,
            _ => false
        };

    public async Task SelectProductAsync(Product product)
    {
        if (!CanStart || !Has("stock.read") || _storageKey is not { } key) return;
        var generation = _generation;
        using var request = new CancellationTokenSource();
        _request = request; _busy = true; _error = null; _message = null;
        try
        {
            // The enrolled product list has no variant. The default variant is read by the master product code.
            var result = await Api.ReadInventoryAsync<ProductVariant>(Root + "/products/" + Uri.EscapeDataString(product.Input.Code), request.Token);
            if (!Current(generation, key)) return;
            if (result.StatusCode is >= 200 and < 300 && result.Error is null && result.Value is { } value
                && ValidVariant(value) && value.ProductId == product.Id)
            {
                _product = product; _variant = value; _movement = null; _reservation = null; _editor = Editor.None; _focus = Focus.None;
                ClearObserved();
                try { await VariantChanged.InvokeAsync(value); }
                catch (Exception) when (!_disposed)
                {
                    if (Current(generation, key)) _error = T("inventory.stock.movementListError", "품목은 선택됐지만 전표 목록을 불러오지 못했습니다. 다시 조회해 주세요.", "The product was selected, but its movement list could not be loaded. Search again.");
                }
            }
            else _error = result.StatusCode is >= 200 and < 300 ? InvalidResponse() : ErrorMessage(result.Code, result.StatusCode);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { if (Current(generation, key)) _error = UnknownMessage(); }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            if (Current(generation, key)) { _busy = false; StateHasChanged(); }
        }
        await FocusEditorAsync();
    }

    public async Task SelectMovementAsync(StockMovement movement)
    {
        if (!CanStart || !Has("stock.read")) return;
        _error = null; _message = null;
        if (!ValidMovement(movement)) _error = InvalidResponse();
        else { _movement = movement; _editor = Editor.None; _focus = Focus.Movement; _reversalReference = ""; }
        ClearObserved();
        StateHasChanged();
        await FocusEditorAsync();
    }

    public async Task SelectWarehouseAsync(Warehouse warehouse)
    {
        if (!CanStart || !Has("stock.warehouse.read")) return;
        _error = null; _message = null;
        if (!ValidWarehouse(warehouse)) _error = InvalidResponse();
        else { _warehouse = warehouse; _editor = Editor.Warehouse; _focus = Focus.Warehouse; LoadWarehouseFields(); }
        ClearObserved();
        StateHasChanged();
        await FocusEditorAsync();
    }

    private void NewWarehouse()
    {
        if (!CanStart || !CanWriteWarehouse) return;
        _warehouse = null; _editor = Editor.Warehouse; _warehouseCode = ""; _warehouseName = ""; _error = null; _message = null;
    }

    private void LoadWarehouseFields() { _warehouseCode = _warehouse!.Code; _warehouseName = _warehouse.Name; }

    private async Task SaveWarehouseAsync()
    {
        if (!CanStart || !CanWriteWarehouse) return;
        _warehouseCode = _warehouseCode.Trim(); _warehouseName = _warehouseName.Trim();
        if (!ValidWarehouseInput(_warehouseCode, _warehouseName)) { _error = WarehouseInputMessage(); return; }
        if (_warehouse is null)
        {
            var id = Guid.NewGuid();
            await BeginAsync("warehouse-create", id, new StockController.WarehouseCreate(id, _warehouseCode, _warehouseName));
        }
        else await BeginAsync("warehouse-update", _warehouse.Id, new StockController.WarehouseChange(_warehouse.Version, _warehouseCode, _warehouseName));
    }

    private Task ChangeWarehouseActiveAsync() => CanStart && CanWriteWarehouse && _warehouse is not null
        ? BeginAsync("warehouse-active", _warehouse.Id, new StockController.ActiveChange(_warehouse.Version, !_warehouse.Active))
        : Task.CompletedTask;

    public async Task SelectReservationAsync(StockReservation reservation)
    {
        if (!CanStart || !Has("stock.read")) return;
        _error = null; _message = null;
        if (!ValidReservation(reservation)) _error = InvalidResponse();
        else { _reservation = reservation; _editor = Editor.None; _focus = Focus.Reservation; }
        ClearObserved();
        StateHasChanged();
        await FocusEditorAsync();
    }

    private async Task ReadReservationAsync()
    {
        if (!CanStart || !Has("stock.read") || _storageKey is not { } key) return;
        if (!Guid.TryParse(_reservationId.Trim(), out var id) || id == Guid.Empty) { _error = ReservationIdMessage(); return; }
        var generation = _generation;
        using var request = new CancellationTokenSource();
        _request = request; _busy = true; _error = null; _message = null;
        try
        {
            var result = await Api.ReadInventoryAsync<StockReservation>(Root + $"/reservations/{id:D}", request.Token);
            if (!Current(generation, key)) return;
            if (result.StatusCode is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == id && ValidReservation(value))
            { _reservation = value; _editor = Editor.None; _focus = Focus.Reservation; ClearObserved(); }
            else _error = result.StatusCode is >= 200 and < 300 ? InvalidResponse() : ErrorMessage(result.Code, result.StatusCode);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { if (Current(generation, key)) _error = UnknownMessage(); }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            if (Current(generation, key)) { _busy = false; StateHasChanged(); }
        }
    }

    private void StartReservation()
    {
        if (!CanStart || !CanReserve) return;
        _editor = Editor.Reserve; _reservation = null; _error = null; _message = null;
        _reservationWarehouse = ""; _reservationQuantity = ""; _reservationReference = "";
    }

    private async Task ReserveAsync()
    {
        if (!CanStart || !CanReserve) return;
        _reservationReference = _reservationReference.Trim();
        if (!decimal.TryParse(_reservationQuantity, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity)
            || WarehouseId(_reservationWarehouse) is not { } warehouse || !ValidQuantity(quantity, false) || !ValidReference(_reservationReference))
        { _error = ReservationInputMessage(); return; }
        var id = Guid.NewGuid();
        await BeginAsync("reserve", id, new StockController.ReservationCommand(id, _variant!.Id, warehouse, quantity, _reservationReference));
    }

    private Task ReleaseAsync() => CanStart && CanRelease
        ? BeginAsync("release", _reservation!.Id, new StockController.VersionedCommand(_reservation.Version))
        : Task.CompletedTask;

    private Task ConsumeAsync() => CanStart && CanConsume
        ? BeginAsync("consume", _reservation!.Id, new StockController.VersionedCommand(_reservation.Version), _reservation.OperationId)
        : Task.CompletedTask;

    private async Task ReverseAsync()
    {
        if (!CanStart || !CanReverse) return;
        _reversalReference = _reversalReference.Trim();
        if (!ValidReference(_reversalReference)) { _error = ReferenceMessage(); return; }
        await BeginAsync("reverse", _movement!.Id, new StockController.ReversalCommand(_movement.Version, Guid.NewGuid(), _reversalReference));
    }

    private async Task FocusEditorAsync()
    {
        try { await _heading.FocusAsync(); }
        catch (JSException) { /* Selection remains valid if browser focus is unavailable. */ }
    }

    private void StartPosting()
    {
        if (!CanStart || !CanPost) return;
        _editor = Editor.Posting; _movement = null; _error = null; _message = null;
        _kind = nameof(StockMovementKind.Receipt); _from = ""; _to = ""; _quantity = ""; _reference = "";
    }

    private async Task PostAsync()
    {
        if (!CanStart || !CanPost) return;
        _reference = _reference.Trim();
        if (!Enum.TryParse<StockMovementKind>(_kind, out var kind) || kind == StockMovementKind.Reversal
            || !decimal.TryParse(_quantity, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var quantity))
        { _error = InputMessage(); return; }
        var from = WarehouseId(_from); var to = WarehouseId(_to);
        var posting = new StockPosting(Guid.NewGuid(), _variant!.Id, kind, quantity,
            kind is StockMovementKind.Issue or StockMovementKind.Transfer ? from : null,
            kind is StockMovementKind.Receipt or StockMovementKind.Adjustment or StockMovementKind.Transfer ? to : null, _reference);
        if (!ValidPosting(posting)) { _error = InputMessage(); return; }
        await BeginAsync("post", posting.OperationId, posting);
    }

    private Guid? WarehouseId(string value)
        => Guid.TryParse(value, out var id) && Warehouses?.Any(w => w.Id == id && w.Active) == true ? id : null;

    private async Task BeginAsync(string kind, Guid id, object body, Guid? operationId = null)
    {
        if (!CanStart || Permission(kind) is not { } grant || !Has(grant) || _storageKey is not { } key) return;
        var generation = _generation;
        var intent = new PendingWrite(1, UserId!, Scope.Membership.BusinessUserId, Scope.Membership.TenantId,
            Scope.Membership.OrganizationId, kind, id, JsonSerializer.SerializeToElement(body, Json), DateTimeOffset.UtcNow, OperationId: operationId);
        _busy = true; _error = null; _message = null; _acknowledged = false;
        ClearObserved();
        try
        {
            // No HTTP before this durable acknowledgement. Cancellation is not evidence of rollback.
            await Storage.SetAsync(key, intent);
            if (!Current(generation, key)) return;
            _pending = intent;
            await SendIntentAsync(intent, false, generation, key);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            if (Current(generation, key)) { _storageError = true; _error = StorageMessage(); }
        }
        finally { if (Current(generation, key)) { _busy = false; StateHasChanged(); } }
    }

    private async Task SendIntentAsync(PendingWrite intent, bool retry, int generation, string key)
    {
        using var request = new CancellationTokenSource();
        _request = request;
        try
        {
            var body = RequestBody(intent);
            if (body is null) { _storageError = true; _error = StorageMessage(); return; }
            var (method, path) = WriteRoute(intent);
            int status; string? code; bool valid; Guid resultId;
            if (intent.Kind.StartsWith("warehouse-", StringComparison.Ordinal))
            {
                var result = await Api.WriteInventoryAsync<Warehouse>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidWarehouse(value) && MatchesWarehouseOutcome(value, intent, body);
                if (valid) { _warehouse = result.Value; _editor = Editor.Warehouse; _focus = Focus.Warehouse; LoadWarehouseFields(); }
            }
            else if (intent.Kind is "reserve" or "release")
            {
                var result = await Api.WriteInventoryAsync<StockReservation>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidReservation(value) && MatchesReservationOutcome(value, intent, body);
                if (valid) { _reservation = result.Value; _editor = Editor.None; _focus = Focus.Reservation; }
            }
            else
            {
                var result = await Api.WriteInventoryAsync<StockMovement>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidMovement(value) && MatchesMovementOutcome(value, intent, body);
                if (valid)
                {
                    _movement = result.Value; _editor = Editor.None; _focus = Focus.Movement;
                    if (intent.Kind == "consume" && _reservation?.Id == intent.Id)
                        _reservation = _reservation with { State = StockReservationState.Consumed };
                }
            }
            if (valid)
            {
                _pending = intent with { Confirmed = true, ResultId = resultId };
                _message = T("inventory.write.saved", "저장했습니다. 현재 반환된 상태를 표시합니다.", "Saved. The current returned state is shown.");
                try
                {
                    await Storage.SetAsync(key, _pending);
                    if (!Current(generation, key)) return;
                    await Storage.DeleteAsync(key);
                    if (!Current(generation, key)) return;
                    _pending = null;
                }
                catch (Exception ex) when (StorageFailure(ex))
                {
                    if (!Current(generation, key)) return;
                    _storageError = true;
                    _error = T("inventory.write.savedRecoveryError", "저장은 완료됐지만 복구 기록을 정리하지 못했습니다. 기록을 다시 확인해 주세요.", "The write succeeded, but its recovery record could not be cleared. Check the record again.");
                }
                try { await Saved.InvokeAsync(); }
                catch (Exception) when (!_disposed)
                {
                    if (Current(generation, key)) _error = T("inventory.write.savedRefreshError", "저장은 완료됐지만 목록을 갱신하지 못했습니다. 다시 조회해 주세요.", "The write succeeded, but the lists could not be refreshed. Search again.");
                }
            }
            else
            {
                _error = status is >= 200 and < 300 ? InvalidResponse() : ErrorMessage(code, status);
                // Only a first, definite rejection disproves this attempt. A retry 409 does not
                // disprove an earlier commit whose response was lost.
                if (!retry && status is 400 or 401 or 403 or 404 or 409)
                {
                    try
                    {
                        await Storage.DeleteAsync(key);
                        if (Current(generation, key)) _pending = null;
                    }
                    catch (Exception ex) when (StorageFailure(ex))
                    { if (Current(generation, key)) { _storageError = true; _error = StorageMessage(); } }
                }
            }
        }
        catch (OperationCanceledException)
        { if (Current(generation, key)) _error = UnknownMessage(); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { if (Current(generation, key)) _error = UnknownMessage(); }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }

    private async Task RetryAsync()
    {
        if (_busy || _loading || _storageError || _pending is not { Confirmed: false } intent
            || _storageKey is not { } key || !OwnerValid || Permission(intent.Kind) is not { } grant || !Has(grant)) return;
        var generation = _generation;
        _busy = true; _error = null; _message = null;
        try { await SendIntentAsync(intent, true, generation, key); }
        finally { if (Current(generation, key)) { _busy = false; StateHasChanged(); } }
    }

    // What "current state" means for an intent: the record its outcome can be read from. A posting or
    // reservation that never returned an ID has nothing to read; only its same-body replay can confirm it.
    private (Focus Target, Guid Id)? ReadTarget => _pending is { } p ? p.Kind switch
    {
        "warehouse-create" => p.ResultId is { } created ? (Focus.Warehouse, created) : null,
        "warehouse-update" or "warehouse-active" => (Focus.Warehouse, p.Id),
        "post" => p.ResultId is { } posted ? (Focus.Movement, posted) : null,
        "reverse" => (Focus.Movement, p.ResultId ?? p.Id),
        "reserve" => p.ResultId is { } reserved ? (Focus.Reservation, reserved) : null,
        "release" or "consume" => (Focus.Reservation, p.Id),
        _ => null
    } : _focus switch
    {
        Focus.Warehouse when _warehouse is not null => (Focus.Warehouse, _warehouse.Id),
        Focus.Movement when _movement is not null => (Focus.Movement, _movement.Id),
        Focus.Reservation when _reservation is not null => (Focus.Reservation, _reservation.Id),
        _ => null
    };

    private bool CanReadCurrent => ReadTarget is { } target && Has(target.Target == Focus.Warehouse ? "stock.warehouse.read" : "stock.read");

    private async Task ReadCurrentAsync()
    {
        if (_busy || _loading || !CanReadCurrent || ReadTarget is not { } target || _storageKey is not { } key) return;
        var generation = _generation;
        using var request = new CancellationTokenSource();
        _request = request; _busy = true; _error = null;
        try
        {
            bool valid; int status; string? code;
            switch (target.Target)
            {
                case Focus.Warehouse:
                {
                    var result = await Api.ReadInventoryAsync<Warehouse>(Root + $"/warehouses/{target.Id:D}", request.Token);
                    if (!Current(generation, key)) return;
                    status = result.StatusCode; code = result.Code;
                    valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == target.Id && ValidWarehouse(value);
                    if (valid) { ClearObserved(); _observedWarehouse = result.Value; }
                    break;
                }
                case Focus.Movement:
                {
                    var result = await Api.ReadInventoryAsync<StockMovement>(Root + $"/movements/{target.Id:D}", request.Token);
                    if (!Current(generation, key)) return;
                    status = result.StatusCode; code = result.Code;
                    valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == target.Id && ValidMovement(value);
                    if (valid) { ClearObserved(); _observedMovement = result.Value; }
                    break;
                }
                default:
                {
                    var result = await Api.ReadInventoryAsync<StockReservation>(Root + $"/reservations/{target.Id:D}", request.Token);
                    if (!Current(generation, key)) return;
                    status = result.StatusCode; code = result.Code;
                    valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == target.Id && ValidReservation(value);
                    if (valid) { ClearObserved(); _observedReservation = result.Value; }
                    break;
                }
            }
            if (valid) _message = T("inventory.write.currentRead", "현재 상태를 조회했습니다. 이것만으로 이전 요청의 실행 여부를 확정하지 않습니다.", "Current state loaded. This alone does not prove whether the earlier request executed.");
            else _error = status is >= 200 and < 300 ? InvalidResponse() : ErrorMessage(code, status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { if (Current(generation, key)) _error = UnknownMessage(); }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            if (Current(generation, key)) { _busy = false; StateHasChanged(); }
        }
    }

    private void AdoptObserved()
    {
        if (!CanStart) return;
        // Reading never silently replaces a version or the user's draft. This explicit action does.
        if (_observedWarehouse is not null) { _warehouse = _observedWarehouse; _editor = Editor.Warehouse; _focus = Focus.Warehouse; LoadWarehouseFields(); }
        else if (_observedMovement is not null) { _movement = _observedMovement; _editor = Editor.None; _focus = Focus.Movement; _reversalReference = ""; }
        else if (_observedReservation is not null) { _reservation = _observedReservation; _editor = Editor.None; _focus = Focus.Reservation; }
        ClearObserved(); _error = null; _message = null;
    }

    private void ClearObserved() { _observedWarehouse = null; _observedMovement = null; _observedReservation = null; }

    private async Task ClearRecoveryAsync()
    {
        if (_busy || _loading || _storageKey is not { } key || !OwnerValid
            || (_pending?.Confirmed != true && !_acknowledged)) return;
        var generation = _generation;
        _busy = true;
        try
        {
            await Storage.DeleteAsync(key);
            if (!Current(generation, key)) return;
            _pending = null; _storageError = false; _loaded = true; _acknowledged = false; _error = null;
            _message = T("inventory.write.recoveryClosed", "이 탭의 복구 기록을 닫았습니다. 서버 데이터는 변경하지 않았습니다.", "This tab's recovery record was closed. Server data was not changed.");
        }
        catch (Exception ex) when (StorageFailure(ex))
        { if (Current(generation, key)) { _storageError = true; _error = StorageMessage(); } }
        finally { if (Current(generation, key)) { _busy = false; StateHasChanged(); } }
    }

    private (HttpMethod Method, string Path) WriteRoute(PendingWrite intent) => intent.Kind switch
    {
        "warehouse-create" => (HttpMethod.Post, Root + "/warehouses"),
        "warehouse-update" => (HttpMethod.Put, Root + $"/warehouses/{intent.Id:D}"),
        "warehouse-active" => (HttpMethod.Put, Root + $"/warehouses/{intent.Id:D}/active"),
        "post" => (HttpMethod.Post, Root + "/movements"),
        "reverse" => (HttpMethod.Post, Root + $"/movements/{intent.Id:D}/reverse"),
        "reserve" => (HttpMethod.Post, Root + "/reservations"),
        "release" or "consume" => (HttpMethod.Post, Root + $"/reservations/{intent.Id:D}/{intent.Kind}"),
        _ => throw new InvalidOperationException("Unknown stock command.")
    };

    private bool ValidScope(BusinessScope? scope) => scope is not null && scope.ProductId == "NexaOne.MES"
        && Guid.TryParse(scope.TenantId, out var tenant) && tenant == Scope.Membership.TenantId
        && Guid.TryParse(scope.OrganizationId, out var organization) && organization == Scope.Membership.OrganizationId;
    private bool ValidVariant(ProductVariant value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && value.ProductId != Guid.Empty && ValidScope(value.Scope) && !string.IsNullOrWhiteSpace(value.Sku) && !string.IsNullOrWhiteSpace(value.Unit);
    private bool ValidWarehouse(Warehouse value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && ValidWarehouseInput(value.Code, value.Name);
    private bool ValidReservation(StockReservation value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && value.OperationId != Guid.Empty && value.VariantId != Guid.Empty && value.WarehouseId != Guid.Empty
        && ValidQuantity(value.Quantity, false) && ValidReference(value.Reference) && Enum.IsDefined(value.State);
    private bool ValidMovement(StockMovement value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && value.Posting is not null && value.Deltas is { Count: > 0 }
        && (value.Posting.Kind == StockMovementKind.Reversal ? value.ReversalOfId is { } original && original != Guid.Empty : ValidPosting(value.Posting));

    private static bool MatchesMovementOutcome(StockMovement value, PendingWrite intent, object body) => intent.Kind switch
    {
        // A replayed posting returns the earlier ledger entry for the same operation and input.
        "post" => value.Posting == (StockPosting)body && value.ReversalOfId is null,
        // A replayed reversal returns the earlier reversal entry of the same original movement and reference.
        "reverse" => body is StockController.ReversalCommand reversal && value.ReversalOfId == intent.Id
            && value.Posting.Kind == StockMovementKind.Reversal && value.Posting.OperationId == reversal.OperationId
            && value.Posting.Reference == reversal.Reference,
        // Consumption is only proven by an issue ledger entry under the reservation's own operation.
        "consume" => intent.OperationId is { } operation && value.Posting.OperationId == operation
            && value.Posting.Kind == StockMovementKind.Issue && value.ReversalOfId is null,
        _ => false
    };

    private static bool MatchesWarehouseOutcome(Warehouse value, PendingWrite intent, object body) => body switch
    {
        // A create receipt returns the current warehouse, which may have been edited since creation.
        StockController.WarehouseCreate => true,
        StockController.WarehouseChange change => value.Id == intent.Id && value.Version != change.Version
            && value.Code == change.Code && value.Name == change.Name,
        StockController.ActiveChange active => value.Id == intent.Id && value.Active == active.Active,
        _ => false
    };

    private static bool MatchesReservationOutcome(StockReservation value, PendingWrite intent, object body) => intent.Kind switch
    {
        // A replayed reservation returns the earlier record for the same operation and input.
        "reserve" => body is StockController.ReservationCommand reserve && value.OperationId == reserve.OperationId
            && value.VariantId == reserve.VariantId && value.WarehouseId == reserve.WarehouseId
            && value.Quantity == reserve.Quantity && value.Reference == reserve.Reference
            && Guid.TryParse(value.CreatedBy, out var creator) && creator == intent.BusinessUserId,
        "release" => value.Id == intent.Id && value.State == StockReservationState.Released,
        _ => false
    };

    private static bool StorageFailure(Exception ex) => ex is JSException or InvalidOperationException or JsonException
        or CryptographicException or OperationCanceledException;
    private string StorageMessage() => T("inventory.write.storageError", "복구 기록을 읽거나 저장하지 못했습니다. 결과를 확인하기 전에는 새 요청을 보내지 않습니다.", "The recovery record could not be read or saved. New requests are blocked until it is checked.");
    private string UnknownMessage() => T("inventory.write.unknown", "처리 결과를 확인하지 못했습니다. 같은 요청을 재확인하거나 현재 상태를 조회해 주세요.", "The outcome is unknown. Recheck the same request or read the current state.");
    private string InvalidResponse() => T("inventory.write.invalidResponse", "응답이 선택한 범위 또는 요청과 맞지 않습니다. 저장 결과를 다시 확인해 주세요.", "The response does not match the selected scope or request. Recheck the write outcome.");
    private string WarehouseInputMessage() => T("inventory.stock.invalidWarehouse", "창고 코드(80자 이하)와 이름(255자 이하)을 확인해 주세요.", "Check the warehouse code (up to 80 characters) and name (up to 255 characters).");
    private string ReservationInputMessage() => T("inventory.stock.invalidReservation", "활성 창고, 양수이며 소수 6자리 이하인 수량, 참조를 확인해 주세요.", "Check the active warehouse, a positive quantity with at most 6 decimals, and a reference.");
    private string ReservationIdMessage() => T("inventory.stock.invalidReservationId", "예약 ID는 GUID 형식이어야 합니다.", "The reservation ID must be a GUID.");
    private string ReferenceMessage() => T("inventory.stock.invalidReference", "참조는 앞뒤 공백 없이 255자 이하로 입력해 주세요.", "Enter a reference of up to 255 characters without surrounding whitespace.");
    private string InputMessage() => T("inventory.stock.invalidInput", "전표 종류에 맞는 창고, 0이 아닌 소수 6자리 이하 수량과 참조를 확인해 주세요.", "Check the warehouses for the movement kind, a nonzero quantity with at most 6 decimals, and a reference.");

    private string ErrorMessage(string? code, int status) => code switch
    {
        "BUSINESS_VERSION_CONFLICT" => T("inventory.write.versionConflict", "다른 변경으로 버전이 바뀌었습니다. 현재 상태를 확인한 뒤 다시 결정해 주세요.", "The version changed. Read the current state before deciding what to do next."),
        "WAREHOUSE_CODE_CONFLICT" => T("inventory.stock.warehouseCodeConflict", "같은 코드의 창고가 이미 있습니다.", "A warehouse with this code already exists."),
        "STOCK_OPERATION_CONFLICT" or "WAREHOUSE_CREATE_OPERATION_CONFLICT" => T("inventory.write.operationConflict", "같은 요청 ID에 다른 내용이 기록돼 있습니다. 원래 요청과 현재 상태를 확인해 주세요.", "This request ID has different recorded input. Check the original request and current state."),
        "INVALID_STOCK_QUANTITY" => InputMessage(),
        _ when status is 401 or 403 => T("inventory.write.accessChanged", "인증 또는 작업 권한이 변경됐습니다. 로그인과 업무 범위를 다시 확인해 주세요.", "Authentication or permission changed. Check your session and business scope."),
        _ when status == 404 => T("inventory.write.notFound", "대상을 찾을 수 없습니다. 현재 목록과 업무 범위를 확인해 주세요.", "The record was not found. Check the current list and business scope."),
        _ when status == 400 => InputMessage(),
        _ when status == 409 => T("inventory.write.conflict", "현재 데이터와 충돌했습니다. 입력과 현재 상태를 확인해 주세요.", "The request conflicts with current data. Check the input and current state."),
        _ => UnknownMessage()
    };

    private string ReservationState(StockReservationState state) => state switch
    {
        StockReservationState.Active => T("inventory.stock.reservationActive", "활성", "Active"),
        StockReservationState.Consumed => T("inventory.stock.reservationConsumed", "소비됨", "Consumed"),
        StockReservationState.Released => T("inventory.stock.reservationReleased", "해제됨", "Released"),
        _ => state.ToString()
    };

    private string KindName(StockMovementKind kind) => kind switch
    {
        StockMovementKind.Receipt => T("inventory.stock.receipt", "입고", "Receipt"),
        StockMovementKind.Issue => T("inventory.stock.issue", "출고", "Issue"),
        StockMovementKind.Transfer => T("inventory.stock.transfer", "이동", "Transfer"),
        StockMovementKind.Adjustment => T("inventory.stock.adjustment", "조정", "Adjustment"),
        StockMovementKind.Reversal => T("inventory.stock.reversal", "반제", "Reversal"),
        _ => kind.ToString()
    };

    private string ActionName(string kind) => kind switch
    {
        "warehouse-create" => T("inventory.stock.createWarehouse", "창고 등록", "Register warehouse"),
        "warehouse-update" => T("inventory.stock.saveWarehouse", "창고 변경 저장", "Save warehouse changes"),
        "warehouse-active" => T("inventory.stock.changeWarehouseActivity", "창고 활성 변경", "Change warehouse activity"),
        "post" => T("inventory.stock.post", "전표 등록", "Post movement"),
        "reverse" => T("inventory.stock.reverse", "전표 반제", "Reverse movement"),
        "reserve" => T("inventory.stock.reserve", "예약 등록", "Reserve stock"),
        "release" => T("inventory.stock.release", "예약 해제", "Release reservation"),
        "consume" => T("inventory.stock.consume", "예약 소비", "Consume reservation"),
        _ => ""
    };

    private string WarehouseName(Guid? id) => id is { } value && Warehouses?.FirstOrDefault(w => w.Id == value) is { } warehouse
        ? warehouse.Code + " · " + warehouse.Name : id?.ToString("D") ?? "";
    private static string Quantity(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private void LanguageChanged() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }
    public void Dispose() { _disposed = true; ++_generation; _request?.Cancel(); Ui.Changed -= LanguageChanged; }
}
