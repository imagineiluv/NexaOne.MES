using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Collection(ChildProcessSmokeCollection.Name)]
[Trait("Category", "HostSmoke")]
public sealed class EquipmentSharingHostTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions HttpJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Real_IVT_host_routes_shared_contracts_and_uses_live_membership_and_worker_identity()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        var seed = new EquipmentSharingProductSeed();
        await using var database = new SqliteConnection(
            $"Data Source={host.DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=10");
        await database.OpenAsync();
        await database.ExecuteAsync(EquipmentSharingProductSeed.Sql, seed);

        using var administrator = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var otherOrganization = Guid.NewGuid();
        var route = $"/api/v1/ivt/shared-equipment/{tenant}/{organization}";
        var otherRoute = $"/api/v1/ivt/shared-equipment/{tenant}/{otherOrganization}";
        await Status(await administrator.GetAsync(route + "/assets/" + Guid.NewGuid()), HttpStatusCode.Unauthorized);
        await Login(administrator, "admin", "admin", seed.OtherPlant);

        // /diag is produced after the child host validates the plugin bean against its DefaultALC contract.
        var diagnostics = await Body<JsonElement>(await administrator.GetAsync("/diag"));
        var descriptor = diagnostics.GetProperty("bridges").EnumerateArray().Single(item =>
            item.GetProperty("contract").GetString() == typeof(IEquipmentSharingBridge).FullName);
        descriptor.GetProperty("module").GetString().Should().Be("Ivt");
        descriptor.GetProperty("beanName").GetString().Should().Be("equipmentSharingBridge");
        descriptor.GetProperty("implementation").GetString().Should().Be(typeof(EquipmentSharingBridge).FullName);

        var binding = await Body<InventoryScopeBinding>(await administrator.PutAsJsonAsync(
            route + "/binding", new { plantId = seed.Plant, active = true }));
        binding.PlantId.Should().Be(seed.Plant);
        await Body<InventoryScopeBinding>(await administrator.PutAsJsonAsync(
            otherRoute + "/binding", new { plantId = seed.OtherPlant, active = true }));
        var equipmentInput = new { operationId = Guid.NewGuid(), code = "HTTP-SHARED", name = "HTTP shared equipment", capacity = 2, requiresApproval = true };
        await Status(await administrator.PostAsJsonAsync(route + "/assets", equipmentInput), HttpStatusCode.Forbidden);

        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/";
        var requester = await Body<BusinessMembership>(await administrator.PutAsJsonAsync(
            membershipRoute + seed.Requester, new BusinessMembershipChange(0, true, EquipmentSharingProductSeed.Grants)));
        var reviewer = await Body<BusinessMembership>(await administrator.PutAsJsonAsync(
            membershipRoute + seed.Reviewer, new BusinessMembershipChange(0, true, ["equipment.booking.decide"])));
        await Body<BusinessMembership>(await administrator.PutAsJsonAsync(
            $"/api/v1/sys/business-memberships/{tenant}/{otherOrganization}/users/{seed.Requester}",
            new BusinessMembershipChange(0, true, ["equipment.read"])));

        using var member = new HttpClient { BaseAddress = administrator.BaseAddress };
        using var approver = new HttpClient { BaseAddress = administrator.BaseAddress };
        await Login(member, seed.Requester, EquipmentSharingProductSeed.Password, seed.OtherPlant);
        await Login(approver, seed.Reviewer, EquipmentSharingProductSeed.Password, seed.OtherPlant);
        await Status(await member.GetAsync(route + "/binding"), HttpStatusCode.Forbidden);
        var asset = await Body<SharedEquipment>(await member.PostAsJsonAsync(route + "/assets", equipmentInput));
        asset.Scope.Should().Be(new BusinessScope("NexaOne.MES", tenant.ToString("D"), organization.ToString("D")));
        (await Body<SharedEquipment>(await member.PostAsJsonAsync(route + "/assets", equipmentInput))).Should().Be(asset);
        (await Body<SharedEquipment>(await member.GetAsync(route + "/assets/" + asset.Id))).Should().Be(asset);
        // Code lookup reads the current code; idempotent creation recovery uses the stable operation ID above.
        var lookup = "/assets/by-code?code=" + Uri.EscapeDataString(equipmentInput.code);
        (await Body<SharedEquipment>(await member.GetAsync(route + lookup))).Should().Be(asset);
        await Error(await member.GetAsync(otherRoute + lookup), HttpStatusCode.NotFound, "EQUIPMENT_NOT_FOUND");
        await Error(await member.GetAsync(otherRoute + "/assets/" + asset.Id), HttpStatusCode.NotFound, "EQUIPMENT_NOT_FOUND");

        var start = DateTimeOffset.UtcNow.AddDays(1);
        await Error(await member.PutAsJsonAsync(route + "/bookings/" + Guid.NewGuid(), new
        {
            equipmentId = asset.Id, workerId = seed.OtherWorker, start, end = start.AddHours(1), quantity = 1
        }), HttpStatusCode.NotFound, "EMPLOYEE_NOT_FOUND");
        var bookingId = Guid.NewGuid();
        var forgedEmployee = Guid.NewGuid();
        var bookingInput = new
        {
            equipmentId = asset.Id, workerId = seed.Worker, start, end = start.AddHours(1), quantity = 1,
            requestedBy = reviewer.BusinessUserId, employeeId = forgedEmployee, plantId = seed.OtherPlant,
            tenantId = Guid.NewGuid(), organizationId = otherOrganization
        };
        var booking = await Body<EquipmentBooking>(await member.PutAsJsonAsync(route + "/bookings/" + bookingId, bookingInput));
        booking.State.Should().Be(EquipmentBookingState.Requested);
        booking.Scope.Should().Be(asset.Scope);
        booking.RequestedBy.Should().Be(requester.BusinessUserId.ToString("D"));
        booking.EmployeeId.Should().NotBe(forgedEmployee);
        (await database.ExecuteScalarAsync<string>(
            "SELECT EMPLOYEE_ID FROM IVT_WORKER_IDENTITY WHERE WORKER_ID=@Worker", seed))
            .Should().Be(booking.EmployeeId.ToString("D"));
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM IVT_WORKER_IDENTITY WHERE WORKER_ID=@OtherWorker", seed)).Should().Be(0);
        (await Body<EquipmentBooking>(await member.PutAsJsonAsync(route + "/bookings/" + bookingId, bookingInput))).Should().Be(booking);
        await Error(await member.PostAsJsonAsync(route + $"/bookings/{bookingId}/decide",
            new { version = booking.Version, approve = true, userId = seed.Reviewer }),
            HttpStatusCode.Conflict, "SELF_APPROVAL_NOT_ALLOWED");
        var approved = await Body<EquipmentBooking>(await approver.PostAsJsonAsync(route + $"/bookings/{bookingId}/decide",
            new { version = booking.Version, approve = true }));
        approved.State.Should().Be(EquipmentBookingState.Approved);
        var cancelled = await Body<EquipmentBooking>(await member.PostAsJsonAsync(route + $"/bookings/{bookingId}/cancel",
            new { version = approved.Version }));
        cancelled.State.Should().Be(EquipmentBookingState.Cancelled);
        (await Body<EquipmentBooking>(await member.GetAsync(route + "/bookings/" + bookingId))).Should().Be(cancelled);

        var auditActors = (await database.QueryAsync<string>("""
            SELECT USER_ID FROM IVT_SHARED_EQUIPMENT_AUDIT
             WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization
            """, new { tenant = tenant.ToString("D"), organization = organization.ToString("D") })).ToArray();
        auditActors.Should().HaveCount(4, "save, request, approval and cancellation each append one audit; replay appends none");
        auditActors.Count(actor => actor == requester.BusinessUserId.ToString("D")).Should().Be(3);
        auditActors.Count(actor => actor == reviewer.BusinessUserId.ToString("D")).Should().Be(1);
        await Body<BusinessMembership>(await administrator.PutAsJsonAsync(membershipRoute + seed.Requester,
            new BusinessMembershipChange(requester.Version, false, [])));
        await Status(await member.GetAsync(route + "/bookings/" + bookingId), HttpStatusCode.Forbidden);
        await Status(await member.GetAsync(route + lookup), HttpStatusCode.Forbidden);
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
        using (response)
            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
        }
    }
}

// Ordinary SQLite runs report these as skipped, never passed. The existing required gate disables
// this discovery-time skip so a missing SQL connection fails through MssqlContractDatabase.
public sealed class EquipmentMssqlFactAttribute : FactAttribute
{
    public EquipmentMssqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MssqlContractDatabase.ConnectionEnvironmentVariable))
            && !string.Equals(Environment.GetEnvironmentVariable(MssqlContractDatabase.RequiredEnvironmentVariable),
                "true", StringComparison.OrdinalIgnoreCase))
            Skip = $"Requires {MssqlContractDatabase.ConnectionEnvironmentVariable}; no SQL Server acceptance was executed.";
    }
}

[Trait("Category", "MssqlContract")]
public sealed class EquipmentSharingMssqlTests(ITestOutputHelper output)
{
    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_creation_receipt_survives_rename_and_rejects_changed_retry()
    {
        var h = await Harness.CreateAsync(output); var operation = Guid.NewGuid();
        var asset = await h.Bridge.CreateEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, operation, "INITIAL", "Original", 1, false);
        var changed = await h.Bridge.UpdateEquipmentAsync(h.Seed.Reviewer, h.Tenant, h.Organization, "RENAMED", "Updated", 2, false, asset.Id, asset.Version);
        (await h.NewBridge().CreateEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, operation, "INITIAL", "Original", 1, false)).Should().Be(changed);
        await Error(() => h.Bridge.CreateEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, operation, "INITIAL", "Changed retry", 1, false), "EQUIPMENT_CREATE_OPERATION_CONFLICT");
        (await h.Count("IVT_SHARED_EQUIPMENT")).Should().Be(1);
        (await h.Count("IVT_SHARED_EQUIPMENT_CREATION")).Should().Be(1);
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(2);
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_retains_lifecycle_identity_versions_and_audit_on_fresh_bridge_replay()
    {
        var h = await Harness.CreateAsync(output);
        var asset = await h.Asset(1, approval: true);
        var booking = await h.Request(asset);
        booking.State.Should().Be(EquipmentBookingState.Requested);
        booking.RequestedBy.Should().Be(h.Requester.BusinessUserId.ToString("D"));
        await Error(() => h.Bridge.DecideBookingAsync(h.Seed.Requester, h.Tenant, h.Organization,
            booking.Id, booking.Version, true), "SELF_APPROVAL_NOT_ALLOWED");
        await Error(() => h.Bridge.DecideBookingAsync(h.Seed.Reviewer, h.Tenant, h.Organization,
            booking.Id, Guid.NewGuid(), true), "BUSINESS_VERSION_CONFLICT");
        var approved = await h.Bridge.DecideBookingAsync(h.Seed.Reviewer, h.Tenant, h.Organization, booking.Id, booking.Version, true);
        approved.State.Should().Be(EquipmentBookingState.Approved);
        h.Clock.Now = booking.Start;
        var checkedOut = await h.Bridge.CheckOutAsync(h.Seed.Requester, h.Tenant, h.Organization, booking.Id, approved.Version);
        checkedOut.State.Should().Be(EquipmentBookingState.CheckedOut);
        h.Clock.Now = booking.End.AddDays(1);
        await Error(() => h.Request(asset), "EQUIPMENT_CAPACITY_UNAVAILABLE");
        await h.Database.ExecuteAsync("UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID=@Worker", h.Seed);
        var returned = await h.NewBridge().ReturnAsync(h.Seed.Requester, h.Tenant, h.Organization, booking.Id, checkedOut.Version);
        returned.State.Should().Be(EquipmentBookingState.Returned);
        returned.ReturnedAt.Should().Be(h.Clock.Now);
        (await h.NewBridge().GetBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, booking.Id)).Should().Be(returned);
        (await h.NewBridge().ReturnAsync(h.Seed.Requester, h.Tenant, h.Organization, booking.Id, checkedOut.Version)).Should().Be(returned);
        (await h.Request(asset, booking.Id, booking.Start, booking.End)).Should().Be(returned);
        await Error(() => h.Request(asset, booking.Id, booking.Start, booking.End, quantity: 2), "BOOKING_OPERATION_CONFLICT");

        (await h.Database.ScalarAsync<string>(
            "SELECT EMPLOYEE_ID FROM IVT_WORKER_IDENTITY WHERE WORKER_ID=@Worker", h.Seed)).Should().Be(booking.EmployeeId.ToString("D"));
        (await h.Count("IVT_SHARED_BOOKING")).Should().Be(1);
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(5);
        await h.Audit(booking, "requested", null, h.Requester.BusinessUserId);
        await h.Audit(approved, "approved", booking.Version, h.Reviewer.BusinessUserId);
        await h.Audit(checkedOut, "checked-out", approved.Version, h.Requester.BusinessUserId);
        await h.Audit(returned, "returned", checkedOut.Version, h.Requester.BusinessUserId);
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_uses_peak_overlap_and_releases_cancelled_capacity()
    {
        var h = await Harness.CreateAsync(output);
        var asset = await h.Asset(2);
        var start = h.Clock.Now.AddHours(1);
        await h.Request(asset, start: start, end: start.AddHours(1));
        await h.Request(asset, start: start.AddHours(1), end: start.AddHours(2));
        var spanning = await h.Request(asset, start: start, end: start.AddHours(2));
        spanning.State.Should().Be(EquipmentBookingState.Approved);
        await Error(() => h.Request(asset, start: start, end: start.AddHours(2)), "EQUIPMENT_CAPACITY_UNAVAILABLE");
        await h.Bridge.CancelBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, spanning.Id, spanning.Version);
        var replacement = await h.Request(asset, start: start, end: start.AddHours(2));
        (await h.NewBridge().GetBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, replacement.Id)).Should().Be(replacement);
        (await h.Count("IVT_SHARED_BOOKING")).Should().Be(4);
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(6);
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_competing_transactions_reserve_one_capacity_and_leave_no_losing_audit()
    {
        var h = await Harness.CreateAsync(output);
        var asset = await h.Asset(1);
        // Resolve the stable worker identity first, so this race targets capacity rather than first-use identity creation.
        var warmup = await h.Request(asset);
        await h.Bridge.CancelBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, warmup.Id, warmup.Version);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var attempts = ids.Select(Attempt).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);
        outcomes.Count(success => success).Should().Be(1);
        var winner = ids[Array.IndexOf(outcomes, true)];
        var loser = ids[Array.IndexOf(outcomes, false)];
        (await h.NewBridge().GetBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, winner)).State
            .Should().Be(EquipmentBookingState.Approved);
        await Error(() => h.Request(asset, loser), "EQUIPMENT_CAPACITY_UNAVAILABLE");
        (await h.Count("IVT_SHARED_BOOKING")).Should().Be(2, "only the cancelled warmup and winning reservation are durable");
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(4);
        (await h.Database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM IVT_SHARED_EQUIPMENT_AUDIT
             WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND RESOURCE_ID=@loser
            """, new { tenant = h.Tenant.ToString("D"), organization = h.Organization.ToString("D"), loser = loser.ToString("D") })).Should().Be(0);

        async Task<bool> Attempt(Guid id)
        {
            await start.Task;
            try
            {
                await h.NewBridge().RequestBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, id, asset.Id,
                    h.Seed.Worker, h.Clock.Now.AddHours(1), h.Clock.Now.AddHours(2), 1, timeout.Token);
                return true;
            }
            catch (BusinessException error) when (error.Code == "EQUIPMENT_CAPACITY_UNAVAILABLE") { return false; }
            // SQL Server can choose a serializable reader as a deadlock victim. Its rollback is a valid losing outcome;
            // the fresh retry above must still observe capacity exhaustion. Other database errors fail this test.
            catch (SqlException error) when (error.Number == 1205) { return false; }
        }
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_audit_failure_rolls_back_new_booking_and_worker_identity()
    {
        var h = await Harness.CreateAsync(output);
        var asset = await h.Asset(1);
        var bookingId = Guid.NewGuid();
        var trigger = "equipment_audit_test_" + Guid.NewGuid().ToString("N");
        // Only this unique tenant is faulted; other contract tests keep their normal audit behavior.
        await h.Database.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.IVT_SHARED_EQUIPMENT_AUDIT AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE TENANT_ID='{h.Tenant:D}')
                    THROW 51099, 'Equipment acceptance audit failure', 1;
            END;
            """);
        try
        {
            var failure = await Assert.ThrowsAsync<SqlException>(() => h.Request(asset, bookingId));
            failure.Number.Should().Be(51099);
            (await h.Count("IVT_SHARED_BOOKING")).Should().Be(0);
            (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(1);
            (await h.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM IVT_WORKER_IDENTITY WHERE WORKER_ID=@Worker", h.Seed)).Should().Be(0);
        }
        finally
        {
            await h.Database.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}]");
        }
        var recovered = await h.Request(asset, bookingId);
        recovered.State.Should().Be(EquipmentBookingState.Approved);
        (await h.Count("IVT_SHARED_BOOKING")).Should().Be(1);
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(2);
    }

    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness(MssqlContractDatabase database)
    {
        public MssqlContractDatabase Database { get; } = database;
        public EquipmentSharingProductSeed Seed { get; } = new();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Organization { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public EquipmentSharingBridge Bridge => NewBridge();
        public BusinessMembership Requester { get; private set; } = null!;
        public BusinessMembership Reviewer { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(ITestOutputHelper output)
        {
            var database = await MssqlContractDatabase.TryCreateAsync(output)
                ?? throw new InvalidOperationException("SQL Server acceptance cannot pass without a configured database.");
            var h = new Harness(database);
            await database.ExecuteAsync(EquipmentSharingProductSeed.Sql, h.Seed);
            await h.Bridge.BindScopeAsync("admin", h.Tenant, h.Organization, h.Seed.Plant, null, true);
            var memberships = new BusinessMembershipBridge(database.DataSource);
            var requester = await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization,
                h.Seed.Requester, new(0, true, EquipmentSharingProductSeed.Grants));
            requester.IsSuccess.Should().BeTrue();
            h.Requester = requester.Value;
            var reviewer = await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization,
                h.Seed.Reviewer, new(0, true, EquipmentSharingProductSeed.Grants));
            reviewer.IsSuccess.Should().BeTrue();
            h.Reviewer = reviewer.Value;
            return h;
        }

        public EquipmentSharingBridge NewBridge() => new(Database.DataSource,
            new BusinessMembershipBridge(Database.DataSource), new BusinessMasterDirectory(Database.DataSource), Clock);
        public Task<SharedEquipment> Asset(int capacity, bool approval = false)
            => Bridge.CreateEquipmentAsync(Seed.Requester, Tenant, Organization, Guid.NewGuid(), "SQL-SHARED", "SQL shared equipment", capacity, approval);
        public Task<EquipmentBooking> Request(SharedEquipment asset, Guid? id = null,
            DateTimeOffset? start = null, DateTimeOffset? end = null, int quantity = 1)
            => Bridge.RequestBookingAsync(Seed.Requester, Tenant, Organization, id ?? Guid.NewGuid(), asset.Id,
                Seed.Worker, start ?? Clock.Now.AddHours(1), end ?? Clock.Now.AddHours(2), quantity);
        public Task<int> Count(string table)
            => Database.ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization",
                new { tenant = Tenant.ToString("D"), organization = Organization.ToString("D") });
        public async Task Audit(EquipmentBooking booking, string operation, Guid? previous, Guid actor)
            => (await Database.ScalarAsync<int>("""
                SELECT COUNT(*) FROM IVT_SHARED_EQUIPMENT_AUDIT
                 WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND RESOURCE_TYPE='equipment-booking'
                   AND RESOURCE_ID=@id AND OPERATION=@operation AND VERSION=@version AND USER_ID=@actor
                   AND ((@previous IS NULL AND PREVIOUS_VERSION IS NULL) OR PREVIOUS_VERSION=@previous)
                """, new { tenant = Tenant.ToString("D"), organization = Organization.ToString("D"),
                id = booking.Id.ToString("D"), operation, version = booking.Version.ToString("D"),
                actor = actor.ToString("D"), previous = previous?.ToString("D") })).Should().Be(1);
    }
}

internal sealed class EquipmentSharingProductSeed
{
    public const string Password = "equipment-product-test";
    public static readonly string[] Grants = ["equipment.read", "equipment.write", "equipment.booking.read",
        "equipment.booking.request", "equipment.booking.decide", "equipment.booking.cancel", "equipment.booking.checkout", "equipment.booking.return"];
    public string Plant { get; } = "EQP-" + Guid.NewGuid().ToString("N");
    public string OtherPlant { get; } = "EQP-" + Guid.NewGuid().ToString("N");
    public string Worker { get; } = "EQW-" + Guid.NewGuid().ToString("N");
    public string OtherWorker { get; } = "EQW-" + Guid.NewGuid().ToString("N");
    public string Role { get; } = "EQR-" + Guid.NewGuid().ToString("N");
    public string Requester { get; } = "EQU-" + Guid.NewGuid().ToString("N");
    public string Reviewer { get; } = "EQU-" + Guid.NewGuid().ToString("N");
    public DateTime Now { get; } = DateTime.UtcNow;
    public string PasswordHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant();

    public const string Sql = """
        INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Plant, 'Shared plant', 'admin', @Now, 'admin', @Now),
                   (@OtherPlant, 'Other plant', 'admin', @Now, 'admin', @Now);
        INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Worker, 'Shared worker', @Plant, 1, 'admin', @Now, 'admin', @Now),
                   (@OtherWorker, 'Other worker', @OtherPlant, 1, 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Role, 'Equipment member', '', 'admin', @Now, 'admin', @Now);
        INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID,
                             IS_ACTIVE, IS_DELETED, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Requester, 'Equipment requester', @PasswordHash, '', @Role, 1, 0, 'admin', @Now, 'admin', @Now),
                   (@Reviewer, 'Equipment reviewer', @PasswordHash, '', @Role, 1, 0, 'admin', @Now, 'admin', @Now);
        """;
}
