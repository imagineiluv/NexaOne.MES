using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ERP.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Collection(ChildProcessSmokeCollection.Name)]
[Trait("Category", "HostSmoke")]
public sealed class BillingHostTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions HttpJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Real_host_resolves_billing_contract_and_runs_estimate_invoice_and_payment_over_http()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        var seed = new BillingProductSeed();
        await using var database = new SqliteConnection(
            $"Data Source={host.DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=10");
        await database.OpenAsync();
        await database.ExecuteAsync(BillingProductSeed.Sql, seed);
        using var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var route = $"/api/v1/erp/billing/{tenant}/{organization}";
        await Status(await admin.GetAsync(route + "/contacts"), HttpStatusCode.Unauthorized);
        await Login(admin, "admin", "admin", seed.Plant);
        var diagnostics = await Body<JsonElement>(await admin.GetAsync("/diag"));
        var descriptor = diagnostics.GetProperty("bridges").EnumerateArray().Single(item =>
            item.GetProperty("contract").GetString() == typeof(IBillingBridge).FullName);
        descriptor.GetProperty("module").GetString().Should().Be("Erp");
        descriptor.GetProperty("beanName").GetString().Should().Be("billingBridge");
        descriptor.GetProperty("implementation").GetString().Should().Be(typeof(BillingBridge).FullName);
        var expenseDescriptor = diagnostics.GetProperty("bridges").EnumerateArray().Single(item =>
            item.GetProperty("contract").GetString() == typeof(IExpenseBridge).FullName);
        expenseDescriptor.GetProperty("module").GetString().Should().Be("Erp");
        expenseDescriptor.GetProperty("beanName").GetString().Should().Be("expenseBridge");
        expenseDescriptor.GetProperty("implementation").GetString().Should().Be(typeof(BillingBridge).FullName);
        var reportDescriptor = diagnostics.GetProperty("bridges").EnumerateArray().Single(item =>
            item.GetProperty("contract").GetString() == typeof(IFinancialReportBridge).FullName);
        reportDescriptor.GetProperty("module").GetString().Should().Be("Erp");
        reportDescriptor.GetProperty("beanName").GetString().Should().Be("financialReportBridge");
        reportDescriptor.GetProperty("implementation").GetString().Should().Be(typeof(BillingBridge).FullName);
        // Membership and grants only: no IVT plant binding is created for this organization.
        await Status(await admin.PutAsync(route + "/contacts/" + seed.Customer, null), HttpStatusCode.Forbidden);
        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/{seed.User}";
        var membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(0, true, BillingProductSeed.Grants)));
        using var member = new HttpClient { BaseAddress = admin.BaseAddress };
        await Login(member, seed.User, BillingProductSeed.Password, seed.Plant);
        // Scope discovery lists memberships with any billing grant; no IVT plant binding is consulted.
        var scopes = await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/billing/scopes/me"));
        scopes.Total.Should().Be(1); scopes.Items.Single().Should().Match<BusinessMembership>(m => m.TenantId == tenant && m.OrganizationId == organization);
        (await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/financial-reports/scopes/me"))).Total.Should().Be(1);
        (await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/cash-flow-reports/scopes/me"))).Total.Should().Be(1);
        (await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/expenses/scopes/me"))).Total
            .Should().Be(0, "billing-only grants must not disclose an expense scope");
        (await Body<BusinessPage<BusinessMembership>>(await admin.GetAsync("/api/v1/erp/billing/scopes/me"))).Total.Should().Be(0);
        await Error(await member.GetAsync("/api/v1/erp/billing/scopes/me?limit=0"), HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");

        var contact = await Body<BillingContact>(await member.PutAsync(route + "/contacts/" + seed.Customer, null));
        contact.Name.Should().Be("청구 고객"); contact.Active.Should().BeTrue();
        (await Body<BillingContact>(await member.PutAsync(route + "/contacts/" + seed.Customer, null))).Should().Be(contact);
        await Error(await member.PutAsync(route + "/contacts/MISSING-" + Guid.NewGuid().ToString("N"), null), HttpStatusCode.NotFound, "CUSTOMER_NOT_FOUND");
        var contacts = await Body<BusinessPage<BillingContact>>(await member.GetAsync(route + "/contacts"));
        contacts.Total.Should().Be(1); contacts.Items.Single().Should().Be(contact);
        var automatic = new BillingController.AutomaticDocumentCreate(Guid.NewGuid(),
            new(contact.Id, BillingInvoiceType.ByProducts, new(2026, 9, 1), new(2026, 9, 30),
                new(2026, 9, 30), new(2026, 10, 30), "KRW"));
        await Error(await member.PostAsJsonAsync(route + "/documents/automatic", automatic, HttpJson),
            HttpStatusCode.NotFound, "AUTOMATIC_BILLING_SOURCE_NOT_FOUND");

        var input = new BillingDocumentInput(contact.Id, new(2026, 9, 22), new(2026, 10, 22), "KRW",
            [new("설계 ' % _", BillingProductSeed.PreciseAmount, 1m), new("Hosting", 50m, 2m, ApplyTax: false, ApplyDiscount: false)],
            Discount: new(BillingAdjustmentType.Flat, 0.5m), Tax: new(BillingAdjustmentType.Percent, 10m));
        var create = new BillingController.DocumentCreate(Guid.NewGuid(), BillingKind.Estimate, input);
        var estimate = await Body<BillingDocument>(await member.PostAsJsonAsync(route + "/documents", create, HttpJson));
        estimate.Number.Should().Be(1); estimate.Input.Lines[0].UnitPrice.Should().Be(BillingProductSeed.PreciseAmount);
        estimate.Totals.Should().Be(new BillingTotals(BillingProductSeed.PreciseAmount + 100m, 0.5m,
            decimal.Round(BillingProductSeed.PreciseAmount * 0.1m, 6, MidpointRounding.AwayFromZero),
            BillingProductSeed.PreciseAmount + 100m - 0.5m + decimal.Round(BillingProductSeed.PreciseAmount * 0.1m, 6, MidpointRounding.AwayFromZero)));
        (await Body<BillingDocument>(await member.PostAsJsonAsync(route + "/documents", create, HttpJson))).Id.Should().Be(estimate.Id);
        await Error(await member.PostAsJsonAsync(route + "/documents", create with { Kind = BillingKind.Invoice }, HttpJson), HttpStatusCode.Conflict, "BILLING_OPERATION_CONFLICT");
        await Error(await member.PostAsJsonAsync(route + "/documents", create with { OperationId = Guid.NewGuid(), Input = input with { Currency = "krw" } }, HttpJson),
            HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");

        var sent = await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/sent", new BillingController.VersionedCommand(estimate.Version), HttpJson));
        await Error(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/sent", new BillingController.VersionedCommand(estimate.Version), HttpJson),
            HttpStatusCode.Conflict, "BUSINESS_VERSION_CONFLICT");
        var accepted = await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/decision", new BillingController.DecisionCommand(sent.Version, true), HttpJson));
        accepted.Status.Should().Be(BillingStatus.Accepted);
        var invoice = await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/conversion", new BillingController.ConversionCommand(Guid.NewGuid(), accepted.Version), HttpJson));
        invoice.Kind.Should().Be(BillingKind.Invoice); invoice.ConvertedFromId.Should().Be(estimate.Id); invoice.Totals.Should().Be(estimate.Totals);
        (await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{estimate.Id}"))).ConvertedToId.Should().Be(invoice.Id);
        var invoiceSent = await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/documents/{invoice.Id}/sent", new BillingController.VersionedCommand(invoice.Version), HttpJson));

        using (var pdf = await member.GetAsync(route + $"/documents/{invoice.Id}/pdf"))
        {
            pdf.StatusCode.Should().Be(HttpStatusCode.OK, await pdf.Content.ReadAsStringAsync());
            pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            (await pdf.Content.ReadAsByteArrayAsync()).Should().StartWith([0x25, 0x50, 0x44, 0x46]);
        }
        var share = await Body<BillingController.ShareCreated>(await member.PostAsJsonAsync(
            route + $"/documents/{invoice.Id}/shares",
            new BillingController.ShareCreate(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1)), HttpJson));
        share.SecretReturned.Should().BeTrue();
        share.PublicUrl.Should().NotBeNull();
        using (var publicPdf = await admin.GetAsync(share.PublicUrl))
        {
            publicPdf.StatusCode.Should().Be(HttpStatusCode.OK, await publicPdf.Content.ReadAsStringAsync());
            publicPdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            publicPdf.Headers.CacheControl!.NoStore.Should().BeTrue();
            publicPdf.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        }
        (await Body<BusinessPage<BillingShareAccess>>(await member.GetAsync(
            route + $"/shares/{share.Link.Id}/access"))).Total.Should().Be(1);
        await Body<BillingShareLink>(await member.PostAsJsonAsync(route + $"/shares/{share.Link.Id}/revoke",
            new BillingController.VersionedCommand(share.Link.Version), HttpJson));
        await Error(await admin.GetAsync(share.PublicUrl), HttpStatusCode.NotFound, "BILLING_SHARE_NOT_FOUND");

        var paymentRoute = route + $"/documents/{invoice.Id}/payments";
        var pay = new BillingController.PaymentCommand(Guid.NewGuid(), new(invoice.Id, 100m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.BankTransfer, "TX"));
        var payment = await Body<PaymentRecord>(await member.PostAsJsonAsync(paymentRoute, pay, HttpJson));
        (await Body<PaymentRecord>(await member.PostAsJsonAsync(paymentRoute, pay, HttpJson))).Should().Be(payment);
        await Error(await member.PostAsJsonAsync(paymentRoute, pay with { Input = pay.Input with { Amount = 1m } }, HttpJson), HttpStatusCode.Conflict, "PAYMENT_OPERATION_CONFLICT");
        await Error(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/payments", pay with { OperationId = Guid.NewGuid() }, HttpJson), HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");
        var partiallyPaid = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        partiallyPaid.Status.Should().Be(BillingStatus.PartiallyPaid); partiallyPaid.Paid.Should().Be(100m);
        var creditInput = new BillingDocumentInput(contact.Id, new(2026, 9, 23), new(2026, 9, 23), "KRW",
            [new("Correction", 10m, 1m)]);
        var creditCreate = new BillingController.CreditNoteCreate(Guid.NewGuid(), creditInput);
        var credit = await Body<BillingDocument>(await member.PostAsJsonAsync(
            route + $"/documents/{invoice.Id}/credits", creditCreate, HttpJson));
        (await Body<BillingDocument>(await member.PostAsJsonAsync(
            route + $"/documents/{invoice.Id}/credits", creditCreate, HttpJson))).Id.Should().Be(credit.Id);
        var issuedCredit = await Body<BillingDocument>(await member.PostAsJsonAsync(
            route + $"/credit-notes/{credit.Id}/issue", new BillingController.VersionedCommand(credit.Version), HttpJson));
        var partiallyCredited = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        partiallyCredited.Status.Should().Be(BillingStatus.PartiallyCredited);
        partiallyCredited.Credited.Should().Be(10m);
        var reportRoute = $"/api/v1/erp/financial-reports/{tenant}/{organization}?start=2026-09-01&end=2026-09-30";
        var report = await Body<FinancialReport>(await member.GetAsync(reportRoute));
        report.Currencies.Should().ContainSingle().Which.Should().Match<FinancialCurrencyTotals>(value =>
            value.Currency == "KRW" && value.InvoiceCount == 1 && value.Paid == 100m && value.Credited == 10m
            && value.Invoiced == invoice.Totals.Total && value.Outstanding == invoice.Totals.Total - 110m);
        using (var csv = await member.GetAsync(reportRoute.Replace("?", "/export.csv?", StringComparison.Ordinal)))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
            csv.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
            (await csv.Content.ReadAsStringAsync()).Should().Contain("currency,invoice_count,invoiced,paid,credited,outstanding")
                .And.Contain(",1,").And.Contain(",100,");
        }
        var cashRoute = $"/api/v1/erp/cash-flow-reports/{tenant}/{organization}?start=2026-09-22&end=2026-09-22";
        var cash = await Body<CashFlowReport>(await member.GetAsync(cashRoute));
        cash.Currencies.Should().Equal(new CashFlowCurrencyTotals("KRW", 1, 100m));
        using (var csv = await member.GetAsync(cashRoute.Replace("?", "/export.csv?", StringComparison.Ordinal)))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
            csv.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
            csv.Content.Headers.ContentDisposition!.FileNameStar.Should()
                .Be("cash-flow-report-20260922-20260922.csv");
            (await csv.Content.ReadAsStringAsync()).Should()
                .Contain("currency,payment_count,received").And.Contain("KRW,1,100");
        }
        var snapshotId = Guid.NewGuid();
        var snapshotRoute = $"/api/v1/erp/report-snapshots/{tenant}/{organization}";
        var snapshotRequest = new CreateFinancialReportSnapshotRequest(snapshotId,
            FinancialReportSnapshotKind.CashFlow, new(2026, 9, 22), new(2026, 9, 22));
        var snapshot = await Body<FinancialReportSnapshot>(await member.PostAsJsonAsync(
            snapshotRoute, snapshotRequest, HttpJson));
        snapshot.Summary.Id.Should().Be(snapshotId);
        snapshot.CashFlowReport!.Currencies.Should().Equal(new CashFlowCurrencyTotals("KRW", 1, 100m));
        (await Body<FinancialReportSnapshot>(await member.PostAsJsonAsync(
            snapshotRoute, snapshotRequest, HttpJson))).Should().BeEquivalentTo(snapshot);
        (await Body<BusinessPage<FinancialReportSnapshotSummary>>(await member.GetAsync(
            snapshotRoute + "?kind=CashFlow"))).Total.Should().Be(1);
        using (var csv = await member.GetAsync(snapshotRoute + $"/{snapshotId}/export.csv"))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
            csv.Content.Headers.ContentDisposition!.FileNameStar.Should().Contain(snapshotId.ToString("D"));
            (await csv.Content.ReadAsStringAsync()).Should().Contain("KRW,1,100");
        }
        (await Body<FinancialReportSnapshotComparison>(await member.PostAsync(
            snapshotRoute + $"/{snapshotId}/regenerate", null))).Matches.Should().BeTrue();
        await Error(await member.PostAsJsonAsync(route + $"/documents/{invoice.Id}/void", new BillingController.VersionedCommand(partiallyCredited.Version), HttpJson),
            HttpStatusCode.Conflict, "BILLING_DOCUMENT_HAS_PAYMENTS");
        var cancelled = await Body<PaymentRecord>(await member.PostAsJsonAsync(route + $"/payments/{payment.Id}/cancel", new BillingController.CancelCommand(payment.Version, "정정"), HttpJson));
        cancelled.State.Should().Be(PaymentState.Cancelled); cancelled.CancelReason.Should().Be("정정");
        (await Body<CashFlowReport>(await member.GetAsync(cashRoute))).Currencies.Should().BeEmpty();
        (await Body<FinancialReportSnapshotComparison>(await member.PostAsync(
            snapshotRoute + $"/{snapshotId}/regenerate", null))).Matches.Should().BeFalse();
        var snapshotAudit = await Body<BusinessPage<FinancialReportSnapshotAuditEntry>>(await member.GetAsync(
            snapshotRoute + $"/{snapshotId}/audit"));
        snapshotAudit.Total.Should().Be(4);
        (await Body<PaymentRecord>(await member.GetAsync(route + $"/payments/{payment.Id}"))).Should().Be(cancelled);
        var payments = await Body<BusinessPage<PaymentRecord>>(await member.GetAsync(paymentRoute + "?state=Cancelled"));
        payments.Total.Should().Be(1); payments.Items.Single().Should().Be(cancelled);
        var back = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        back.Status.Should().Be(BillingStatus.PartiallyCredited); back.Paid.Should().Be(0m); back.Credited.Should().Be(10m);
        await Error(await member.PostAsJsonAsync(route + $"/documents/{invoice.Id}/void", new BillingController.VersionedCommand(back.Version), HttpJson),
            HttpStatusCode.Conflict, "BILLING_DOCUMENT_HAS_CREDITS");
        await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/credit-notes/{credit.Id}/void",
            new BillingController.VersionedCommand(issuedCredit.Version), HttpJson));
        back = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        back.Status.Should().Be(BillingStatus.Sent); back.Credited.Should().Be(0m);
        var voided = await Body<BillingDocument>(await member.PostAsJsonAsync(route + $"/documents/{invoice.Id}/void", new BillingController.VersionedCommand(back.Version), HttpJson));
        voided.Status.Should().Be(BillingStatus.Void);

        var documents = await Body<BusinessPage<BillingDocument>>(await member.GetAsync(route + "/documents?kind=Invoice&status=Void"));
        documents.Total.Should().Be(1); documents.Items.Single().Id.Should().Be(invoice.Id);
        (await Body<BusinessPage<BillingDocument>>(await member.GetAsync(route + "/documents?offset=1&limit=1"))).Items.Single().Id.Should().Be(invoice.Id);
        await Error(await member.GetAsync(route + "/documents?limit=0"), HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");
        await Error(await member.GetAsync(route + $"/documents/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "BILLING_DOCUMENT_NOT_FOUND");
        await Status(await member.GetAsync($"/api/v1/erp/billing/{tenant}/{Guid.NewGuid()}/documents"), HttpStatusCode.Forbidden);
        // Removing the grant takes effect on the next request without a new login.
        var narrowed = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(membership.Version, true, ["billing.read"])));
        await Status(await member.PostAsJsonAsync(route + "/documents", create with { OperationId = Guid.NewGuid() }, HttpJson), HttpStatusCode.Forbidden);
        (await Body<BusinessPage<BillingDocument>>(await member.GetAsync(route + "/documents"))).Total.Should().Be(3);
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(narrowed.Version, true, [])));
        (await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/billing/scopes/me"))).Total.Should().Be(0, "a membership without billing grants is not a billing scope");
        (await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ERP_BILLING_LINE")).Should().Be(5);
        (await database.ExecuteScalarAsync<string>("SELECT UNIT_PRICE FROM ERP_BILLING_LINE WHERE LINE_NO=1 AND DOCUMENT_ID=@id", new { id = invoice.Id.ToString("D") }))
            .Should().Be("899999999999.123456", "durable amounts are canonical decimal text");
    }

    private static async Task Login(HttpClient client, string userId, string password, string plantId)
    {
        var login = await Body<JsonElement>(await client.PostAsJsonAsync("/api/v1/auth/login", new { userId, password, plantId }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
    }
    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>(HttpJson))!;
        }
    }
    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response) response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(expected, body);
            JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
        }
    }
}

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class BillingMssqlTests(ITestOutputHelper output)
{
    [StockMssqlFact]
    public async Task Actual_SQL_Server_migrations_documents_lines_numbers_and_payments_survive_fresh_bridges()
    {
        var h = await Harness.CreateAsync(output);
        string[] tables = ["ERP_BILLING_CONTACT", "ERP_BILLING_NUMBER", "ERP_BILLING_DOCUMENT", "ERP_BILLING_LINE", "ERP_BILLING_PAYMENT", "ERP_BILLING_AUDIT",
            "ERP_AUTOMATIC_BILLING_GENERATION", "ERP_AUTOMATIC_BILLING_SOURCE", "ERP_BILLING_SHARE_LINK",
            "ERP_BILLING_SHARE_ACCESS", "ERP_BILLING_SHARE_DELIVERY"];
        (await h.Database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name IN @tables", new { tables })).Should().Be(11);
        (await h.Database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id=t.object_id
             WHERE t.schema_id=SCHEMA_ID('dbo') AND TYPE_NAME(c.user_type_id)='varchar'
               AND ((t.name='ERP_BILLING_DOCUMENT' AND c.name IN ('SUBTOTAL','DISCOUNT_AMOUNT','TAX_AMOUNT','TOTAL','PAID','CREDITED'))
                 OR (t.name='ERP_BILLING_LINE' AND c.name IN ('UNIT_PRICE','QUANTITY')) OR (t.name='ERP_BILLING_PAYMENT' AND c.name='AMOUNT'))
            """)).Should().Be(9, "durable amounts must use decimal text, including on SQL Server");
        var contact = await h.Bridge.EnrollContactAsync(h.Seed.User, h.Tenant, h.Organization, h.Seed.Customer);
        var input = new BillingDocumentInput(contact.Id, new(2026, 9, 22), new(2026, 10, 22), "KRW",
            [new("설계 ' % _", BillingProductSeed.PreciseAmount, 1m), new("B", 1m, 3m)], Tax: new(BillingAdjustmentType.Percent, 10m), Note: "메모");
        var operation = Guid.NewGuid();
        var estimate = await h.Bridge.CreateDocumentAsync(h.Seed.User, h.Tenant, h.Organization, operation, BillingKind.Estimate, input);
        var replayed = await h.Bridge.CreateDocumentAsync(h.Seed.User, h.Tenant, h.Organization, operation, BillingKind.Estimate, input);
        replayed.Id.Should().Be(estimate.Id); replayed.Input.Lines.Should().Equal(estimate.Input.Lines); replayed.Totals.Should().Be(estimate.Totals);
        replayed.Input.Lines[0].UnitPrice.Should().Be(BillingProductSeed.PreciseAmount);
        var sent = await h.Bridge.MarkSentAsync(h.Seed.User, h.Tenant, h.Organization, estimate.Id, estimate.Version);
        var accepted = await h.Bridge.DecideEstimateAsync(h.Seed.User, h.Tenant, h.Organization, sent.Id, sent.Version, true);
        var invoice = await h.Bridge.ConvertEstimateAsync(h.Seed.User, h.Tenant, h.Organization, Guid.NewGuid(), accepted.Id, accepted.Version);
        var second = await h.Bridge.CreateDocumentAsync(h.Seed.User, h.Tenant, h.Organization, Guid.NewGuid(), BillingKind.Invoice, input);
        (estimate.Number, invoice.Number, second.Number).Should().Be((1L, 1L, 2L));
        var invoiceSent = await h.Bridge.MarkSentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id, invoice.Version);
        var share = await h.Bridge.CreateShareAsync(h.Seed.User, h.Tenant, h.Organization,
            Guid.NewGuid(), invoice.Id, DateTimeOffset.UtcNow.AddHours(1));
        (await h.Bridge.OpenPublicShareAsync(share.Token!, "192.0.2.1", "mssql-contract"))
            .View.Document.Id.Should().Be(invoice.Id);
        (await h.Bridge.ListShareAccessAsync(h.Seed.User, h.Tenant, h.Organization, share.Link.Id))
            .Total.Should().Be(1);
        var payment = await h.Bridge.RecordPaymentAsync(h.Seed.User, h.Tenant, h.Organization, Guid.NewGuid(),
            new(invoice.Id, 0.000001m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.Cash, "TX", "메모"));
        var paid = await h.Bridge.GetDocumentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id);
        paid.Status.Should().Be(BillingStatus.PartiallyPaid); paid.Paid.Should().Be(0.000001m);
        IFinancialReportBridge reporting = h.Bridge;
        (await reporting.BuildCashFlowAsync(h.Seed.User, h.Tenant, h.Organization,
            new(new(2026, 9, 22), new(2026, 9, 22)))).Currencies.Should()
            .Equal(new CashFlowCurrencyTotals("KRW", 1, 0.000001m));
        var cancelled = await h.Bridge.CancelPaymentAsync(h.Seed.User, h.Tenant, h.Organization, payment.Id, payment.Version, "정정");
        (await h.Bridge.GetPaymentAsync(h.Seed.User, h.Tenant, h.Organization, payment.Id)).Should().Be(cancelled);
        (await h.Bridge.GetDocumentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id)).Status.Should().Be(BillingStatus.Sent);
        (await reporting.BuildCashFlowAsync(h.Seed.User, h.Tenant, h.Organization,
            new(new(2026, 9, 22), new(2026, 9, 22)))).Currencies.Should().BeEmpty();
        var creditInput = new BillingDocumentInput(contact.Id, new(2026, 9, 23), new(2026, 9, 23), "KRW",
            [new("Correction", 2m, 1m)]);
        var credit = await h.Bridge.CreateCreditNoteAsync(h.Seed.User, h.Tenant, h.Organization,
            Guid.NewGuid(), invoice.Id, creditInput);
        await h.Bridge.IssueCreditNoteAsync(h.Seed.User, h.Tenant, h.Organization, credit.Id, credit.Version);
        var credited = await h.Bridge.GetDocumentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id);
        credited.Status.Should().Be(BillingStatus.PartiallyCredited); credited.Credited.Should().Be(2m);
        (await reporting.BuildAsync(h.Seed.User, h.Tenant, h.Organization,
            new(new(2026, 9, 1), new(2026, 9, 30)))).Currencies.Single().Credited.Should().Be(2m);
        var page = await h.Bridge.ListDocumentsAsync(h.Seed.User, h.Tenant, h.Organization, BillingKind.Invoice, offset: 1, limit: 1);
        page.Total.Should().Be(2); page.Items.Single().Id.Should().Be(second.Id);
        (await h.Bridge.ListPaymentsAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id, PaymentState.Cancelled)).Items.Single().Id.Should().Be(payment.Id);
        (await h.Count("ERP_BILLING_DOCUMENT")).Should().Be(4);
        (await h.Count("ERP_BILLING_LINE")).Should().Be(7);
        (await h.Count("ERP_BILLING_PAYMENT")).Should().Be(1);
        (await h.Database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE name='IX_ERP_BILLING_PAYMENT_CASH_FLOW'"))
            .Should().Be(1);
        (await h.Database.ScalarAsync<string>("SELECT ADJUSTED_INVOICE_ID FROM ERP_BILLING_DOCUMENT WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND DOCUMENT_ID=@id",
            new { tenant = h.Tenant.ToString("D"), organization = h.Organization.ToString("D"), id = credit.Id.ToString("D") }))
            .Should().Be(invoice.Id.ToString("D"));
        (await h.Database.ScalarAsync<string>("SELECT UNIT_PRICE FROM ERP_BILLING_LINE WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND DOCUMENT_ID=@id AND LINE_NO=1",
            new { tenant = h.Tenant.ToString("D"), organization = h.Organization.ToString("D"), id = invoice.Id.ToString("D") })).Should().Be("899999999999.123456");
    }

    [StockMssqlFact]
    public async Task Actual_SQL_Server_persists_replays_compares_downloads_and_audits_report_snapshots()
    {
        var h = await Harness.CreateAsync(output);
        string[] tables = ["ERP_FINANCIAL_REPORT_SNAPSHOT", "ERP_FINANCIAL_REPORT_SNAPSHOT_AUDIT"];
        (await h.Database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name IN @tables",
            new { tables })).Should().Be(2);
        IFinancialReportBridge reporting = h.Bridge;
        var id = Guid.NewGuid();
        var snapshot = await reporting.CreateSnapshotAsync(h.Seed.User, h.Tenant, h.Organization,
            id, FinancialReportSnapshotKind.CashFlow, new(2026, 9, 1), new(2026, 9, 30));
        snapshot.Summary.Id.Should().Be(id);
        snapshot.CashFlowReport!.Currencies.Should().BeEmpty();
        (await reporting.CreateSnapshotAsync(h.Seed.User, h.Tenant, h.Organization,
            id, FinancialReportSnapshotKind.CashFlow, new(2026, 9, 1), new(2026, 9, 30)))
            .Should().BeEquivalentTo(snapshot);
        (await reporting.RegenerateSnapshotAsync(h.Seed.User, h.Tenant, h.Organization, id))
            .Matches.Should().BeTrue();
        (await reporting.DownloadSnapshotAsync(h.Seed.User, h.Tenant, h.Organization, id))
            .Content.Should().Contain("currency,payment_count,received");
        (await reporting.ListSnapshotsAsync(h.Seed.User, h.Tenant, h.Organization)).Total.Should().Be(1);
        (await reporting.ListSnapshotAuditAsync(h.Seed.User, h.Tenant, h.Organization, id)).Total
            .Should().Be(3, "the idempotent replay does not duplicate the Created event");
        (await h.Count("ERP_FINANCIAL_REPORT_SNAPSHOT")).Should().Be(1);
        (await h.Count("ERP_FINANCIAL_REPORT_SNAPSHOT_AUDIT")).Should().Be(3);
    }

    private sealed class Harness(MssqlContractDatabase database)
    {
        public MssqlContractDatabase Database { get; } = database;
        public BillingProductSeed Seed { get; } = new();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Organization { get; } = Guid.NewGuid();
        public BillingBridge Bridge => new(Database.DataSource, new BusinessMembershipBridge(Database.DataSource), new BusinessMasterDirectory(Database.DataSource));
        public object Scope => new { tenant = Tenant.ToString("D"), organization = Organization.ToString("D") };

        public static async Task<Harness> CreateAsync(ITestOutputHelper output)
        {
            var database = await MssqlContractDatabase.TryCreateAsync(output)
                ?? throw new InvalidOperationException("SQL Server acceptance cannot pass without a configured database.");
            var h = new Harness(database);
            await database.ExecuteAsync(BillingProductSeed.Sql, h.Seed);
            var memberships = new BusinessMembershipBridge(database.DataSource);
            (await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization, h.Seed.User, new(0, true, BillingProductSeed.Grants))).IsSuccess.Should().BeTrue();
            return h;
        }
        public Task<int> Count(string table)
            => Database.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization", Scope);
    }
}

internal sealed class BillingProductSeed
{
    public const string Password = "billing-product-test";
    // Legal six-place decimal near the Framework limit, with digits a binary double cannot preserve.
    public const decimal PreciseAmount = 899999999999.123456m;
    public static readonly string[] Grants =
        ["billing.read", "billing.write", "billing.decide", "billing.pay", "billing.credit", "billing.deliver", "financial-report.read"];
    public string Plant { get; } = "BLP-" + Guid.NewGuid().ToString("N");
    public string Customer { get; } = "BLC-" + Guid.NewGuid().ToString("N");
    public string Role { get; } = "BLR-" + Guid.NewGuid().ToString("N");
    public string User { get; } = "BLU-" + Guid.NewGuid().ToString("N");
    public DateTime Now { get; } = DateTime.UtcNow;
    public string PasswordHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant();
    public const string Sql = """
        INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Plant, 'Billing plant', 'admin', @Now, 'admin', @Now);
        INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Customer, '청구 고객', 1, 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Role, 'Billing member', '', 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, IS_ACTIVE, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@User, 'Billing clerk', @PasswordHash, '', @Role, 1, 0, 'admin', @Now, 'admin', @Now);
        """;
}
