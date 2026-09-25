using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Components.Pages;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BillingWorkflowPanelTests : BunitContext
{
    private readonly Mock<IApiClient> _api = new(MockBehavior.Strict);
    private readonly InventorySessionStorageJs _browser = new();
    private readonly ProtectedSessionStorage _storage;
    private readonly List<(HttpMethod Method, string Path, object Body, string Owner)> _writes = [];
    private readonly List<string> _reads = [];
    private readonly List<BillingDocument?> _documentChanges = [];
    private int _saved;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BusinessUser = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly BusinessScope Business = new("NexaOne.MES", Tenant.ToString("D"), Organization.ToString("D"));
    private static string Root => $"api/v1/erp/billing/{Tenant:D}/{Organization:D}";

    public BillingWorkflowPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _storage = new ProtectedSessionStorage(_browser, new EphemeralDataProtectionProvider());
        var ui = new UiTextService();
        ui.Load("EnUs", new());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(ui);
        Services.AddSingleton(_storage);
        Writes<BillingContact>((_, _, _, _) => Task.FromResult(Failure<BillingContact>(503)));
        Writes<BillingDocument>((_, _, _, _) => Task.FromResult(Failure<BillingDocument>(503)));
        Writes<PaymentRecord>((_, _, _, _) => Task.FromResult(Failure<PaymentRecord>(503)));
    }

    [Fact]
    public void Grants_decide_which_actions_are_offered()
    {
        var cut = Panel(Membership("billing.read"));
        cut.FindAll("#billing-contact-form").Should().BeEmpty();
        cut.FindAll("#billing-start-estimate").Should().BeEmpty();
        cut.Find("#billing-workflows").TextContent.Should().Contain("Billing");

        cut = Panel(Membership("billing.read", "billing.write"));
        cut.Find("#billing-contact-form").Should().NotBeNull();
        cut.Find("#billing-start-estimate").HasAttribute("disabled").Should().BeTrue("a contact must be selected before a document can be drafted");
    }

    [Fact]
    public async Task Credit_action_requires_the_credit_grant_and_an_open_issued_invoice()
    {
        var invoice = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 1) with { Status = BillingStatus.Sent };
        var cut = Panel(Membership("billing.read", "billing.write"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice));
        cut.FindAll("#billing-start-credit").Should().BeEmpty("billing.credit is a separate money authority");

        cut = Panel(Membership("billing.read", "billing.credit"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice with { Status = BillingStatus.Draft }));
        cut.Find("#billing-start-credit").HasAttribute("disabled").Should().BeTrue("draft invoices are not creditable");

        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice with { Status = BillingStatus.FullyPaid, Paid = invoice.Totals.Total }));
        cut.Find("#billing-start-credit").HasAttribute("disabled").Should().BeTrue("an invoice with no open balance cannot accept another credit");
    }

    [Fact]
    public async Task Contact_enrollment_sends_the_customer_after_a_durable_intent_and_selects_the_returned_contact()
    {
        var membership = Membership("billing.read", "billing.write");
        var contact = Contact("CUST-1", "고객");
        Writes<BillingContact>(async (method, path, body, owner) =>
        {
            var intent = await StoredIntent();
            intent.UserId.Should().Be("operator"); intent.BusinessUserId.Should().Be(BusinessUser);
            intent.Kind.Should().Be("contact"); intent.Confirmed.Should().BeFalse();
            method.Should().Be(HttpMethod.Put); path.Should().Be(Root + "/contacts/CUST-1"); owner.Should().Be("operator");
            return Ok(contact);
        });
        var cut = Panel(membership);
        cut.Find("#billing-contact-customer").Change("CUST-1");
        await cut.Find("#billing-contact-form").SubmitAsync(EventArgs.Empty);

        cut.WaitForAssertion(() => cut.Find("#billing-selected-contact").TextContent.Should().Contain("고객").And.Contain("CUST-1"));
        _writes.Should().ContainSingle();
        (await StoredIntentOrNull()).Should().BeNull("a confirmed write clears its recovery record");
        _saved.Should().Be(1);
        cut.Find("#billing-start-estimate").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Estimate_draft_sends_lines_and_adjustments_and_shows_server_totals()
    {
        var membership = Membership("billing.read", "billing.write");
        var contact = Contact("CUST-1", "고객");
        BillingController.DocumentCreate? sent = null;
        Writes<BillingDocument>((method, path, body, _) =>
        {
            sent = (BillingController.DocumentCreate)body;
            method.Should().Be(HttpMethod.Post); path.Should().Be(Root + "/documents");
            return Task.FromResult(Ok(Document(sent.OperationId, sent.Kind, sent.Input, 1)));
        });
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectContactAsync(contact));
        cut.Find("#billing-start-estimate").Click();
        cut.Find("#billing-document-date").Change("2026-09-22");
        cut.Find("#billing-due-date").Change("2026-10-22");
        cut.Find("#billing-currency").Change("KRW");
        cut.Find("#billing-add-line").Click();
        var rows = cut.FindAll("#billing-lines tbody tr");
        rows.Should().HaveCount(2);
        cut.FindAll(".billing-line-description")[0].Change("Design");
        cut.FindAll(".billing-line-unit-price")[0].Change("100");
        cut.FindAll(".billing-line-quantity")[0].Change("2");
        cut.FindAll(".billing-line-description")[1].Change("Hosting");
        cut.FindAll(".billing-line-unit-price")[1].Change("50");
        cut.FindAll(".billing-line-quantity")[1].Change("1");
        cut.FindAll(".billing-line-apply-tax")[1].Change(false);
        cut.FindAll(".billing-line-apply-discount")[1].Change(false);
        cut.Find("#billing-discount-type").Change("Percent");
        cut.Find("#billing-discount-value").Change("10");
        cut.Find("#billing-tax-type").Change("Percent");
        cut.Find("#billing-tax-value").Change("10");
        cut.Find("#billing-terms").Change("net 30");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        sent.Should().NotBeNull();
        sent!.Kind.Should().Be(BillingKind.Estimate);
        sent.Input.ContactId.Should().Be(contact.Id);
        sent.Input.Lines.Should().Equal(new BillingLine("Design", 100m, 2m), new BillingLine("Hosting", 50m, 1m, false, false));
        sent.Input.Discount.Should().Be(new BillingAdjustment(BillingAdjustmentType.Percent, 10m));
        sent.Input.Tax.Should().Be(new BillingAdjustment(BillingAdjustmentType.Percent, 10m));
        sent.Input.Tax2.Should().BeNull(); sent.Input.Terms.Should().Be("net 30");
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Estimate").And.Contain("#1").And.Contain("Draft").And.Contain("250"));
        _documentChanges.Should().ContainSingle().Which!.Number.Should().Be(1);
        (await StoredIntentOrNull()).Should().BeNull();
        _saved.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_line_input_is_rejected_locally_without_any_request()
    {
        var membership = Membership("billing.read", "billing.write");
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectContactAsync(Contact("CUST-1", "고객")));
        cut.Find("#billing-start-invoice").Click();
        cut.Find("#billing-document-date").Change("2026-09-22");
        cut.Find("#billing-due-date").Change("2026-09-01");
        cut.Find("#billing-currency").Change("KRW");
        cut.FindAll(".billing-line-description")[0].Change("A");
        cut.FindAll(".billing-line-unit-price")[0].Change("1");
        cut.FindAll(".billing-line-quantity")[0].Change("0");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        cut.Find("#billing-workflows [role=alert]").TextContent.Should().Contain("Check");
        _writes.Should().BeEmpty(); _browser.Values.Should().BeEmpty();
        cut.Find("#billing-due-date").Change("2026-10-01");
        cut.FindAll(".billing-line-quantity")[0].Change("1.1234567");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Expense_backed_lines_are_read_only_and_preserved_when_other_draft_values_change()
    {
        var expenseId = Guid.NewGuid();
        var input = Input(Guid.NewGuid()) with
        {
            Lines = [new BillingLine("Expense", 25.5m, 1m, false, false, expenseId), new("Editable", 1m, 1m)]
        };
        var draft = Document(Guid.NewGuid(), BillingKind.Invoice, input, 1);
        BillingController.DocumentChange? sent = null;
        Writes<BillingDocument>((_, path, body, _) =>
        {
            sent = (BillingController.DocumentChange)body;
            path.Should().Be(Root + $"/documents/{draft.Id:D}");
            return Task.FromResult(Ok(draft with { Version = Guid.NewGuid(), Input = sent.Input }));
        });
        var cut = Panel(Membership("billing.read", "billing.write"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft));
        cut.Find("#billing-edit-document").Click();

        cut.FindAll(".billing-line-description")[0].HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".billing-remove-line")[0].HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".billing-line-description")[1].Change("Changed");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        sent.Should().NotBeNull();
        sent!.Input.Lines[0].Should().Be(new BillingLine("Expense", 25.5m, 1m, false, false, expenseId));
        sent.Input.Lines[1].Description.Should().Be("Changed");
    }

    [Fact]
    public async Task Credit_grant_creates_a_draft_for_the_selected_invoice_without_billing_write()
    {
        var invoice = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 8) with { Status = BillingStatus.Sent };
        BillingController.CreditNoteCreate? sent = null;
        Writes<BillingDocument>((method, path, body, _) =>
        {
            sent = (BillingController.CreditNoteCreate)body;
            method.Should().Be(HttpMethod.Post);
            path.Should().Be(Root + $"/documents/{invoice.Id:D}/credits");
            return Task.FromResult(Ok(CreditDocument(sent.OperationId, invoice.Id, sent.Input, 2)));
        });
        var cut = Panel(Membership("billing.read", "billing.credit"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice));

        cut.FindAll("#billing-edit-document").Should().BeEmpty("billing.write is not granted");
        cut.Find("#billing-start-credit").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-start-credit").Click();
        cut.Find("#billing-credit-context").TextContent.Should().Contain(invoice.Id.ToString()).And.Contain("10 KRW");
        cut.FindAll("#billing-due-date").Should().BeEmpty("credit due date is always its document date");
        cut.Find("#billing-currency").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#billing-document-date").Change("2026-09-24");
        cut.FindAll(".billing-line-description")[0].Change("Service adjustment");
        cut.FindAll(".billing-line-unit-price")[0].Change("4.5");
        cut.FindAll(".billing-line-quantity")[0].Change("1");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        sent.Should().NotBeNull();
        sent!.OperationId.Should().NotBe(Guid.Empty);
        sent.Input.ContactId.Should().Be(invoice.Input.ContactId);
        sent.Input.Currency.Should().Be(invoice.Input.Currency);
        sent.Input.DueDate.Should().Be(sent.Input.DocumentDate);
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent
            .Should().Contain("Credit note").And.Contain(invoice.Id.ToString()));
        cut.Find("#billing-edit-credit").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-issue-credit").HasAttribute("disabled").Should().BeFalse();
        (await StoredIntentOrNull()).Should().BeNull();
        _saved.Should().Be(1);
    }

    [Fact]
    public async Task Credit_note_edit_issue_and_void_use_the_credit_routes_and_versions()
    {
        var invoiceId = Guid.NewGuid();
        var draft = CreditDocument(Guid.NewGuid(), invoiceId, CreditInput(Guid.NewGuid()), 3);
        var cut = Panel(Membership("billing.read", "billing.credit"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft));

        BillingController.DocumentChange? change = null;
        Writes<BillingDocument>((method, path, body, _) =>
        {
            change = (BillingController.DocumentChange)body;
            method.Should().Be(HttpMethod.Put); path.Should().Be(Root + $"/credit-notes/{draft.Id:D}");
            change.Version.Should().Be(draft.Version);
            return Task.FromResult(Ok(draft with { Version = Guid.NewGuid(), Input = change.Input }));
        });
        cut.Find("#billing-edit-credit").Click();
        cut.FindAll(".billing-line-description")[0].Change("Corrected adjustment");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => change.Should().NotBeNull());
        var updated = _documentChanges.Last()!;
        updated.Input.Lines[0].Description.Should().Be("Corrected adjustment");

        Writes<BillingDocument>((method, path, body, _) =>
        {
            method.Should().Be(HttpMethod.Post); path.Should().Be(Root + $"/credit-notes/{updated.Id:D}/issue");
            ((BillingController.VersionedCommand)body).Version.Should().Be(updated.Version);
            return Task.FromResult(Ok(updated with { Version = Guid.NewGuid(), Status = BillingStatus.Sent }));
        });
        cut.Find("#billing-issue-credit").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Sent"));
        var issued = _documentChanges.Last()!;
        cut.Find("#billing-edit-credit").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#billing-void-credit").HasAttribute("disabled").Should().BeFalse();

        Writes<BillingDocument>((method, path, body, _) =>
        {
            method.Should().Be(HttpMethod.Post); path.Should().Be(Root + $"/credit-notes/{issued.Id:D}/void");
            ((BillingController.VersionedCommand)body).Version.Should().Be(issued.Version);
            return Task.FromResult(Ok(issued with { Version = Guid.NewGuid(), Status = BillingStatus.Void }));
        });
        cut.Find("#billing-void-credit").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Void"));
        cut.Find("#billing-void-credit").HasAttribute("disabled").Should().BeTrue();
        _writes.Should().HaveCount(3);
    }

    [Fact]
    public async Task Unknown_credit_creation_retries_the_same_operation_and_invoice()
    {
        var invoice = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 5) with { Status = BillingStatus.Sent };
        var attempts = 0;
        Writes<BillingDocument>((_, path, body, _) =>
        {
            ++attempts;
            var command = (BillingController.CreditNoteCreate)body;
            path.Should().Be(Root + $"/documents/{invoice.Id:D}/credits");
            return Task.FromResult(attempts == 1
                ? Failure<BillingDocument>(503, "INVENTORY_RESPONSE_UNAVAILABLE")
                : Ok(CreditDocument(command.OperationId, invoice.Id, command.Input, 1)));
        });
        var cut = Panel(Membership("billing.read", "billing.credit"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice));
        cut.Find("#billing-start-credit").Click();
        cut.Find("#billing-document-date").Change("2026-09-24");
        cut.FindAll(".billing-line-description")[0].Change("Adjustment");
        cut.FindAll(".billing-line-unit-price")[0].Change("2");
        cut.FindAll(".billing-line-quantity")[0].Change("1");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        cut.WaitForAssertion(() => cut.Find("#billing-pending").Should().NotBeNull());
        var first = (BillingController.CreditNoteCreate)_writes[0].Body;
        cut.Find("#billing-retry").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Credit note"));
        var replay = (BillingController.CreditNoteCreate)_writes[1].Body;
        replay.OperationId.Should().Be(first.OperationId);
        _writes.Should().HaveCount(2);
        (await StoredIntentOrNull()).Should().BeNull();
    }

    [Fact]
    public async Task Credit_creation_rejects_a_success_response_for_another_invoice()
    {
        var invoice = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 5) with { Status = BillingStatus.Sent };
        Writes<BillingDocument>((_, _, body, _) =>
        {
            var command = (BillingController.CreditNoteCreate)body;
            return Task.FromResult(Ok(CreditDocument(command.OperationId, Guid.NewGuid(), command.Input, 1)));
        });
        var cut = Panel(Membership("billing.read", "billing.credit"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice));
        cut.Find("#billing-start-credit").Click();
        cut.Find("#billing-document-date").Change("2026-09-24");
        cut.FindAll(".billing-line-description")[0].Change("Adjustment");
        cut.FindAll(".billing-line-unit-price")[0].Change("2");
        cut.FindAll(".billing-line-quantity")[0].Change("1");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        cut.WaitForAssertion(() => cut.Find("#billing-write-error").TextContent.Should().Contain("does not match"));
        cut.Find("#billing-pending").Should().NotBeNull("a mismatched 2xx response cannot prove the write outcome");
        (await StoredIntent()).Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Selected_estimate_offers_send_decision_conversion_and_void_by_status_and_grant()
    {
        var membership = Membership("billing.read", "billing.write", "billing.decide");
        var draft = Document(Guid.NewGuid(), BillingKind.Estimate, Input(Contact("CUST-1", "고객").Id), 3);
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft));
        cut.Find("#billing-selected-document").TextContent.Should().Contain("#3");
        cut.Find("#billing-send").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-void").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-accept").HasAttribute("disabled").Should().BeTrue("only a sent estimate can be decided");
        cut.Find("#billing-convert").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#billing-edit-document").HasAttribute("disabled").Should().BeFalse();

        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft with { Status = BillingStatus.Sent }));
        cut.Find("#billing-send").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#billing-accept").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-reject").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#billing-edit-document").HasAttribute("disabled").Should().BeTrue();

        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft with { Status = BillingStatus.Accepted }));
        cut.Find("#billing-convert").HasAttribute("disabled").Should().BeFalse();
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft with { Status = BillingStatus.Accepted, ConvertedToId = Guid.NewGuid() }));
        cut.Find("#billing-convert").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#billing-void").HasAttribute("disabled").Should().BeTrue("a converted estimate cannot be voided");

        cut = Panel(Membership("billing.read", "billing.write"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft with { Status = BillingStatus.Sent }));
        cut.FindAll("#billing-accept").Should().BeEmpty("billing.decide is not granted");
    }

    [Fact]
    public async Task Decision_sends_the_version_and_shows_the_returned_status()
    {
        var membership = Membership("billing.read", "billing.decide");
        var sent = Document(Guid.NewGuid(), BillingKind.Estimate, Input(Guid.NewGuid()), 1) with { Status = BillingStatus.Sent };
        Writes<BillingDocument>((method, path, body, _) =>
        {
            method.Should().Be(HttpMethod.Post); path.Should().Be(Root + $"/documents/{sent.Id:D}/decision");
            var command = (BillingController.DecisionCommand)body;
            command.Version.Should().Be(sent.Version); command.Accepted.Should().BeTrue();
            return Task.FromResult(Ok(sent with { Status = BillingStatus.Accepted, Version = Guid.NewGuid() }));
        });
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(sent));
        cut.Find("#billing-accept").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Accepted"));
        _writes.Should().ContainSingle();
        _documentChanges.Last()!.Status.Should().Be(BillingStatus.Accepted);
    }

    [Fact]
    public async Task Conversion_uses_a_fresh_operation_and_selects_the_returned_invoice()
    {
        var membership = Membership("billing.read", "billing.write");
        var accepted = Document(Guid.NewGuid(), BillingKind.Estimate, Input(Guid.NewGuid()), 2) with { Status = BillingStatus.Accepted };
        BillingController.ConversionCommand? command = null;
        Writes<BillingDocument>((_, path, body, _) =>
        {
            command = (BillingController.ConversionCommand)body;
            path.Should().Be(Root + $"/documents/{accepted.Id:D}/conversion");
            return Task.FromResult(Ok(Document(command.OperationId, BillingKind.Invoice, accepted.Input, 7) with { ConvertedFromId = accepted.Id }));
        });
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(accepted));
        cut.Find("#billing-convert").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Invoice").And.Contain("#7"));
        command!.Version.Should().Be(accepted.Version); command.OperationId.Should().NotBe(Guid.Empty);
        (await StoredIntentOrNull()).Should().BeNull();
    }

    [Fact]
    public async Task Payment_recording_sends_the_invoice_currency_and_shows_the_returned_record()
    {
        var membership = Membership("billing.read", "billing.pay");
        var invoice = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 4) with { Status = BillingStatus.Sent };
        BillingController.PaymentCommand? command = null;
        Writes<PaymentRecord>((method, path, body, _) =>
        {
            command = (BillingController.PaymentCommand)body;
            method.Should().Be(HttpMethod.Post); path.Should().Be(Root + $"/documents/{invoice.Id:D}/payments");
            return Task.FromResult(Ok(Payment(command.OperationId, command.Input)));
        });
        // The invoice's paid amount and status change on the server with the payment; the panel re-reads it.
        Reads<BillingDocument>(path => path == Root + $"/documents/{invoice.Id:D}" ? Ok(invoice with { Status = BillingStatus.PartiallyPaid, Paid = 4.5m, Version = Guid.NewGuid() }) : Failure<BillingDocument>(404));
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(invoice));
        cut.FindAll("#billing-send").Should().BeEmpty("billing.write is not granted");
        cut.Find("#billing-start-payment").Click();
        cut.Find("#billing-payment-amount").Change("4.5");
        cut.Find("#billing-payment-method").Change("Cash");
        cut.Find("#billing-payment-paid-at").Change("2026-09-22T09:00");
        cut.Find("#billing-payment-reference").Change("TX-9");
        await cut.Find("#billing-payment-form").SubmitAsync(EventArgs.Empty);

        command.Should().NotBeNull();
        command!.Input.Should().Be(new PaymentInput(invoice.Id, 4.5m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.Cash, "TX-9"));
        cut.WaitForAssertion(() => cut.Find("#billing-selected-payment").TextContent.Should().Contain("4.5").And.Contain("Recorded"));
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Partially paid").And.Contain("Paid 4.5"));
        _reads.Should().ContainSingle().Which.Should().Be(Root + $"/documents/{invoice.Id:D}");
        _documentChanges.Last()!.Status.Should().Be(BillingStatus.PartiallyPaid);
        _saved.Should().Be(1);
        cut.Find("#billing-cancel-payment-form").Should().NotBeNull();
    }

    [Fact]
    public async Task Draft_invoice_refuses_payment_locally()
    {
        var cut = Panel(Membership("billing.read", "billing.pay"));
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 1)));
        cut.Find("#billing-start-payment").HasAttribute("disabled").Should().BeTrue();
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(Document(Guid.NewGuid(), BillingKind.Estimate, Input(Guid.NewGuid()), 1) with { Status = BillingStatus.Sent }));
        cut.Find("#billing-start-payment").HasAttribute("disabled").Should().BeTrue("estimates are never paid");
    }

    [Fact]
    public async Task Payment_cancellation_sends_the_reason_and_keeps_the_cancelled_record_selected()
    {
        var membership = Membership("billing.read", "billing.pay");
        var payment = Payment(Guid.NewGuid(), new(Guid.NewGuid(), 3m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.Cash));
        Writes<PaymentRecord>((_, path, body, _) =>
        {
            path.Should().Be(Root + $"/payments/{payment.Id:D}/cancel");
            var command = (BillingController.CancelCommand)body;
            command.Version.Should().Be(payment.Version); command.Reason.Should().Be("wrong account");
            return Task.FromResult(Ok(payment with { Version = Guid.NewGuid(), State = PaymentState.Cancelled, CancelledBy = BusinessUser.ToString("D"), CancelledAt = DateTimeOffset.UtcNow, CancelReason = "wrong account" }));
        });
        Reads<BillingDocument>(_ => Failure<BillingDocument>(404));
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectPaymentAsync(payment));
        cut.Find("#billing-cancel-reason").Change("wrong account");
        await cut.Find("#billing-cancel-payment-form").SubmitAsync(EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find("#billing-selected-payment").TextContent.Should().Contain("Cancelled"));
        cut.FindAll("#billing-cancel-payment-form").Should().BeEmpty("a cancelled payment cannot be cancelled again");
        _reads.Should().BeEmpty("no document is selected, so there is nothing to re-read");
        _saved.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_outcome_keeps_the_intent_and_a_retry_resends_the_same_operation()
    {
        var membership = Membership("billing.read", "billing.write");
        var contact = Contact("CUST-1", "고객");
        var attempts = 0;
        Writes<BillingDocument>((_, _, body, _) =>
        {
            ++attempts;
            var create = (BillingController.DocumentCreate)body;
            return Task.FromResult(attempts == 1 ? Failure<BillingDocument>(503, "INVENTORY_RESPONSE_UNAVAILABLE") : Ok(Document(create.OperationId, create.Kind, create.Input, 1)));
        });
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectContactAsync(contact));
        cut.Find("#billing-start-invoice").Click();
        cut.Find("#billing-document-date").Change("2026-09-22");
        cut.Find("#billing-due-date").Change("2026-10-22");
        cut.Find("#billing-currency").Change("KRW");
        cut.FindAll(".billing-line-description")[0].Change("A");
        cut.FindAll(".billing-line-unit-price")[0].Change("1");
        cut.FindAll(".billing-line-quantity")[0].Change("1");
        await cut.Find("#billing-document-form").SubmitAsync(EventArgs.Empty);

        cut.WaitForAssertion(() => cut.Find("#billing-pending").Should().NotBeNull());
        var intent = await StoredIntent();
        intent.Kind.Should().Be("create"); intent.Confirmed.Should().BeFalse();
        cut.Find("#billing-start-invoice").HasAttribute("disabled").Should().BeTrue("no new request while an outcome is unknown");
        cut.Find("#billing-retry").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("#1"));
        _writes.Should().HaveCount(2);
        ((BillingController.DocumentCreate)_writes[1].Body).OperationId.Should().Be(((BillingController.DocumentCreate)_writes[0].Body).OperationId);
        (await StoredIntentOrNull()).Should().BeNull();
    }

    [Fact]
    public async Task Stored_intent_of_another_scope_is_ignored_and_a_definite_rejection_clears_the_record()
    {
        var membership = Membership("billing.read", "billing.write");
        var draft = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 1);
        Writes<BillingDocument>((_, _, _, _) => Task.FromResult(Failure<BillingDocument>(409, "BUSINESS_VERSION_CONFLICT")));
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft));
        cut.Find("#billing-send").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-workflows [role=alert]").TextContent.Should().Contain("version"));
        (await StoredIntentOrNull()).Should().BeNull("a first definite 409 disproves the attempt");
        cut.Find("#billing-send").HasAttribute("disabled").Should().BeFalse();

        var other = Membership("billing.read", "billing.write") with { OrganizationId = Guid.NewGuid() };
        cut = Panel(other);
        cut.FindAll("#billing-pending").Should().BeEmpty();
    }

    [Fact]
    public async Task Reading_the_current_document_after_an_unknown_outcome_requires_adoption()
    {
        var membership = Membership("billing.read", "billing.write");
        var draft = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 5);
        var current = draft with { Status = BillingStatus.Sent, Version = Guid.NewGuid() };
        Writes<BillingDocument>((_, _, _, _) => Task.FromResult(Failure<BillingDocument>(503, "INVENTORY_RESPONSE_UNAVAILABLE")));
        Reads<BillingDocument>(path => path == Root + $"/documents/{draft.Id:D}" ? Ok(current) : Failure<BillingDocument>(404));
        var cut = Panel(membership);
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(draft));
        cut.Find("#billing-send").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-pending").Should().NotBeNull());
        cut.Find("#billing-read-current").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-observed").TextContent.Should().Contain("Sent"));
        cut.Find("#billing-selected-document").TextContent.Should().Contain("Draft", "reading never replaces the selection silently");
        cut.Find("#billing-adopt").HasAttribute("disabled").Should().BeTrue("the unknown outcome must be closed before editing resumes");
        cut.Find("#billing-acknowledge").Change(true);
        cut.Find("#billing-clear-recovery").Click();
        cut.WaitForAssertion(() => cut.FindAll("#billing-pending").Should().BeEmpty());
        (await StoredIntentOrNull()).Should().BeNull();
        cut.Find("#billing-adopt").Click();
        cut.WaitForAssertion(() => cut.Find("#billing-selected-document").TextContent.Should().Contain("Sent"));
        _documentChanges.Last()!.Status.Should().Be(BillingStatus.Sent);
    }

    [Fact]
    public async Task Foreign_scope_documents_are_rejected_on_selection()
    {
        var cut = Panel(Membership("billing.read", "billing.write"));
        var foreign = Document(Guid.NewGuid(), BillingKind.Invoice, Input(Guid.NewGuid()), 1) with { Scope = new("NexaOne.MES", Tenant.ToString("D"), Guid.NewGuid().ToString("D")) };
        await cut.InvokeAsync(() => cut.Instance.SelectDocumentAsync(foreign));
        cut.FindAll("#billing-selected-document").Should().BeEmpty();
        cut.Find("#billing-workflows [role=alert]").TextContent.Should().Contain("scope");
        _documentChanges.Should().BeEmpty();
    }

    private IRenderedComponent<BillingWorkflowPanel> Panel(BusinessMembership membership)
    {
        var cut = Render<BillingWorkflowPanel>(p => p.Add(c => c.Membership, membership).Add(c => c.UserId, "operator")
            .Add(c => c.Saved, () => { ++_saved; return Task.CompletedTask; })
            .Add(c => c.DocumentChanged, (BillingDocument? d) => { _documentChanges.Add(d); return Task.CompletedTask; }));
        cut.WaitForAssertion(() => cut.Find("#billing-workflows").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private void Writes<T>(Func<HttpMethod, string, object, string, Task<(T?, int, string?, string?)>> respond) where T : class
        => _api.Setup(api => api.WriteInventoryAsync<T>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, object body, string owner, CancellationToken _) =>
            {
                _writes.Add((method, path, body, owner));
                return respond(method, path, body, owner);
            });
    private void Reads<T>(Func<string, (T?, int, string?, string?)> respond) where T : class
        => _api.Setup(api => api.ReadInventoryAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => { _reads.Add(path); return Task.FromResult(respond(path)); });
    private async Task<BillingWorkflowPanel.PendingWrite?> StoredIntentOrNull() => _browser.Values.Count == 0 ? null : await StoredIntent();
    private async Task<BillingWorkflowPanel.PendingWrite> StoredIntent()
    {
        var stored = await _storage.GetAsync<BillingWorkflowPanel.PendingWrite>(_browser.Values.Keys.Single());
        stored.Success.Should().BeTrue();
        return stored.Value!;
    }
    private static (T?, int, string?, string?) Ok<T>(T value) where T : class => (value, 200, null, null);
    private static (T?, int, string?, string?) Failure<T>(int status, string? code = null) where T : class => (null, status, code, "Request failed");
    private static BusinessMembership Membership(params string[] grants) => new(Tenant, Organization, "operator", BusinessUser, true, 1, grants);
    private static BillingContact Contact(string customer, string name) => new(Guid.NewGuid(), Guid.NewGuid(), customer, name, true);
    private static BillingDocumentInput Input(Guid contact) => new(contact, new(2026, 9, 22), new(2026, 10, 22), "KRW", [new("A", 10m, 1m)]);
    private static BillingDocument Document(Guid operation, BillingKind kind, BillingDocumentInput input, long number)
    {
        var subtotal = input.Lines.Sum(l => l.LineTotal);
        var discountable = input.Lines.Where(l => l.ApplyDiscount).Sum(l => l.LineTotal);
        var taxable = input.Lines.Where(l => l.ApplyTax).Sum(l => l.LineTotal);
        var discount = input.Discount is { Type: BillingAdjustmentType.Percent } d ? discountable * d.Value / 100m : input.Discount?.Value ?? 0m;
        var tax = input.Tax is { Type: BillingAdjustmentType.Percent } t ? taxable * t.Value / 100m : input.Tax?.Value ?? 0m;
        return new(Guid.NewGuid(), Business, Guid.NewGuid(), operation, kind, number, input, new(subtotal, discount, tax, subtotal - discount + tax), BillingStatus.Draft, BusinessUser.ToString("D"));
    }
    private static BillingDocumentInput CreditInput(Guid contact)
        => new(contact, new(2026, 9, 24), new(2026, 9, 24), "KRW", [new("Adjustment", 5m, 1m)]);
    private static BillingDocument CreditDocument(Guid operation, Guid invoiceId, BillingDocumentInput input, long number)
        => Document(operation, BillingKind.CreditNote, input, number) with { AdjustedInvoiceId = invoiceId };
    private static PaymentRecord Payment(Guid operation, PaymentInput input)
        => new(Guid.NewGuid(), Business, Guid.NewGuid(), operation, input, BusinessUser.ToString("D"), PaymentState.Recorded);
}
