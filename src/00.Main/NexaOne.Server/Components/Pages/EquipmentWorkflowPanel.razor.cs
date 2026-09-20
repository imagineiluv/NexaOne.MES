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
using NexaOne.ServiceContracts.Mdm;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;

namespace NexaOne.Server.Components.Pages;

public partial class EquipmentWorkflowPanel : IDisposable
{
    [Inject] public IApiClient Api { get; set; } = null!;
    [Inject] public UiTextService Ui { get; set; } = null!;
    [Inject] public ProtectedSessionStorage Storage { get; set; } = null!;
    [Parameter, EditorRequired] public InventoryAccessScope Scope { get; set; } = null!;
    [Parameter] public string? UserId { get; set; }
    [Parameter] public WorkerDto? Worker { get; set; }
    [Parameter] public EventCallback Saved { get; set; }

    // A browser recovery envelope, not a second equipment/booking business model.
    public sealed record PendingWrite(int Schema, string UserId, Guid BusinessUserId, Guid TenantId,
        Guid OrganizationId, string Kind, Guid Id, JsonElement Payload, DateTimeOffset CreatedAt,
        bool Confirmed = false, Guid? ResultId = null);

    private enum Editor { None, Asset, Request, Booking }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _storageKey;
    private int _generation;
    private bool _interactive, _loaded, _loading, _busy, _storageError, _acknowledged, _disposed;
    private CancellationTokenSource? _request;
    private PendingWrite? _pending;
    private SharedEquipment? _asset;
    private EquipmentBooking? _booking;
    private SharedEquipment? _observedAsset;
    private EquipmentBooking? _observedBooking;
    private Editor _editor;
    private string _code = "", _name = "", _start = "", _end = "";
    private int _capacity = 1, _quantity = 1;
    private bool _requiresApproval = true;
    private string? _error, _message;
    private ElementReference _heading;

    private bool Has(string grant) => Scope?.Membership.Permissions.Contains(grant, StringComparer.Ordinal) == true;
    private bool OwnerValid => Scope is not null && !string.IsNullOrWhiteSpace(UserId)
        && string.Equals(Scope.Membership.UserId, UserId, StringComparison.Ordinal)
        && Scope.Membership.IsActive && Scope.Membership.Version > 0 && Scope.Membership.BusinessUserId != Guid.Empty
        && Scope.Membership.TenantId != Guid.Empty && Scope.Membership.OrganizationId != Guid.Empty;
    private bool CanStart => OwnerValid && _loaded && !_busy && !_loading && !_storageError && _pending is null;
    private bool CanRequest => Has("equipment.booking.request") && Has("equipment.read")
        && _asset is { Active: true } && Worker is { IsActive: true } && Worker.PlantId == Scope.Binding.PlantId;
    private bool OwnRequest => _booking is not null && Guid.TryParse(_booking.RequestedBy, out var id)
        && id == Scope.Membership.BusinessUserId;
    private string Root => $"api/v1/ivt/shared-equipment/{Scope.Membership.TenantId:D}/{Scope.Membership.OrganizationId:D}";
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);

    protected override void OnInitialized() => Ui.Changed += LanguageChanged;

    protected override void OnParametersSet()
    {
        var key = OwnerValid ? StorageKey(UserId!, Scope) : null;
        if (key == _storageKey) return;
        ++_generation;
        _request?.Cancel();
        _storageKey = key;
        _pending = null; _asset = null; _booking = null; _observedAsset = null; _observedBooking = null; _editor = Editor.None;
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
        return "nexaone_inventory_write_v1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)));
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
        && RequestBody(value) is not null;

    private static string? Permission(string kind) => kind switch
    {
        "create" or "update" or "active" => "equipment.write",
        "request" => "equipment.booking.request", "approve" or "deny" => "equipment.booking.decide",
        "cancel" => "equipment.booking.cancel", "checkout" => "equipment.booking.checkout",
        "return" => "equipment.booking.return", _ => null
    };

    private static object? RequestBody(PendingWrite value)
    {
        // Rehydrate only these existing controller contracts. A stored URL or method is never trusted.
        return value.Kind switch
        {
            "create" => value.Payload.Deserialize<EquipmentSharingController.EquipmentCreate>(Json) is { } create
                && create.OperationId == value.Id && ValidAssetInput(create.Code, create.Name, create.Capacity) ? create : null,
            "update" => value.Payload.Deserialize<EquipmentSharingController.EquipmentChange>(Json) is { } update
                && update.ExpectedVersion is { } version && version != Guid.Empty
                && ValidAssetInput(update.Code, update.Name, update.Capacity) ? update : null,
            "active" => value.Payload.Deserialize<EquipmentSharingController.ActiveChange>(Json) is { Version: var version } active
                && version != Guid.Empty ? active : null,
            "request" => value.Payload.Deserialize<EquipmentSharingController.BookingRequest>(Json) is { } request
                && request.EquipmentId != Guid.Empty && !string.IsNullOrWhiteSpace(request.WorkerId)
                && request.WorkerId.Length <= 50 && request.Quantity > 0 && request.Start != default
                && request.End > request.Start && request.Start.Offset == TimeSpan.Zero && request.End.Offset == TimeSpan.Zero ? request : null,
            "approve" or "deny" => value.Payload.Deserialize<EquipmentSharingController.BookingDecision>(Json) is { } decision
                && decision.Version != Guid.Empty && decision.Approve == (value.Kind == "approve") ? decision : null,
            "cancel" or "checkout" or "return" => value.Payload.Deserialize<EquipmentSharingController.VersionedCommand>(Json) is { } command
                && command.Version != Guid.Empty ? command : null,
            _ => null
        };
    }

    private static bool ValidAssetInput(string? code, string? name, int capacity)
        => !string.IsNullOrWhiteSpace(code) && code.Length <= 80 && code == code.Trim()
            && !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name == name.Trim() && capacity > 0;

    public async Task SelectEquipmentAsync(SharedEquipment asset)
    {
        if (!CanStart || !Has("equipment.read")) return;
        _error = null; _message = null; _observedAsset = null; _observedBooking = null;
        if (!ValidAsset(asset)) _error = InvalidResponse();
        else { _asset = asset; _booking = null; _editor = Editor.Asset; LoadAssetFields(); }
        StateHasChanged();
        await FocusEditorAsync();
    }

    public async Task SelectBookingAsync(EquipmentBooking booking)
    {
        if (!CanStart || !Has("equipment.booking.read")) return;
        _error = null; _message = null; _observedAsset = null; _observedBooking = null;
        if (!ValidBooking(booking)) _error = InvalidResponse();
        else { _booking = booking; _editor = Editor.Booking; }
        StateHasChanged();
        await FocusEditorAsync();
    }

    private async Task FocusEditorAsync()
    {
        try { await _heading.FocusAsync(); }
        catch (JSException) { /* Selection remains valid if browser focus is unavailable. */ }
    }

    private void NewAsset()
    {
        if (!CanStart || !Has("equipment.write")) return;
        _asset = null; _booking = null; _editor = Editor.Asset;
        _observedAsset = null; _observedBooking = null;
        _code = ""; _name = ""; _capacity = 1; _requiresApproval = true; _error = null; _message = null;
    }

    private void LoadAssetFields()
    {
        _code = _asset!.Code; _name = _asset.Name; _capacity = _asset.Capacity; _requiresApproval = _asset.RequiresApproval;
    }

    private void StartBooking()
    {
        if (!CanStart || !CanRequest) return;
        _editor = Editor.Request; _booking = null; _quantity = 1; _error = null; _message = null;
        _observedAsset = null; _observedBooking = null;
        var start = DateTimeOffset.UtcNow.AddHours(1);
        _start = start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        _end = start.AddHours(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    }

    private async Task SaveAssetAsync()
    {
        if (!CanStart || !Has("equipment.write")) return;
        _code = _code.Trim(); _name = _name.Trim();
        if (!ValidAssetInput(_code, _name, _capacity)) { _error = InputMessage(); return; }
        if (_asset is null)
        {
            var id = Guid.NewGuid();
            await BeginAsync("create", id, new EquipmentSharingController.EquipmentCreate(id, _code, _name, _capacity, _requiresApproval));
        }
        else await BeginAsync("update", _asset.Id, new EquipmentSharingController.EquipmentChange(
            _code, _name, _capacity, _requiresApproval, _asset.Version));
    }

    private Task ChangeActiveAsync() => CanStart && Has("equipment.write") && _asset is not null
        ? BeginAsync("active", _asset.Id, new EquipmentSharingController.ActiveChange(_asset.Version, !_asset.Active))
        : Task.CompletedTask;

    private async Task RequestBookingAsync()
    {
        if (!CanStart || !CanRequest) return;
        if (!UtcInput(_start, out var start) || !UtcInput(_end, out var end) || end <= start || _quantity <= 0)
        { _error = InputMessage(); return; }
        await BeginAsync("request", Guid.NewGuid(), new EquipmentSharingController.BookingRequest(
            _asset!.Id, Worker!.WorkerId, start, end, _quantity));
    }

    private static bool UtcInput(string value, out DateTimeOffset result)
    {
        result = default;
        if (!DateTime.TryParseExact(value, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        result = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return true;
    }

    private bool CanTransition(string kind) => CanStart && _booking is not null && Permission(kind) is { } grant && Has(grant)
        && (kind switch
        {
            "approve" or "deny" => _booking.State == EquipmentBookingState.Requested && !OwnRequest,
            "cancel" => _booking.State is EquipmentBookingState.Requested or EquipmentBookingState.Approved,
            "checkout" => _booking.State == EquipmentBookingState.Approved,
            "return" => _booking.State == EquipmentBookingState.CheckedOut,
            _ => false
        });

    private Task TransitionAsync(string kind)
    {
        if (!CanTransition(kind)) return Task.CompletedTask;
        object body = kind is "approve" or "deny"
            ? new EquipmentSharingController.BookingDecision(_booking!.Version, kind == "approve")
            : new EquipmentSharingController.VersionedCommand(_booking!.Version);
        return BeginAsync(kind, _booking!.Id, body);
    }

    private async Task BeginAsync(string kind, Guid id, object body)
    {
        if (!CanStart || Permission(kind) is not { } grant || !Has(grant) || _storageKey is not { } key) return;
        var generation = _generation;
        var intent = new PendingWrite(1, UserId!, Scope.Membership.BusinessUserId, Scope.Membership.TenantId,
            Scope.Membership.OrganizationId, kind, id, JsonSerializer.SerializeToElement(body, Json), DateTimeOffset.UtcNow);
        _busy = true; _error = null; _message = null; _acknowledged = false;
        _observedAsset = null; _observedBooking = null;
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

    private async Task RetryAsync()
    {
        if (_busy || _loading || _storageError || _pending is not { Confirmed: false } intent
            || _storageKey is not { } key || !OwnerValid || Permission(intent.Kind) is not { } grant || !Has(grant)) return;
        var generation = _generation;
        _busy = true; _error = null; _message = null;
        try { await SendIntentAsync(intent, true, generation, key); }
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
            var asset = intent.Kind is "create" or "update" or "active";
            var (method, path) = WriteRoute(intent);
            int status; string? code; bool valid; Guid resultId;
            if (asset)
            {
                var result = await Api.WriteInventoryAsync<SharedEquipment>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidAsset(value) && MatchesAssetOutcome(value, intent, body);
                resultId = result.Value?.Id ?? Guid.Empty;
                if (valid) { _asset = result.Value; _editor = Editor.Asset; LoadAssetFields(); }
            }
            else
            {
                var result = await Api.WriteInventoryAsync<EquipmentBooking>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidBooking(value) && value.Id == intent.Id
                    && MatchesBookingOutcome(value, intent, body);
                resultId = result.Value?.Id ?? Guid.Empty;
                if (valid) { _booking = result.Value; _editor = Editor.Booking; }
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
                // Only a first, definite rejection disproves this attempt. A retry409 does not
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

    private (HttpMethod Method, string Path) WriteRoute(PendingWrite intent) => intent.Kind switch
    {
        "create" => (HttpMethod.Post, Root + "/assets"),
        "update" => (HttpMethod.Put, Root + $"/assets/{intent.Id:D}"),
        "active" => (HttpMethod.Put, Root + $"/assets/{intent.Id:D}/active"),
        "request" => (HttpMethod.Put, Root + $"/bookings/{intent.Id:D}"),
        "approve" or "deny" => (HttpMethod.Post, Root + $"/bookings/{intent.Id:D}/decide"),
        "checkout" => (HttpMethod.Post, Root + $"/bookings/{intent.Id:D}/check-out"),
        "cancel" or "return" => (HttpMethod.Post, Root + $"/bookings/{intent.Id:D}/{intent.Kind}"),
        _ => throw new InvalidOperationException("Unknown equipment command.")
    };

    private bool CanReadCurrent => _pending is { } p ? (p.Kind is "create" or "update" or "active"
        ? Has("equipment.read") && (p.Kind != "create" || p.ResultId.HasValue) : Has("equipment.booking.read"))
        : _editor == Editor.Booking ? _booking is not null && Has("equipment.booking.read")
        : _asset is not null && Has("equipment.read");

    private async Task ReadCurrentAsync()
    {
        if (_busy || _loading || !CanReadCurrent || _storageKey is not { } key) return;
        var intent = _pending;
        var generation = _generation;
        using var request = new CancellationTokenSource();
        _request = request; _busy = true; _error = null;
        try
        {
            var asset = intent is not null ? intent.Kind is "create" or "update" or "active" : _editor != Editor.Booking;
            var id = intent?.ResultId ?? intent?.Id ?? (asset ? _asset!.Id : _booking!.Id);
            bool valid; int status; string? code;
            if (asset)
            {
                var result = await Api.ReadInventoryAsync<SharedEquipment>(Root + $"/assets/{id:D}", request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == id && ValidAsset(value);
                if (valid) { _observedAsset = result.Value; _observedBooking = null; }
            }
            else
            {
                var result = await Api.ReadInventoryAsync<EquipmentBooking>(Root + $"/bookings/{id:D}", request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == id && ValidBooking(value);
                if (valid) { _observedBooking = result.Value; _observedAsset = null; }
            }
            if (valid) _message = T("inventory.write.currentRead", "현재 상태를 조회했습니다. 이것만으로 이전 요청의 실행 여부를 확정하지 않습니다.", "Current state loaded. This alone does not prove whether the earlier request executed.");
            else _error = ErrorMessage(code, status);
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
        // Reading never silently replaces the version or the user's draft. This explicit action does.
        if (_observedAsset is not null) { _asset = _observedAsset; _editor = Editor.Asset; LoadAssetFields(); }
        else if (_observedBooking is not null) { _booking = _observedBooking; _editor = Editor.Booking; }
        _observedAsset = null; _observedBooking = null; _error = null; _message = null;
    }

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

    private bool ValidScope(BusinessScope? scope) => scope is not null && scope.ProductId == "NexaOne.MES"
        && Guid.TryParse(scope.TenantId, out var tenant) && tenant == Scope.Membership.TenantId
        && Guid.TryParse(scope.OrganizationId, out var organization) && organization == Scope.Membership.OrganizationId;
    private bool ValidAsset(SharedEquipment value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && ValidAssetInput(value.Code, value.Name, value.Capacity);
    private bool ValidBooking(EquipmentBooking value) => value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && value.EquipmentId != Guid.Empty && value.EmployeeId != Guid.Empty
        && Guid.TryParse(value.RequestedBy, out var requester) && requester != Guid.Empty && value.Quantity > 0
        && Enum.IsDefined(value.State) && value.Start != default && value.End > value.Start
        && value.Start.Offset == TimeSpan.Zero && value.End.Offset == TimeSpan.Zero
        && (value.State == EquipmentBookingState.Returned) == value.ReturnedAt.HasValue
        && (!value.ReturnedAt.HasValue || value.ReturnedAt.Value >= value.Start && value.ReturnedAt.Value.Offset == TimeSpan.Zero);

    private static bool MatchesRequest(EquipmentBooking value, EquipmentSharingController.BookingRequest body, Guid actor)
        => value.EquipmentId == body.EquipmentId && value.Start == body.Start && value.End == body.End
            && value.Quantity == body.Quantity && Guid.TryParse(value.RequestedBy, out var requester) && requester == actor;

    private static bool MatchesAssetOutcome(SharedEquipment value, PendingWrite intent, object body) => body switch
    {
        // A create receipt returns the current asset, which may have been edited since creation.
        EquipmentSharingController.EquipmentCreate => true,
        EquipmentSharingController.EquipmentChange change => value.Id == intent.Id && value.Version != change.ExpectedVersion
            && value.Code == change.Code && value.Name == change.Name && value.Capacity == change.Capacity
            && value.RequiresApproval == change.RequiresApproval,
        EquipmentSharingController.ActiveChange active => value.Id == intent.Id && value.Active == active.Active,
        _ => false
    };

    private static bool MatchesBookingOutcome(EquipmentBooking value, PendingWrite intent, object body) => intent.Kind switch
    {
        "request" => MatchesRequest(value, (EquipmentSharingController.BookingRequest)body, intent.BusinessUserId),
        "approve" => value.State is EquipmentBookingState.Approved or EquipmentBookingState.CheckedOut or EquipmentBookingState.Returned,
        "deny" => value.State == EquipmentBookingState.Denied,
        "cancel" => value.State == EquipmentBookingState.Cancelled,
        "checkout" => value.State is EquipmentBookingState.CheckedOut or EquipmentBookingState.Returned,
        "return" => value.State == EquipmentBookingState.Returned,
        _ => false
    };

    private static bool StorageFailure(Exception ex) => ex is JSException or InvalidOperationException or JsonException
        or CryptographicException or OperationCanceledException;
    private string StorageMessage() => T("inventory.write.storageError", "복구 기록을 읽거나 저장하지 못했습니다. 결과를 확인하기 전에는 새 요청을 보내지 않습니다.", "The recovery record could not be read or saved. New requests are blocked until it is checked.");
    private string UnknownMessage() => T("inventory.write.unknown", "처리 결과를 확인하지 못했습니다. 같은 요청을 재확인하거나 현재 상태를 조회해 주세요.", "The outcome is unknown. Recheck the same request or read the current state.");
    private string InvalidResponse() => T("inventory.write.invalidResponse", "응답이 선택한 범위 또는 요청과 맞지 않습니다. 저장 결과를 다시 확인해 주세요.", "The response does not match the selected scope or request. Recheck the write outcome.");
    private string InputMessage() => T("inventory.write.invalidInput", "필수 코드·이름, 양수 수량과 시작보다 늦은 UTC 종료 시각을 확인해 주세요.", "Check required code/name, positive quantities, and a UTC end later than the start.");

    private string ErrorMessage(string? code, int status) => code switch
    {
        "BUSINESS_VERSION_CONFLICT" => T("inventory.write.versionConflict", "다른 변경으로 버전이 바뀌었습니다. 현재 상태를 확인한 뒤 다시 결정해 주세요.", "The version changed. Read the current state before deciding what to do next."),
        "EQUIPMENT_CAPACITY_UNAVAILABLE" => T("inventory.write.capacityUnavailable", "해당 기간에 남은 장비 수량이 부족합니다. 기간 또는 수량을 변경해 주세요.", "Equipment capacity is unavailable for that interval. Change the dates or quantity."),
        "EQUIPMENT_HAS_OPEN_BOOKINGS" => T("inventory.write.openBookings", "진행 중인 예약이 있어 이 장비 설정을 변경할 수 없습니다.", "Open bookings prevent this equipment change."),
        "SELF_APPROVAL_NOT_ALLOWED" => T("inventory.write.selfDecision", "본인이 요청한 예약은 직접 승인하거나 거절할 수 없습니다.", "You cannot approve or deny your own request."),
        "BOOKING_OUTSIDE_CHECKOUT_WINDOW" => T("inventory.write.checkoutWindow", "대여는 예약 시작 이상, 종료 이전에 가능합니다.", "Check-out is available from the booking start until before its end."),
        "BOOKING_START_IN_PAST" or "BOOKING_EXPIRED" or "INVALID_UTC_INTERVAL" or "INVALID_RETURN_CLOCK" => T("inventory.write.timeError", "예약의 UTC 기간과 현재 시각을 확인해 주세요.", "Check the UTC booking interval and current time."),
        "EMPLOYEE_NOT_FOUND" => T("inventory.write.workerUnavailable", "작업자가 현재 공장에서 사용 가능하지 않습니다. 작업자 목록을 새로 조회해 주세요.", "The worker is unavailable in the current plant. Refresh the worker list."),
        "EQUIPMENT_INACTIVE" => T("inventory.write.assetInactive", "비활성 장비에는 새 예약이나 대여를 진행할 수 없습니다.", "Inactive equipment cannot receive a new booking or check-out."),
        "BOOKING_OPERATION_CONFLICT" or "EQUIPMENT_CREATE_OPERATION_CONFLICT" => T("inventory.write.operationConflict", "같은 요청 ID에 다른 내용이 기록돼 있습니다. 원래 요청과 현재 상태를 확인해 주세요.", "This request ID has different recorded input. Check the original request and current state."),
        "BOOKING_NOT_REQUESTED" or "BOOKING_NOT_CANCELLABLE" or "BOOKING_NOT_APPROVED" or "BOOKING_NOT_CHECKED_OUT" => T("inventory.write.stateConflict", "현재 예약 상태에서는 이 작업을 할 수 없습니다. 예약을 다시 조회해 주세요.", "This action is unavailable in the current booking state. Reload the booking."),
        _ when status is 401 or 403 => T("inventory.write.accessChanged", "인증 또는 작업 권한이 변경됐습니다. 로그인과 업무 범위를 다시 확인해 주세요.", "Authentication or permission changed. Check your session and business scope."),
        _ when status == 404 => T("inventory.write.notFound", "대상을 찾을 수 없습니다. 현재 목록과 업무 범위를 확인해 주세요.", "The record was not found. Check the current list and business scope."),
        _ when status == 400 => InputMessage(),
        _ when status == 409 => T("inventory.write.conflict", "현재 데이터와 충돌했습니다. 입력과 현재 상태를 확인해 주세요.", "The request conflicts with current data. Check the input and current state."),
        _ => UnknownMessage()
    };

    private string BookingState(EquipmentBookingState state) => state switch
    {
        EquipmentBookingState.Requested => T("inventory.requested", "승인 요청", "Requested"),
        EquipmentBookingState.Approved => T("inventory.approved", "승인", "Approved"),
        EquipmentBookingState.CheckedOut => T("inventory.checkedOut", "대여 중", "Checked out"),
        EquipmentBookingState.Returned => T("inventory.returned", "반납", "Returned"),
        EquipmentBookingState.Denied => T("inventory.denied", "거절", "Denied"),
        EquipmentBookingState.Cancelled => T("inventory.cancelled", "취소", "Cancelled"),
        _ => state.ToString()
    };

    private string ActionName(string kind) => kind switch
    {
        "create" => T("inventory.write.createAsset", "장비 등록", "Register equipment"),
        "update" => T("inventory.write.saveAsset", "장비 변경 저장", "Save equipment changes"),
        "active" => T("inventory.write.changeActivity", "장비 활성 변경", "Change equipment activity"),
        "request" => T("inventory.write.requestBooking", "예약 요청", "Request booking"),
        "approve" => T("inventory.write.approve", "예약 승인", "Approve booking"),
        "deny" => T("inventory.write.deny", "예약 거절", "Deny booking"),
        "cancel" => T("inventory.write.cancel", "예약 취소", "Cancel booking"),
        "checkout" => T("inventory.write.checkout", "장비 대여", "Check out equipment"),
        "return" => T("inventory.write.return", "장비 반납", "Return equipment"),
        _ => ""
    };

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private void LanguageChanged() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }
    public void Dispose() { _disposed = true; ++_generation; _request?.Cancel(); Ui.Changed -= LanguageChanged; }
}
