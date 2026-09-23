using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;

namespace NexaOne.Server.Components.Pages;

public partial class RecurringWorkflowPanel : IDisposable
{
    [Inject] public IApiClient Api { get; set; } = null!;
    [Inject] public UiTextService Ui { get; set; } = null!;
    [Inject] public ProtectedSessionStorage Storage { get; set; } = null!;
    [Parameter, EditorRequired] public BusinessMembership Membership { get; set; } = null!;
    [Parameter] public string? UserId { get; set; }
    [Parameter] public EventCallback Saved { get; set; }

    public sealed record PendingWrite(int Schema, string UserId, Guid BusinessUserId, Guid TenantId,
        Guid OrganizationId, string Kind, Guid Id, JsonElement Payload, DateTimeOffset CreatedAt,
        bool Confirmed = false, Guid? ResultId = null);

    public sealed class LineDraft
    {
        public string Description { get; set; } = "";
        public string Price { get; set; } = "";
        public string Quantity { get; set; } = "";
        public bool Tax { get; set; } = true;
        public bool Discount { get; set; } = true;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _storageKey, _error, _message;
    private int _generation;
    private bool _interactive, _loaded, _loading, _busy, _storageError, _acknowledged, _disposed, _creating;
    private CancellationTokenSource? _request;
    private PendingWrite? _pending;
    private RecurringRule? _rule;
    private RecurringExecution? _execution;
    private ElementReference _heading;

    private string _name = "", _startMonth = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture), _endMonth = "", _month = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private RecurringTarget _target = RecurringTarget.Billing;
    private int _day = 1, _dueDays = 30;
    private string _billingKind = nameof(BillingKind.Invoice), _contactId = "", _employeeId = "", _categoryId = "", _vendorId = "", _currency = "KRW", _amount = "", _terms = "", _notes = "", _reference = "", _purpose = "", _receipt = "";
    private bool _isBonus, _split;
    private ExpenseType _expenseType = ExpenseType.TaxDeductible;
    private List<LineDraft> _lines = [new()];

    private bool Has(string? grant) => grant is not null && Membership?.Permissions.Contains(grant, StringComparer.Ordinal) == true;
    private bool OwnerValid => Membership is not null && !string.IsNullOrWhiteSpace(UserId)
        && string.Equals(Membership.UserId, UserId, StringComparison.Ordinal) && Membership.IsActive
        && Membership.Version > 0 && Membership.BusinessUserId != Guid.Empty && Membership.TenantId != Guid.Empty
        && Membership.OrganizationId != Guid.Empty;
    private bool CanStart => OwnerValid && _loaded && !_loading && !_busy && !_storageError && _pending is null;
    private bool CanWrite => Has("recurring.write");
    private bool CanExecute => Has("recurring.execute");
    private string Root => $"api/v1/erp/recurring/{Membership.TenantId:D}/{Membership.OrganizationId:D}";

    protected override void OnInitialized() => Ui.Changed += LanguageChanged;
    protected override void OnParametersSet()
    {
        var key = OwnerValid ? StorageKey(UserId!, Membership) : null;
        if (key == _storageKey) return;
        ++_generation; _request?.Cancel(); _storageKey = key; _pending = null; _rule = null; _execution = null;
        _loaded = false; _loading = false; _busy = false; _storageError = false; _acknowledged = false; _creating = false; _error = null; _message = null;
    }
    protected override async Task OnAfterRenderAsync(bool firstRender) { _interactive = true; if (!_loaded && !_loading && !_storageError && _storageKey is not null) await LoadRecoveryAsync(); }
    private void LanguageChanged() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }
    private static string StorageKey(string userId, BusinessMembership scope)
    {
        var owner = JsonSerializer.Serialize(new[] { userId, scope.BusinessUserId.ToString("D"), scope.TenantId.ToString("D"), scope.OrganizationId.ToString("D") });
        return "nexaone_recurring_write_v1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)));
    }
    private bool Current(int generation, string key) => !_disposed && generation == _generation && key == _storageKey;
    private async Task LoadRecoveryAsync()
    {
        if (!_interactive || _storageKey is not { } key || _busy || _loading) return;
        var generation = _generation; _loading = true; _error = null;
        try
        {
            var stored = await Storage.GetAsync<PendingWrite>(key);
            if (!Current(generation, key)) return;
            if (stored.Success && stored.Value is not null) { if (!ValidIntent(stored.Value)) throw new JsonException("Invalid recovery envelope."); _pending = stored.Value; } else _pending = null;
            _loaded = true; _storageError = false; _acknowledged = false;
        }
        catch (Exception ex) when (StorageFailure(ex)) { if (Current(generation, key)) { _storageError = true; _error = RecoveryError(); } }
        finally { if (Current(generation, key)) { _loading = false; StateHasChanged(); } }
    }
    private bool ValidIntent(PendingWrite value) => value.Schema == 1 && value.UserId == UserId && value.BusinessUserId == Membership.BusinessUserId
        && value.TenantId == Membership.TenantId && value.OrganizationId == Membership.OrganizationId && value.Id != Guid.Empty
        && value.CreatedAt != default && value.Payload.ValueKind == JsonValueKind.Object && Permission(value.Kind) is not null
        && (!value.Confirmed || value.ResultId is { } result && result != Guid.Empty) && RequestBody(value) is not null;
    private static string? Permission(string kind) => kind switch { "create" or "deactivate" => "recurring.write", "execute" => "recurring.execute", _ => null };
    private static object? RequestBody(PendingWrite value)
    {
        try
        {
            return value.Kind switch
            {
                "create" => value.Payload.Deserialize<RecurringController.RuleCreate>(Json) is { } create && create.OperationId == value.Id && ValidCreate(create) ? create : null,
                "deactivate" => value.Payload.Deserialize<RecurringController.VersionedCommand>(Json) is { Version: var version } command && version != Guid.Empty ? command : null,
                "execute" => value.Payload.Deserialize<RecurringController.OccurrenceCommand>(Json) is { Month.Day: 1 } command ? command : null,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    public async Task SelectRuleAsync(RecurringRule value)
    {
        if (!InScope(value.Scope)) { _error = T("recurring.foreignScope", "다른 범위의 규칙은 선택할 수 없습니다.", "A rule from another scope cannot be selected."); _rule = null; return; }
        _rule = value; _month = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture); _creating = false; _error = null; _message = null;
        await InvokeAsync(StateHasChanged);
    }
    private bool InScope(BusinessScope scope) => string.Equals(scope.ProductId, "NexaOne.MES", StringComparison.Ordinal)
        && Guid.TryParse(scope.TenantId, out var tenant) && tenant == Membership.TenantId
        && Guid.TryParse(scope.OrganizationId, out var organization) && organization == Membership.OrganizationId;
    private void StartCreate() { if (!CanStart || !CanWrite) return; _creating = true; _error = null; _message = null; }
    private async Task CreateAsync()
    {
        if (!CanStart || !CanWrite || !TryCreate(out var command)) { _error = T("recurring.invalidRule", "규칙 이름, 월 범위, 실행일, 통화, 금액과 필수 ID를 확인해 주세요.", "Check the rule name, month range, day, currency, amounts and required IDs."); return; }
        await BeginAsync("create", command.OperationId, command);
    }
    private Task DeactivateAsync() => _rule is { Active: true } rule && CanStart && CanWrite
        ? BeginAsync("deactivate", rule.Id, new RecurringController.VersionedCommand(rule.Version)) : Task.CompletedTask;
    private Task ExecuteAsync()
    {
        if (_rule is not { } rule || !CanStart || !CanExecute || !TryMonth(_month, out var month)) { _error = T("recurring.invalidMonth", "실행할 월을 선택해 주세요.", "Select a month to run."); return Task.CompletedTask; }
        return BeginAsync("execute", rule.Id, new RecurringController.OccurrenceCommand(month));
    }
    private async Task BeginAsync(string kind, Guid id, object body)
    {
        if (_storageKey is not { } key) return;
        var intent = new PendingWrite(1, UserId!, Membership.BusinessUserId, Membership.TenantId, Membership.OrganizationId, kind, id, JsonSerializer.SerializeToElement(body, body.GetType(), Json), DateTimeOffset.UtcNow);
        _busy = true; _error = null; _message = null;
        try { await Storage.SetAsync(key, intent); _pending = intent; await SendAsync(intent); }
        catch (Exception ex) when (StorageFailure(ex)) { _storageError = true; _pending = null; _error = RecoveryError(); }
        finally { _busy = false; StateHasChanged(); }
    }
    private async Task RetryAsync() { if (_pending is not { Confirmed: false } intent || _busy || !Has(Permission(intent.Kind))) return; _busy = true; _error = null; try { await SendAsync(intent); } finally { _busy = false; StateHasChanged(); } }
    private async Task SendAsync(PendingWrite intent)
    {
        var body = RequestBody(intent); if (body is null) { _storageError = true; _error = RecoveryError(); return; }
        var path = intent.Kind switch { "create" => $"{Root}/rules", "deactivate" => $"{Root}/rules/{intent.Id:D}/deactivate", _ => $"{Root}/rules/{intent.Id:D}/occurrences" };
        if (intent.Kind == "execute")
        {
            var result = await Api.WriteInventoryAsync<RecurringExecution>(HttpMethod.Post, path, body, intent.UserId, Token());
            if (Success(result.StatusCode, result.Error, result.Value)) { var value = result.Value!; if (!InScope(value.Occurrence.Scope) || value.Occurrence.RuleId != intent.Id) { _error = RecoveryError(); return; } _execution = value; await ConfirmAndClearAsync(intent, value.Occurrence.Id); _message = T("recurring.executed", "월 발생 건을 실행했습니다.", "The monthly occurrence was run."); }
            else await HandleFailureAsync(result.StatusCode, result.Code, result.Error);
        }
        else
        {
            var result = await Api.WriteInventoryAsync<RecurringRule>(HttpMethod.Post, path, body, intent.UserId, Token());
            if (Success(result.StatusCode, result.Error, result.Value)) { var value = result.Value!; var identityMatches = intent.Kind == "create" ? value.OperationId == intent.Id : value.Id == intent.Id; if (!InScope(value.Scope) || !identityMatches) { _error = RecoveryError(); return; } _rule = value; _creating = false; await ConfirmAndClearAsync(intent, value.Id); _message = intent.Kind == "create" ? T("recurring.created", "반복 규칙을 만들었습니다.", "The recurring rule was created.") : T("recurring.deactivated", "반복 규칙을 비활성화했습니다.", "The recurring rule was deactivated."); }
            else await HandleFailureAsync(result.StatusCode, result.Code, result.Error);
        }
    }
    private CancellationToken Token() { _request?.Cancel(); _request?.Dispose(); _request = new CancellationTokenSource(); return _request.Token; }
    private static bool Success<T>(int status, string? error, T? value) where T : class => status is >= 200 and < 300 && error is null && value is not null;
    private async Task ConfirmAndClearAsync(PendingWrite intent, Guid resultId)
    {
        if (_storageKey is not { } key) return;
        var confirmed = intent with { Confirmed = true, ResultId = resultId };
        try { await Storage.SetAsync(key, confirmed); _pending = confirmed; }
        catch (Exception ex) when (StorageFailure(ex))
        {
            _pending = intent;
            _message = T("recurring.savedRecovery", "작업은 완료됐지만 복구 기록을 갱신하지 못했습니다. 표시된 결과를 확인한 뒤 기록을 닫아 주세요.", "The action completed, but its recovery record could not be updated. Verify the displayed result, then close the record.");
            await Saved.InvokeAsync();
            return;
        }
        await Saved.InvokeAsync();
        try { await Storage.DeleteAsync(key); _pending = null; _acknowledged = false; }
        catch (Exception ex) when (StorageFailure(ex)) { _message = T("recurring.savedRecovery", "작업은 완료됐지만 복구 기록을 갱신하지 못했습니다. 표시된 결과를 확인한 뒤 기록을 닫아 주세요.", "The action completed, but its recovery record could not be updated. Verify the displayed result, then close the record."); }
    }
    private async Task HandleFailureAsync(int status, string? code, string? error)
    {
        if (status is >= 400 and < 500 && status is not 408 and not 429)
        {
            if (_storageKey is { } key) { try { await Storage.DeleteAsync(key); _pending = null; } catch (Exception ex) when (StorageFailure(ex)) { _storageError = true; } }
            _error = WriteError(status, code, error); return;
        }
        _error = T("recurring.unknownOutcome", "응답을 확정할 수 없습니다. 같은 요청 재시도로 안전하게 확인하세요.", "The outcome could not be confirmed. Retry the same request safely.");
    }
    private async Task ClearRecoveryAsync()
    {
        if (_storageKey is not { } key || _pending is null || _busy || !_pending.Confirmed && !_acknowledged) return;
        _busy = true; try { await Storage.DeleteAsync(key); _pending = null; _acknowledged = false; _error = null; } catch (Exception ex) when (StorageFailure(ex)) { _storageError = true; _error = RecoveryError(); } finally { _busy = false; }
    }

    private bool TryCreate(out RecurringController.RuleCreate command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(_name) || _name != _name.Trim() || _name.Length > 200 || !TryMonth(_startMonth, out var start)
            || _endMonth.Length > 0 && !TryMonth(_endMonth, out _) || !(_day is >= 1 and <= 31) || !ValidCurrency(_currency)) return false;
        DateOnly? end = null; if (_endMonth.Length > 0) { TryMonth(_endMonth, out var parsed); if (parsed < start) return false; end = parsed; }
        var schedule = new RecurringSchedule(start, end, _day); var operation = Guid.NewGuid();
        if (_target == RecurringTarget.Billing)
        {
            if (!Guid.TryParse(_contactId, out var billingContact) || billingContact == Guid.Empty || !Enum.TryParse<BillingKind>(_billingKind, out var kind) || _dueDays is < 0 or > 365) return false;
            var lines = new List<BillingLine>(); foreach (var line in _lines) { if (string.IsNullOrWhiteSpace(line.Description) || line.Description != line.Description.Trim() || line.Description.Length > 255 || !Amount(line.Price, false, out var price) || !Amount(line.Quantity, true, out var quantity)) return false; lines.Add(new(line.Description, price, quantity, line.Tax, line.Discount)); }
            command = new(operation, _name, schedule, _target, new(kind, billingContact, _dueDays, _currency, lines, Terms: Optional(_terms), Note: Optional(_notes))); return true;
        }
        if (!Amount(_amount, true, out var amount)) return false;
        if (_target == RecurringTarget.Income)
        {
            if (!Guid.TryParse(_contactId, out var incomeContact) || incomeContact == Guid.Empty || !OptionalId(_employeeId, out var employee)) return false;
            command = new(operation, _name, schedule, _target, Income: new(amount, incomeContact, employee, _currency, _isBonus, Optional(_reference), Optional(_notes))); return true;
        }
        if (!Guid.TryParse(_categoryId, out var category) || category == Guid.Empty || !Guid.TryParse(_vendorId, out var vendor) || vendor == Guid.Empty || !OptionalId(_employeeId, out var expenseEmployee)) return false;
        Guid? expenseContact = null; if (_expenseType == ExpenseType.BillableToContact && (!Guid.TryParse(_contactId, out var billableContact) || billableContact == Guid.Empty)) return false; else if (_expenseType == ExpenseType.BillableToContact) expenseContact = Guid.Parse(_contactId);
        if (_split && expenseEmployee.HasValue) return false;
        command = new(operation, _name, schedule, _target, Expense: new(amount, _expenseType, category, vendor, expenseEmployee, expenseContact, null, _currency, Optional(_purpose), Optional(_reference), Optional(_notes), Optional(_receipt), SplitAcrossEmployees: _split)); return true;
    }
    private static bool ValidCreate(RecurringController.RuleCreate value)
    {
        if (value.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(value.Name) || value.Name != value.Name.Trim() || value.Name.Length > 200 || value.Schedule is null || value.Schedule.StartMonth.Day != 1 || value.Schedule.DayOfMonth is < 1 or > 31 || value.Schedule.EndMonth is { Day: not 1 } || value.Schedule.EndMonth < value.Schedule.StartMonth) return false;
        return (value.Target, value.Billing, value.Income, value.Expense) switch { (RecurringTarget.Billing, not null, null, null) => true, (RecurringTarget.Income, null, not null, null) => true, (RecurringTarget.Expense, null, null, not null) => true, _ => false };
    }
    private static bool TryMonth(string value, out DateOnly month) => DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    private static bool Amount(string value, bool positive, out decimal amount) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && (positive ? amount > 0 : amount >= 0) && amount <= 1_000_000_000_000m && decimal.Round(amount, 6) == amount;
    private static bool ValidCurrency(string value) => value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z');
    private static bool OptionalId(string value, out Guid? id) { id = null; if (string.IsNullOrWhiteSpace(value)) return true; if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty) return false; id = parsed; return true; }
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool StorageFailure(Exception value) => value is JSException or InvalidOperationException or CryptographicException or JsonException;
    private string RecoveryError() => T("recurring.recoveryError", "탭 복구 저장소를 사용할 수 없습니다. 새 요청을 시작하지 않았습니다.", "The tab recovery store is unavailable. No new request was started.");
    private string WriteError(int status, string? code, string? error) => status switch { 401 => T("inventory.unauthorized", "인증이 만료되었습니다. 다시 로그인해 주세요.", "Your session expired. Sign in again."), 403 => T("inventory.forbidden", "현재 조회 권한이 없습니다. 범위를 새로고침해 접근 상태를 확인하세요.", "You no longer have access to this read. Refresh scopes to check access."), 404 => T("recurring.notFound", "규칙 또는 참조한 기준정보를 찾을 수 없습니다.", "The rule or referenced master data was not found."), 409 => T("recurring.conflict", "현재 규칙 상태와 요청이 충돌합니다. 목록을 새로고침해 주세요.", "The request conflicts with the current rule state. Refresh the list."), _ => error ?? code ?? T("recurring.writeFailed", "작업을 완료하지 못했습니다.", "The action could not be completed.") };
    private string ActionName(string kind) => kind switch { "create" => T("recurring.create", "규칙 만들기", "Create rule"), "deactivate" => T("recurring.deactivate", "규칙 비활성화", "Deactivate rule"), _ => T("recurring.execute", "월 발생 실행", "Run monthly occurrence") };
    private string TargetName(RecurringTarget value) => value switch { RecurringTarget.Billing => T("recurring.billing", "청구", "Billing"), RecurringTarget.Income => T("recurring.income", "수입", "Income"), _ => T("recurring.expense", "지출", "Expense") };
    private static string Period(RecurringSchedule value) => $"{value.StartMonth:yyyy-MM} – {(value.EndMonth.HasValue ? value.EndMonth.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "∞")}";
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);
    public void Dispose() { _disposed = true; ++_generation; Ui.Changed -= LanguageChanged; _request?.Cancel(); _request?.Dispose(); }
}
