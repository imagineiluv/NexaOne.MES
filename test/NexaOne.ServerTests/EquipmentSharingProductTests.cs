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
using NexaOne.ServiceContracts.Mdm;
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

    [Fact]
    public async Task Real_host_binds_equipment_list_defaults_and_booking_filters_with_live_permissions()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        var seed = new EquipmentSharingProductSeed();
        await using var database = new SqliteConnection($"Data Source={host.DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=10");
        await database.OpenAsync();
        await database.ExecuteAsync(EquipmentSharingProductSeed.Sql, seed);
        using var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var other = Guid.NewGuid();
        var route = $"/api/v1/ivt/shared-equipment/{tenant}/{organization}";
        var otherRoute = $"/api/v1/ivt/shared-equipment/{tenant}/{other}";
        foreach (var resource in new[] { "assets", "bookings" })
            await Status(await admin.GetAsync(route + "/" + resource), HttpStatusCode.Unauthorized);
        await Login(admin, "admin", "admin", seed.OtherPlant);
        await Body<InventoryScopeBinding>(await admin.PutAsJsonAsync(route + "/binding", new { plantId = seed.Plant, active = true }));
        await Body<InventoryScopeBinding>(await admin.PutAsJsonAsync(otherRoute + "/binding", new { plantId = seed.OtherPlant, active = true }));
        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/{seed.Requester}";
        var memberGrant = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(0, true, EquipmentSharingProductSeed.Grants)));
        await Body<BusinessMembership>(await admin.PutAsJsonAsync($"/api/v1/sys/business-memberships/{tenant}/{other}/users/{seed.Requester}",
            new BusinessMembershipChange(0, true, EquipmentSharingProductSeed.Grants)));
        using var member = new HttpClient { BaseAddress = admin.BaseAddress };
        await Login(member, seed.Requester, EquipmentSharingProductSeed.Password, seed.OtherPlant);
        var assets = new List<SharedEquipment>();
        foreach (var code in new[] { "LIST-A", "LIST-B", "LIST-C" })
            assets.Add(await Body<SharedEquipment>(await member.PostAsJsonAsync(route + "/assets",
                new { operationId = Guid.NewGuid(), code, name = "장비 %_[x]", capacity = 3, requiresApproval = true })));
        await Body<SharedEquipment>(await member.PutAsJsonAsync(route + $"/assets/{assets[0].Id}/active", new { version = assets[0].Version, active = false }));
        var foreign = await Body<SharedEquipment>(await member.PostAsJsonAsync(otherRoute + "/assets",
            new { operationId = Guid.NewGuid(), code = "FOREIGN", name = "장비 %_[x]", capacity = 1, requiresApproval = true }));
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var bookings = new List<EquipmentBooking>();
        foreach (var asset in new[] { assets[1], assets[1], assets[2] })
            bookings.Add(await Body<EquipmentBooking>(await member.PutAsJsonAsync(route + "/bookings/" + Guid.NewGuid(),
                new { equipmentId = asset.Id, workerId = seed.Worker, start, end = start.AddHours(1), quantity = 1 })));
        bookings[0] = await Body<EquipmentBooking>(await member.PostAsJsonAsync(route + $"/bookings/{bookings[0].Id}/cancel", new { version = bookings[0].Version }));
        await Body<EquipmentBooking>(await member.PutAsJsonAsync(otherRoute + "/bookings/" + Guid.NewGuid(),
            new { equipmentId = foreign.Id, workerId = seed.OtherWorker, start, end = start.AddHours(1), quantity = 1 }));
        await database.ExecuteAsync("UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID=@Worker", seed);
        var audits = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_SHARED_EQUIPMENT_AUDIT");

        var defaults = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + "/assets"));
        defaults.Total.Should().Be(2);
        defaults.Items.Select(a => a.Id).Should().BeEquivalentTo(assets.Skip(1).Select(a => a.Id));
        var text = Uri.EscapeDataString("장비 %_[X]");
        var page = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + $"/assets?text={text}&offset=1&limit=1"));
        page.Total.Should().Be(2); page.Items.Should().BeEquivalentTo(defaults.Items.Skip(1));
        var inactive = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + $"/assets?text={text}&includeInactive=true&limit=100"));
        inactive.Total.Should().Be(3); inactive.Items.Should().HaveCount(3).And.Contain(a => !a.Active);
        var pastAssets = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + $"/assets?text={text}&offset=2&limit=1"));
        pastAssets.Total.Should().Be(2); pastAssets.Items.Should().BeEmpty();
        var all = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + "/bookings"));
        all.Total.Should().Be(3); all.Items.Should().BeEquivalentTo(bookings);
        var byEquipment = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + $"/bookings?equipmentId={assets[1].Id}"));
        byEquipment.Total.Should().Be(2);
        var bookingPage = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + $"/bookings?equipmentId={assets[1].Id}&offset=1&limit=1"));
        bookingPage.Total.Should().Be(2); bookingPage.Items.Should().BeEquivalentTo(byEquipment.Items.Skip(1));
        var requested = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + $"/bookings?equipmentId={assets[1].Id}&state=Requested&limit=100"));
        requested.Total.Should().Be(1); requested.Items.Should().BeEquivalentTo([bookings[1]]);
        var cancelled = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + "/bookings?state=Cancelled"));
        cancelled.Total.Should().Be(1); cancelled.Items.Should().BeEquivalentTo([bookings[0]]);
        var past = await Body<BusinessPage<EquipmentBooking>>(await member.GetAsync(route + "/bookings?state=Cancelled&offset=1&limit=1"));
        past.Total.Should().Be(1); past.Items.Should().BeEmpty();
        // Keep one active asset with a space and one without, so a null-converted filter cannot pass.
        await database.ExecuteAsync("""
            UPDATE IVT_SHARED_EQUIPMENT SET NAME='NoSpaces'
             WHERE TENANT_ID=@tenant AND ORGANIZATION_ID=@organization AND EQUIPMENT_ID=@id
            """, new { tenant = tenant.ToString("D"), organization = organization.ToString("D"), id = assets[2].Id.ToString("D") });
        foreach (var spaceQuery in new[] { "text=%20&limit=1", "query.Text=%20&query.Limit=1&text=unmatched" })
        {
            var spacedAssets = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + "/assets?" + spaceQuery));
            spacedAssets.Total.Should().Be(1);
            spacedAssets.Items.Should().ContainSingle().Which.Id.Should().Be(assets[1].Id);
        }
        var prefixedAssets = await Body<BusinessPage<SharedEquipment>>(await member.GetAsync(route + "/assets?query.Limit=1&text=%20"));
        prefixedAssets.Total.Should().Be(2); prefixedAssets.Items.Should().ContainSingle();
        var tooLongText = Uri.EscapeDataString(new string(' ', 256));
        foreach (var invalid in new[] { $"text={tooLongText}", $"query.Text={tooLongText}&query.Limit=1" })
            await Status(await member.GetAsync(route + "/assets?" + invalid), HttpStatusCode.BadRequest);
        foreach (var invalid in new[] { "equipmentId=invalid", $"equipmentId={Guid.Empty}", "state=invalid", "state=999" })
            await Status(await member.GetAsync(route + "/bookings?" + invalid), HttpStatusCode.BadRequest);
        foreach (var resource in new[] { "assets", "bookings" })
        {
            await Status(await admin.GetAsync(route + "/" + resource), HttpStatusCode.Forbidden);
            foreach (var invalid in new[] { "offset=-1", "limit=0", "limit=101", "offset=invalid" })
                await Status(await member.GetAsync(route + "/" + resource + "?" + invalid), HttpStatusCode.BadRequest);
        }
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(memberGrant.Version, true, EquipmentSharingProductSeed.Grants.Where(g => g != "equipment.read" && g != "equipment.booking.read").ToArray())));
        foreach (var resource in new[] { "assets", "bookings" })
            await Status(await member.GetAsync(route + "/" + resource), HttpStatusCode.Forbidden);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(audits);
    }

    [Fact]
    public async Task Real_host_selectors_use_authenticated_membership_and_binding_with_raw_worker_queries_and_live_revocation()
    {
        using var host = await HostProcess.StartAsync(output, springConfig: null, expectListening: true);
        host.Listening.Should().BeTrue(host.Log);
        var seed = new EquipmentSharingProductSeed();
        await using var database = new SqliteConnection($"Data Source={host.DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=10");
        await database.OpenAsync();
        await database.ExecuteAsync(EquipmentSharingProductSeed.Sql, seed);
        await database.ExecuteAsync(EquipmentSharingProductSeed.SelectorWorkersSql, seed);
        using var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        const string scopesRoute = "/api/v1/ivt/scopes/me";
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var other = Guid.NewGuid(); var foreign = Guid.NewGuid();
        var route = $"/api/v1/ivt/shared-equipment/{tenant}/{organization}";
        await Status(await admin.GetAsync(scopesRoute), HttpStatusCode.Unauthorized);
        await Status(await admin.GetAsync(route + "/workers"), HttpStatusCode.Unauthorized);
        await Login(admin, "admin", "admin", seed.OtherPlant);
        foreach (var scope in new[] { (organization, seed.Plant), (other, seed.OtherPlant), (foreign, seed.SelectorForeignPlant) })
            await Body<InventoryScopeBinding>(await admin.PutAsJsonAsync($"/api/v1/ivt/shared-equipment/{tenant}/{scope.Item1}/binding",
                new { plantId = scope.Item2, active = true }));
        var membershipRoute = $"/api/v1/sys/business-memberships/{tenant}/{organization}/users/{seed.Requester}";
        var membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(0, true, ["equipment.booking.request"])));
        foreach (var scope in new[] { other, Guid.NewGuid() }) // One eligible stock scope and one unbound membership.
            await Body<BusinessMembership>(await admin.PutAsJsonAsync($"/api/v1/sys/business-memberships/{tenant}/{scope}/users/{seed.Requester}",
                new BusinessMembershipChange(0, true, ["stock.read"])));
        await Body<BusinessMembership>(await admin.PutAsJsonAsync($"/api/v1/sys/business-memberships/{tenant}/{foreign}/users/{seed.Reviewer}",
            new BusinessMembershipChange(0, true, ["equipment.read"])));
        using var member = new HttpClient { BaseAddress = admin.BaseAddress };
        await Login(member, seed.Requester, EquipmentSharingProductSeed.Password, seed.OtherPlant);
        var writes = await database.ExecuteScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, seed);

        // The absolute route needs no scope key or sys:manage, and ignores another user's supplied identity.
        var scopes = await Body<BusinessPage<InventoryAccessScope>>(await member.GetAsync(scopesRoute + $"?userId={seed.Reviewer}&plantId={seed.SelectorForeignPlant}"));
        scopes.Total.Should().Be(2);
        scopes.Items.Select(s => s.Membership.OrganizationId.ToString("D")).Should().Equal(
            new[] { organization.ToString("D"), other.ToString("D") }.OrderBy(id => id, StringComparer.Ordinal));
        scopes.Items.Should().OnlyContain(s => s.Membership.UserId == seed.Requester && s.Membership.TenantId == tenant
            && s.Binding.OrganizationId == s.Membership.OrganizationId && s.Plant.PlantId == s.Binding.PlantId);
        var page = await Body<BusinessPage<InventoryAccessScope>>(await member.GetAsync(scopesRoute + "?offset=1&limit=1"));
        page.Total.Should().Be(2); page.Items.Should().BeEquivalentTo(scopes.Items.Skip(1));
        var past = await Body<BusinessPage<InventoryAccessScope>>(await member.GetAsync(scopesRoute + "?offset=2&limit=1"));
        past.Total.Should().Be(2); past.Items.Should().BeEmpty();
        var noMembership = await Body<BusinessPage<InventoryAccessScope>>(await admin.GetAsync(scopesRoute));
        noMembership.Total.Should().Be(0); noMembership.Items.Should().BeEmpty();
        var workers = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + $"/workers?plantId={seed.OtherPlant}&userId={seed.Reviewer}"));
        workers.Total.Should().Be(3);
        workers.Items.Should().HaveCount(3).And.OnlyContain(w => w.PlantId == seed.Plant && w.IsActive);
        var text = Uri.EscapeDataString("작업 %_[X]");
        var filtered = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + $"/workers?text={text}&offset=1&limit=1"));
        filtered.Total.Should().Be(2); filtered.Items.Should().ContainSingle().Which.WorkerId.Should().Be(seed.SelectorWorkerB);
        var workerPast = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + $"/workers?text={text}&offset=2&limit=1"));
        workerPast.Total.Should().Be(2); workerPast.Items.Should().BeEmpty();
        foreach (var query in new[] { "text=%20", "text=%20&text=NoSpaces" })
        {
            var spaces = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + "/workers?" + query));
            spaces.Total.Should().Be(2);
            spaces.Items.Select(w => w.WorkerId).Should().Equal(seed.SelectorWorkerA, seed.SelectorWorkerB);
        }
        var maximumText = Uri.EscapeDataString(new string(' ', 256));
        var maximum = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + "/workers?text=" + maximumText));
        maximum.Total.Should().Be(0); maximum.Items.Should().BeEmpty();
        await Status(await member.GetAsync(route + "/workers?text=" + maximumText + "%20"), HttpStatusCode.BadRequest);
        foreach (var endpoint in new[] { scopesRoute, route + "/workers" })
            foreach (var invalid in new[] { "offset=-1", "limit=0", "limit=101", "offset=invalid" })
                await Status(await member.GetAsync(endpoint + "?" + invalid), HttpStatusCode.BadRequest);
        await Status(await member.GetAsync($"/api/v1/ivt/shared-equipment/{tenant}/{other}/workers"), HttpStatusCode.Forbidden);
        await Status(await member.GetAsync($"/api/v1/ivt/shared-equipment/{tenant}/{foreign}/workers"), HttpStatusCode.Forbidden);
        await database.ExecuteAsync("""
            UPDATE MDM_WORKER SET PLANT_ID=@OtherPlant WHERE WORKER_ID=@SelectorWorkerA;
            UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID=@SelectorWorkerB;
            """, seed);
        var changed = await Body<BusinessPage<WorkerDto>>(await member.GetAsync(route + "/workers?text=" + text));
        changed.Total.Should().Be(0); changed.Items.Should().BeEmpty();
        (await database.ExecuteScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, seed)).Should().Be(writes);
        membership = await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute,
            new BusinessMembershipChange(membership.Version, true, ["equipment.read"])));
        await Status(await member.GetAsync(route + "/workers"), HttpStatusCode.Forbidden);
        (await Body<BusinessPage<InventoryAccessScope>>(await member.GetAsync(scopesRoute))).Total.Should().Be(2);
        await Body<BusinessMembership>(await admin.PutAsJsonAsync(membershipRoute, new BusinessMembershipChange(membership.Version, false, [])));
        var revokedWrites = await database.ExecuteScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, seed);
        var revoked = await Body<BusinessPage<InventoryAccessScope>>(await member.GetAsync(scopesRoute));
        revoked.Total.Should().Be(1); revoked.Items.Should().ContainSingle().Which.Binding.OrganizationId.Should().Be(other);
        await Status(await member.GetAsync(route + "/workers"), HttpStatusCode.Forbidden);
        (await database.ExecuteScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, seed)).Should().Be(revokedWrites);
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
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class EquipmentSharingMssqlTests(ITestOutputHelper output)
{
    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_selector_scans_continue_after_nonnull_membership_and_worker_cursors()
    {
        var h = await Harness.CreateAsync(output);
        var tenants = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") }
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        // More than one batch in the first tenant forces both branches of the paired cursor:
        // remaining organizations in that tenant, then smaller organization IDs in the next tenant.
        var memberships = Enumerable.Range(0, 150).Select(index => new
        {
            Tenant = tenants[index < 130 ? 0 : 1], Organization = $"00000000-0000-0000-0000-{index % 130 + 1:D12}",
            h.Seed.Requester, h.Seed.Now, Plant = h.Seed.Plant + $"-S{index:D3}", Version = Guid.NewGuid().ToString("D"),
            Bound = new[] { 0, 127, 128, 129, 149 }.Contains(index), ActiveBinding = index != 128
        }).ToArray();
        await h.Database.ExecuteAsync("""
            INSERT INTO SYS_BUSINESS_MEMBERSHIP
                (TENANT_ID, ORGANIZATION_ID, USER_ID, IS_ACTIVE, PERMISSIONS, MEMBERSHIP_VERSION, UPDATED_BY, UPDATED_AT)
            VALUES (@Tenant, @Organization, @Requester, 1, 'stock.read', 1, 'admin', @Now)
            """, memberships);
        await h.Database.ExecuteAsync("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Plant, 'Native continuation fixture', 'admin', @Now, 'admin', @Now);
            INSERT INTO IVT_BUSINESS_SCOPE
                (TENANT_ID, ORGANIZATION_ID, PLANT_ID, VERSION, IS_ACTIVE, UPDATED_BY, UPDATED_AT)
            VALUES (@Tenant, @Organization, @Plant, @Version, @ActiveBinding, 'admin', @Now)
            """, memberships.Where(row => row.Bound));
        var workers = Enumerable.Range(0, 150).Select(index => new
        {
            Id = h.Seed.Worker + $"-{index:D3}", h.Seed.Plant, h.Seed.Now, Active = index != 128,
            Name = new[] { 0, 127, 128, 149 }.Contains(index) ? "Native %_[x]" : "Unmatched"
        }).ToArray();
        await h.Database.ExecuteAsync("""
            INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@Id, @Name, @Plant, @Active, 'admin', @Now, 'admin', @Now)
            """, workers);
        var expectedKeys = memberships.Where(row => row.Bound && row.ActiveBinding)
            .Select(row => row.Tenant + "/" + row.Organization)
            .Append(h.Tenant.ToString("D") + "/" + h.Organization.ToString("D"))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();

        var scopes = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester);
        scopes.Total.Should().Be(5);
        scopes.Items.Select(s => s.Membership.TenantId.ToString("D") + "/" + s.Membership.OrganizationId.ToString("D"))
            .Should().Equal(expectedKeys);
        var page = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester, 3, 2);
        page.Total.Should().Be(5);
        page.Items.Should().BeEquivalentTo(scopes.Items.Skip(3), options => options.WithStrictOrdering());
        var past = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester, 5, 1);
        past.Total.Should().Be(5); past.Items.Should().BeEmpty();

        var matchingWorkers = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "native %_[X]");
        matchingWorkers.Total.Should().Be(3);
        matchingWorkers.Items.Select(w => w.WorkerId).Should().Equal(workers[0].Id, workers[127].Id, workers[149].Id);
        var workerPage = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "native %_[X]", 1, 1);
        workerPage.Total.Should().Be(3);
        workerPage.Items.Should().ContainSingle().Which.WorkerId.Should().Be(workers[127].Id);
        var workerPast = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "native %_[X]", 3, 1);
        workerPast.Total.Should().Be(3); workerPast.Items.Should().BeEmpty();
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_selectors_compose_eligible_owned_scopes_and_current_workers_without_read_side_effects()
    {
        var h = await Harness.CreateAsync(output);
        await h.Database.ExecuteAsync(EquipmentSharingProductSeed.SelectorWorkersSql, h.Seed);
        var other = Guid.NewGuid(); var foreign = Guid.NewGuid(); var unbound = Guid.NewGuid();
        var memberships = new BusinessMembershipBridge(h.Database.DataSource);
        await h.Bridge.BindScopeAsync("admin", h.Tenant, other, h.Seed.OtherPlant, null, true);
        await h.Bridge.BindScopeAsync("admin", h.Tenant, foreign, h.Seed.SelectorForeignPlant, null, true);
        foreach (var organization in new[] { other, unbound })
            (await memberships.SaveMembershipAsync("admin", h.Tenant, organization, h.Seed.Requester, new(0, true, ["stock.read"]))).IsSuccess.Should().BeTrue();
        (await memberships.SaveMembershipAsync("admin", h.Tenant, foreign, h.Seed.Reviewer, new(0, true, ["equipment.read"]))).IsSuccess.Should().BeTrue();
        var writes = await h.Database.ScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, h.Seed);

        var scopes = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester);
        scopes.Total.Should().Be(2);
        scopes.Items.Select(s => s.Membership.OrganizationId.ToString("D")).Should().Equal(
            new[] { h.Organization.ToString("D"), other.ToString("D") }.OrderBy(id => id, StringComparer.Ordinal));
        scopes.Items.Should().OnlyContain(s => s.Membership.UserId == h.Seed.Requester && s.Binding.Active
            && s.Membership.TenantId == h.Tenant && s.Binding.OrganizationId == s.Membership.OrganizationId && s.Plant.PlantId == s.Binding.PlantId);
        var page = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester, 1, 1);
        page.Total.Should().Be(2); page.Items.Should().BeEquivalentTo(scopes.Items.Skip(1));
        var past = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester, 2, 1);
        past.Total.Should().Be(2); past.Items.Should().BeEmpty();
        var workers = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "%_[X]");
        workers.Total.Should().Be(2);
        workers.Items.Select(w => w.WorkerId).Should().Equal(h.Seed.SelectorWorkerA, h.Seed.SelectorWorkerB);
        workers.Items.Should().OnlyContain(w => w.IsActive && w.PlantId == h.Seed.Plant);
        var workerPage = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "%_[X]", 1, 1);
        workerPage.Total.Should().Be(2); workerPage.Items.Should().Equal(workers.Items.Skip(1));
        var workerPast = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "%_[X]", 2, 1);
        workerPast.Total.Should().Be(2); workerPast.Items.Should().BeEmpty();
        (await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, " ")).Total.Should().Be(2);
        await Error(() => h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, other), "BUSINESS_ACCESS_DENIED");
        await h.Database.ExecuteAsync("""
            UPDATE MDM_WORKER SET PLANT_ID=@OtherPlant WHERE WORKER_ID=@SelectorWorkerA;
            UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID=@SelectorWorkerB;
            """, h.Seed);
        var changed = await h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization, "%_[X]");
        changed.Total.Should().Be(0); changed.Items.Should().BeEmpty();
        (await h.Database.ScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, h.Seed)).Should().Be(writes);
        (await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization, h.Seed.Requester,
            new(h.Requester.Version, false, []))).IsSuccess.Should().BeTrue();
        var revokedWrites = await h.Database.ScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, h.Seed);
        var revoked = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester);
        revoked.Total.Should().Be(1); revoked.Items.Should().ContainSingle().Which.Binding.OrganizationId.Should().Be(other);
        await Error(() => h.Bridge.ListWorkersAsync(h.Seed.Requester, h.Tenant, h.Organization), "BUSINESS_ACCESS_DENIED");
        await h.Database.ExecuteAsync("UPDATE SYS_USER SET IS_DELETED=1 WHERE USER_ID=@Requester", h.Seed);
        var deleted = await h.Bridge.ListAccessibleScopesAsync(h.Seed.Requester);
        deleted.Total.Should().Be(0); deleted.Items.Should().BeEmpty();
        (await h.Database.ScalarAsync<long>(EquipmentSharingProductSeed.SelectorWritesSql, h.Seed)).Should().Be(revokedWrites);
    }

    [EquipmentMssqlFact]
    public async Task Actual_SQL_Server_lists_preserve_literal_filters_scoped_totals_and_inactive_worker_history()
    {
        var h = await Harness.CreateAsync(output);
        var assets = new List<SharedEquipment>();
        foreach (var code in new[] { "LIST-A", "LIST-B", "LIST-C" })
            assets.Add(await h.Bridge.CreateEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, Guid.NewGuid(), code, "장비 %_[x]", 4, false));
        await h.Bridge.CreateEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, Guid.NewGuid(), "LIST-D", "장비 anything-x", 1, false);
        await h.Bridge.SetEquipmentActiveAsync(h.Seed.Requester, h.Tenant, h.Organization, assets[0].Id, assets[0].Version, false);
        var returned = await h.Request(assets[1]);
        var cancelled = await h.Request(assets[1]);
        cancelled = await h.Bridge.CancelBookingAsync(h.Seed.Requester, h.Tenant, h.Organization, cancelled.Id, cancelled.Version);
        var reviewerBooking = await h.Bridge.RequestBookingAsync(h.Seed.Reviewer, h.Tenant, h.Organization, Guid.NewGuid(), assets[1].Id,
            h.Seed.Worker, returned.Start, returned.End, 1);
        var secondEquipment = await h.Request(assets[2]);
        var other = Guid.NewGuid();
        var memberships = new BusinessMembershipBridge(h.Database.DataSource);
        await h.Bridge.BindScopeAsync("admin", h.Tenant, other, h.Seed.OtherPlant, null, true);
        (await memberships.SaveMembershipAsync("admin", h.Tenant, other, h.Seed.Requester, new(0, true, EquipmentSharingProductSeed.Grants))).IsSuccess.Should().BeTrue();
        var foreign = await h.Bridge.CreateEquipmentAsync(h.Seed.Requester, h.Tenant, other, Guid.NewGuid(), "FOREIGN", "장비 %_[x]", 1, false);
        await h.Bridge.RequestBookingAsync(h.Seed.Requester, h.Tenant, other, Guid.NewGuid(), foreign.Id,
            h.Seed.OtherWorker, returned.Start, returned.End, 1);
        h.Clock.Now = returned.Start;
        returned = await h.Bridge.CheckOutAsync(h.Seed.Requester, h.Tenant, h.Organization, returned.Id, returned.Version);
        h.Clock.Now = returned.End;
        returned = await h.Bridge.ReturnAsync(h.Seed.Requester, h.Tenant, h.Organization, returned.Id, returned.Version);
        await h.Database.ExecuteAsync("UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID=@Worker", h.Seed);
        var audits = await h.Count("IVT_SHARED_EQUIPMENT_AUDIT");

        var equipment = await h.Bridge.ListEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, new("장비 %_[X]"));
        equipment.Total.Should().Be(2);
        equipment.Items.Select(a => a.Id).Should().BeEquivalentTo(assets.Skip(1).Select(a => a.Id));
        var equipmentPage = await h.Bridge.ListEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, new("장비 %_[X]", Offset: 1, Limit: 1));
        equipmentPage.Total.Should().Be(2); equipmentPage.Items.Should().Equal(equipment.Items.Skip(1));
        var equipmentPast = await h.Bridge.ListEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, new("장비 %_[X]", Offset: 20, Limit: 1));
        equipmentPast.Total.Should().Be(2); equipmentPast.Items.Should().BeEmpty();
        (await h.Bridge.ListEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, new("장비 %_[X]", true)))
            .Items.Should().Contain(a => a.Id == assets[0].Id && !a.Active).And.HaveCount(3);
        var all = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization);
        all.Total.Should().Be(4);
        all.Items.Select(b => b.Id).Should().BeEquivalentTo([returned.Id, cancelled.Id, reviewerBooking.Id, secondEquipment.Id]);
        var equipmentBookings = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization, assets[1].Id);
        equipmentBookings.Total.Should().Be(3);
        var page = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization, assets[1].Id, offset: 1, limit: 1);
        page.Total.Should().Be(3); page.Items.Should().Equal(equipmentBookings.Items.Skip(1).Take(1));
        var history = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization, assets[1].Id, EquipmentBookingState.Returned);
        history.Total.Should().Be(1); history.Items.Should().Equal(returned);
        var approved = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization, state: EquipmentBookingState.Approved);
        approved.Total.Should().Be(2);
        approved.Items.Select(b => b.Id).Should().BeEquivalentTo([reviewerBooking.Id, secondEquipment.Id]);
        var past = await h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization, assets[1].Id, EquipmentBookingState.Returned, 1, 1);
        past.Total.Should().Be(1); past.Items.Should().BeEmpty();
        (await memberships.SaveMembershipAsync("admin", h.Tenant, h.Organization, h.Seed.Requester,
            new(h.Requester.Version, true, EquipmentSharingProductSeed.Grants.Where(g => g != "equipment.read" && g != "equipment.booking.read").ToArray())))
            .IsSuccess.Should().BeTrue();
        await Error(() => h.Bridge.ListEquipmentAsync(h.Seed.Requester, h.Tenant, h.Organization, new()), "BUSINESS_ACCESS_DENIED");
        await Error(() => h.Bridge.ListBookingsAsync(h.Seed.Requester, h.Tenant, h.Organization), "BUSINESS_ACCESS_DENIED");
        (await h.Count("IVT_SHARED_EQUIPMENT_AUDIT")).Should().Be(audits);
    }

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
    public string SelectorWorkerA => Worker + "-A";
    public string SelectorWorkerB => Worker + "-B";
    public string SelectorInactiveWorker => Worker + "-C";
    public string SelectorForeignPlant => Plant + "-F";
    public string Role { get; } = "EQR-" + Guid.NewGuid().ToString("N");
    public string Requester { get; } = "EQU-" + Guid.NewGuid().ToString("N");
    public string Reviewer { get; } = "EQU-" + Guid.NewGuid().ToString("N");
    public DateTime Now { get; } = DateTime.UtcNow;
    public string PasswordHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant();

    public const string SelectorWorkersSql = """
        INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES (@SelectorForeignPlant, 'Foreign selector plant', 'admin', @Now, 'admin', @Now);
        UPDATE MDM_WORKER SET WORKER_NAME='NoSpaces' WHERE WORKER_ID=@Worker;
        UPDATE MDM_WORKER SET WORKER_NAME=@SelectorWorkerName WHERE WORKER_ID=@OtherWorker;
        INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES (@SelectorWorkerA, @SelectorWorkerName, @Plant, 1, 'admin', @Now, 'admin', @Now),
               (@SelectorWorkerB, @SelectorWorkerName, @Plant, 1, 'admin', @Now, 'admin', @Now),
               (@SelectorInactiveWorker, @SelectorWorkerName, @Plant, 0, 'admin', @Now, 'admin', @Now);
        """;
    public string SelectorWorkerName => "작업 %_[x]";
    // Seed keys isolate these counts even when unrelated SQL fixtures share the database.
    public const string SelectorWritesSql = """
        SELECT (SELECT COUNT(*) FROM SYS_BUSINESS_IDENTITY WHERE USER_ID IN (@Requester,@Reviewer))
             + (SELECT COUNT(*) FROM IVT_WORKER_IDENTITY WHERE WORKER_ID IN (@Worker,@OtherWorker,@SelectorWorkerA,@SelectorWorkerB,@SelectorInactiveWorker))
             + (SELECT COUNT(*) FROM SYS_BUSINESS_MEMBERSHIP_AUDIT WHERE USER_ID IN (@Requester,@Reviewer))
             + (SELECT COUNT(*) FROM IVT_BUSINESS_SCOPE_AUDIT WHERE PLANT_ID IN (@Plant,@OtherPlant,@SelectorForeignPlant))
             + (SELECT COUNT(*) FROM IVT_SHARED_EQUIPMENT_AUDIT WHERE USER_ID IN
                 (SELECT BUSINESS_USER_ID FROM SYS_BUSINESS_IDENTITY WHERE USER_ID IN (@Requester,@Reviewer)))
        """;

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
