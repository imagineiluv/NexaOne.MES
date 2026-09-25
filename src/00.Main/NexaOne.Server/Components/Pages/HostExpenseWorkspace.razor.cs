using System.Globalization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaFramework.Service.Projects;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Components.Pages;

public partial class HostExpenseWorkspace
{
    private const int PageSize = 50;
    private const long MaxReceiptBytes = 10 * 1024 * 1024;
    private BusinessPage<BusinessMembership>? _scopes;
    private BusinessPage<ExpenseCategory>? _categories;
    private BusinessPage<ExpenseVendor>? _vendors;
    private BusinessPage<ExpenseTag>? _tags;
    private BusinessPage<ExpenseCategory>? _directoryCategories;
    private BusinessPage<ExpenseVendor>? _directoryVendors;
    private BusinessPage<ExpenseTag>? _directoryTags;
    private BusinessPage<ExpenseEmployee>? _employees;
    private BusinessPage<BillingContact>? _contacts;
    private WorkPage<Project>? _projects;
    private BusinessPage<ExpenseRecord>? _expenses;
    private BusinessPage<BillingDocument>? _invoiceCandidates;
    private BusinessMembership? _scope;
    private ExpenseRecord? _selected;
    private ExpenseReceipt? _receipt;
    private IBrowserFile? _receiptFile;
    private int _receiptInputKey;
    private ExpenseCategory? _selectedCategory;
    private ExpenseVendor? _selectedVendor;
    private ExpenseTag? _selectedTag;
    private BillingDocument? _linkedInvoice;
    private PendingCreate? _pendingCreate;
    private PendingReimbursement? _pendingReimbursement;
    private PendingInvoiceOperation? _pendingInvoice;
    private readonly ExpenseDraft _draft = new();
    private string _categoryName = "", _vendorName = "", _tagName = "", _invoiceDescription = "";
    private string _categoryEditName = "", _vendorEditName = "", _vendorEditPhone = "";
    private string _vendorEditWebsite = "", _vendorEditEmail = "", _tagEditName = "";
    private string _ledgerStart = "", _ledgerEnd = "", _ledgerCategory = "", _ledgerVendor = "";
    private string _ledgerType = "", _ledgerStatus = "", _ledgerState = "";
    private string? _userId, _error, _notice;
    private string? _ledgerValidation;
    private Guid _invoiceId;
    private int _invoiceOffset, _expenseOffset, _employeeOffset, _contactOffset, _projectOffset;
    private int _tagOffset, _directoryTagOffset;
    private LedgerFilter _ledgerFilter = new();
    private bool _authenticated, _identityLoading = true, _busy, _interactive, _disposed, _confirmReceiptDelete;
    private Task<AuthenticationState>? _pendingAuthentication;
    private CancellationTokenSource? _request;
    private int _identityVersion;

    private bool CreateLocked => _busy || _pendingCreate is not null;
    private bool LedgerLocked => _busy || _pendingInvoice is not null || _pendingReimbursement is not null;
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
    private bool HasPreviousExpensePage => _expenseOffset > 0;
    private bool HasNextExpensePage => _expenses is { } page && (long)_expenseOffset + PageSize < page.Total
        && _expenseOffset <= int.MaxValue - PageSize;
    private bool HasPreviousEmployeePage => _employeeOffset > 0;
    private bool HasNextEmployeePage => _employees is { } page
        && (long)_employeeOffset + page.Items.Count < page.Total;
    private bool HasPreviousContactPage => _contactOffset > 0;
    private bool HasNextContactPage => _contacts is { } page
        && (long)_contactOffset + page.Items.Count < page.Total;
    private bool HasPreviousProjectPage => _projectOffset > 0;
    private bool HasNextProjectPage => _projects is { } page
        && (long)_projectOffset + page.Items.Count < page.Total;
    private bool HasPreviousTagPage => _tagOffset > 0;
    private bool HasNextTagPage => _tags is { } page
        && (long)_tagOffset + page.Items.Count < page.Total;
    private bool HasPreviousDirectoryTagPage => _directoryTagOffset > 0;
    private bool HasNextDirectoryTagPage => _directoryTags is { } page
        && (long)_directoryTagOffset + page.Items.Count < page.Total;
    private bool HasLedgerFilters => _ledgerFilter != new LedgerFilter();
    private bool HasLedgerDraft => _ledgerStart.Length > 0 || _ledgerEnd.Length > 0
        || _ledgerCategory.Length > 0 || _ledgerVendor.Length > 0 || _ledgerType.Length > 0
        || _ledgerStatus.Length > 0 || _ledgerState.Length > 0;
    private IReadOnlyList<ExpenseCategory> ActiveCategories => _categories?.Items.Where(value => value.Active).ToArray() ?? [];
    private IReadOnlyList<ExpenseVendor> ActiveVendors => _vendors?.Items.Where(value => value.Active).ToArray() ?? [];
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
        _scopes = null; _scope = null; _categories = null; _vendors = null; _tags = null;
        ResetReferenceChoices();
        ResetLedgerState();
        _directoryCategories = null; _directoryVendors = null; _directoryTags = null;
        ClearDirectorySelection();
        _pendingCreate = null;
        _error = null; _notice = null;
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
        _scope = scope; _categories = null; _vendors = null; _tags = null;
        ResetReferenceChoices();
        ResetLedgerState();
        _directoryCategories = null; _directoryVendors = null; _directoryTags = null;
        ClearDirectorySelection();
        _pendingCreate = null;
        _error = null; _notice = null;
        await RunAsync(async ct =>
        {
            if (Can("expense.directory.read")) await LoadDirectoriesCoreAsync(ct);
            if (OwnsRequest(ct) && Can("expense.write")) await LoadReferenceChoicesCoreAsync(ct);
            if (OwnsRequest(ct) && Can("expense.read")) await LoadExpensesCoreAsync(ct);
        });
    }

    private void ResetReferenceChoices()
    {
        _employees = null; _contacts = null; _projects = null;
        _employeeOffset = _contactOffset = _projectOffset = _tagOffset = _directoryTagOffset = 0;
        _draft.EmployeeId = _draft.ContactId = _draft.ProjectId = Guid.Empty;
        _draft.TagIds.Clear();
    }

    private async Task LoadReferenceChoicesCoreAsync(CancellationToken ct)
    {
        await LoadEmployeesCoreAsync(ct);
        if (OwnsRequest(ct) && Can("billing.read")) await LoadContactsCoreAsync(ct);
        if (OwnsRequest(ct) && Can("crm.project.read")) await LoadProjectsCoreAsync(ct);
    }

    private Task MoveEmployeePageAsync(int direction) => RunAsync(async ct =>
    {
        _employeeOffset = NextOffset(_employeeOffset, direction); _draft.EmployeeId = Guid.Empty;
        await LoadEmployeesCoreAsync(ct);
    });

    private async Task LoadEmployeesCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<ExpenseEmployee>>(
            $"{Root}/employees?offset={_employeeOffset}&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(result, value => _employees = value);
    }

    private Task MoveContactPageAsync(int direction) => RunAsync(async ct =>
    {
        _contactOffset = NextOffset(_contactOffset, direction); _draft.ContactId = Guid.Empty;
        await LoadContactsCoreAsync(ct);
    });

    private async Task LoadContactsCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<BillingContact>>(
            $"api/v1/erp/billing/{SelectedKey}/contacts?offset={_contactOffset}&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(result, value => _contacts = value);
    }

    private Task MoveProjectPageAsync(int direction) => RunAsync(async ct =>
    {
        _projectOffset = NextOffset(_projectOffset, direction); _draft.ProjectId = Guid.Empty;
        await LoadProjectsCoreAsync(ct);
    });

    private async Task LoadProjectsCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<WorkPage<Project>>(
            $"api/v1/crm/{SelectedKey}/projects?offset={_projectOffset}&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(result, value => _projects = value);
    }

    private Task MoveTagPageAsync(int direction) => RunAsync(async ct =>
    {
        _tagOffset = NextOffset(_tagOffset, direction);
        await LoadTagsCoreAsync(ct);
    });

    private async Task LoadTagsCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<ExpenseTag>>(
            $"{Root}/tags?offset={_tagOffset}&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is { } page && page.Items.Count == 0 && page.Total > 0 && _tagOffset >= page.Total)
        {
            _tagOffset = (int)((page.Total - 1) / PageSize * PageSize);
            result = await Api.ReadInventoryAsync<BusinessPage<ExpenseTag>>(
                $"{Root}/tags?offset={_tagOffset}&limit={PageSize}", ct);
            if (!OwnsRequest(ct)) return;
        }
        Accept(result, value => _tags = value);
    }

    private Task MoveDirectoryTagPageAsync(int direction) => RunAsync(async ct =>
    {
        _directoryTagOffset = NextOffset(_directoryTagOffset, direction);
        await LoadDirectoryTagsCoreAsync(ct);
    });

    private async Task LoadDirectoryTagsCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<ExpenseTag>>(
            $"{Root}/tags?includeInactive=true&offset={_directoryTagOffset}&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(result, value =>
        {
            _directoryTags = value;
            if (_selectedTag is not null)
                SetSelectedTag(value.Items.FirstOrDefault(item => item.Id == _selectedTag.Id));
        });
    }

    private static int NextOffset(int current, int direction)
        => direction < 0 ? Math.Max(0, current - PageSize)
            : current <= int.MaxValue - PageSize ? current + PageSize : current;

    private Task LoadDirectoriesAsync() => RunAsync(LoadDirectoriesCoreAsync);
    private async Task LoadDirectoriesCoreAsync(CancellationToken ct)
    {
        var categories = await Api.ReadInventoryAsync<BusinessPage<ExpenseCategory>>(
            $"{Root}/categories?offset=0&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(categories, value =>
        {
            _categories = value;
            if (_draft.CategoryId != Guid.Empty && value.Items.All(item => item.Id != _draft.CategoryId || !item.Active))
                _draft.CategoryId = Guid.Empty;
        });
        var vendors = await Api.ReadInventoryAsync<BusinessPage<ExpenseVendor>>(
            $"{Root}/vendors?offset=0&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(vendors, value =>
        {
            _vendors = value;
            if (_draft.VendorId != Guid.Empty && value.Items.All(item => item.Id != _draft.VendorId || !item.Active))
                _draft.VendorId = Guid.Empty;
        });
        if (!OwnsRequest(ct)) return;
        await LoadTagsCoreAsync(ct);
        if (!OwnsRequest(ct)) return;
        _directoryCategories = _categories;
        _directoryVendors = _vendors;
        _directoryTags = _tags;
        if (!Can("expense.directory.write"))
        {
            return;
        }
        var allCategories = await Api.ReadInventoryAsync<BusinessPage<ExpenseCategory>>(
            $"{Root}/categories?includeInactive=true&offset=0&limit={PageSize}", ct);
        if (!OwnsRequest(ct)) return;
        Accept(allCategories, value =>
        {
            _directoryCategories = value;
            if (_selectedCategory is not null)
                SetSelectedCategory(value.Items.FirstOrDefault(item => item.Id == _selectedCategory.Id));
        });
        var allVendors = await Api.ReadInventoryAsync<BusinessPage<ExpenseVendor>>(
            $"{Root}/vendors?includeInactive=true&offset=0&limit={PageSize}", ct);
        if (OwnsRequest(ct)) Accept(allVendors, value =>
        {
            _directoryVendors = value;
            if (_selectedVendor is not null)
                SetSelectedVendor(value.Items.FirstOrDefault(item => item.Id == _selectedVendor.Id));
        });
        if (OwnsRequest(ct)) await LoadDirectoryTagsCoreAsync(ct);
    }

    private Task LoadExpensesAsync() => RunAsync(async ct =>
    {
        await LoadExpensesCoreAsync(ct);
        if (OwnsRequest(ct) && _selected is not null) await LoadInvoiceContextCoreAsync(ct);
    });
    private async Task LoadExpensesCoreAsync(CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<BusinessPage<ExpenseRecord>>(ExpenseLedgerPath(), ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is { } page && page.Items.Count == 0 && page.Total > 0 && _expenseOffset >= page.Total)
        {
            _expenseOffset = (int)((page.Total - 1) / PageSize * PageSize);
            result = await Api.ReadInventoryAsync<BusinessPage<ExpenseRecord>>(ExpenseLedgerPath(), ct);
            if (!OwnsRequest(ct)) return;
        }
        Accept(result, value => _expenses = value);
        if (_selected is not null && _expenses?.Items.FirstOrDefault(value => value.Id == _selected.Id) is { } refreshed)
            _selected = refreshed;
    }

    private async Task SearchExpensesAsync()
    {
        if (LedgerLocked || !TryLedgerFilter(out var filter)) return;
        _ledgerFilter = filter;
        _expenseOffset = 0;
        ClearSelectedExpense();
        await LoadExpensesAsync();
    }

    private async Task ClearExpenseFiltersAsync()
    {
        if (LedgerLocked) return;
        ClearLedgerDraft();
        _ledgerFilter = new();
        _ledgerValidation = null;
        _expenseOffset = 0;
        ClearSelectedExpense();
        await LoadExpensesAsync();
    }

    private async Task MoveExpensePageAsync(int direction)
    {
        if (LedgerLocked) return;
        var next = (long)_expenseOffset + direction * PageSize;
        if (next < 0 || next > int.MaxValue) return;
        _expenseOffset = (int)next;
        ClearSelectedExpense();
        await LoadExpensesAsync();
    }

    private bool TryLedgerFilter(out LedgerFilter filter)
    {
        filter = new();
        _ledgerValidation = null;
        var hasStart = _ledgerStart.Length > 0;
        var hasEnd = _ledgerEnd.Length > 0;
        DateOnly start = default;
        DateOnly end = default;
        if (hasStart != hasEnd
            || hasStart && (!DateOnly.TryParseExact(_ledgerStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out start)
                || !DateOnly.TryParseExact(_ledgerEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out end)
                || end < start || end.DayNumber - start.DayNumber > 366))
        {
            _ledgerValidation = T("expenseWorkspace.invalidLedgerRange",
                "시작일과 종료일을 함께 입력하고 366일 이내의 올바른 범위를 선택하세요.",
                "Enter both dates and choose a valid range of no more than 366 days.");
            return false;
        }
        if (!TryOptionalGuid(_ledgerCategory, out var categoryId)
            || !TryOptionalGuid(_ledgerVendor, out var vendorId)
            || !TryOptionalEnum<ExpenseType>(_ledgerType, out var type)
            || !TryOptionalEnum<ExpenseStatus>(_ledgerStatus, out var status)
            || !TryOptionalEnum<ExpenseState>(_ledgerState, out var state))
        {
            _ledgerValidation = T("expenseWorkspace.invalidLedgerFilter",
                "비용 원장 필터 값을 확인하세요.", "Check the expense-ledger filters.");
            return false;
        }
        DateOnly? parsedStart = hasStart ? start : null;
        DateOnly? parsedEnd = hasEnd ? end : null;
        filter = new(parsedStart, parsedEnd, categoryId, vendorId, type, status, state);
        return true;
    }

    private string ExpenseLedgerPath()
    {
        var query = new List<string>();
        if (_ledgerFilter.Start is { } start) query.Add($"start={start:yyyy-MM-dd}");
        if (_ledgerFilter.End is { } end) query.Add($"end={end:yyyy-MM-dd}");
        if (_ledgerFilter.CategoryId is { } category) query.Add($"categoryId={category:D}");
        if (_ledgerFilter.VendorId is { } vendor) query.Add($"vendorId={vendor:D}");
        if (_ledgerFilter.Type is { } type) query.Add($"type={type}");
        if (_ledgerFilter.Status is { } status) query.Add($"status={status}");
        if (_ledgerFilter.State is { } state) query.Add($"state={state}");
        query.Add($"offset={_expenseOffset}");
        query.Add($"limit={PageSize}");
        return $"{Root}?{string.Join('&', query)}";
    }

    private void ResetLedgerState()
    {
        _expenses = null;
        _expenseOffset = 0;
        _ledgerFilter = new();
        _ledgerValidation = null;
        ClearLedgerDraft();
        ClearSelectedExpense();
    }

    private void ClearLedgerDraft()
        => _ledgerStart = _ledgerEnd = _ledgerCategory = _ledgerVendor = _ledgerType = _ledgerStatus = _ledgerState = "";

    private void ClearSelectedExpense()
    {
        _selected = null;
        _receipt = null;
        ClearReceiptFile();
        _confirmReceiptDelete = false;
        _pendingReimbursement = null;
        _pendingInvoice = null;
        _invoiceCandidates = null;
        _linkedInvoice = null;
        _invoiceId = Guid.Empty;
        _invoiceOffset = 0;
        _invoiceDescription = "";
    }

    private Task CreateCategoryAsync() => CreateDirectoryAsync("categories", new ExpenseCategoryInput(_categoryName.Trim()),
        () => _categoryName = "");
    private Task CreateVendorAsync() => CreateDirectoryAsync("vendors", new ExpenseVendorInput(_vendorName.Trim()),
        () => _vendorName = "");
    private Task CreateTagAsync() => RunAsync(async ct =>
    {
        if (_userId is null) return;
        if (string.IsNullOrWhiteSpace(_tagName))
        {
            _error = T("expenseWorkspace.nameRequired", "이름을 입력하세요.", "Enter a name.");
            return;
        }
        var result = await WriteAsync<ExpenseTag>(HttpMethod.Post, $"{Root}/tags",
            new ExpenseTagInput(_tagName.Trim()), ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is null || result.Error is not null) { Accept(result, _ => { }); return; }
        _tagName = "";
        _notice = T("expenseWorkspace.tagSaved", "태그를 추가했습니다.", "Tag added.");
        await LoadDirectoriesCoreAsync(ct);
    });

    private void SelectCategory(ExpenseCategory value) => SetSelectedCategory(value);

    private void SetSelectedCategory(ExpenseCategory? value)
    {
        _selectedCategory = value;
        _categoryEditName = value?.Input.Name ?? "";
    }

    private void SelectVendor(ExpenseVendor value) => SetSelectedVendor(value);

    private void SetSelectedVendor(ExpenseVendor? value)
    {
        _selectedVendor = value;
        _vendorEditName = value?.Input.Name ?? "";
        _vendorEditPhone = value?.Input.Phone ?? "";
        _vendorEditWebsite = value?.Input.Website ?? "";
        _vendorEditEmail = value?.Input.Email ?? "";
    }

    private void SelectTag(ExpenseTag value) => SetSelectedTag(value);

    private void SetSelectedTag(ExpenseTag? value)
    {
        _selectedTag = value;
        _tagEditName = value?.Input.Name ?? "";
    }

    private void ClearDirectorySelection()
    {
        SetSelectedCategory(null);
        SetSelectedVendor(null);
        SetSelectedTag(null);
    }

    private Task UpdateCategoryAsync()
    {
        if (_selectedCategory is null || string.IsNullOrWhiteSpace(_categoryEditName))
        {
            _error = T("expenseWorkspace.nameRequired", "이름을 입력하세요.", "Enter a name.");
            return Task.CompletedTask;
        }
        var current = _selectedCategory;
        var input = current.Input with { Name = _categoryEditName.Trim() };
        return MutateCategoryAsync(current, HttpMethod.Put, new CategoryChange(current.Version, input),
            value => Same(value.Input, input),
            T("expenseWorkspace.directoryUpdated", "카테고리를 수정했습니다.", "Category updated."));
    }

    private Task SetCategoryActiveAsync()
    {
        if (_selectedCategory is null) return Task.CompletedTask;
        var current = _selectedCategory;
        var active = !current.Active;
        return MutateCategoryAsync(current, HttpMethod.Post, new ActiveChange(current.Version, active),
            value => value.Active == active,
            active
                ? T("expenseWorkspace.categoryActivated", "카테고리를 다시 활성화했습니다.", "Category reactivated.")
                : T("expenseWorkspace.categoryDeactivated", "카테고리를 비활성화했습니다.", "Category deactivated."),
            "/active");
    }

    private Task UpdateVendorAsync()
    {
        if (_selectedVendor is null || string.IsNullOrWhiteSpace(_vendorEditName))
        {
            _error = T("expenseWorkspace.nameRequired", "이름을 입력하세요.", "Enter a name.");
            return Task.CompletedTask;
        }
        var current = _selectedVendor;
        var input = current.Input with
        {
            Name = _vendorEditName.Trim(),
            Phone = Text(_vendorEditPhone),
            Website = Text(_vendorEditWebsite),
            Email = Text(_vendorEditEmail)
        };
        return MutateVendorAsync(current, HttpMethod.Put, new VendorChange(current.Version, input),
            value => Same(value.Input, input),
            T("expenseWorkspace.directoryVendorUpdated", "거래처를 수정했습니다.", "Vendor updated."));
    }

    private Task SetVendorActiveAsync()
    {
        if (_selectedVendor is null) return Task.CompletedTask;
        var current = _selectedVendor;
        var active = !current.Active;
        return MutateVendorAsync(current, HttpMethod.Post, new ActiveChange(current.Version, active),
            value => value.Active == active,
            active
                ? T("expenseWorkspace.vendorActivated", "거래처를 다시 활성화했습니다.", "Vendor reactivated.")
                : T("expenseWorkspace.vendorDeactivated", "거래처를 비활성화했습니다.", "Vendor deactivated."),
            "/active");
    }

    private Task UpdateTagAsync()
    {
        if (_selectedTag is null || string.IsNullOrWhiteSpace(_tagEditName))
        {
            _error = T("expenseWorkspace.nameRequired", "이름을 입력하세요.", "Enter a name.");
            return Task.CompletedTask;
        }
        var current = _selectedTag;
        var input = new ExpenseTagInput(_tagEditName.Trim());
        return MutateTagAsync(current, HttpMethod.Put, new TagChange(current.Version, input),
            value => value.Input == input,
            T("expenseWorkspace.tagUpdated", "태그를 수정했습니다.", "Tag updated."));
    }

    private Task SetTagActiveAsync()
    {
        if (_selectedTag is null) return Task.CompletedTask;
        var current = _selectedTag;
        var active = !current.Active;
        return MutateTagAsync(current, HttpMethod.Post, new ActiveChange(current.Version, active),
            value => value.Active == active,
            active
                ? T("expenseWorkspace.tagActivated", "태그를 다시 활성화했습니다.", "Tag reactivated.")
                : T("expenseWorkspace.tagDeactivated", "태그를 비활성화했습니다.", "Tag deactivated."),
            "/active");
    }

    private Task MutateTagAsync(ExpenseTag current, HttpMethod method, object body,
        Func<ExpenseTag, bool> intended, string notice, string suffix = "") => RunAsync(async ct =>
    {
        var result = await WriteAsync<ExpenseTag>(method, $"{Root}/tags/{current.Id:D}{suffix}", body, ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is { } saved && saved.Id == current.Id && intended(saved) && result.Error is null)
        {
            ApplyTag(saved);
            await LoadTagsCoreAsync(ct);
            if (OwnsRequest(ct)) _notice = notice;
            return;
        }
        _error = result.Value is not null
            ? T("expenseWorkspace.directoryInvalidResponse", "기준정보 응답이 현재 요청과 일치하지 않습니다. 최신 상태를 다시 불러왔습니다.", "The directory response does not match this request. The latest state was reloaded.")
            : result.Error ?? result.Code ?? string.Format(CultureInfo.InvariantCulture, "HTTP {0}", result.StatusCode);
        await RecoverTagAsync(current.Id, intended, ct);
    });

    private async Task RecoverTagAsync(Guid id, Func<ExpenseTag, bool> intended, CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<ExpenseTag>($"{Root}/tags/{id:D}", ct);
        if (!OwnsRequest(ct) || result.Value is not { } latest || latest.Id != id) return;
        ApplyTag(latest);
        if (intended(latest))
        {
            _error = null;
            _notice = T("expenseWorkspace.directoryRecovered", "저장된 기준정보 결과를 확인했습니다.", "The stored directory result was recovered.");
        }
    }

    private Task MutateCategoryAsync(ExpenseCategory current, HttpMethod method, object body,
        Func<ExpenseCategory, bool> intended, string notice, string suffix = "") => RunAsync(async ct =>
    {
        var result = await WriteAsync<ExpenseCategory>(method, $"{Root}/categories/{current.Id:D}{suffix}", body, ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is { } saved && saved.Id == current.Id && intended(saved) && result.Error is null)
        {
            ApplyCategory(saved); _notice = notice; return;
        }
        _error = result.Value is not null
            ? T("expenseWorkspace.directoryInvalidResponse", "기준정보 응답이 현재 요청과 일치하지 않습니다. 최신 상태를 다시 불러왔습니다.", "The directory response does not match this request. The latest state was reloaded.")
            : result.Error ?? result.Code ?? string.Format(CultureInfo.InvariantCulture, "HTTP {0}", result.StatusCode);
        await RecoverCategoryAsync(current.Id, intended, ct);
    });

    private async Task RecoverCategoryAsync(Guid id, Func<ExpenseCategory, bool> intended, CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<ExpenseCategory>($"{Root}/categories/{id:D}", ct);
        if (!OwnsRequest(ct) || result.Value is not { } latest || latest.Id != id) return;
        ApplyCategory(latest);
        if (intended(latest))
        {
            _error = null;
            _notice = T("expenseWorkspace.directoryRecovered", "저장된 기준정보 결과를 확인했습니다.", "The stored directory result was recovered.");
        }
    }

    private Task MutateVendorAsync(ExpenseVendor current, HttpMethod method, object body,
        Func<ExpenseVendor, bool> intended, string notice, string suffix = "") => RunAsync(async ct =>
    {
        var result = await WriteAsync<ExpenseVendor>(method, $"{Root}/vendors/{current.Id:D}{suffix}", body, ct);
        if (!OwnsRequest(ct)) return;
        if (result.Value is { } saved && saved.Id == current.Id && intended(saved) && result.Error is null)
        {
            ApplyVendor(saved); _notice = notice; return;
        }
        _error = result.Value is not null
            ? T("expenseWorkspace.directoryInvalidResponse", "기준정보 응답이 현재 요청과 일치하지 않습니다. 최신 상태를 다시 불러왔습니다.", "The directory response does not match this request. The latest state was reloaded.")
            : result.Error ?? result.Code ?? string.Format(CultureInfo.InvariantCulture, "HTTP {0}", result.StatusCode);
        await RecoverVendorAsync(current.Id, intended, ct);
    });

    private async Task RecoverVendorAsync(Guid id, Func<ExpenseVendor, bool> intended, CancellationToken ct)
    {
        var result = await Api.ReadInventoryAsync<ExpenseVendor>($"{Root}/vendors/{id:D}", ct);
        if (!OwnsRequest(ct) || result.Value is not { } latest || latest.Id != id) return;
        ApplyVendor(latest);
        if (intended(latest))
        {
            _error = null;
            _notice = T("expenseWorkspace.directoryRecovered", "저장된 기준정보 결과를 확인했습니다.", "The stored directory result was recovered.");
        }
    }

    private void ApplyCategory(ExpenseCategory value)
    {
        if (_directoryCategories is { } directoryPage)
            _directoryCategories = new(directoryPage.Items.Select(item => item.Id == value.Id ? value : item).ToArray(),
                directoryPage.Total);
        _categories = ApplyActive(_categories, value);
        SetSelectedCategory(value);
        if (!value.Active && _draft.CategoryId == value.Id) _draft.CategoryId = Guid.Empty;
    }

    private void ApplyVendor(ExpenseVendor value)
    {
        if (_directoryVendors is { } directoryPage)
            _directoryVendors = new(directoryPage.Items.Select(item => item.Id == value.Id ? value : item).ToArray(),
                directoryPage.Total);
        _vendors = ApplyActive(_vendors, value);
        SetSelectedVendor(value);
        if (!value.Active && _draft.VendorId == value.Id) _draft.VendorId = Guid.Empty;
    }

    private void ApplyTag(ExpenseTag value)
    {
        if (_directoryTags is { } directoryPage)
            _directoryTags = new(directoryPage.Items.Select(item => item.Id == value.Id ? value : item).ToArray(),
                directoryPage.Total);
        _tags = ApplyActive(_tags, value);
        SetSelectedTag(value);
        if (!value.Active) _draft.TagIds.Remove(value.Id);
    }

    private void ToggleTag(Guid id)
    {
        if (!_draft.TagIds.Add(id)) _draft.TagIds.Remove(id);
    }

    private void RemoveTag(Guid id) => _draft.TagIds.Remove(id);

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
            || _draft.Currency.Trim().Length != 3
            || _draft.Currency.Trim().Any(value => value is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')))
        { _error = T("expenseWorkspace.invalidInput", "양수 금액, 3자리 통화, 카테고리와 거래처를 확인하세요.", "Check the positive amount, three-letter currency, category, and vendor."); return false; }
        if (_draft.SplitAcrossEmployees && _draft.EmployeeId != Guid.Empty)
        { _error = T("expenseWorkspace.employeeOrSplit", "직원 한 명 지정과 활성 직원 균등 분할은 함께 사용할 수 없습니다.", "Choose either one employee or a split across active employees."); return false; }
        if (_draft.Type == ExpenseType.BillableToContact && _draft.ContactId == Guid.Empty)
        { _error = T("expenseWorkspace.contactRequired", "고객 청구 비용에는 유효한 고객 연락처 ID가 필요합니다.", "A valid customer contact ID is required for a billable expense."); return false; }
        ExpenseTax? tax = null;
        if (_draft.TaxType.Length > 0)
        {
            if (!Enum.TryParse<ExpenseTaxType>(_draft.TaxType, false, out var taxType)
                || !Enum.IsDefined(taxType) || _draft.TaxValue < 0
                || decimal.Round(_draft.TaxValue, 6) != _draft.TaxValue
                || taxType == ExpenseTaxType.Percentage && _draft.TaxValue > 100
                || taxType == ExpenseTaxType.Flat && _draft.TaxValue > _draft.Amount)
            { _error = T("expenseWorkspace.taxInvalid", "포함 세금은 0~100% 비율이거나 금액 이하의 정액이어야 합니다.", "Included tax must be a 0–100% rate or a flat value no greater than the amount."); return false; }
            tax = new(taxType, _draft.TaxValue, Text(_draft.TaxLabel));
        }
        input = new(_draft.Amount, _draft.Type, _draft.CategoryId, _draft.VendorId,
            _draft.SplitAcrossEmployees || _draft.EmployeeId == Guid.Empty ? null : _draft.EmployeeId,
            _draft.Type == ExpenseType.BillableToContact ? _draft.ContactId : null,
            _draft.ProjectId == Guid.Empty ? null : _draft.ProjectId,
            _draft.Currency.Trim().ToUpperInvariant(), _draft.ValueDate, Text(_draft.Purpose),
            Text(_draft.Reference), Text(_draft.Notes), Text(_draft.Receipt), Tax: tax,
            SplitAcrossEmployees: _draft.SplitAcrossEmployees,
            TagIds: _draft.TagIds.Count == 0 ? null : _draft.TagIds.OrderBy(value => value).ToArray());
        return true;
    }

    private Task SelectExpenseAsync(ExpenseRecord value) => RunAsync(async ct =>
    {
        _selected = value; _pendingReimbursement = null; _pendingInvoice = null;
        _receipt = null; ClearReceiptFile(); _confirmReceiptDelete = false;
        _invoiceCandidates = null; _linkedInvoice = null; _invoiceId = Guid.Empty; _invoiceOffset = 0;
        _invoiceDescription = value.Input.Purpose ?? value.Input.Reference ?? "";
        await LoadReceiptCoreAsync(ct);
        if (OwnsRequest(ct)) await LoadInvoiceContextCoreAsync(ct);
    });

    private async Task LoadReceiptCoreAsync(CancellationToken ct)
    {
        if (_selected is null) return;
        var expenseId = _selected.Id;
        var result = await Api.ReadInventoryAsync<ExpenseReceipt>($"{Root}/{expenseId:D}/receipt", ct);
        if (!OwnsRequest(ct) || _selected?.Id != expenseId) return;
        if (result.StatusCode == 404 && result.Code == "EXPENSE_RECEIPT_NOT_FOUND")
        {
            _receipt = null;
            return;
        }
        if (result.Value?.ExpenseId == expenseId && result.Error is null) _receipt = result.Value;
        else if (result.Value is not null)
            _error = T("expenseWorkspace.receiptInvalidResponse", "영수증 응답이 선택한 비용과 일치하지 않습니다. 비용을 다시 선택하세요.", "The receipt response does not match the selected expense. Select the expense again.");
        else Accept(result, _ => { });
    }

    private void SelectReceiptFile(InputFileChangeEventArgs args)
    {
        _notice = null;
        var file = args.File;
        if (file.Size is < 1 or > MaxReceiptBytes || !SafeReceiptFileName(file.Name)
            || ReceiptContentType(file) is null)
        {
            ClearReceiptFile();
            _error = T("expenseWorkspace.receiptFileInvalid", "PDF·JPG·PNG·WebP 파일을 10MB 이하로 선택하세요.", "Choose a PDF, JPG, PNG, or WebP file no larger than 10 MB.");
            return;
        }
        _error = null;
        _receiptFile = file;
        _confirmReceiptDelete = false;
    }

    private Task UploadReceiptAsync() => RunAsync(async ct =>
    {
        if (_selected is null || _receiptFile is null || _userId is null) return;
        var expenseId = _selected.Id;
        var file = _receiptFile;
        var contentType = ReceiptContentType(file)!;
        await using var stream = file.OpenReadStream(MaxReceiptBytes, ct);
        var result = await Api.UploadInventoryFileAsync<ExpenseReceipt>($"{Root}/{expenseId:D}/receipt",
            stream, file.Name, contentType, _receipt?.Version, _userId, ct);
        if (!OwnsRequest(ct) || _selected?.Id != expenseId) return;
        if (result.Value?.ExpenseId == expenseId && result.Error is null)
        {
            _receipt = result.Value;
            ClearReceiptFile();
            _confirmReceiptDelete = false;
            _notice = T("expenseWorkspace.receiptUploaded", "영수증 파일을 저장했습니다.", "Receipt file saved.");
            return;
        }
        if (result.Value is not null)
            _error = T("expenseWorkspace.receiptInvalidResponse", "영수증 응답이 선택한 비용과 일치하지 않습니다. 비용을 다시 선택하세요.", "The receipt response does not match the selected expense. Select the expense again.");
        else Accept(result, _ => { });
        if (result.Code == "INVENTORY_RESPONSE_UNAVAILABLE") ClearReceiptFile();
        if (OwnsRequest(ct) && (result.StatusCode == 409 || result.Code == "INVENTORY_RESPONSE_UNAVAILABLE"))
            await LoadReceiptCoreAsync(ct);
    });

    private Task DownloadReceiptAsync() => RunAsync(async ct =>
    {
        if (_selected is null || _receipt is null) return;
        var expenseId = _selected.Id;
        var result = await Api.DownloadInventoryFileAsync($"{Root}/{expenseId:D}/receipt/download", ct);
        if (!OwnsRequest(ct) || _selected?.Id != expenseId) return;
        if (result.Content is null || result.FileName is null || result.ContentType is null || result.Error is not null)
        {
            _error = result.Error ?? result.Code ?? string.Format(CultureInfo.InvariantCulture, "HTTP {0}", result.StatusCode);
            return;
        }
        try
        {
            await using var stream = new MemoryStream(result.Content, writable: false);
            using var reference = new DotNetStreamReference(stream);
            await JS.InvokeVoidAsync("nxDownloadStream", ct, result.FileName, result.ContentType, reference);
        }
        catch (JSException)
        {
            _error = T("expenseWorkspace.receiptDownloadFailed", "브라우저에서 영수증 다운로드를 시작하지 못했습니다. 다시 시도하세요.", "The browser could not start the receipt download. Try again.");
        }
    });

    private void RequestReceiptDelete() => _confirmReceiptDelete = true;
    private void CancelReceiptDelete() => _confirmReceiptDelete = false;

    private Task DeleteReceiptAsync() => RunAsync(async ct =>
    {
        if (_selected is null || _receipt is null || !_confirmReceiptDelete) return;
        var expenseId = _selected.Id;
        var version = _receipt.Version;
        var result = await WriteAsync<ReceiptDeleted>(HttpMethod.Delete,
            $"{Root}/{expenseId:D}/receipt?version={version:D}", new { }, ct);
        if (!OwnsRequest(ct) || _selected?.Id != expenseId) return;
        if (result.Value is { } deleted && deleted.ExpenseId == expenseId && deleted.Version == version
            && result.Error is null)
        {
            _receipt = null;
            ClearReceiptFile();
            _confirmReceiptDelete = false;
            _notice = T("expenseWorkspace.receiptDeleted", "영수증 파일을 삭제했습니다.", "Receipt file deleted.");
            return;
        }
        if (result.Value is not null)
            _error = T("expenseWorkspace.receiptInvalidResponse", "영수증 응답이 선택한 비용과 일치하지 않습니다. 비용을 다시 선택하세요.", "The receipt response does not match the selected expense. Select the expense again.");
        else Accept(result, _ => { });
        if (OwnsRequest(ct) && result.StatusCode == 409) await LoadReceiptCoreAsync(ct);
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
    private static bool TryOptionalGuid(string value, out Guid? parsed)
    {
        parsed = null;
        if (value.Length == 0) return true;
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty) return false;
        parsed = id;
        return true;
    }
    private static bool TryOptionalEnum<T>(string value, out T? parsed) where T : struct, Enum
    {
        parsed = null;
        if (value.Length == 0) return true;
        if (!Enum.TryParse<T>(value, out var item) || !Enum.IsDefined(item)) return false;
        parsed = item;
        return true;
    }
    private static bool Same(ExpenseCategoryInput left, ExpenseCategoryInput right)
        => string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && (left.TagIds ?? []).SequenceEqual(right.TagIds ?? []);
    private static bool Same(ExpenseVendorInput left, ExpenseVendorInput right)
        => string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Phone, right.Phone, StringComparison.Ordinal)
            && string.Equals(left.Website, right.Website, StringComparison.Ordinal)
            && string.Equals(left.Email, right.Email, StringComparison.Ordinal)
            && (left.TagIds ?? []).SequenceEqual(right.TagIds ?? []);
    private static BusinessPage<ExpenseCategory>? ApplyActive(BusinessPage<ExpenseCategory>? page,
        ExpenseCategory value) => ApplyActive(page, value, item => item.Id, item => item.Active);
    private static BusinessPage<ExpenseVendor>? ApplyActive(BusinessPage<ExpenseVendor>? page,
        ExpenseVendor value) => ApplyActive(page, value, item => item.Id, item => item.Active);
    private static BusinessPage<ExpenseTag>? ApplyActive(BusinessPage<ExpenseTag>? page,
        ExpenseTag value) => ApplyActive(page, value, item => item.Id, item => item.Active);
    private static BusinessPage<T>? ApplyActive<T>(BusinessPage<T>? page, T value,
        Func<T, Guid> id, Func<T, bool> active)
    {
        if (page is null) return null;
        var exists = page.Items.Any(item => id(item) == id(value));
        if (!active(value))
            return exists
                ? new(page.Items.Where(item => id(item) != id(value)).ToArray(), Math.Max(0, page.Total - 1))
                : page;
        if (exists)
            return new(page.Items.Select(item => id(item) == id(value) ? value : item).ToArray(), page.Total);
        return page.Items.Count < PageSize
            ? new([.. page.Items, value], page.Total + 1)
            : new(page.Items, page.Total + 1);
    }
    private static string Amount(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static bool SafeReceiptFileName(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && value == value.Trim()
            && value is not "." and not ".." && !value.Contains('/') && !value.Contains('\\')
            && !value.Any(char.IsControl);
    private static string? ReceiptContentType(IBrowserFile file)
    {
        if (file.ContentType is "application/pdf" or "image/jpeg" or "image/png" or "image/webp")
            return file.ContentType;
        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };
    }
    private static string ReceiptSize(long bytes) => bytes < 1024
        ? string.Format(CultureInfo.InvariantCulture, "{0} B", bytes)
        : bytes < 1024 * 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / 1024d)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / (1024d * 1024d));
    private void ClearReceiptFile()
    {
        _receiptFile = null;
        _receiptInputKey++;
    }
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);
    private string DirectoryState(bool active) => active
        ? T("expenseWorkspace.active", "활성", "Active")
        : T("expenseWorkspace.inactive", "비활성", "Inactive");
    private string TagLabel(Guid id)
        => _directoryTags?.Items.FirstOrDefault(value => value.Id == id)?.Input.Name
            ?? _tags?.Items.FirstOrDefault(value => value.Id == id)?.Input.Name
            ?? Short(id);
    private string TypeName(ExpenseType type) => type switch { ExpenseType.TaxDeductible => T("expenseWorkspace.taxDeductible", "세무상 공제", "Tax deductible"), ExpenseType.NotTaxDeductible => T("expenseWorkspace.notTaxDeductible", "공제 불가", "Not tax deductible"), _ => T("expenseWorkspace.billable", "고객 청구", "Billable to contact") };
    private string TaxName(ExpenseTax tax)
    {
        var value = tax.Type == ExpenseTaxType.Percentage
            ? Amount(tax.Value) + "%"
            : Amount(tax.Value) + " " + (_selected?.Input.Currency ?? "");
        return tax.Label is null ? value : tax.Label + " · " + value;
    }
    private string StatusName(ExpenseRecord value) => value.State == ExpenseState.Cancelled
        ? StateName(value.State) : StatusName(value.Status);
    private string StatusName(ExpenseStatus value) => value switch
    {
        ExpenseStatus.Uninvoiced => T("expenseWorkspace.uninvoiced", "미청구", "Uninvoiced"),
        ExpenseStatus.Invoiced => T("expenseWorkspace.invoiced", "청구됨", "Invoiced"),
        ExpenseStatus.Paid => T("expenseWorkspace.paid", "지급됨", "Paid"),
        _ => T("expenseWorkspace.notBillable", "청구 대상 아님", "Not billable")
    };
    private string StateName(ExpenseState value) => value == ExpenseState.Cancelled
        ? T("expenseWorkspace.cancelledState", "취소됨", "Cancelled")
        : T("expenseWorkspace.active", "활성", "Active");
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
    internal sealed record CategoryChange(Guid Version, ExpenseCategoryInput Input);
    internal sealed record VendorChange(Guid Version, ExpenseVendorInput Input);
    internal sealed record TagChange(Guid Version, ExpenseTagInput Input);
    internal sealed record ActiveChange(Guid Version, bool Active);
    private sealed record LedgerFilter(DateOnly? Start = null, DateOnly? End = null,
        Guid? CategoryId = null, Guid? VendorId = null, ExpenseType? Type = null,
        ExpenseStatus? Status = null, ExpenseState? State = null);
    internal sealed record VersionedRequest(Guid Version);
    internal sealed record CancelExpenseRequest(Guid Version, string? Reason);
    internal sealed record ReimbursementRequest(Guid OperationId, Guid Version, DateTimeOffset PaidAt, string? Reference);
    internal sealed record InvoiceLinkRequest(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion, string? Description);
    internal sealed record InvoiceUnlinkRequest(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion);
    internal sealed record ReceiptDeleted(Guid ExpenseId, Guid Version);
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
        public Guid ContactId { get; set; }
        public Guid EmployeeId { get; set; }
        public Guid ProjectId { get; set; }
        public bool SplitAcrossEmployees { get; set; }
        public HashSet<Guid> TagIds { get; } = [];
        public string TaxType { get; set; } = "";
        public decimal TaxValue { get; set; }
        public string TaxLabel { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Receipt { get; set; } = "";
        public void Reset()
        {
            Amount = 0; Type = default; CategoryId = Guid.Empty; VendorId = Guid.Empty;
            Currency = "KRW"; ValueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            ContactId = EmployeeId = ProjectId = Guid.Empty; SplitAcrossEmployees = false;
            TagIds.Clear();
            TaxType = ""; TaxValue = 0; TaxLabel = Purpose = Reference = Notes = Receipt = "";
        }
    }
}
