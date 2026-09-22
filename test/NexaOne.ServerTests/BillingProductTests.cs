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
        // Membership and grants only: no IVT plant binding is created for this organization.
        await Status(await admin.PutAsync(route + "/contacts/" + seed.Customer, null), HttpStatusCode.Forbidden);
        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/{seed.User}";
        var membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(0, true, BillingProductSeed.Grants)));
        using var member = new HttpClient { BaseAddress = admin.BaseAddress };
        await Login(member, seed.User, BillingProductSeed.Password, seed.Plant);
        // Scope discovery lists memberships with any billing grant; no IVT plant binding is consulted.
        var scopes = await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/billing/scopes/me"));
        scopes.Total.Should().Be(1); scopes.Items.Single().Should().Match<BusinessMembership>(m => m.TenantId == tenant && m.OrganizationId == organization);
        (await Body<BusinessPage<BusinessMembership>>(await admin.GetAsync("/api/v1/erp/billing/scopes/me"))).Total.Should().Be(0);
        await Error(await member.GetAsync("/api/v1/erp/billing/scopes/me?limit=0"), HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");

        var contact = await Body<BillingContact>(await member.PutAsync(route + "/contacts/" + seed.Customer, null));
        contact.Name.Should().Be("청구 고객"); contact.Active.Should().BeTrue();
        (await Body<BillingContact>(await member.PutAsync(route + "/contacts/" + seed.Customer, null))).Should().Be(contact);
        await Error(await member.PutAsync(route + "/contacts/MISSING-" + Guid.NewGuid().ToString("N"), null), HttpStatusCode.NotFound, "CUSTOMER_NOT_FOUND");
        var contacts = await Body<BusinessPage<BillingContact>>(await member.GetAsync(route + "/contacts"));
        contacts.Total.Should().Be(1); contacts.Items.Single().Should().Be(contact);

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

        var paymentRoute = route + $"/documents/{invoice.Id}/payments";
        var pay = new BillingController.PaymentCommand(Guid.NewGuid(), new(invoice.Id, 100m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.BankTransfer, "TX"));
        var payment = await Body<PaymentRecord>(await member.PostAsJsonAsync(paymentRoute, pay, HttpJson));
        (await Body<PaymentRecord>(await member.PostAsJsonAsync(paymentRoute, pay, HttpJson))).Should().Be(payment);
        await Error(await member.PostAsJsonAsync(paymentRoute, pay with { Input = pay.Input with { Amount = 1m } }, HttpJson), HttpStatusCode.Conflict, "PAYMENT_OPERATION_CONFLICT");
        await Error(await member.PostAsJsonAsync(route + $"/documents/{estimate.Id}/payments", pay with { OperationId = Guid.NewGuid() }, HttpJson), HttpStatusCode.BadRequest, "INVALID_BUSINESS_INPUT");
        var partiallyPaid = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        partiallyPaid.Status.Should().Be(BillingStatus.PartiallyPaid); partiallyPaid.Paid.Should().Be(100m);
        await Error(await member.PostAsJsonAsync(route + $"/documents/{invoice.Id}/void", new BillingController.VersionedCommand(partiallyPaid.Version), HttpJson),
            HttpStatusCode.Conflict, "BILLING_DOCUMENT_HAS_PAYMENTS");
        var cancelled = await Body<PaymentRecord>(await member.PostAsJsonAsync(route + $"/payments/{payment.Id}/cancel", new BillingController.CancelCommand(payment.Version, "정정"), HttpJson));
        cancelled.State.Should().Be(PaymentState.Cancelled); cancelled.CancelReason.Should().Be("정정");
        (await Body<PaymentRecord>(await member.GetAsync(route + $"/payments/{payment.Id}"))).Should().Be(cancelled);
        var payments = await Body<BusinessPage<PaymentRecord>>(await member.GetAsync(paymentRoute + "?state=Cancelled"));
        payments.Total.Should().Be(1); payments.Items.Single().Should().Be(cancelled);
        var back = await Body<BillingDocument>(await member.GetAsync(route + $"/documents/{invoice.Id}"));
        back.Status.Should().Be(BillingStatus.Sent); back.Paid.Should().Be(0m);
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
        (await Body<BusinessPage<BillingDocument>>(await member.GetAsync(route + "/documents"))).Total.Should().Be(2);
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(narrowed.Version, true, [])));
        (await Body<BusinessPage<BusinessMembership>>(await member.GetAsync("/api/v1/erp/billing/scopes/me"))).Total.Should().Be(0, "a membership without billing grants is not a billing scope");
        (await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ERP_BILLING_LINE")).Should().Be(4);
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
        string[] tables = ["ERP_BILLING_CONTACT", "ERP_BILLING_NUMBER", "ERP_BILLING_DOCUMENT", "ERP_BILLING_LINE", "ERP_BILLING_PAYMENT", "ERP_BILLING_AUDIT"];
        (await h.Database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name IN @tables", new { tables })).Should().Be(6);
        (await h.Database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id=t.object_id
             WHERE t.schema_id=SCHEMA_ID('dbo') AND TYPE_NAME(c.user_type_id)='varchar'
               AND ((t.name='ERP_BILLING_DOCUMENT' AND c.name IN ('SUBTOTAL','DISCOUNT_AMOUNT','TAX_AMOUNT','TOTAL','PAID'))
                 OR (t.name='ERP_BILLING_LINE' AND c.name IN ('UNIT_PRICE','QUANTITY')) OR (t.name='ERP_BILLING_PAYMENT' AND c.name='AMOUNT'))
            """)).Should().Be(8, "durable amounts must use decimal text, including on SQL Server");
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
        var payment = await h.Bridge.RecordPaymentAsync(h.Seed.User, h.Tenant, h.Organization, Guid.NewGuid(),
            new(invoice.Id, 0.000001m, "KRW", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), PaymentMethod.Cash, "TX", "메모"));
        var paid = await h.Bridge.GetDocumentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id);
        paid.Status.Should().Be(BillingStatus.PartiallyPaid); paid.Paid.Should().Be(0.000001m);
        var cancelled = await h.Bridge.CancelPaymentAsync(h.Seed.User, h.Tenant, h.Organization, payment.Id, payment.Version, "정정");
        (await h.Bridge.GetPaymentAsync(h.Seed.User, h.Tenant, h.Organization, payment.Id)).Should().Be(cancelled);
        (await h.Bridge.GetDocumentAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id)).Status.Should().Be(BillingStatus.Sent);
        var page = await h.Bridge.ListDocumentsAsync(h.Seed.User, h.Tenant, h.Organization, BillingKind.Invoice, offset: 1, limit: 1);
        page.Total.Should().Be(2); page.Items.Single().Id.Should().Be(second.Id);
        (await h.Bridge.ListPaymentsAsync(h.Seed.User, h.Tenant, h.Organization, invoice.Id, PaymentState.Cancelled)).Items.Single().Id.Should().Be(payment.Id);
        (await h.Count("ERP_BILLING_DOCUMENT")).Should().Be(3);
        (await h.Count("ERP_BILLING_LINE")).Should().Be(6);
        (await h.Count("ERP_BILLING_PAYMENT")).Should().Be(1);
        (await h.Database.ScalarAsync<string>("SELECT UNIT_PRICE FROM ERP_BILLING_LINE WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND DOCUMENT_ID=@id AND LINE_NO=1",
            new { tenant = h.Tenant.ToString("D"), organization = h.Organization.ToString("D"), id = invoice.Id.ToString("D") })).Should().Be("899999999999.123456");
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
    public static readonly string[] Grants = ["billing.read", "billing.write", "billing.decide", "billing.pay"];
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
