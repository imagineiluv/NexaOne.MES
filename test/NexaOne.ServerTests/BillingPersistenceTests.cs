using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Erp;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Real SQLite persistence of the ERP billing bridge: scope, grants, documents, lines, numbers, payments and audit.</summary>
public sealed class BillingPersistenceTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly BillingBridge _bridge;
    private readonly BusinessMembershipBridge _memberships;
    private static readonly string[] Grants = ["billing.read", "billing.write", "billing.decide", "billing.pay", "billing.credit", "billing.deliver",
        "delivery.manage-template", "delivery.manage-profile", "delivery.queue"];
    private static readonly DateOnly Issued = new(2026, 9, 22), Due = new(2026, 10, 22);
    private static readonly DateTimeOffset PaidAt = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    public BillingPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _bridge = NewBridge(); _memberships = new(DataSource());
        Execute("""
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE) VALUES ('CUST-1','고객 하나',1), ('CUST-2','Second customer',1), ('CUST-OFF','Closed customer',0);
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('BILL-MEMBER','Billing member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('bill-user','Clerk','','','BILL-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('bill-reader','Reader','','','BILL-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('bill-outsider','Outsider','','','BILL-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }
    public async Task InitializeAsync()
    {
        // No IVT plant binding: billing is organization scoped and needs SYS membership plus grants only.
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "bill-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "bill-reader", new(0, true, ["billing.read"]))).IsSuccess.Should().BeTrue();
    }
    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private BillingBridge NewBridge(TimeProvider? clock = null) => new(DataSource(), new BusinessMembershipBridge(DataSource()), new BusinessMasterDirectory(DataSource()), clock);
    private void Execute(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); c.Execute(sql, values); }
    private T Scalar<T>(string sql, object? values = null) { using var c = new SqliteConnection(_connectionString); c.Open(); return c.ExecuteScalar<T>(sql, values)!; }
    private long Count(string table) => Scalar<long>("SELECT COUNT(*) FROM " + table);
    private Task<BillingContact> Contact(string customer = "CUST-1") => _bridge.EnrollContactAsync("bill-user", _tenant, _organization, customer);
    private static BillingDocumentInput Input(Guid contact, params BillingLine[] lines)
        => new(contact, Issued, Due, "KRW", lines.Length == 0 ? [new("Service", 10m, 1m)] : lines);
    private Task<BillingDocument> Create(Guid contact, BillingKind kind = BillingKind.Invoice, BillingDocumentInput? input = null, Guid? operation = null)
        => _bridge.CreateDocumentAsync("bill-user", _tenant, _organization, operation ?? Guid.NewGuid(), kind, input ?? Input(contact));
    private async Task<BillingDocument> Sent(Guid contact, BillingKind kind = BillingKind.Invoice, BillingDocumentInput? input = null)
    {
        var draft = await Create(contact, kind, input);
        return await _bridge.MarkSentAsync("bill-user", _tenant, _organization, draft.Id, draft.Version);
    }
    private Task<PaymentRecord> Pay(BillingDocument invoice, decimal amount, Guid? operation = null, string currency = "KRW", int hour = 9)
        => _bridge.RecordPaymentAsync("bill-user", _tenant, _organization, operation ?? Guid.NewGuid(),
            new(invoice.Id, amount, currency, PaidAt.AddHours(hour - 9), PaymentMethod.BankTransfer, "TX-1"));
    private Task<BillingDocument> Read(Guid id, string user = "bill-user") => NewBridge().GetDocumentAsync(user, _tenant, _organization, id);
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);
    private static void SameDocument(BillingDocument expected, BillingDocument actual)
    {
        (actual with { Input = expected.Input }).Should().Be(expected);
        actual.Input.Lines.Should().Equal(expected.Input.Lines);
        (actual.Input with { Lines = expected.Input.Lines }).Should().Be(expected.Input);
    }

    [Fact]
    public async Task Public_share_freezes_the_document_audits_access_and_honors_revoke_and_expiry()
    {
        var now = new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var bridge = NewBridge(clock);
        var contact = await bridge.EnrollContactAsync("bill-user", _tenant, _organization, "CUST-1");
        var draft = await bridge.CreateDocumentAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), BillingKind.Invoice, Input(contact.Id, new BillingLine("한글 서비스", 120m, 1m)));
        await Error(() => bridge.CreateShareAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), draft.Id, now.AddHours(1)), "BILLING_DOCUMENT_NOT_DELIVERABLE");
        var sent = await bridge.MarkSentAsync("bill-user", _tenant, _organization, draft.Id, draft.Version);

        var operation = Guid.NewGuid();
        var created = await bridge.CreateShareAsync("bill-user", _tenant, _organization,
            operation, sent.Id, now.AddHours(1));
        created.Token.Should().NotBeNull().And.HaveLength(43);
        var replay = await NewBridge(clock).CreateShareAsync("bill-user", _tenant, _organization,
            operation, sent.Id, now.AddHours(1));
        replay.Link.Id.Should().Be(created.Link.Id);
        replay.Token.Should().BeNull("share secrets are returned only once");
        await Error(() => bridge.CreateShareAsync("bill-user", _tenant, _organization,
            operation, sent.Id, now.AddHours(2)), "BILLING_OPERATION_REUSED");

        await bridge.RecordPaymentAsync("bill-user", _tenant, _organization, Guid.NewGuid(),
            new(sent.Id, 20m, "KRW", now, PaymentMethod.BankTransfer));
        var opened = await NewBridge(clock).OpenPublicShareAsync(created.Token!, "192.0.2.10", "test-agent");
        opened.View.Document.Paid.Should().Be(0m, "the public link owns an immutable snapshot");
        opened.View.Contact.Name.Should().Be("고객 하나");
        opened.Link.AccessCount.Should().Be(1);
        var access = await bridge.ListShareAccessAsync("bill-user", _tenant, _organization, created.Link.Id);
        access.Total.Should().Be(1);
        access.Items.Single().Should().Match<BillingShareAccess>(entry => entry.ClientAddressHash != "192.0.2.10"
            && entry.ClientAddressHash!.Length == 64 && entry.UserAgent == "test-agent");
        Scalar<long>("SELECT ACCESS_COUNT FROM ERP_BILLING_SHARE_LINK WHERE SHARE_ID=@id",
            new { id = created.Link.Id.ToString("D") }).Should().Be(1);
        Scalar<string>("SELECT TOKEN_HASH FROM ERP_BILLING_SHARE_LINK WHERE SHARE_ID=@id",
            new { id = created.Link.Id.ToString("D") }).Should().NotBe(created.Token);

        var revoked = await bridge.RevokeShareAsync("bill-user", _tenant, _organization,
            created.Link.Id, created.Link.Version);
        revoked.RevokedAt.Should().Be(now);
        await Error(() => NewBridge(clock).OpenPublicShareAsync(created.Token!, null, null),
            "BILLING_SHARE_NOT_FOUND");

        var expiring = await bridge.CreateShareAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), sent.Id, now.AddMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(11));
        await Error(() => NewBridge(clock).OpenPublicShareAsync(expiring.Token!, null, null),
            "BILLING_SHARE_NOT_FOUND");
        (await bridge.ListSharesAsync("bill-user", _tenant, _organization, sent.Id)).Total.Should().Be(2);
    }

    [Fact]
    public async Task Public_share_queues_an_idempotent_email_through_the_delivery_outbox()
    {
        var now = new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
        var bridge = NewBridge(new MutableTimeProvider(now));
        var contact = await bridge.EnrollContactAsync("bill-user", _tenant, _organization, "CUST-1");
        var draft = await bridge.CreateDocumentAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), BillingKind.Invoice, Input(contact.Id));
        var sent = await bridge.MarkSentAsync("bill-user", _tenant, _organization, draft.Id, draft.Version);
        var share = await bridge.CreateShareAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), sent.Id, now.AddHours(1));
        var template = await bridge.CreateTemplateAsync("bill-user", _tenant, _organization,
            "Invoice link", "Invoice {{documentNumber}}", "{{customerName}}: {{publicUrl}} ({{expiresAt}})",
            ["documentNumber", "customerName", "publicUrl", "expiresAt"]);
        var profile = await bridge.CreateProfileAsync("bill-user", _tenant, _organization,
            "SMTP", "smtp", "secret://smtp", new(3, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
        var operation = Guid.NewGuid();
        var publicUrl = $"https://billing.example.test/api/v1/erp/billing/public/{share.Token}/pdf";

        var queued = await bridge.QueueShareDeliveryAsync("bill-user", _tenant, _organization,
            operation, share.Link.Id, share.Token!, template.Id, profile.Id, "customer@example.test", publicUrl);
        var replay = await NewBridge(new MutableTimeProvider(now)).QueueShareDeliveryAsync("bill-user",
            _tenant, _organization, operation, share.Link.Id, share.Token!, template.Id, profile.Id,
            "customer@example.test", publicUrl);

        replay.Delivery.Id.Should().Be(queued.Delivery.Id);
        queued.Delivery.Payload.Subject.Should().Be("Invoice 1");
        queued.Delivery.Payload.Body.Should().Contain("고객 하나").And.Contain(publicUrl);
        Count("COL_DELIVERY_REQUEST").Should().Be(1);
        Count("ERP_BILLING_SHARE_DELIVERY").Should().Be(1);
        var history = await bridge.ListShareDeliveriesAsync("bill-user", _tenant, _organization,
            share.Link.Id);
        history.Total.Should().Be(1);
        history.Items.Single().Delivery.Id.Should().Be(queued.Delivery.Id);
        await Error(() => bridge.QueueShareDeliveryAsync("bill-user", _tenant, _organization,
            Guid.NewGuid(), share.Link.Id, new string('x', 43), template.Id, profile.Id,
            "customer@example.test", publicUrl), "BILLING_SHARE_NOT_FOUND");
    }

    [Fact]
    public async Task Contact_enrollment_is_idempotent_and_lists_live_customer_names()
    {
        var first = await Contact();
        var again = await Contact();
        again.Should().Be(first);
        first.CustomerId.Should().Be("CUST-1"); first.Name.Should().Be("고객 하나"); first.Active.Should().BeTrue();
        await Error(() => Contact("MISSING"), "CUSTOMER_NOT_FOUND");
        await Error(() => Contact("CUST-OFF"), "CUSTOMER_INACTIVE");
        var second = await Contact("CUST-2");
        Execute("UPDATE MDM_CUSTOMER SET CUSTOMER_NAME='Renamed', IS_ACTIVE=0 WHERE CUSTOMER_ID='CUST-2'");
        var page = await NewBridge().ListContactsAsync("bill-reader", _tenant, _organization);
        page.Total.Should().Be(2);
        page.Items.Select(c => (c.Id, c.Name, c.Active)).Should().Equal((first.Id, "고객 하나", true), (second.Id, "Renamed", false));
        (await NewBridge().ListContactsAsync("bill-reader", _tenant, _organization, offset: 1, limit: 1)).Items.Single().Id.Should().Be(second.Id);
        Count("ERP_BILLING_CONTACT").Should().Be(2);
        await Error(() => _bridge.EnrollContactAsync("bill-reader", _tenant, _organization, "CUST-1"), "BUSINESS_ACCESS_DENIED");
    }

    [Fact]
    public async Task Estimate_flows_to_invoice_and_payments_persist_with_recomputed_totals()
    {
        var contact = await Contact();
        var input = Input(contact.Id, new("설계 ' % _", 0.333333m, 3m), new("Hosting", 50m, 2m, ApplyTax: false, ApplyDiscount: false))
            with { Discount = new(BillingAdjustmentType.Percent, 10m), Tax = new(BillingAdjustmentType.Percent, 7.5m), Terms = "net 30", Note = "메모" };
        var estimate = await Create(contact.Id, BillingKind.Estimate, input);
        estimate.Number.Should().Be(1); estimate.Status.Should().Be(BillingStatus.Draft);
        estimate.Totals.Should().Be(new BillingTotals(100.999999m, 0.1m, 0.075m, 100.974999m));
        Count("ERP_BILLING_DOCUMENT").Should().Be(1); Count("ERP_BILLING_LINE").Should().Be(2);
        Scalar<string>("SELECT SUBTOTAL FROM ERP_BILLING_DOCUMENT").Should().Be("100.999999");
        SameDocument(estimate, await Read(estimate.Id));

        var sent = await _bridge.MarkSentAsync("bill-user", _tenant, _organization, estimate.Id, estimate.Version);
        var accepted = await _bridge.DecideEstimateAsync("bill-user", _tenant, _organization, sent.Id, sent.Version, true);
        accepted.Status.Should().Be(BillingStatus.Accepted);
        var operation = Guid.NewGuid();
        var invoice = await _bridge.ConvertEstimateAsync("bill-user", _tenant, _organization, operation, accepted.Id, accepted.Version);
        invoice.Kind.Should().Be(BillingKind.Invoice); invoice.Number.Should().Be(1); invoice.ConvertedFromId.Should().Be(estimate.Id);
        invoice.Totals.Should().Be(estimate.Totals); invoice.Input.Lines.Should().Equal(estimate.Input.Lines);
        (await Read(estimate.Id)).ConvertedToId.Should().Be(invoice.Id);
        SameDocument(invoice, await NewBridge().ConvertEstimateAsync("bill-user", _tenant, _organization, operation, accepted.Id, accepted.Version));
        Count("ERP_BILLING_DOCUMENT").Should().Be(2); Count("ERP_BILLING_LINE").Should().Be(4);

        var sentInvoice = await _bridge.MarkSentAsync("bill-user", _tenant, _organization, invoice.Id, invoice.Version);
        var first = await Pay(sentInvoice, 100m);
        (await Read(invoice.Id)).Should().Match<BillingDocument>(d => d.Status == BillingStatus.PartiallyPaid && d.Paid == 100m && d.Due == 0.974999m);
        var second = await Pay(sentInvoice, 0.974999m, hour: 10); // later paid time: the list order is paid time, then ID
        (await Read(invoice.Id)).Status.Should().Be(BillingStatus.FullyPaid);
        var cancelled = await _bridge.CancelPaymentAsync("bill-user", _tenant, _organization, second.Id, second.Version, "정정");
        cancelled.State.Should().Be(PaymentState.Cancelled); cancelled.CancelledBy.Should().Be(first.CreatedBy); cancelled.CancelReason.Should().Be("정정");
        (await Read(invoice.Id)).Should().Match<BillingDocument>(d => d.Status == BillingStatus.PartiallyPaid && d.Paid == 100m);
        (await NewBridge().GetPaymentAsync("bill-reader", _tenant, _organization, second.Id)).Should().Be(cancelled);
        var payments = await NewBridge().ListPaymentsAsync("bill-reader", _tenant, _organization, invoice.Id);
        payments.Total.Should().Be(2); payments.Items.Select(p => p.Id).Should().Equal(first.Id, second.Id);
        (await NewBridge().ListPaymentsAsync("bill-reader", _tenant, _organization, invoice.Id, PaymentState.Recorded)).Items.Single().Id.Should().Be(first.Id);

        var invoices = await NewBridge().ListDocumentsAsync("bill-reader", _tenant, _organization, BillingKind.Invoice);
        invoices.Total.Should().Be(1); SameDocument(await Read(invoice.Id), invoices.Items[0]);
        (await NewBridge().ListDocumentsAsync("bill-reader", _tenant, _organization, status: BillingStatus.Accepted)).Items.Single().Id.Should().Be(estimate.Id);
        (await NewBridge().ListDocumentsAsync("bill-reader", _tenant, _organization, contactId: Guid.NewGuid())).Total.Should().Be(0);
        Count("ERP_BILLING_PAYMENT").Should().Be(2);
        // enrolled, created, sent, accepted, converted x2, sent, recorded+paid x2, cancelled+payment-cancelled
        Count("ERP_BILLING_AUDIT").Should().Be(13);
        Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_NUMBER").Should().Be(2);
    }

    [Fact]
    public async Task Credit_note_issue_and_void_are_restart_safe_and_atomic()
    {
        var contact = await Contact();
        var invoice = await Sent(contact.Id, input: Input(contact.Id, new BillingLine("Service", 100m, 1m)));
        var creditInput = new BillingDocumentInput(contact.Id, Issued.AddDays(1), Issued.AddDays(1), "KRW",
            [new("Correction", 30m, 1m)]);
        var operation = Guid.NewGuid();
        var note = await _bridge.CreateCreditNoteAsync("bill-user", _tenant, _organization,
            operation, invoice.Id, creditInput);
        SameDocument(note, await NewBridge().CreateCreditNoteAsync("bill-user", _tenant, _organization,
            operation, invoice.Id, creditInput));
        await Error(() => NewBridge().CreateCreditNoteAsync("bill-reader", _tenant, _organization,
            Guid.NewGuid(), invoice.Id, creditInput), "BUSINESS_ACCESS_DENIED");

        Execute("""
            CREATE TRIGGER FAIL_CREDIT_ISSUE BEFORE UPDATE ON ERP_BILLING_DOCUMENT
            WHEN OLD.KIND=2 AND NEW.STATUS=1 BEGIN SELECT RAISE(ABORT,'issue'); END;
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => NewBridge().IssueCreditNoteAsync(
            "bill-user", _tenant, _organization, note.Id, note.Version));
        (await Read(invoice.Id)).Credited.Should().Be(0m);
        (await Read(note.Id)).Status.Should().Be(BillingStatus.Draft);
        Execute("DROP TRIGGER FAIL_CREDIT_ISSUE");

        var issued = await NewBridge().IssueCreditNoteAsync("bill-user", _tenant, _organization,
            note.Id, note.Version);
        var adjusted = await Read(invoice.Id);
        adjusted.Should().Match<BillingDocument>(value => value.Credited == 30m && value.Due == 70m
            && value.Status == BillingStatus.PartiallyCredited);
        Scalar<string>("SELECT ADJUSTED_INVOICE_ID FROM ERP_BILLING_DOCUMENT WHERE DOCUMENT_ID=@id",
            new { id = note.Id.ToString("D") }).Should().Be(invoice.Id.ToString("D"));
        Scalar<string>("SELECT CREDITED FROM ERP_BILLING_DOCUMENT WHERE DOCUMENT_ID=@id",
            new { id = invoice.Id.ToString("D") }).Should().Be("30");

        var voided = await NewBridge().VoidCreditNoteAsync("bill-user", _tenant, _organization,
            issued.Id, issued.Version);
        voided.Status.Should().Be(BillingStatus.Void);
        (await Read(invoice.Id)).Should().Match<BillingDocument>(value => value.Credited == 0m
            && value.Due == 100m && value.Status == BillingStatus.Sent);
    }

    [Fact]
    public async Task Numbers_are_issued_per_kind_and_never_reused_across_bridges()
    {
        var contact = await Contact();
        var e1 = await Create(contact.Id, BillingKind.Estimate);
        var e2 = await NewBridge().CreateDocumentAsync("bill-user", _tenant, _organization, Guid.NewGuid(), BillingKind.Estimate, Input(contact.Id));
        var i1 = await Create(contact.Id);
        var voided = await _bridge.VoidDocumentAsync("bill-user", _tenant, _organization, i1.Id, i1.Version);
        var i2 = await Create(contact.Id);
        (e1.Number, e2.Number, i1.Number, i2.Number).Should().Be((1L, 2L, 1L, 2L));
        voided.Status.Should().Be(BillingStatus.Void);
        Scalar<long>("SELECT NEXT_NUMBER FROM ERP_BILLING_NUMBER WHERE KIND=1").Should().Be(2);
    }

    [Fact]
    public async Task Operation_replay_returns_the_stored_row_and_changed_input_conflicts()
    {
        var contact = await Contact(); var operation = Guid.NewGuid();
        var doc = await Create(contact.Id, operation: operation);
        SameDocument(doc, await NewBridge().CreateDocumentAsync("bill-user", _tenant, _organization, operation, BillingKind.Invoice, Input(contact.Id)));
        await Error(() => Create(contact.Id, input: Input(contact.Id, new BillingLine("Other", 1m, 1m)), operation: operation), "BILLING_OPERATION_CONFLICT");
        var sent = await _bridge.MarkSentAsync("bill-user", _tenant, _organization, doc.Id, doc.Version);
        var payOperation = Guid.NewGuid();
        var payment = await Pay(sent, 4m, payOperation);
        (await NewBridge().RecordPaymentAsync("bill-user", _tenant, _organization, payOperation, payment.Input)).Should().Be(payment);
        await Error(() => Pay(sent, 5m, payOperation), "PAYMENT_OPERATION_CONFLICT");
        Count("ERP_BILLING_DOCUMENT").Should().Be(1); Count("ERP_BILLING_PAYMENT").Should().Be(1);
        (await Read(doc.Id)).Paid.Should().Be(4m);
    }

    [Fact]
    public async Task Draft_update_replaces_lines_and_stale_versions_conflict()
    {
        var contact = await Contact();
        var doc = await Create(contact.Id, input: Input(contact.Id, new("A", 1m, 1m), new("B", 2m, 1m), new("C", 3m, 1m)));
        Count("ERP_BILLING_LINE").Should().Be(3);
        var updated = await _bridge.UpdateDocumentAsync("bill-user", _tenant, _organization, doc.Id, doc.Version,
            Input(contact.Id, new BillingLine("Only", 5m, 2m, ApplyTax: false)) with { Note = "revised" });
        updated.Totals.Total.Should().Be(10m); updated.Number.Should().Be(doc.Number);
        Count("ERP_BILLING_LINE").Should().Be(1);
        Scalar<string>("SELECT DESCRIPTION FROM ERP_BILLING_LINE").Should().Be("Only");
        SameDocument(updated, await Read(doc.Id));
        await Error(() => _bridge.UpdateDocumentAsync("bill-user", _tenant, _organization, doc.Id, doc.Version, Input(contact.Id)), "BUSINESS_VERSION_CONFLICT");
        var sent = await _bridge.MarkSentAsync("bill-user", _tenant, _organization, doc.Id, updated.Version);
        await Error(() => _bridge.UpdateDocumentAsync("bill-user", _tenant, _organization, doc.Id, sent.Version, Input(contact.Id)), "BILLING_DOCUMENT_NOT_DRAFT");
        Count("ERP_BILLING_LINE").Should().Be(1);
    }

    [Fact]
    public async Task Void_refuses_paid_invoices_and_converted_estimates()
    {
        var contact = await Contact();
        var invoice = await Sent(contact.Id);
        await Pay(invoice, 1m);
        var paid = await Read(invoice.Id);
        await Error(() => _bridge.VoidDocumentAsync("bill-user", _tenant, _organization, invoice.Id, paid.Version), "BILLING_DOCUMENT_HAS_PAYMENTS");
        var estimate = await Sent(contact.Id, BillingKind.Estimate);
        var accepted = await _bridge.DecideEstimateAsync("bill-user", _tenant, _organization, estimate.Id, estimate.Version, true);
        await _bridge.ConvertEstimateAsync("bill-user", _tenant, _organization, Guid.NewGuid(), accepted.Id, accepted.Version);
        var converted = await Read(estimate.Id);
        await Error(() => _bridge.VoidDocumentAsync("bill-user", _tenant, _organization, estimate.Id, converted.Version), "BILLING_DOCUMENT_CONVERTED");
        await Error(() => _bridge.DecideEstimateAsync("bill-user", _tenant, _organization, invoice.Id, paid.Version, true), "BILLING_NOT_ESTIMATE");
    }

    [Fact]
    public async Task Scope_membership_and_grants_are_enforced_without_a_plant_binding()
    {
        var contact = await Contact();
        var doc = await Create(contact.Id);
        var otherOrganization = Guid.NewGuid();
        (await _memberships.SaveMembershipAsync("admin", _tenant, otherOrganization, "bill-outsider", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        await Error(() => NewBridge().GetDocumentAsync("bill-outsider", _tenant, otherOrganization, doc.Id), "BILLING_DOCUMENT_NOT_FOUND");
        (await NewBridge().ListDocumentsAsync("bill-outsider", _tenant, otherOrganization)).Total.Should().Be(0);
        await Error(() => NewBridge().CreateDocumentAsync("bill-outsider", _tenant, otherOrganization, Guid.NewGuid(), BillingKind.Invoice, Input(contact.Id)), "BILLING_CONTACT_NOT_FOUND");
        await Error(() => NewBridge().GetDocumentAsync("bill-outsider", _tenant, _organization, doc.Id), "BUSINESS_ACCESS_DENIED");
        await Error(() => NewBridge().CreateDocumentAsync("bill-reader", _tenant, _organization, Guid.NewGuid(), BillingKind.Invoice, Input(contact.Id)), "BUSINESS_ACCESS_DENIED");
        await Error(() => NewBridge().DecideEstimateAsync("bill-reader", _tenant, _organization, doc.Id, doc.Version, true), "BUSINESS_ACCESS_DENIED");
        await Error(() => NewBridge().RecordPaymentAsync("bill-reader", _tenant, _organization, Guid.NewGuid(), new(doc.Id, 1m, "KRW", PaidAt, PaymentMethod.Cash)), "BUSINESS_ACCESS_DENIED");
        await Error(() => NewBridge().GetDocumentAsync("nobody", _tenant, _organization, doc.Id), "BUSINESS_ACCESS_DENIED");
        SameDocument(doc, await Read(doc.Id, "bill-reader"));
        Count("ERP_BILLING_DOCUMENT").Should().Be(1);
    }

    [Fact]
    public async Task Accessible_scopes_list_only_memberships_with_billing_grants_in_stable_order()
    {
        var other = Guid.NewGuid();
        (await _memberships.SaveMembershipAsync("admin", _tenant, other, "bill-user", new(0, true, ["stock.read"]))).IsSuccess.Should().BeTrue();
        var page = await _bridge.ListAccessibleScopesAsync("bill-user");
        page.Total.Should().Be(1); page.Items.Single().OrganizationId.Should().Be(_organization);
        (await _bridge.ListAccessibleScopesAsync("bill-reader")).Items.Single().Permissions.Should().Equal("billing.read");
        (await _bridge.ListAccessibleScopesAsync("bill-outsider")).Total.Should().Be(0);
        (await _bridge.ListAccessibleScopesAsync("bill-user", offset: 1)).Items.Should().BeEmpty();
        await Error(() => _bridge.ListAccessibleScopesAsync("bill-user", limit: 0), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.ListAccessibleScopesAsync(" ", limit: 1), "BUSINESS_ACCESS_DENIED");
    }

    [Fact]
    public async Task Payments_require_the_invoice_currency_and_a_payable_status()
    {
        var contact = await Contact();
        var draft = await Create(contact.Id);
        await Error(() => Pay(draft, 1m), "BILLING_DOCUMENT_NOT_PAYABLE");
        var sent = await _bridge.MarkSentAsync("bill-user", _tenant, _organization, draft.Id, draft.Version);
        await Error(() => Pay(sent, 1m, currency: "USD"), "PAYMENT_CURRENCY_MISMATCH");
        await Error(() => _bridge.RecordPaymentAsync("bill-user", _tenant, _organization, Guid.NewGuid(), new(sent.Id, 0m, "KRW", PaidAt, PaymentMethod.Cash)), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.CancelPaymentAsync("bill-user", _tenant, _organization, Guid.NewGuid(), Guid.NewGuid(), null), "PAYMENT_NOT_FOUND");
        Count("ERP_BILLING_PAYMENT").Should().Be(0);
        (await Read(draft.Id)).Paid.Should().Be(0m);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
