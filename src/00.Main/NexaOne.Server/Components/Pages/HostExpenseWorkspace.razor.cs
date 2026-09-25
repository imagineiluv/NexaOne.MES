using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Components.Pages;

public partial class HostExpenseWorkspace
{
    private const int PageSize = 50;
    private BusinessPage<BusinessMembership>? _scopes;
    private BusinessPage<ExpenseCategory>? _categories;
    private BusinessPage<ExpenseVendor>? _vendors;
    private BusinessPage<ExpenseRecord>? _expenses;
    private BusinessPage<BillingDocument>? _invoiceCandidates;
    private BusinessMembership? _scope;
    private ExpenseRecord? _selected;
    private BillingDocument? _linkedInvoice;
    private PendingCreate? _pendingCreate;
    private PendingReimbursement? _pendingReimbursement;
    private PendingInvoiceOperation? _pendingInvoice;
    private readonly ExpenseDraft _draft = new();
    private string _categoryName = "", _vendorName = "", _invoiceDescription = "";
    private string? _userId, _error, _notice;
    private Guid _invoiceId;
    private int _invoiceOffset;
    private bool _authenticated, _identityLoading = true, _busy, _interactive, _disposed;
    private Task<AuthenticationState>? _pendingAuthentication;
    private CancellationTokenSource? _request;
    private int _identityVersion;

    private bool CreateLocked => _busy || _pendingCreate is not null;
    private bool CanMarkInvoiced => Can("expense.write") && _selected is
        { State: ExpenseState.Active, Input.Type: ExpenseType.BillableToContact, Status: ExpenseStatus.Uninvoiced };
    private bool CanMarkPaid => Can("expense.write") && ReadyToSettle(_selected);
    private bool CanReimburse => Can("expense.reimburse") && ReadyToSettle(_selected)
        && _selected is { Input.EmployeeId: not null } && _selected.Allocations.Count == 0;
    private bool CanCancel => Can("expense.write") && _selected is { State: ExpenseState.Active } value
        && value.Status == InitialStatus(value.Input.Type);
    private bool CanManageInvoice => Can("expense.invoice") && Can("billing.read");
    private bool CanBrowseInvoices => CanManageInvoice && _selected is
        { State: ExpenseState.Active, Input.Type: ExpenseType.BillableToContact,
            Status: ExpenseStatus.Uninvoiced, InvoiceId: null };
    private bool CanUnlinkInvoice => CanManageInvoice && _selected is
        { State: ExpenseState.Active, Status: ExpenseStatus.Invoiced, InvoiceId: not null }
        && _linkedInvoice is { Status: BillingStatus.Draft } invoice && invoice.Id == _selected.InvoiceId;
    private IReadOnlyList<BillingDocument> CompatibleInvoices => _invoiceCandidates?.Items
        .Where(CompatibleInvoice).ToArray() ?? [];
    private bool HasPreviousInvoicePage => _invoiceOffset > 0;
    private bool HasNextInvoicePage => _invoiceCandidates is { } page
        && _invoiceOffset + page.Items.Count < page.Total;
    private string? SelectedKey => _scope is null ? null : ScopeKey(_scope);
    private string Root => $"api/v1/erp/expenses/{SelectedKey}";

    protected override void OnInitialized()
    {
        Authentication.AuthenticationStateChanged += AuthenticationChanged;
        Ui.Changed += LanguageChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed) return;
        _interactive = true;
        await ChangeIdentityAsync(_pendingAuthentication ?? Authentication.GetAuthenticationStateAsync());
        _pendingAuthentication = null;
    }

    private void AuthenticationChanged(Task<AuthenticationState> state)
    {
        if (!_interactive) { _pendingAuthentication = state; return; }
        if (!_disposed) _ = InvokeAsync(() => ChangeIdentityAsync(state));
    }

    private void LanguageChanged() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }

    private async Task ChangeIdentityAsync(Task<AuthenticationState> state)
    {
        var version = ++_identityVersion;
        Clear(); _identityLoading = true; StateHasChanged();
        var auth = await state;
        if (_disposed || version != _identityVersion) return;
        _authenticated = auth.User.Identity?.IsAuthenticated == true;
        _userId = _authenticated ? auth.User.CurrentUserId() : null;
        _identityLoading = false;
        if (_authenticated) await LoadScopesAsync();
        if (!_disposed && version == _identityVersion) StateHasChanged();
    }

    private void Clear()
    {
        CancelRequest();
        _scopes = null; _scope = null; _categories = null; _vendors = null; _expenses = null; _selected = null;
        _invoiceCandidates = null; _linkedInvoice = null;
        _pendingCreate = null; _pendingReimbursement = null; _pendingInvoice = null;
        _invoiceId = Guid.Empty; _invoiceOffset = 0; _invoiceDescription = ""; _error = null; _notice = null;
    }

    private Task LoadScopesAsync() => RunAsync(async ct =>
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<BusinessMembership>>(
            $"api/v1/erp/expenses/scopes/me?offset=0&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(result, value => _scopes = value);
    });

    private async Task ReloadScopesAsync() { Clear(); await LoadScopesAsync(); }

    private async Task SelectScopeAsync(BusinessMembership scope)
    {
        if (SelectedKey == ScopeKey(scope)) return;
        CancelRequest();
        _scope = scope; _categories = null; _vendors = null; _expenses = null; _selected = null;
        _invoiceCandidates = null; _linkedInvoice = null;
        _pendingCreate = null; _pendingReimbursement = null; _pendingInvoice = null;
        _invoiceId = Guid.Empty; _invoiceOffset = 0; _invoiceDescription = ""; _error = null; _notice = null;
        await RunAsync(async ct =>
        {
            if (Can("expense.directory.read")) await LoadDirectoriesCoreAsync(ct);
            if (OwnsRequest(ct) && Can("expense.read")) await LoadExpensesCoreAsync(ct);
        });
    }

    private Task LoadDirectoriesAsync() => RunAsync(LoadDirectoriesCoreAsync);
    private async Task LoadDirectoriesCoreAsync(CancellationToken ct)
    {
        var categories = await Api.ReadInventoryAsync<BusinessPage<ExpenseCategory>>($"{Root}/categories?offset=0&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(categories, value => _categories = value);
        var vendors = await Api.ReadInventoryAsync<BusinessPage<ExpenseVendor>>($"{Root}/vendors?offset=0&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(vendors, value => _vendors = value);
    }

    private Task LoadExpensesAsync() => RunAsync(async ct =>
    {
        await LoadExpensesCoreAsync(ct);
        if (OwnsRequest(ct) && _selected is not null) await LoadInvoiceContextCoreAsync(ct);
    });
    private async Task LoadExpensesCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<ExpenseRecord>>($"{Root}?offset=0&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(result, value => _expenses = value);
        if (_selected is not null && _expenses?.Items.FirstOrDefault(value => value.Id == _selected.Id) is { } refreshed)
            _selected = refreshed;
    }

    private Task CreateCategoryAsync() => CreateDirectoryAsync("categories", new ExpenseCategoryInput(_categoryName.Trim()),
        () => _categoryName = "");
    private Task CreateVendorAsync() => CreateDirectoryAsync("vendors", new ExpenseVendorInput(_vendorName.Trim()),
        () => _vendorName = "");

    private Task CreateDirectoryAsync(string segment, object body, Action clear) => RunAsync(async ct =>
    {
        if (_userId is null) return;
        var name = segment == "categories" ? _categoryName : _vendorName;
        if (string.IsNullOrWhiteSpace(name)) { _error = T("expenseWorkspace.nameRequired", "이름을 입력하세요.", "Enter a name."); return; }
        var saved = false;
        if (segment == "categories")
        {
            var result = await WriteAsync<ExpenseCategory>(HttpMethod.Post, $"{Root}/{segment}", body, ct);
            if (!OwnsRequest(ct)) return;
            saved = result.Value is not null && result.Error is null;
            if (!saved) Accept(result, _ => { });
        }
        else
        {
            var result = await WriteAsync<ExpenseVendor>(HttpMethod.Post, $"{Root}/{segment}", body, ct);
            if (!OwnsRequest(ct)) return;
            saved = result.Value is not null && result.Error is null;
            if (!saved) Accept(result, _ => { });
        }
        if (saved)
        {
            clear(); _notice = T("expenseWorkspace.directorySaved", "기준정보를 추가했습니다.", "Directory entry added.");
            await LoadDirectoriesCoreAsync(ct);
        }
    });

    private async Task CreateExpenseAsync()
    {
        if (_scope is null || _userId is null) return;
        if (!TryInput(out var input)) return;
        _pendingCreate ??= new(Guid.NewGuid(), input);
        var pending = _pendingCreate;
        await RunAsync(async ct =>
        {
            var result = await WriteAsync<ExpenseRecord>(HttpMethod.Post, Root,
                new ExpenseCreateRequest(pending.OperationId, pending.Input), ct);
            if (!OwnsRequest(ct)) return;
            if (Matches(pending, result.Value) && result.Error is null)
            {
                _pendingCreate = null; _selected = result.Value; _draft.Reset();
                _notice = T("expenseWorkspace.created", "비용을 등록했습니다.", "Expense recorded.");
            }
            else if (result.Value is not null)
                _error = T("expenseWorkspace.invalidResponse", "서버 응답이 현재 요청과 일치하지 않습니다. 원장을 새로고침하세요.", "The server response does not match this request. Refresh the ledger.");
            else
            {
                Accept(result, _ => { });
                if (result.StatusCode is >= 400 and < 500) _pendingCreate = null;
            }
            if (Can("expense.read")) await LoadExpensesCoreAsync(ct);
            if (OwnsRequest(ct) && _pendingCreate is not null
                && _expenses?.Items.FirstOrDefault(item => Matches(pending, item)) is { } recovered)
            {
                _pendingCreate = null; _selected = recovered; _draft.Reset(); _error = null;
                _notice = T("expenseWorkspace.createdRecovered", "원장에서 저장 결과를 확인했습니다.", "The stored result was recovered from the ledger.");
            }
        });
    }

    private bool TryInput(out ExpenseInput input)
    {
        input = null!;
        if (_draft.Amount <= 0 || _draft.CategoryId == Guid.Empty || _draft.VendorId == Guid.Empty
            || _draft.Currency.Trim().Length != 3)
        { _error = T("expenseWorkspace.invalidInput", "양수 금액, 3자리 통화, 카테고리와 거래처를 확인하세요.", "Check the positive amount, three-letter currency, category, and vendor."); return false; }
        Guid? employee = null;
        var employeeId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(_draft.Employee)
            && (!Guid.TryParse(_draft.Employee, out employeeId) || employeeId == Guid.Empty))
        { _error = T("expenseWorkspace.employeeInvalid", "직원 ID는 비워 두거나 유효한 GUID여야 합니다.", "Employee ID must be empty or a valid GUID."); return false; }
        else if (!string.IsNullOrWhiteSpace(_draft.Employee)) employee = employeeId;
        Guid? contact = null;
        var parsed = Guid.Empty;
        if (_draft.Type == ExpenseType.BillableToContact
            && (!Guid.TryParse(_draft.Contact, out parsed) || parsed == Guid.Empty))
        { _error = T("expenseWorkspace.contactRequired", "고객 청구 비용에는 유효한 고객 연락처 ID가 필요합니다.", "A valid customer contact ID is required for a billable expense."); return false; }
        else if (_draft.Type == ExpenseType.BillableToContact) contact = parsed;
        input = new(_draft.Amount, _draft.Type, _draft.CategoryId, _draft.VendorId, employee, contact, null,
            _draft.Currency.Trim().ToUpperInvariant(), _draft.ValueDate, Text(_draft.Purpose), Receipt: Text(_draft.Receipt));
        return true;
    }

    private Task SelectExpenseAsync(ExpenseRecord value) => RunAsync(async ct =>
    {
        _selected = value; _pendingReimbursement = null; _pendingInvoice = null;
        _invoiceCandidates = null; _linkedInvoice = null; _invoiceId = Guid.Empty; _invoiceOffset = 0;
        _invoiceDescription = value.Input.Purpose ?? value.Input.Reference ?? "";
        await LoadInvoiceContextCoreAsync(ct);
    });

    private async Task LoadInvoiceContextCoreAsync(CancellationToken ct)
    {
        _invoiceCandidates = null; _linkedInvoice = null; _invoiceId = Guid.Empty; _invoiceOffset = 0;
        if (_selected?.InvoiceId is { } invoiceId && Can("billing.read"))
        {
            var result = await Api.ReadInventoryAsync<BillingDocument>(
                $"api/v1/erp/billing/{SelectedKey}/documents/{invoiceId:D}", ct);
            if (!OwnsRequest(ct)) return;
            if (result.Value?.Id == invoiceId && result.Error is null) _linkedInvoice = result.Value;
            else if (result.Value is not null)
                _error = T("expenseWorkspace.invoiceInvalidResponse", "청구서 연결 응답이 현재 요청과 일치하지 않습니다. 비용과 청구서를 새로고침하세요.", "The invoice-link response does not match this request. Refresh the expense and invoice.");
            else Accept(result, _ => { });
        }
        else if (CanBrowseInvoices)
            await LoadInvoiceCandidatesCoreAsync(ct);
    }

    private Task LoadInvoiceCandidatesAsync(int offset) => RunAsync(async ct =>
    {
        _invoiceOffset = Math.Max(0, offset); _invoiceId = Guid.Empty;
        await LoadInvoiceCandidatesCoreAsync(ct);
    });

    private async Task LoadInvoiceCandidatesCoreAsync(CancellationToken ct)
    {
        if (!CanBrowseInvoices || _selected?.Input.ContactId is not { } contactId) return;
        var result = await Api.ReadInventoryAsync<BusinessPage<BillingDocument>>(
            $"api/v1/erp/billing/{SelectedKey}/documents?kind=Invoice&status=Draft&contactId={contactId:D}&offset={_invoiceOffset}&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(result, value =>
        {
            _invoiceCandidates = value;
            _invoiceId = value.Items.FirstOrDefault(CompatibleInvoice)?.Id ?? Guid.Empty;
        });
    }

    private async Task LinkInvoiceAsync()
    {
        if (!CanBrowseInvoices || _selected is null) return;
        var invoice = _invoiceCandidates?.Items.FirstOrDefault(value => value.Id == _invoiceId && CompatibleInvoice(value));
        if (_pendingInvoice is null && invoice is null)
        {
            _error = T("expenseWorkspace.chooseInvoiceRequired", "연결할 호환 청구서를 선택하세요.", "Choose a compatible invoice to link.");
            return;
        }
        _pendingInvoice ??= new(InvoiceOperation.Link, Guid.NewGuid(), _selected.Id, _selected.Version,
            invoice!.Id, invoice.Version, Text(_invoiceDescription));
        await ExecuteInvoiceOperationAsync(_pendingInvoice);
    }

    private async Task UnlinkInvoiceAsync()
    {
        if (_selected is null || _linkedInvoice is null || (!CanUnlinkInvoice && _pendingInvoice is null)) return;
        _pendingInvoice ??= new(InvoiceOperation.Unlink, Guid.NewGuid(), _selected.Id, _selected.Version,
            _linkedInvoice.Id, _linkedInvoice.Version, null);
        await ExecuteInvoiceOperationAsync(_pendingInvoice);
    }

    private Task ExecuteInvoiceOperationAsync(PendingInvoiceOperation pending) => RunAsync(async ct =>
    {
        var action = pending.Kind == InvoiceOperation.Link ? "invoice-link" : "invoice-unlink";
        object body = pending.Kind == InvoiceOperation.Link
            ? new InvoiceLinkRequest(pending.OperationId, pending.ExpenseVersion, pending.InvoiceId,
                pending.InvoiceVersion, pending.Description)
            : new InvoiceUnlinkRequest(pending.OperationId, pending.ExpenseVersion, pending.InvoiceId,
                pending.InvoiceVersion);
        var result = await WriteAsync<ExpenseInvoiceLink>(HttpMethod.Post,
            $"{Root}/{pending.ExpenseId:D}/{action}", body, ct);
        if (!OwnsRequest(ct)) return;
        if (Matches(pending, result.Value) && result.Error is null)
        {
            CompleteInvoiceOperation(pending, result.Value!);
            return;
        }
        if (result.Value is not null)
            _error = T("expenseWorkspace.invoiceInvalidResponse", "청구서 연결 응답이 현재 요청과 일치하지 않습니다. 비용과 청구서를 새로고침하세요.", "The invoice-link response does not match this request. Refresh the expense and invoice.");
        else
        {
            Accept(result, _ => { });
            if (result.StatusCode is >= 400 and < 500) _pendingInvoice = null;
            else await RecoverInvoiceOperationCoreAsync(pending, ct);
        }
    });

    private async Task RecoverInvoiceOperationCoreAsync(PendingInvoiceOperation pending, CancellationToken ct)
    {
        var expenseResult = await Api.ReadInventoryAsync<ExpenseRecord>($"{Root}/{pending.ExpenseId:D}", ct);
        if (!OwnsRequest(ct) || !MatchesExpense(pending, expenseResult.Value)) return;
        var invoiceResult = await Api.ReadInventoryAsync<BillingDocument>(
            $"api/v1/erp/billing/{SelectedKey}/documents/{pending.InvoiceId:D}", ct);
        if (OwnsRequest(ct) && Matches(pending, expenseResult.Value, invoiceResult.Value))
            CompleteInvoiceOperation(pending, new(expenseResult.Value!, invoiceResult.Value!));
    }

    private void CompleteInvoiceOperation(PendingInvoiceOperation pending, ExpenseInvoiceLink value)
    {
        _pendingInvoice = null; _selected = value.Expense; ApplyExpense(value.Expense);
        _invoiceCandidates = null; _invoiceId = Guid.Empty; _invoiceOffset = 0;
        _linkedInvoice = pending.Kind == InvoiceOperation.Link ? value.Invoice : null;
        _error = null; _notice = pending.Kind == InvoiceOperation.Link
            ? T("expenseWorkspace.invoiceLinked", "비용을 청구서에 연결했습니다.", "Expense linked to invoice.")
            : T("expenseWorkspace.invoiceUnlinked", "비용과 청구서 연결을 해제했습니다.", "Expense unlinked from invoice.");
    }

    private void ApplyExpense(ExpenseRecord value)
    {
        if (_expenses is not { } page) return;
        _expenses = new(page.Items.Select(item => item.Id == value.Id ? value : item).ToArray(), page.Total);
    }

    private Task UpdateStatusAsync(string action)
    {
        if (action == "invoiced" ? !CanMarkInvoiced : action != "paid" || !CanMarkPaid)
            return Task.CompletedTask;
        return MutateSelectedAsync(action, new VersionedRequest(_selected!.Version),
            T("expenseWorkspace.updated", "비용 상태를 갱신했습니다.", "Expense status updated."));
    }

    private async Task ReimburseAsync()
    {
        if (!CanReimburse || _selected is null) return;
        _pendingReimbursement ??= new(Guid.NewGuid(), _selected.Id, _selected.Version, DateTimeOffset.UtcNow);
        var pending = _pendingReimbursement;
        await RunAsync(async ct =>
        {
            var result = await WriteAsync<ExpenseRecord>(HttpMethod.Post, $"{Root}/{pending.ExpenseId:D}/reimbursement",
                new ReimbursementRequest(pending.OperationId, pending.Version, pending.PaidAt, null), ct);
            if (!OwnsRequest(ct)) return;
            if (result.Value?.Id == pending.ExpenseId && result.Value.Reimbursement?.OperationId == pending.OperationId && result.Error is null)
            { _pendingReimbursement = null; _selected = result.Value; _notice = T("expenseWorkspace.reimbursementSaved", "환급 기록을 저장했습니다.", "Reimbursement recorded."); }
            else
            {
                Accept(result, _ => { });
                if (result.StatusCode is >= 400 and < 500) _pendingReimbursement = null;
            }
            await LoadExpensesCoreAsync(ct);
            if (OwnsRequest(ct) && _pendingReimbursement is not null && _selected?.Reimbursement?.OperationId == pending.OperationId)
            { _pendingReimbursement = null; _error = null; _notice = T("expenseWorkspace.reimbursementRecovered", "원장에서 환급 결과를 확인했습니다.", "The reimbursement was recovered from the ledger."); }
        });
    }

    private Task CancelExpenseAsync() => !CanCancel ? Task.CompletedTask : MutateSelectedAsync("cancel",
        new CancelExpenseRequest(_selected!.Version, T("expenseWorkspace.cancelledInWorkspace", "비용 작업 공간에서 취소", "Cancelled in expense workspace")),
        T("expenseWorkspace.cancelled", "비용을 취소했습니다.", "Expense cancelled."));

    private Task MutateSelectedAsync(string action, object body, string notice) => RunAsync(async ct =>
    {
        if (_selected is null) return;
        var id = _selected.Id;
        var result = await WriteAsync<ExpenseRecord>(HttpMethod.Post, $"{Root}/{id:D}/{action}", body, ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value?.Id == id && result.Error is null) { _selected = result.Value; _notice = notice; }
        else Accept(result, _ => { });
        await LoadExpensesCoreAsync(ct);
    });

    private Task<(T? Value, int StatusCode, string? Code, string? Error)> WriteAsync<T>(
        HttpMethod method, string path, object body, CancellationToken ct) where T : class
        => Api.WriteInventoryAsync<T>(method, path, body, _userId!, ct);

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        CancelRequest();
        var request = new CancellationTokenSource(); _request = request; _busy = true; _error = null; _notice = null;
        try { await action(request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_request, request)) { _request = null; _busy = false; } request.Dispose(); }
    }

    private bool OwnsRequest(CancellationToken token) => !_disposed && _request?.Token == token;
    private void CancelRequest() { if (_request is null) return; var request = _request; _request = null; _busy = false; request.Cancel(); }
    private void Accept<T>((T? Value, int StatusCode, string? Code, string? Error) result, Action<T> apply) where T : class
    { if (result.Value is not null && result.Error is null) apply(result.Value); else _error = result.Error ?? result.Code ?? string.Format(CultureInfo.InvariantCulture, "HTTP {0}", result.StatusCode); }
    private bool Can(string permission) => _scope?.Permissions.Contains(permission, StringComparer.Ordinal) == true;
    // The service owns input normalization (for example null tag lists become empty lists). The scoped,
    // caller-supplied operation ID is the durable create/replay identity and is therefore the recovery key.
    private static bool Matches(PendingCreate pending, ExpenseRecord? value) => value?.OperationId == pending.OperationId;
    private bool CompatibleInvoice(BillingDocument value) => _selected is { Input.ContactId: { } contactId } expense
        && value.Kind == BillingKind.Invoice && value.Status == BillingStatus.Draft
        && value.Input.ContactId == contactId
        && string.Equals(value.Input.Currency, expense.Input.Currency, StringComparison.Ordinal);
    private static bool Matches(PendingInvoiceOperation pending, ExpenseInvoiceLink? value)
        => value is not null && Matches(pending, value.Expense, value.Invoice);
    private static bool Matches(PendingInvoiceOperation pending, ExpenseRecord? expense, BillingDocument? invoice)
        => MatchesExpense(pending, expense) && invoice is { Status: BillingStatus.Draft }
            && invoice.Id == pending.InvoiceId
            && (pending.Kind == InvoiceOperation.Link
                ? invoice.Input.Lines.Count(line => line.ExpenseId == pending.ExpenseId) == 1
                : invoice.Input.Lines.All(line => line.ExpenseId != pending.ExpenseId));
    private static bool MatchesExpense(PendingInvoiceOperation pending, ExpenseRecord? expense)
        => expense is { State: ExpenseState.Active } && expense.Id == pending.ExpenseId
            && (pending.Kind == InvoiceOperation.Link
                ? expense.Status == ExpenseStatus.Invoiced && expense.InvoiceId == pending.InvoiceId
                    && expense.InvoiceOperationId == pending.OperationId
                : expense.Status == ExpenseStatus.Uninvoiced && expense.InvoiceId is null
                    && expense.UnlinkedInvoiceId == pending.InvoiceId
                    && expense.InvoiceUnlinkOperationId == pending.OperationId);
    private static string ScopeKey(BusinessMembership scope) => $"{scope.TenantId:D}/{scope.OrganizationId:D}";
    private static ExpenseStatus InitialStatus(ExpenseType type)
        => type == ExpenseType.BillableToContact ? ExpenseStatus.Uninvoiced : ExpenseStatus.NotBillable;
    private static bool ReadyToSettle(ExpenseRecord? value) => value is { State: ExpenseState.Active }
        && value.Status == (value.Input.Type == ExpenseType.BillableToContact
            ? ExpenseStatus.Invoiced : ExpenseStatus.NotBillable);
    private static string Short(Guid value) => value.ToString("N")[..8];
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Amount(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);
    private string TypeName(ExpenseType type) => type switch { ExpenseType.TaxDeductible => T("expenseWorkspace.taxDeductible", "세무상 공제", "Tax deductible"), ExpenseType.NotTaxDeductible => T("expenseWorkspace.notTaxDeductible", "공제 불가", "Not tax deductible"), _ => T("expenseWorkspace.billable", "고객 청구", "Billable to contact") };
    private string StatusName(ExpenseRecord value) => value.State == ExpenseState.Cancelled ? T("expenseWorkspace.cancelledState", "취소됨", "Cancelled") : value.Status switch { ExpenseStatus.Uninvoiced => T("expenseWorkspace.uninvoiced", "미청구", "Uninvoiced"), ExpenseStatus.Invoiced => T("expenseWorkspace.invoiced", "청구됨", "Invoiced"), ExpenseStatus.Paid => T("expenseWorkspace.paid", "지급됨", "Paid"), _ => T("expenseWorkspace.notBillable", "청구 대상 아님", "Not billable") };
    private string BillingStatusName(BillingStatus value) => value switch
    {
        BillingStatus.Draft => T("billing.draft", "작성 중", "Draft"),
        BillingStatus.Sent => T("billing.sent", "전송됨", "Sent"),
        BillingStatus.PartiallyPaid => T("billing.partiallyPaid", "일부 입금", "Partially paid"),
        BillingStatus.FullyPaid => T("billing.fullyPaid", "완납", "Fully paid"),
        BillingStatus.Overpaid => T("billing.overpaid", "초과 입금", "Overpaid"),
        BillingStatus.Void => T("billing.void", "무효", "Void"),
        BillingStatus.PartiallyCredited => T("billing.partiallyCredited", "일부 차감", "Partially credited"),
        BillingStatus.Credited => T("billing.creditedStatus", "전액 차감", "Credited"),
        _ => value.ToString()
    };

    public void Dispose()
    {
        _disposed = true; ++_identityVersion; CancelRequest();
        Authentication.AuthenticationStateChanged -= AuthenticationChanged; Ui.Changed -= LanguageChanged;
    }

    internal sealed record ExpenseCreateRequest(Guid OperationId, ExpenseInput Input);
    internal sealed record VersionedRequest(Guid Version);
    internal sealed record CancelExpenseRequest(Guid Version, string? Reason);
    internal sealed record ReimbursementRequest(Guid OperationId, Guid Version, DateTimeOffset PaidAt, string? Reference);
    internal sealed record InvoiceLinkRequest(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion, string? Description);
    internal sealed record InvoiceUnlinkRequest(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion);
    private sealed record PendingCreate(Guid OperationId, ExpenseInput Input);
    private sealed record PendingReimbursement(Guid OperationId, Guid ExpenseId, Guid Version, DateTimeOffset PaidAt);
    private sealed record PendingInvoiceOperation(InvoiceOperation Kind, Guid OperationId, Guid ExpenseId,
        Guid ExpenseVersion, Guid InvoiceId, Guid InvoiceVersion, string? Description);
    private enum InvoiceOperation { Link, Unlink }
    private sealed class ExpenseDraft
    {
        public decimal Amount { get; set; }
        public ExpenseType Type { get; set; }
        public Guid CategoryId { get; set; }
        public Guid VendorId { get; set; }
        public string Currency { get; set; } = "KRW";
        public DateOnly ValueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        public string Contact { get; set; } = "";
        public string Employee { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Receipt { get; set; } = "";
        public void Reset() { Amount = 0; Type = default; CategoryId = Guid.Empty; VendorId = Guid.Empty; Currency = "KRW"; ValueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date); Contact = Employee = Purpose = Receipt = ""; }
    }
}
