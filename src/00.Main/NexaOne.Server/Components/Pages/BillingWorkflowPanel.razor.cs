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

public partial class BillingWorkflowPanel : IDisposable
{
    [Inject] public IApiClient Api { get; set; } = null!;
    [Inject] public UiTextService Ui { get; set; } = null!;
    [Inject] public ProtectedSessionStorage Storage { get; set; } = null!;
    [Parameter, EditorRequired] public BusinessMembership Membership { get; set; } = null!;
    [Parameter] public string? UserId { get; set; }
    [Parameter] public EventCallback Saved { get; set; }
    [Parameter] public EventCallback<BillingDocument?> DocumentChanged { get; set; }

    // A browser recovery envelope for billing writes, not a second billing business model. It is stored under
    // its own key prefix so a stock or equipment intent in the same tab is never mixed with it.
    public sealed record PendingWrite(int Schema, string UserId, Guid BusinessUserId, Guid TenantId,
        Guid OrganizationId, string Kind, Guid Id, JsonElement Payload, DateTimeOffset CreatedAt,
        bool Confirmed = false, Guid? ResultId = null);

    /// <summary>One editable line of the document form; validated into a BillingLine on submit.</summary>
    public sealed class LineDraft
    {
        public string Description { get; set; } = "";
        public string UnitPrice { get; set; } = "";
        public string Quantity { get; set; } = "";
        public bool ApplyTax { get; set; } = true;
        public bool ApplyDiscount { get; set; } = true;
        public Guid? ExpenseId { get; set; }
    }

    private enum Editor { None, Document, Payment }
    private enum Focus { None, Document, Payment }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxLines = 200;
    private string? _storageKey;
    private int _generation;
    private bool _interactive, _loaded, _loading, _busy, _storageError, _acknowledged, _disposed;
    private CancellationTokenSource? _request;
    private PendingWrite? _pending;
    private BillingContact? _contact;
    private BillingDocument? _document;
    private PaymentRecord? _payment;
    private BillingDocument? _observedDocument;
    private PaymentRecord? _observedPayment;
    private Editor _editor;
    private Focus _focus;
    private string _customerId = "";
    private BillingKind _editorKind = BillingKind.Estimate;
    private bool _editing;
    private Guid? _creditInvoiceId;
    private decimal? _creditAvailable;
    private DateTime? _documentDate, _dueDate;
    private string _paymentPaidAt = "";
    private string _currency = "", _terms = "", _note = "";
    private string _discountType = "", _discountValue = "", _taxType = "", _taxValue = "", _tax2Type = "", _tax2Value = "";
    private List<LineDraft> _lines = [new()];
    private string _paymentAmount = "", _paymentMethod = nameof(PaymentMethod.BankTransfer), _paymentReference = "", _paymentNote = "";
    private string _cancelReason = "";
    private string? _error, _message;
    private ElementReference _heading;

    private bool Has(string grant) => Membership?.Permissions.Contains(grant, StringComparer.Ordinal) == true;
    private bool OwnerValid => Membership is not null && !string.IsNullOrWhiteSpace(UserId)
        && string.Equals(Membership.UserId, UserId, StringComparison.Ordinal)
        && Membership.IsActive && Membership.Version > 0 && Membership.BusinessUserId != Guid.Empty
        && Membership.TenantId != Guid.Empty && Membership.OrganizationId != Guid.Empty;
    private bool CanStart => OwnerValid && _loaded && !_busy && !_loading && !_storageError && _pending is null;
    private bool CanWrite => Has("billing.write");
    private bool CanDecide => Has("billing.decide");
    private bool CanPay => Has("billing.pay");
    private bool CanCredit => Has("billing.credit");
    private bool CanDraft => CanWrite && _contact is not null;
    private bool CanEdit => CanWrite && _document is { Status: BillingStatus.Draft, Kind: not BillingKind.CreditNote };
    private bool CanSend => CanWrite && _document is { Status: BillingStatus.Draft, Kind: not BillingKind.CreditNote };
    private bool CanDecideSelected => CanDecide && _document is { Kind: BillingKind.Estimate, Status: BillingStatus.Sent };
    private bool CanConvert => CanWrite && _document is { Kind: BillingKind.Estimate, Status: BillingStatus.Accepted, ConvertedToId: null };
    private bool CanVoid => CanWrite && _document is { Kind: not BillingKind.CreditNote, ConvertedToId: null } document
        && document.Paid == 0m && document.Credited == 0m
        && document.Status is BillingStatus.Draft or BillingStatus.Sent or BillingStatus.Accepted or BillingStatus.Rejected;
    private bool CanRecordPayment => CanPay && _document is { Kind: BillingKind.Invoice } document
        && document.Status is BillingStatus.Sent or BillingStatus.PartiallyPaid or BillingStatus.FullyPaid
            or BillingStatus.Overpaid or BillingStatus.PartiallyCredited or BillingStatus.Credited;
    private bool CanCancelPayment => CanPay && _payment is { State: PaymentState.Recorded };
    private bool CanCreateCredit => CanCredit && _document is { Kind: BillingKind.Invoice } invoice
        && invoice.Status is not BillingStatus.Draft and not BillingStatus.Void && invoice.Due > 0m;
    private bool CanEditCredit => CanCredit && _document is { Kind: BillingKind.CreditNote, Status: BillingStatus.Draft };
    private bool CanIssueCredit => CanEditCredit;
    private bool CanVoidCredit => CanCredit && _document is { Kind: BillingKind.CreditNote,
        Status: BillingStatus.Draft or BillingStatus.Sent };
    private string Root => $"api/v1/erp/billing/{Membership.TenantId:D}/{Membership.OrganizationId:D}";
    private string T(string key, string ko, string en) => Ui.T(key, Ui.Language == "EnUs" ? en : ko);

    protected override void OnInitialized() => Ui.Changed += LanguageChanged;

    protected override void OnParametersSet()
    {
        var key = OwnerValid ? StorageKey(UserId!, Membership) : null;
        if (key == _storageKey) return;
        ++_generation;
        _request?.Cancel();
        _storageKey = key;
        _pending = null; _contact = null; _document = null; _payment = null; _editor = Editor.None; _focus = Focus.None;
        _creditInvoiceId = null; _creditAvailable = null;
        _observedDocument = null; _observedPayment = null;
        _loaded = false; _loading = false; _busy = false; _storageError = false;
        _error = null; _message = null; _acknowledged = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _interactive = true;
        if (!_loaded && !_loading && !_storageError && _storageKey is not null)
            await LoadRecoveryAsync();
    }

    private static string StorageKey(string userId, BusinessMembership scope)
    {
        var owner = JsonSerializer.Serialize(new[] { userId, scope.BusinessUserId.ToString("D"), scope.TenantId.ToString("D"), scope.OrganizationId.ToString("D") });
        return "nexaone_billing_write_v1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)));
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
        && value.BusinessUserId == Membership.BusinessUserId && value.TenantId == Membership.TenantId
        && value.OrganizationId == Membership.OrganizationId && value.Id != Guid.Empty
        && value.CreatedAt != default && value.Payload.ValueKind == JsonValueKind.Object
        && Permission(value.Kind) is not null && (!value.Confirmed || value.ResultId is { } id && id != Guid.Empty)
        && RequestBody(value) is not null;

    private static string? Permission(string kind) => kind switch
    {
        "contact" or "create" or "update" or "sent" or "void" or "convert" => "billing.write",
        "credit-create" or "credit-update" or "credit-issue" or "credit-void" => "billing.credit",
        "decide" => "billing.decide",
        "pay" or "cancel-payment" => "billing.pay",
        _ => null
    };

    private static object? RequestBody(PendingWrite value)
    {
        // Rehydrate only existing controller contracts. A stored URL or method is never trusted.
        return value.Kind switch
        {
            "contact" => value.Payload.Deserialize<ContactCommand>(Json) is { } contact && ValidCustomer(contact.CustomerId) ? contact : null,
            "create" => value.Payload.Deserialize<BillingController.DocumentCreate>(Json) is { } create
                && create.OperationId == value.Id && Enum.IsDefined(create.Kind) && ValidInput(create.Input) ? create : null,
            "credit-create" => value.Payload.Deserialize<BillingController.CreditNoteCreate>(Json) is { } credit
                && credit.OperationId != Guid.Empty && ValidInput(credit.Input) ? credit : null,
            "update" => value.Payload.Deserialize<BillingController.DocumentChange>(Json) is { } change
                && change.Version != Guid.Empty && ValidInput(change.Input) ? change : null,
            "credit-update" => value.Payload.Deserialize<BillingController.DocumentChange>(Json) is { } creditChange
                && creditChange.Version != Guid.Empty && ValidInput(creditChange.Input) ? creditChange : null,
            "sent" or "void" or "credit-issue" or "credit-void" => value.Payload.Deserialize<BillingController.VersionedCommand>(Json) is { } command && command.Version != Guid.Empty ? command : null,
            "decide" => value.Payload.Deserialize<BillingController.DecisionCommand>(Json) is { } decision && decision.Version != Guid.Empty ? decision : null,
            "convert" => value.Payload.Deserialize<BillingController.ConversionCommand>(Json) is { } conversion
                && conversion.Version != Guid.Empty && conversion.OperationId != Guid.Empty && conversion.OperationId != value.Id ? conversion : null,
            "pay" => value.Payload.Deserialize<BillingController.PaymentCommand>(Json) is { } pay
                && pay.OperationId != Guid.Empty && pay.Input is not null && pay.Input.DocumentId == value.Id && ValidPayment(pay.Input) ? pay : null,
            "cancel-payment" => value.Payload.Deserialize<BillingController.CancelCommand>(Json) is { } cancel
                && cancel.Version != Guid.Empty && (cancel.Reason is null || cancel.Reason.Length <= 4000) ? cancel : null,
            _ => null
        };
    }

    /// <summary>Body of the contact enrollment intent; the route carries the customer, the body is kept for the recovery card.</summary>
    public sealed record ContactCommand(string CustomerId);

    private static bool ValidCustomer(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 50 && value == value.Trim() && !value.Any(char.IsControl);
    private static bool ValidAmount(decimal value, bool positive) => (positive ? value > 0m : value >= 0m) && value <= 1_000_000_000_000m && decimal.Round(value, 6) == value;
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');
    private static bool ValidText(string? value, int max) => value is null || value.Length <= max;
    private static bool ValidAdjustment(BillingAdjustment? value) => value is null || Enum.IsDefined(value.Type)
        && (value.Type == BillingAdjustmentType.Percent ? value.Value is >= 0m and <= 100m && decimal.Round(value.Value, 6) == value.Value : ValidAmount(value.Value, false));
    private static bool ValidInput(BillingDocumentInput? input) => input is not null && input.ContactId != Guid.Empty
        && input.DocumentDate != default && input.DueDate >= input.DocumentDate && ValidCurrency(input.Currency)
        && input.Lines is { Count: >= 1 and <= MaxLines } && input.Lines.All(line => line is not null && !string.IsNullOrWhiteSpace(line.Description)
            && line.Description.Length <= 255 && ValidAmount(line.UnitPrice, false) && ValidAmount(line.Quantity, true))
        && ValidAdjustment(input.Discount) && ValidAdjustment(input.Tax) && ValidAdjustment(input.Tax2)
        && ValidText(input.Terms, 4000) && ValidText(input.Note, 4000);
    private static bool ValidPayment(PaymentInput input) => input.DocumentId != Guid.Empty && ValidAmount(input.Amount, true)
        && ValidCurrency(input.Currency) && input.PaidAt != default && input.PaidAt.Offset == TimeSpan.Zero && Enum.IsDefined(input.Method)
        && ValidText(input.Reference, 255) && ValidText(input.Note, 4000);

    /// <summary>Selects the contact new documents are drafted for. Selection alone saves nothing.</summary>
    public Task SelectContactAsync(BillingContact contact)
    {
        if (!CanStart || !Has("billing.read")) return Task.CompletedTask;
        _error = null; _message = null;
        if (contact is null || contact.Id == Guid.Empty || contact.Version == Guid.Empty || !ValidCustomer(contact.CustomerId)) _error = InvalidResponse();
        else _contact = contact;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>Selects a document row: its status decides which actions open, and dependent panes follow it.</summary>
    public async Task SelectDocumentAsync(BillingDocument document)
    {
        if (!CanStart || !Has("billing.read") || _storageKey is not { } key) return;
        var generation = _generation;
        _error = null; _message = null;
        if (!ValidDocument(document)) { _error = InvalidResponse(); StateHasChanged(); return; }
        _document = document; _payment = null; _editor = Editor.None; _focus = Focus.Document; _editing = false;
        ClearObserved();
        StateHasChanged();
        await NotifyDocumentAsync(document, generation, key);
        await FocusEditorAsync();
    }

    /// <summary>Selects a payment row of the current document for cancellation or comparison.</summary>
    public Task SelectPaymentAsync(PaymentRecord payment)
    {
        if (!CanStart || !Has("billing.read")) return Task.CompletedTask;
        _error = null; _message = null;
        if (!ValidPaymentRecord(payment)) _error = InvalidResponse();
        else { _payment = payment; _editor = Editor.None; _focus = Focus.Payment; _cancelReason = ""; }
        ClearObserved();
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task NotifyDocumentAsync(BillingDocument? document, int generation, string key)
    {
        try { await DocumentChanged.InvokeAsync(document); }
        catch (Exception) when (!_disposed)
        {
            if (Current(generation, key)) _error = T("billing.paymentListError", "문서는 선택됐지만 입금 목록을 불러오지 못했습니다. 다시 조회해 주세요.", "The document was selected, but its payment list could not be loaded. Search again.");
        }
    }

    private async Task EnrollContactAsync()
    {
        if (!CanStart || !CanWrite) return;
        _customerId = _customerId.Trim();
        if (!ValidCustomer(_customerId)) { _error = CustomerMessage(); return; }
        await BeginAsync("contact", Guid.NewGuid(), new ContactCommand(_customerId));
    }

    private void StartDocument(BillingKind kind)
    {
        if (!CanStart || !CanDraft) return;
        _editor = Editor.Document; _editing = false; _editorKind = kind; _error = null; _message = null;
        _documentDate = DateTime.UtcNow.Date; _dueDate = null; _currency = "";
        _terms = ""; _note = ""; _discountType = ""; _discountValue = ""; _taxType = ""; _taxValue = ""; _tax2Type = ""; _tax2Value = "";
        _lines = [new()];
    }

    private void EditDocument()
    {
        if (!CanStart || !CanEdit || _document is not { } document) return;
        _editor = Editor.Document; _editing = true; _editorKind = document.Kind; _error = null; _message = null;
        var input = document.Input;
        _documentDate = input.DocumentDate.ToDateTime(TimeOnly.MinValue); _dueDate = input.DueDate.ToDateTime(TimeOnly.MinValue); _currency = input.Currency; _terms = input.Terms ?? ""; _note = input.Note ?? "";
        (_discountType, _discountValue) = Adjustment(input.Discount); (_taxType, _taxValue) = Adjustment(input.Tax); (_tax2Type, _tax2Value) = Adjustment(input.Tax2);
        _lines = input.Lines.Select(line => new LineDraft { Description = line.Description, UnitPrice = Amount(line.UnitPrice),
            Quantity = Amount(line.Quantity), ApplyTax = line.ApplyTax, ApplyDiscount = line.ApplyDiscount,
            ExpenseId = line.ExpenseId }).ToList();
    }

    private void StartCreditNote()
    {
        if (!CanStart || !CanCreateCredit || _document is not { } invoice) return;
        _editor = Editor.Document; _editing = false; _editorKind = BillingKind.CreditNote;
        _creditInvoiceId = invoice.Id; _creditAvailable = invoice.Due; _error = null; _message = null;
        var date = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (date < invoice.Input.DocumentDate) date = invoice.Input.DocumentDate;
        _documentDate = date.ToDateTime(TimeOnly.MinValue); _dueDate = _documentDate; _currency = invoice.Input.Currency;
        _terms = ""; _note = ""; _discountType = ""; _discountValue = ""; _taxType = ""; _taxValue = ""; _tax2Type = ""; _tax2Value = "";
        _lines = [new()];
    }

    private void EditCreditNote()
    {
        if (!CanStart || !CanEditCredit || _document is not { AdjustedInvoiceId: { } invoiceId } note) return;
        _editor = Editor.Document; _editing = true; _editorKind = BillingKind.CreditNote;
        _creditInvoiceId = invoiceId; _creditAvailable = null; _error = null; _message = null;
        var input = note.Input;
        _documentDate = input.DocumentDate.ToDateTime(TimeOnly.MinValue); _dueDate = _documentDate;
        _currency = input.Currency; _terms = input.Terms ?? ""; _note = input.Note ?? "";
        (_discountType, _discountValue) = Adjustment(input.Discount); (_taxType, _taxValue) = Adjustment(input.Tax); (_tax2Type, _tax2Value) = Adjustment(input.Tax2);
        _lines = input.Lines.Select(line => new LineDraft { Description = line.Description, UnitPrice = Amount(line.UnitPrice),
            Quantity = Amount(line.Quantity), ApplyTax = line.ApplyTax, ApplyDiscount = line.ApplyDiscount }).ToList();
    }

    private void AddLine() { if (_lines.Count < MaxLines) _lines.Add(new()); }
    private void RemoveLine(LineDraft line) { if (_lines.Count > 1) _lines.Remove(line); }

    private async Task SaveDocumentAsync()
    {
        var credit = _editorKind == BillingKind.CreditNote;
        if (!CanStart || (credit
                ? _editing ? !CanEditCredit : !CanCreateCredit
                : _editing ? !CanEdit : !CanDraft)) return;
        var contact = credit || _editing ? _document!.Input.ContactId : _contact!.Id;
        var input = BuildInput(contact);
        if (input is null || !ValidInput(input)) { _error = credit ? CreditInputMessage() : DocumentInputMessage(); return; }
        if (credit && _editing)
            await BeginAsync("credit-update", _document!.Id, new BillingController.DocumentChange(_document.Version, input));
        else if (credit)
        {
            if (_creditInvoiceId is not { } invoiceId) { _error = InvalidResponse(); return; }
            await BeginAsync("credit-create", invoiceId, new BillingController.CreditNoteCreate(Guid.NewGuid(), input));
        }
        else if (_editing) await BeginAsync("update", _document!.Id, new BillingController.DocumentChange(_document.Version, input));
        else
        {
            var operation = Guid.NewGuid();
            await BeginAsync("create", operation, new BillingController.DocumentCreate(operation, _editorKind, input));
        }
    }

    private BillingDocumentInput? BuildInput(Guid contact)
    {
        if (_documentDate is not { } issuedDay || (_editorKind != BillingKind.CreditNote && _dueDate is null)) return null;
        var issued = DateOnly.FromDateTime(issuedDay);
        var due = _editorKind == BillingKind.CreditNote ? issued : DateOnly.FromDateTime(_dueDate!.Value);
        var lines = new List<BillingLine>(_lines.Count);
        foreach (var line in _lines)
        {
            if (!TryAmount(line.UnitPrice, out var price) || !TryAmount(line.Quantity, out var quantity)) return null;
            lines.Add(new(line.Description.Trim(), price, quantity, line.ApplyTax, line.ApplyDiscount, line.ExpenseId));
        }
        if (!TryAdjustment(_discountType, _discountValue, out var discount) || !TryAdjustment(_taxType, _taxValue, out var tax) || !TryAdjustment(_tax2Type, _tax2Value, out var tax2)) return null;
        _currency = _currency.Trim().ToUpperInvariant(); _terms = _terms.Trim(); _note = _note.Trim();
        return new(contact, issued, due, _currency, lines, discount, tax, tax2, _terms.Length == 0 ? null : _terms, _note.Length == 0 ? null : _note);
    }

    private static bool TryAmount(string text, out decimal value)
        => decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    private static bool TryAdjustment(string type, string text, out BillingAdjustment? value)
    {
        value = null;
        if (type.Length == 0) return text.Trim().Length == 0;
        if (!Enum.TryParse<BillingAdjustmentType>(type, out var kind) || !TryAmount(text, out var amount)) return false;
        value = new(kind, amount);
        return true;
    }
    private static (string Type, string Value) Adjustment(BillingAdjustment? value) => value is null ? ("", "") : (value.Type.ToString(), Amount(value.Value));

    private Task SendAsync() => CanStart && CanSend ? BeginAsync("sent", _document!.Id, new BillingController.VersionedCommand(_document.Version)) : Task.CompletedTask;
    private Task VoidAsync() => CanStart && CanVoid ? BeginAsync("void", _document!.Id, new BillingController.VersionedCommand(_document.Version)) : Task.CompletedTask;
    private Task DecideAsync(bool accepted) => CanStart && CanDecideSelected
        ? BeginAsync("decide", _document!.Id, new BillingController.DecisionCommand(_document.Version, accepted)) : Task.CompletedTask;
    private Task ConvertAsync() => CanStart && CanConvert
        ? BeginAsync("convert", _document!.Id, new BillingController.ConversionCommand(Guid.NewGuid(), _document.Version)) : Task.CompletedTask;
    private Task IssueCreditAsync() => CanStart && CanIssueCredit
        ? BeginAsync("credit-issue", _document!.Id, new BillingController.VersionedCommand(_document.Version)) : Task.CompletedTask;
    private Task VoidCreditAsync() => CanStart && CanVoidCredit
        ? BeginAsync("credit-void", _document!.Id, new BillingController.VersionedCommand(_document.Version)) : Task.CompletedTask;

    private void StartPayment()
    {
        if (!CanStart || !CanRecordPayment) return;
        _editor = Editor.Payment; _payment = null; _error = null; _message = null;
        _paymentAmount = ""; _paymentMethod = nameof(PaymentMethod.BankTransfer); _paymentReference = ""; _paymentNote = "";
        _paymentPaidAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
    }

    private async Task RecordPaymentAsync()
    {
        if (!CanStart || !CanRecordPayment) return;
        _paymentReference = _paymentReference.Trim(); _paymentNote = _paymentNote.Trim();
        // Browsers send datetime-local values with or without seconds; the field is labelled UTC and stored as that instant.
        if (!TryAmount(_paymentAmount, out var amount) || !Enum.TryParse<PaymentMethod>(_paymentMethod, out var method)
            || !DateTime.TryParseExact(_paymentPaidAt.Trim(), ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var paidAt))
        { _error = PaymentInputMessage(); return; }
        var input = new PaymentInput(_document!.Id, amount, _document.Input.Currency, new DateTimeOffset(DateTime.SpecifyKind(paidAt, DateTimeKind.Unspecified), TimeSpan.Zero), method,
            _paymentReference.Length == 0 ? null : _paymentReference, _paymentNote.Length == 0 ? null : _paymentNote);
        if (!ValidPayment(input)) { _error = PaymentInputMessage(); return; }
        await BeginAsync("pay", _document.Id, new BillingController.PaymentCommand(Guid.NewGuid(), input));
    }

    private async Task CancelPaymentAsync()
    {
        if (!CanStart || !CanCancelPayment) return;
        _cancelReason = _cancelReason.Trim();
        if (_cancelReason.Length > 4000) { _error = PaymentInputMessage(); return; }
        await BeginAsync("cancel-payment", _payment!.Id, new BillingController.CancelCommand(_payment.Version, _cancelReason.Length == 0 ? null : _cancelReason));
    }

    private async Task FocusEditorAsync()
    {
        try { await _heading.FocusAsync(); }
        catch (JSException) { /* Selection remains valid if browser focus is unavailable. */ }
    }

    private async Task BeginAsync(string kind, Guid id, object body)
    {
        if (!CanStart || Permission(kind) is not { } grant || !Has(grant) || _storageKey is not { } key) return;
        var generation = _generation;
        var intent = new PendingWrite(1, UserId!, Membership.BusinessUserId, Membership.TenantId, Membership.OrganizationId,
            kind, id, JsonSerializer.SerializeToElement(body, Json), DateTimeOffset.UtcNow);
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
            var (method, path) = WriteRoute(intent, body);
            int status; string? code; bool valid; Guid resultId;
            if (intent.Kind == "contact")
            {
                var result = await Api.WriteInventoryAsync<BillingContact>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && value.Id != Guid.Empty && value.Version != Guid.Empty && value.CustomerId == ((ContactCommand)body).CustomerId;
                if (valid) { _contact = result.Value; _customerId = ""; }
            }
            else if (intent.Kind is "pay" or "cancel-payment")
            {
                var result = await Api.WriteInventoryAsync<PaymentRecord>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidPaymentRecord(value) && MatchesPaymentOutcome(value, intent, body);
                if (valid)
                {
                    _payment = result.Value; _editor = Editor.None; _focus = Focus.Payment; _cancelReason = "";
                    // The invoice's paid amount and status changed with the record; refresh the selected document from the server.
                    if (_document?.Id == result.Value!.Input.DocumentId)
                    {
                        await RefreshDocumentAsync(_document.Id, request.Token, generation, key);
                        if (!Current(generation, key)) return;
                    }
                }
            }
            else
            {
                var result = await Api.WriteInventoryAsync<BillingDocument>(method, path, body, intent.UserId, request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code; resultId = result.Value?.Id ?? Guid.Empty;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value
                    && ValidDocument(value) && MatchesDocumentOutcome(value, intent, body);
                if (valid)
                {
                    _document = result.Value; _payment = null; _editor = Editor.None; _focus = Focus.Document; _editing = false;
                    await NotifyDocumentAsync(result.Value, generation, key);
                    if (!Current(generation, key)) return;
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

    private async Task RefreshDocumentAsync(Guid id, CancellationToken ct, int generation, string key)
    {
        var result = await Api.ReadInventoryAsync<BillingDocument>(Root + $"/documents/{id:D}", ct);
        if (!Current(generation, key)) return;
        if (result.StatusCode is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == id && ValidDocument(value))
        {
            _document = value;
            await NotifyDocumentAsync(value, generation, key);
        }
        else if (Current(generation, key))
            _error = T("billing.documentRefreshError", "입금은 저장됐지만 문서의 현재 상태를 다시 읽지 못했습니다. 현재 상태를 조회해 주세요.", "The payment was saved, but the document's current state could not be re-read. Read the current state.");
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

    // What "current state" means for an intent: the record its outcome can be read from. A creation, conversion or
    // payment that never returned an ID has nothing to read; only its same-body replay can confirm it.
    private (Focus Target, Guid Id)? ReadTarget => _pending is { } p ? p.Kind switch
    {
        "create" or "credit-create" => p.ResultId is { } created ? (Focus.Document, created) : null,
        "update" or "sent" or "void" or "decide" or "credit-update" or "credit-issue" or "credit-void" => (Focus.Document, p.Id),
        "convert" => (Focus.Document, p.ResultId ?? p.Id),
        "pay" => p.ResultId is { } paid ? (Focus.Payment, paid) : (Focus.Document, p.Id),
        "cancel-payment" => (Focus.Payment, p.Id),
        _ => null
    } : _focus switch
    {
        Focus.Document when _document is not null => (Focus.Document, _document.Id),
        Focus.Payment when _payment is not null => (Focus.Payment, _payment.Id),
        _ => null
    };

    private bool CanReadCurrent => ReadTarget is not null && Has("billing.read");

    private async Task ReadCurrentAsync()
    {
        if (_busy || _loading || !CanReadCurrent || ReadTarget is not { } target || _storageKey is not { } key) return;
        var generation = _generation;
        using var request = new CancellationTokenSource();
        _request = request; _busy = true; _error = null;
        try
        {
            bool valid; int status; string? code;
            if (target.Target == Focus.Document)
            {
                var result = await Api.ReadInventoryAsync<BillingDocument>(Root + $"/documents/{target.Id:D}", request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == target.Id && ValidDocument(value);
                if (valid) { ClearObserved(); _observedDocument = result.Value; }
            }
            else
            {
                var result = await Api.ReadInventoryAsync<PaymentRecord>(Root + $"/payments/{target.Id:D}", request.Token);
                if (!Current(generation, key)) return;
                status = result.StatusCode; code = result.Code;
                valid = status is >= 200 and < 300 && result.Error is null && result.Value is { } value && value.Id == target.Id && ValidPaymentRecord(value);
                if (valid) { ClearObserved(); _observedPayment = result.Value; }
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

    private async Task AdoptObservedAsync()
    {
        if (!CanStart || _storageKey is not { } key) return;
        var generation = _generation;
        // Reading never silently replaces a version or the user's draft. This explicit action does.
        var document = _observedDocument;
        if (document is not null) { _document = document; _payment = null; _editor = Editor.None; _focus = Focus.Document; _editing = false; }
        else if (_observedPayment is not null) { _payment = _observedPayment; _editor = Editor.None; _focus = Focus.Payment; _cancelReason = ""; }
        ClearObserved(); _error = null; _message = null;
        if (document is not null) await NotifyDocumentAsync(document, generation, key);
    }

    private void ClearObserved() { _observedDocument = null; _observedPayment = null; }

    private async Task ClearRecoveryAsync()
    {
        if (_busy || _loading || _storageKey is not { } key || !OwnerValid || (_pending?.Confirmed != true && !_acknowledged)) return;
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

    private (HttpMethod Method, string Path) WriteRoute(PendingWrite intent, object body) => intent.Kind switch
    {
        "contact" => (HttpMethod.Put, Root + "/contacts/" + Uri.EscapeDataString(((ContactCommand)body).CustomerId)),
        "create" => (HttpMethod.Post, Root + "/documents"),
        "credit-create" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/credits"),
        "update" => (HttpMethod.Put, Root + $"/documents/{intent.Id:D}"),
        "credit-update" => (HttpMethod.Put, Root + $"/credit-notes/{intent.Id:D}"),
        "sent" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/sent"),
        "void" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/void"),
        "credit-issue" => (HttpMethod.Post, Root + $"/credit-notes/{intent.Id:D}/issue"),
        "credit-void" => (HttpMethod.Post, Root + $"/credit-notes/{intent.Id:D}/void"),
        "decide" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/decision"),
        "convert" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/conversion"),
        "pay" => (HttpMethod.Post, Root + $"/documents/{intent.Id:D}/payments"),
        "cancel-payment" => (HttpMethod.Post, Root + $"/payments/{intent.Id:D}/cancel"),
        _ => throw new InvalidOperationException("Unknown billing command.")
    };

    private bool ValidScope(BusinessScope? scope) => scope is not null && scope.ProductId == "NexaOne.MES"
        && Guid.TryParse(scope.TenantId, out var tenant) && tenant == Membership.TenantId
        && Guid.TryParse(scope.OrganizationId, out var organization) && organization == Membership.OrganizationId;
    private bool ValidDocument(BillingDocument? value) => value is not null && value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && value.OperationId != Guid.Empty && Enum.IsDefined(value.Kind) && value.Number > 0
        && Enum.IsDefined(value.Status) && value.Totals is not null && ValidInput(value.Input) && value.Paid >= 0m && value.Credited >= 0m
        && value.ConvertedFromId != value.Id && value.ConvertedToId != value.Id && value.AdjustedInvoiceId != value.Id
        && value.AdjustedInvoiceId.HasValue == (value.Kind == BillingKind.CreditNote)
        && (value.Kind != BillingKind.CreditNote || value.Paid == 0m && value.Credited == 0m
            && value.Input.DueDate == value.Input.DocumentDate && value.Totals.Total > 0m);
    private bool ValidPaymentRecord(PaymentRecord? value) => value is not null && value.Id != Guid.Empty && value.Version != Guid.Empty
        && ValidScope(value.Scope) && value.OperationId != Guid.Empty && value.Input is not null && ValidPayment(value.Input) && Enum.IsDefined(value.State)
        && (value.State == PaymentState.Cancelled) == (value.CancelledBy is not null);

    private static bool MatchesDocumentOutcome(BillingDocument value, PendingWrite intent, object body) => intent.Kind switch
    {
        // A replayed creation returns the earlier document for the same operation and input.
        "create" => body is BillingController.DocumentCreate create && value.OperationId == create.OperationId && value.Kind == create.Kind
            && value.Input.Lines.SequenceEqual(create.Input.Lines) && value.Input.ContactId == create.Input.ContactId,
        "credit-create" => body is BillingController.CreditNoteCreate credit && value.Kind == BillingKind.CreditNote
            && value.OperationId == credit.OperationId && value.AdjustedInvoiceId == intent.Id
            && SameInput(value.Input, credit.Input),
        "update" => body is BillingController.DocumentChange change && value.Id == intent.Id && value.Version != change.Version
            && value.Input.Lines.SequenceEqual(change.Input.Lines),
        "credit-update" => body is BillingController.DocumentChange creditChange && value.Id == intent.Id
            && value.Kind == BillingKind.CreditNote && value.Status == BillingStatus.Draft
            && value.Version != creditChange.Version && SameInput(value.Input, creditChange.Input),
        "sent" => value.Id == intent.Id && value.Status == BillingStatus.Sent,
        "void" => value.Id == intent.Id && value.Status == BillingStatus.Void,
        "credit-issue" => body is BillingController.VersionedCommand issue && value.Id == intent.Id
            && value.Kind == BillingKind.CreditNote && value.Status == BillingStatus.Sent && value.Version != issue.Version,
        "credit-void" => body is BillingController.VersionedCommand voidCredit && value.Id == intent.Id
            && value.Kind == BillingKind.CreditNote && value.Status == BillingStatus.Void && value.Version != voidCredit.Version,
        "decide" => body is BillingController.DecisionCommand decision && value.Id == intent.Id
            && value.Status == (decision.Accepted ? BillingStatus.Accepted : BillingStatus.Rejected),
        // A replayed conversion returns the invoice created for this estimate under the same operation.
        "convert" => body is BillingController.ConversionCommand conversion && value.Kind == BillingKind.Invoice
            && value.ConvertedFromId == intent.Id && value.OperationId == conversion.OperationId,
        _ => false
    };

    private static bool SameInput(BillingDocumentInput left, BillingDocumentInput right)
        => left.ContactId == right.ContactId && left.DocumentDate == right.DocumentDate && left.DueDate == right.DueDate
            && left.Currency == right.Currency && left.Lines.SequenceEqual(right.Lines)
            && left.Discount == right.Discount && left.Tax == right.Tax && left.Tax2 == right.Tax2
            && left.Terms == right.Terms && left.Note == right.Note;

    private static bool MatchesPaymentOutcome(PaymentRecord value, PendingWrite intent, object body) => intent.Kind switch
    {
        // A replayed payment returns the earlier record for the same operation and input, even after cancellation.
        "pay" => body is BillingController.PaymentCommand pay && value.OperationId == pay.OperationId && value.Input == pay.Input,
        "cancel-payment" => value.Id == intent.Id && value.State == PaymentState.Cancelled,
        _ => false
    };

    private static bool StorageFailure(Exception ex) => ex is JSException or InvalidOperationException or JsonException
        or CryptographicException or OperationCanceledException;
    private string StorageMessage() => T("inventory.write.storageError", "복구 기록을 읽거나 저장하지 못했습니다. 결과를 확인하기 전에는 새 요청을 보내지 않습니다.", "The recovery record could not be read or saved. New requests are blocked until it is checked.");
    private string UnknownMessage() => T("inventory.write.unknown", "처리 결과를 확인하지 못했습니다. 같은 요청을 재확인하거나 현재 상태를 조회해 주세요.", "The outcome is unknown. Recheck the same request or read the current state.");
    private string InvalidResponse() => T("inventory.write.invalidResponse", "응답이 선택한 범위 또는 요청과 맞지 않습니다. 저장 결과를 다시 확인해 주세요.", "The response does not match the selected scope or request. Recheck the write outcome.");
    private string CustomerMessage() => T("billing.invalidCustomer", "고객 코드는 앞뒤 공백 없이 50자 이하로 입력해 주세요.", "Enter a customer code of up to 50 characters without surrounding whitespace.");
    private string DocumentInputMessage() => T("billing.invalidDocument", "날짜(만기일은 문서일 이후), 통화 3자, 항목(설명·0 이상 단가·양수 수량, 소수 6자리 이하)과 할인/세금 값을 확인해 주세요.", "Check the dates (due on or after the document date), the 3-letter currency, each line (description, unit price ≥ 0, positive quantity, at most 6 decimals) and the discount/tax values.");
    private string CreditInputMessage() => T("billing.invalidCreditNote", "신용 메모는 양수 합계여야 하며 문서일·만기일이 같아야 합니다. 항목과 할인·세금을 확인해 주세요.", "A credit note must have a positive total and the same document and due date. Check its lines, discount and taxes.");
    private string PaymentInputMessage() => T("billing.invalidPayment", "양수 금액(소수 6자리 이하), 결제 방법, UTC 입금 시각, 참조(255자 이하)를 확인해 주세요.", "Check a positive amount with at most 6 decimals, the method, the UTC paid time and a reference of up to 255 characters.");

    private string ErrorMessage(string? code, int status) => code switch
    {
        "BUSINESS_VERSION_CONFLICT" => T("inventory.write.versionConflict", "다른 변경으로 버전이 바뀌었습니다. 현재 상태를 확인한 뒤 다시 결정해 주세요.", "The version changed. Read the current state before deciding what to do next."),
        "BILLING_OPERATION_CONFLICT" or "PAYMENT_OPERATION_CONFLICT" => T("inventory.write.operationConflict", "같은 요청 ID에 다른 내용이 기록돼 있습니다. 원래 요청과 현재 상태를 확인해 주세요.", "This request ID has different recorded input. Check the original request and current state."),
        "CUSTOMER_NOT_FOUND" => T("billing.customerNotFound", "해당 고객 코드를 찾을 수 없습니다.", "The customer code was not found."),
        "CUSTOMER_INACTIVE" => T("billing.customerInactive", "비활성 고객은 계약처로 등록할 수 없습니다.", "An inactive customer cannot be enrolled as a contact."),
        "BILLING_DOCUMENT_NOT_DRAFT" or "BILLING_STATUS_TRANSITION" or "BILLING_NOT_ESTIMATE" or "BILLING_NOT_INVOICE" or "BILLING_DOCUMENT_NOT_PAYABLE"
            => T("billing.statusConflict", "현재 문서 상태에서는 이 작업을 할 수 없습니다. 현재 상태를 조회해 주세요.", "This action is not allowed in the document's current status. Read the current state."),
        "BILLING_DOCUMENT_CONVERTED" => T("billing.converted", "이미 청구로 전환된 견적입니다.", "This estimate was already converted to an invoice."),
        "BILLING_DOCUMENT_HAS_PAYMENTS" => T("billing.hasPayments", "입금이 기록된 청구는 무효화할 수 없습니다. 입금을 먼저 취소하세요.", "An invoice with recorded payments cannot be voided. Cancel its payments first."),
        "BILLING_DOCUMENT_NOT_CREDITABLE" => T("billing.notCreditable", "작성 중이거나 무효화된 청구에는 신용 메모를 만들 수 없습니다.", "A credit note cannot be created for a draft or void invoice."),
        "BILLING_CREDIT_CONTEXT_MISMATCH" => T("billing.creditContextMismatch", "신용 메모의 계약처 또는 통화가 원본 청구와 다릅니다. 현재 상태를 다시 조회해 주세요.", "The credit note contact or currency differs from the invoice. Read the current state again."),
        "BILLING_CREDIT_EXCEEDS_INVOICE" or "BILLING_CREDIT_EXCEEDS_DUE" => T("billing.creditExceedsDue", "신용 금액이 원본 청구의 허용 잔액을 초과합니다. 청구의 현재 잔액을 확인해 주세요.", "The credit exceeds the invoice's available balance. Check the invoice's current due amount."),
        "BILLING_NOT_CREDIT_NOTE" => T("billing.notCreditNote", "선택한 문서는 신용 메모가 아닙니다.", "The selected document is not a credit note."),
        "PAYMENT_CURRENCY_MISMATCH" => T("billing.currencyMismatch", "입금 통화가 청구 통화와 다릅니다.", "The payment currency differs from the invoice currency."),
        "BILLING_CONTACT_NOT_FOUND" => T("billing.contactNotFound", "선택한 계약처가 이 범위에 없습니다. 계약처를 다시 선택해 주세요.", "The selected contact is not in this scope. Select a contact again."),
        "INVALID_BILLING_TOTAL" or "BILLING_AMOUNT_OVERFLOW" => PendingCredit ? CreditInputMessage() : DocumentInputMessage(),
        _ when status is 401 or 403 => T("inventory.write.accessChanged", "인증 또는 작업 권한이 변경됐습니다. 로그인과 업무 범위를 다시 확인해 주세요.", "Authentication or permission changed. Check your session and business scope."),
        _ when status == 404 => T("inventory.write.notFound", "대상을 찾을 수 없습니다. 현재 목록과 업무 범위를 확인해 주세요.", "The record was not found. Check the current list and business scope."),
        _ when status == 400 => PendingCredit ? CreditInputMessage() : DocumentInputMessage(),
        _ when status == 409 => T("inventory.write.conflict", "현재 데이터와 충돌했습니다. 입력과 현재 상태를 확인해 주세요.", "The request conflicts with current data. Check the input and current state."),
        _ => UnknownMessage()
    };

    private bool PendingCredit => _pending?.Kind.StartsWith("credit-", StringComparison.Ordinal) == true;

    private string KindName(BillingKind kind) => kind switch
    {
        BillingKind.Estimate => T("billing.estimate", "견적", "Estimate"),
        BillingKind.Invoice => T("billing.invoice", "청구", "Invoice"),
        BillingKind.CreditNote => T("billing.creditNote", "대변 전표", "Credit note"),
        _ => kind.ToString(),
    };
    private string StatusName(BillingStatus status) => status switch
    {
        BillingStatus.Draft => T("billing.draft", "작성 중", "Draft"), BillingStatus.Sent => T("billing.sent", "전송됨", "Sent"),
        BillingStatus.Accepted => T("billing.accepted", "승인됨", "Accepted"), BillingStatus.Rejected => T("billing.rejected", "거절됨", "Rejected"),
        BillingStatus.PartiallyPaid => T("billing.partiallyPaid", "일부 입금", "Partially paid"), BillingStatus.FullyPaid => T("billing.fullyPaid", "완납", "Fully paid"),
        BillingStatus.Overpaid => T("billing.overpaid", "초과 입금", "Overpaid"), BillingStatus.Void => T("billing.void", "무효", "Void"),
        BillingStatus.PartiallyCredited => T("billing.partiallyCredited", "일부 대변 처리", "Partially credited"),
        BillingStatus.Credited => T("billing.creditedStatus", "대변 처리 완료", "Credited"), _ => status.ToString()
    };
    private string PaymentStateName(PaymentState state) => state == PaymentState.Recorded ? T("billing.recorded", "기록됨", "Recorded") : T("billing.cancelledPayment", "취소됨", "Cancelled");
    private string MethodName(PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => T("billing.bankTransfer", "계좌 이체", "Bank transfer"), PaymentMethod.Cash => T("billing.cash", "현금", "Cash"),
        PaymentMethod.Cheque => T("billing.cheque", "수표", "Cheque"), PaymentMethod.CreditCard => T("billing.creditCard", "신용카드", "Credit card"),
        PaymentMethod.Debit => T("billing.debit", "직불", "Debit"), PaymentMethod.Online => T("billing.online", "온라인", "Online"), _ => method.ToString()
    };
    private string ActionName(string kind) => kind switch
    {
        "contact" => T("billing.enrollContact", "계약처 등록", "Enroll contact"),
        "create" => T("billing.createDocument", "문서 작성", "Create document"),
        "update" => T("billing.saveDocument", "문서 변경 저장", "Save document changes"),
        "sent" => T("billing.send", "전송 표시", "Mark sent"),
        "void" => T("billing.voidAction", "무효화", "Void"),
        "decide" => T("billing.decide", "견적 결정", "Decide estimate"),
        "convert" => T("billing.convert", "청구로 전환", "Convert to invoice"),
        "credit-create" => T("billing.createCreditNote", "신용 메모 작성", "Create credit note"),
        "credit-update" => T("billing.updateCreditNote", "신용 메모 변경 저장", "Save credit note changes"),
        "credit-issue" => T("billing.issueCreditNote", "신용 메모 발행", "Issue credit note"),
        "credit-void" => T("billing.voidCreditNote", "신용 메모 무효화", "Void credit note"),
        "pay" => T("billing.recordPayment", "입금 기록", "Record payment"),
        "cancel-payment" => T("billing.cancelPayment", "입금 취소", "Cancel payment"),
        _ => ""
    };
    private static string Amount(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private void LanguageChanged() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }
    public void Dispose() { _disposed = true; ++_generation; _request?.Cancel(); Ui.Changed -= LanguageChanged; }
}
