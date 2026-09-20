using System.Data;
using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Sqlite;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Infrastructure;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class EquipmentSharingPersistenceTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IAsyncLifetime
{
    private readonly string _path;
    private readonly string _connectionString;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organization = Guid.NewGuid();
    private readonly Clock _clock = new();
    private readonly EquipmentSharingBridge _bridge;
    private readonly BusinessMembershipBridge _memberships;
    private InventoryScopeBinding _binding = null!;
    private static readonly string[] Grants = ["equipment.read", "equipment.write", "equipment.booking.read",
        "equipment.booking.request", "equipment.booking.decide", "equipment.booking.cancel", "equipment.booking.checkout", "equipment.booking.return"];

    public EquipmentSharingPersistenceTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
        _bridge = NewBridge(); _memberships = new(DataSource());
        Execute("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES ('SHARED-P1','Shared plant'), ('SHARED-P2','Other plant');
            INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE)
                VALUES ('SHARED-W1','Worker','SHARED-P1',1), ('SHARED-W2','Other worker','SHARED-P2',1);
            INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, PERMISSIONS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('SHARED-MEMBER','Shared member','', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('shared-user','Requester','','','SHARED-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP),
                       ('shared-reviewer','Reviewer','','','SHARED-MEMBER', 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
            """);
    }
    public async Task InitializeAsync()
    {
        _binding = await _bridge.BindScopeAsync("admin", _tenant, _organization, "SHARED-P1", null, true);
        foreach (var user in new[] { "shared-user", "shared-reviewer" })
            (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, user, new(0, true, Grants))).IsSuccess.Should().BeTrue();
    }
    public Task DisposeAsync() { File.Delete(_path); return Task.CompletedTask; }
    private EesDataSource DataSource() => new() { Provider = new SqliteProvider(), ConnectionString = _connectionString };
    private EquipmentSharingBridge NewBridge() => new(DataSource(), new BusinessMembershipBridge(DataSource()), new BusinessMasterDirectory(DataSource()), _clock);
    private void Execute(string sql, object? values = null) { using var connection = new SqliteConnection(_connectionString); connection.Open(); connection.Execute(sql, values); }
    private long Count(string table) { using var connection = new SqliteConnection(_connectionString); connection.Open(); return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table); }
    private Task<SharedEquipment> Asset(int capacity = 1, bool approval = true)
        => _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, Guid.NewGuid(), Guid.NewGuid().ToString("N"), "공유 자산", capacity, approval);
    private Task<EquipmentBooking> Request(SharedEquipment asset, Guid? id = null, string worker = "SHARED-W1",
        DateTimeOffset? start = null, DateTimeOffset? end = null, int quantity = 1, CancellationToken ct = default)
        => _bridge.RequestBookingAsync("shared-user", _tenant, _organization, id ?? Guid.NewGuid(), asset.Id, worker,
            start ?? _clock.Now.AddHours(1), end ?? _clock.Now.AddHours(2), quantity, ct);
    private static async Task Error(Func<Task> action, string code)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code.Should().Be(code);

    private (long BusinessIdentities, long WorkerIdentities, long MembershipAudits, long ScopeAudits, long EquipmentAudits) SelectorWrites()
        => (Count("SYS_BUSINESS_IDENTITY"), Count("IVT_WORKER_IDENTITY"), Count("SYS_BUSINESS_MEMBERSHIP_AUDIT"),
            Count("IVT_BUSINESS_SCOPE_AUDIT"), Count("IVT_SHARED_EQUIPMENT_AUDIT"));

    [Fact]
    public async Task Scope_selector_filters_across_300_memberships_before_paging_and_composes_only_current_owned_scopes()
    {
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "shared-user", new(1, true, []))).IsSuccess.Should().BeTrue();
        // Exercise every supported inventory operation as a sole qualifying grant.
        string[] inventoryGrants = ["stock.warehouse.read", "stock.warehouse.write", "stock.product.write", "stock.read", "stock.post",
            "stock.reverse", "stock.reserve", "stock.consume", "stock.release", "equipment.read", "equipment.write",
            "equipment.booking.read", "equipment.booking.request", "equipment.booking.decide", "equipment.booking.cancel",
            "equipment.booking.checkout", "equipment.booking.return"];
        var rows = Enumerable.Range(0, 301).Select(index =>
        {
            var eligible = index < inventoryGrants.Length || new[] { 127, 128, 255, 256, 299, 300 }.Contains(index);
            return new
            {
                tenant = index < 150 ? "10000000-0000-0000-0000-000000000001"
                    : index < 300 ? "20000000-0000-0000-0000-000000000001" : "30000000-0000-0000-0000-000000000001",
                organization = $"00000000-0000-0000-0000-{index % 150 + 1:D12}",
                plant = $"SELECT-{index:D3}", name = $"Selector plant {index:D3}", version = Guid.NewGuid().ToString("D"),
                user = index == 300 ? "shared-reviewer" : "shared-user",
                permissions = index < inventoryGrants.Length ? inventoryGrants[index] : eligible || index % 4 != 0 ? "stock.read" : "",
                activeMember = eligible || index % 4 != 3,
                bound = eligible || index % 4 != 1,
                activeBinding = eligible || index % 4 != 2,
                eligible
            };
        }).ToArray();
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@plant, @name, 'admin', CURRENT_TIMESTAMP, 'admin', CURRENT_TIMESTAMP);
                INSERT INTO SYS_BUSINESS_MEMBERSHIP (TENANT_ID, ORGANIZATION_ID, USER_ID, IS_ACTIVE, PERMISSIONS,
                                                     MEMBERSHIP_VERSION, UPDATED_BY, UPDATED_AT)
                VALUES (@tenant, @organization, @user, @activeMember, @permissions, 1, 'admin', CURRENT_TIMESTAMP);
                """, rows, transaction);
            connection.Execute("""
                INSERT INTO IVT_BUSINESS_SCOPE (TENANT_ID, ORGANIZATION_ID, PLANT_ID, VERSION, IS_ACTIVE, UPDATED_BY, UPDATED_AT)
                VALUES (@tenant, @organization, @plant, @version, @activeBinding, 'admin', CURRENT_TIMESTAMP)
                """, rows.Where(row => row.bound), transaction);
            transaction.Commit();
        }
        var expected = rows.Where(row => row.eligible && row.user == "shared-user").ToArray();
        var writes = SelectorWrites();

        var all = await NewBridge().ListAccessibleScopesAsync("shared-user", limit: 100);
        all.Total.Should().Be(22);
        all.Items.Select(s => s.Binding.PlantId).Should().Equal(expected.Select(r => r.plant));
        foreach (var scope in all.Items)
        {
            scope.Membership.UserId.Should().Be("shared-user");
            scope.Membership.IsActive.Should().BeTrue();
            scope.Binding.Active.Should().BeTrue();
            scope.Binding.TenantId.Should().Be(scope.Membership.TenantId);
            scope.Binding.OrganizationId.Should().Be(scope.Membership.OrganizationId);
            scope.Plant.PlantId.Should().Be(scope.Binding.PlantId);
            scope.Plant.PlantName.Should().Be(rows.Single(r => r.plant == scope.Binding.PlantId).name);
        }
        var page = await _bridge.ListAccessibleScopesAsync("shared-user", 17, 4);
        page.Total.Should().Be(22);
        page.Items.Should().BeEquivalentTo(all.Items.Skip(17).Take(4), options => options.WithStrictOrdering());
        var past = await _bridge.ListAccessibleScopesAsync("shared-user", 22, 1);
        past.Total.Should().Be(22); past.Items.Should().BeEmpty();
        SelectorWrites().Should().Be(writes);
    }

    [Theory]
    [InlineData("UPDATE SYS_USER SET IS_ACTIVE=0 WHERE USER_ID='shared-user'")]
    [InlineData("UPDATE SYS_USER SET IS_DELETED=1 WHERE USER_ID='shared-user'")]
    [InlineData("UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=0 WHERE USER_ID='shared-user'")]
    [InlineData("DELETE FROM SYS_BUSINESS_MEMBERSHIP_AUDIT WHERE USER_ID='shared-user'; DELETE FROM SYS_BUSINESS_MEMBERSHIP WHERE USER_ID='shared-user'")]
    [InlineData("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='' WHERE USER_ID='shared-user'")]
    [InlineData("UPDATE IVT_BUSINESS_SCOPE SET IS_ACTIVE=0")]
    [InlineData("PRAGMA foreign_keys=OFF; UPDATE IVT_BUSINESS_SCOPE SET PLANT_ID='missing-plant'; PRAGMA foreign_keys=ON;")]
    public async Task Selectors_recheck_current_user_membership_binding_and_plant_eligibility(string mutation)
    {
        (await _bridge.ListAccessibleScopesAsync("shared-user")).Total.Should().Be(1);
        (await _bridge.ListWorkersAsync("shared-user", _tenant, _organization)).Total.Should().Be(1);
        Execute(mutation);
        var writes = SelectorWrites();

        var scopes = await _bridge.ListAccessibleScopesAsync("shared-user");
        scopes.Total.Should().Be(0); scopes.Items.Should().BeEmpty();
        await Error(() => _bridge.ListWorkersAsync("shared-user", _tenant, _organization), "BUSINESS_ACCESS_DENIED");
        SelectorWrites().Should().Be(writes);
    }

    [Fact]
    public async Task Scope_selector_without_memberships_does_not_infer_admin_authority_or_create_identity()
    {
        var writes = SelectorWrites();
        var scopes = await _bridge.ListAccessibleScopesAsync("admin");
        scopes.Total.Should().Be(0); scopes.Items.Should().BeEmpty();
        SelectorWrites().Should().Be(writes);
    }

    [Fact]
    public async Task Worker_selector_filters_literal_live_plant_workers_and_never_reserves_identity_or_bypasses_booking_rechecks()
    {
        Execute("""
            INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('SELECT-A','작업 %_[x]','SHARED-P1',1,'admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP),
                   ('SELECT-B','작업 %_[x]','SHARED-P1',1,'admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP),
                   ('SELECT-C','작업 %_[x]','SHARED-P1',0,'admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP),
                   ('SELECT-D','NoSpaces','SHARED-P1',1,'admin',CURRENT_TIMESTAMP,'admin',CURRENT_TIMESTAMP);
            UPDATE MDM_WORKER SET WORKER_NAME='작업 %_[x]' WHERE WORKER_ID='SHARED-W2';
            """);
        var asset = await Asset();
        var writes = SelectorWrites();
        var all = await NewBridge().ListWorkersAsync("shared-user", _tenant, _organization, "%_[X]");
        all.Total.Should().Be(2);
        all.Items.Select(w => w.WorkerId).Should().Equal("SELECT-A", "SELECT-B");
        all.Items.Should().OnlyContain(w => w.IsActive && w.PlantId == "SHARED-P1");
        var page = await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, "%_[X]", 1, 1);
        page.Total.Should().Be(2); page.Items.Should().Equal(all.Items.Skip(1));
        var past = await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, "%_[X]", 2, 1);
        past.Total.Should().Be(2); past.Items.Should().BeEmpty();
        (await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, " ")).Total.Should().Be(2);
        (await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, "select-a")).Items.Should().ContainSingle();
        Execute("UPDATE MDM_WORKER SET PLANT_ID='SHARED-P2' WHERE WORKER_ID='SELECT-A'; UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID='SELECT-B'");
        var changed = await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, "%_[X]");
        changed.Total.Should().Be(0); changed.Items.Should().BeEmpty();
        await Error(() => Request(asset, worker: "SELECT-A"), "EMPLOYEE_NOT_FOUND");
        await Error(() => Request(asset, worker: "SELECT-B"), "EMPLOYEE_NOT_FOUND");
        SelectorWrites().Should().Be(writes);
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "shared-user",
            new(1, true, ["equipment.read", "equipment.booking.read"]))).IsSuccess.Should().BeTrue();
        var revokedWrites = SelectorWrites();
        await Error(() => _bridge.ListWorkersAsync("shared-user", _tenant, _organization), "BUSINESS_ACCESS_DENIED");
        SelectorWrites().Should().Be(revokedWrites);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task Selectors_reject_invalid_page_bounds(int offset, int limit)
    {
        await Error(() => _bridge.ListAccessibleScopesAsync("shared-user", offset, limit), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.ListWorkersAsync("shared-user", _tenant, _organization, offset: offset, limit: limit), "INVALID_BUSINESS_INPUT");
    }

    [Fact]
    public async Task Worker_selector_accepts_256_literal_spaces_but_rejects_257()
    {
        var page = await _bridge.ListWorkersAsync("shared-user", _tenant, _organization, new string(' ', 256));
        page.Total.Should().Be(0); page.Items.Should().BeEmpty();
        await Error(() => _bridge.ListWorkersAsync("shared-user", _tenant, _organization, new string(' ', 257)), "INVALID_BUSINESS_INPUT");
    }

    [Fact]
    public async Task Equipment_lists_filter_literal_names_activity_and_scope_before_paging_without_audits()
    {
        var assets = new List<SharedEquipment>();
        foreach (var code in new[] { "LIST-A", "LIST-B", "LIST-C" })
            assets.Add(await _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, Guid.NewGuid(), code, "장비 %_[x]", 1, false));
        await _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, Guid.NewGuid(), "LIST-D", "장비 anything-x", 1, false);
        await _bridge.SetEquipmentActiveAsync("shared-user", _tenant, _organization, assets[0].Id, assets[0].Version, false);
        var other = Guid.NewGuid();
        await _bridge.BindScopeAsync("admin", _tenant, other, "SHARED-P2", null, true);
        (await _memberships.SaveMembershipAsync("admin", _tenant, other, "shared-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        await _bridge.CreateEquipmentAsync("shared-user", _tenant, other, Guid.NewGuid(), "LIST-E", "장비 %_[x]", 1, false);
        var audits = Count("IVT_SHARED_EQUIPMENT_AUDIT");
        var query = new InventoryQuery("장비 %_[X]");

        var all = await NewBridge().ListEquipmentAsync("shared-user", _tenant, _organization, query);
        all.Total.Should().Be(2);
        all.Items.Select(a => a.Id).Should().BeEquivalentTo(assets.Skip(1).Select(a => a.Id));
        var page = await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, query with { Offset = 1, Limit = 1 });
        page.Total.Should().Be(2); page.Items.Should().Equal(all.Items.Skip(1));
        var past = await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, query with { Offset = 20, Limit = 1 });
        past.Total.Should().Be(2); past.Items.Should().BeEmpty();
        (await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, query with { IncludeInactive = true }))
            .Items.Should().Contain(a => a.Id == assets[0].Id && !a.Active).And.HaveCount(3);
        (await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, new("list-b"))).Items.Should().ContainSingle();
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(audits);
    }

    [Fact]
    public async Task Booking_lists_filter_equipment_and_state_but_keep_scope_wide_history_after_worker_deactivation()
    {
        var first = await Asset(capacity: 4, approval: false);
        var second = await Asset(capacity: 4, approval: false);
        var returned = await Request(first);
        var cancelled = await Request(first);
        cancelled = await _bridge.CancelBookingAsync("shared-user", _tenant, _organization, cancelled.Id, cancelled.Version);
        var reviewerBooking = await _bridge.RequestBookingAsync("shared-reviewer", _tenant, _organization, Guid.NewGuid(), first.Id,
            "SHARED-W1", returned.Start, returned.End, 1);
        var otherEquipment = await Request(second);
        var otherTenant = Guid.NewGuid();
        await _bridge.BindScopeAsync("admin", otherTenant, _organization, "SHARED-P2", null, true);
        (await _memberships.SaveMembershipAsync("admin", otherTenant, _organization, "shared-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        var foreign = await _bridge.CreateEquipmentAsync("shared-user", otherTenant, _organization, Guid.NewGuid(), "FOREIGN", "Foreign", 1, false);
        await _bridge.RequestBookingAsync("shared-user", otherTenant, _organization, Guid.NewGuid(), foreign.Id,
            "SHARED-W2", returned.Start, returned.End, 1);
        _clock.Now = returned.Start;
        returned = await _bridge.CheckOutAsync("shared-user", _tenant, _organization, returned.Id, returned.Version);
        _clock.Now = returned.End;
        returned = await _bridge.ReturnAsync("shared-user", _tenant, _organization, returned.Id, returned.Version);
        Execute("UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID='SHARED-W1'");
        var audits = Count("IVT_SHARED_EQUIPMENT_AUDIT");

        var all = await NewBridge().ListBookingsAsync("shared-user", _tenant, _organization);
        all.Total.Should().Be(4);
        all.Items.Select(b => b.Id).Should().BeEquivalentTo([returned.Id, cancelled.Id, reviewerBooking.Id, otherEquipment.Id]);
        var assetPage = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, first.Id);
        assetPage.Total.Should().Be(3);
        var page = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, first.Id, offset: 1, limit: 1);
        page.Total.Should().Be(3); page.Items.Should().Equal(assetPage.Items.Skip(1).Take(1));
        var history = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, first.Id, EquipmentBookingState.Returned);
        history.Total.Should().Be(1); history.Items.Should().Equal(returned);
        var approved = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, state: EquipmentBookingState.Approved);
        approved.Total.Should().Be(2);
        approved.Items.Select(b => b.Id).Should().BeEquivalentTo([reviewerBooking.Id, otherEquipment.Id]);
        var past = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, first.Id, EquipmentBookingState.Cancelled, 1, 1);
        past.Total.Should().Be(1); past.Items.Should().BeEmpty();
        var empty = await _bridge.ListBookingsAsync("shared-user", _tenant, _organization, second.Id, EquipmentBookingState.Returned);
        empty.Total.Should().Be(0); empty.Items.Should().BeEmpty();
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(audits);
    }

    [Fact]
    public async Task Equipment_lists_recheck_each_current_read_grant_without_auditing_reads()
    {
        await Request(await Asset());
        var audits = Count("IVT_SHARED_EQUIPMENT_AUDIT");
        (await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, new())).Total.Should().Be(1);
        (await _bridge.ListBookingsAsync("shared-user", _tenant, _organization)).Total.Should().Be(1);
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "shared-user",
            new(1, true, Grants.Where(g => g != "equipment.read").ToArray()))).IsSuccess.Should().BeTrue();
        await Error(() => _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, new("absent", Offset: 20)), "BUSINESS_ACCESS_DENIED");
        (await _bridge.ListBookingsAsync("shared-user", _tenant, _organization)).Total.Should().Be(1);
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "shared-user",
            new(2, true, Grants.Where(g => g != "equipment.booking.read").ToArray()))).IsSuccess.Should().BeTrue();
        (await _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, new())).Total.Should().Be(1);
        await Error(() => _bridge.ListBookingsAsync("shared-user", _tenant, _organization, offset: 20), "BUSINESS_ACCESS_DENIED");
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(audits);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task Equipment_lists_reject_invalid_page_bounds(int offset, int limit)
    {
        await Error(() => _bridge.ListEquipmentAsync("shared-user", _tenant, _organization, new(Offset: offset, Limit: limit)), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.ListBookingsAsync("shared-user", _tenant, _organization, offset: offset, limit: limit), "INVALID_BUSINESS_INPUT");
    }

    [Fact]
    public async Task Booking_list_rejects_empty_equipment_id_and_undefined_state()
    {
        await Error(() => _bridge.ListBookingsAsync("shared-user", _tenant, _organization, Guid.Empty), "INVALID_BUSINESS_INPUT");
        await Error(() => _bridge.ListBookingsAsync("shared-user", _tenant, _organization, state: (EquipmentBookingState)999), "INVALID_BUSINESS_INPUT");
    }

    [Fact]
    public async Task Real_database_lifecycle_retains_identity_audit_and_replay_after_restart()
    {
        var asset = await Asset(); var bookingId = Guid.NewGuid();
        var booking = await Request(asset, bookingId);
        await Error(() => _bridge.DecideBookingAsync("shared-user", _tenant, _organization, booking.Id, booking.Version, true), "SELF_APPROVAL_NOT_ALLOWED");
        var approved = await _bridge.DecideBookingAsync("shared-reviewer", _tenant, _organization, booking.Id, booking.Version, true);
        _clock.Now = booking.Start;
        var checkedOut = await _bridge.CheckOutAsync("shared-user", _tenant, _organization, booking.Id, approved.Version);
        _clock.Now = booking.End.AddDays(1);
        var returned = await NewBridge().ReturnAsync("shared-user", _tenant, _organization, booking.Id, checkedOut.Version);
        returned.State.Should().Be(EquipmentBookingState.Returned);
        (await NewBridge().GetBookingAsync("shared-user", _tenant, _organization, booking.Id)).Should().Be(returned);
        (await Request(asset, bookingId, start: booking.Start, end: booking.End)).Should().Be(returned);
        (await NewBridge().ReturnAsync("shared-user", _tenant, _organization, booking.Id, checkedOut.Version)).Should().Be(returned);
        Count("IVT_WORKER_IDENTITY").Should().Be(1);
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(5);
    }

    [Fact]
    public async Task Current_code_lookup_and_duplicate_code_conflicts_remain_scoped()
    {
        var asset = await Asset();
        (await NewBridge().GetEquipmentByCodeAsync("shared-user", _tenant, _organization, asset.Code)).Should().Be(asset);
        await Error(() => _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, Guid.NewGuid(), asset.Code, "Changed retry", 1, false), "EQUIPMENT_CODE_CONFLICT");
        var second = await Asset();
        await Error(() => _bridge.UpdateEquipmentAsync("shared-user", _tenant, _organization, asset.Code, "Changed code", 1, false, second.Id, second.Version), "EQUIPMENT_CODE_CONFLICT");
        await Error(() => _bridge.GetEquipmentByCodeAsync("shared-user", _tenant, Guid.NewGuid(), asset.Code), "BUSINESS_ACCESS_DENIED");
        Count("IVT_SHARED_EQUIPMENT").Should().Be(2); Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Creation_replay_survives_rename_without_read_permission_and_rejects_changed_actor_or_payload()
    {
        var operation = Guid.NewGuid();
        var member = (await _memberships.GetMembershipAsync("admin", _tenant, _organization, "shared-user")).Value;
        (await _memberships.SaveMembershipAsync("admin", _tenant, _organization, "shared-user", new(member.Version, true, ["equipment.write"]))).IsSuccess.Should().BeTrue();
        var asset = await _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, operation, "ORIGINAL", "Initial", 1, false);
        var changed = await _bridge.UpdateEquipmentAsync("shared-reviewer", _tenant, _organization, "RENAMED", "Changed", 2, false, asset.Id, asset.Version);
        (await NewBridge().CreateEquipmentAsync("shared-user", _tenant, _organization, operation, "ORIGINAL", "Initial", 1, false)).Should().Be(changed);
        await Error(() => _bridge.GetEquipmentByCodeAsync("shared-user", _tenant, _organization, "RENAMED"), "BUSINESS_ACCESS_DENIED");
        await Error(() => _bridge.CreateEquipmentAsync("shared-user", _tenant, _organization, operation, "ORIGINAL", "Altered", 1, false), "EQUIPMENT_CREATE_OPERATION_CONFLICT");
        await Error(() => _bridge.CreateEquipmentAsync("shared-reviewer", _tenant, _organization, operation, "ORIGINAL", "Initial", 1, false), "EQUIPMENT_CREATE_OPERATION_CONFLICT");
        Count("IVT_SHARED_EQUIPMENT").Should().Be(1); Count("IVT_SHARED_EQUIPMENT_CREATION").Should().Be(1);
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Creation_receipt_failure_rolls_back_asset_and_audit()
    {
        Execute("CREATE TRIGGER creation_failure BEFORE INSERT ON IVT_SHARED_EQUIPMENT_CREATION BEGIN SELECT RAISE(ABORT, 'receipt unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Asset());
        Count("IVT_SHARED_EQUIPMENT").Should().Be(0); Count("IVT_SHARED_EQUIPMENT_CREATION").Should().Be(0);
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(0);
    }

    [Fact]
    public async Task Changed_retry_and_stale_update_do_not_append_effects_or_audits()
    {
        var asset = await Asset(2); var booking = await Request(asset);
        await Error(() => Request(asset, booking.Id, start: booking.Start, end: booking.End, quantity: 2), "BOOKING_OPERATION_CONFLICT");
        await Error(() => _bridge.SetEquipmentActiveAsync("shared-user", _tenant, _organization, asset.Id, Guid.NewGuid(), false), "BUSINESS_VERSION_CONFLICT");
        Count("IVT_SHARED_BOOKING").Should().Be(1); Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(2);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("user")]
    [InlineData("grants")]
    public async Task Current_authority_is_checked_for_each_operation(string revoked)
    {
        var asset = await Asset();
        Execute(revoked switch
        {
            "member" => "UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=0 WHERE USER_ID='shared-user'",
            "user" => "UPDATE SYS_USER SET IS_ACTIVE=0 WHERE USER_ID='shared-user'",
            _ => "UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS='equipment.read' WHERE USER_ID='shared-user'"
        });
        await Error(() => Request(asset), "BUSINESS_ACCESS_DENIED");
        Count("IVT_SHARED_BOOKING").Should().Be(0); Count("IVT_WORKER_IDENTITY").Should().Be(0);
    }

    [Theory]
    [InlineData("equipment.booking.request|*")]
    [InlineData("equipment.write|equipment.read")]
    [InlineData("equipment.read|equipment.read")]
    public async Task Corrupt_stored_grants_never_authorize_writes(string permissions)
    {
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        connection.Execute("UPDATE SYS_BUSINESS_MEMBERSHIP SET PERMISSIONS=@permissions WHERE USER_ID='shared-user'", new { permissions });
        await Error(() => Asset(), "BUSINESS_ACCESS_DENIED");
        Count("IVT_SHARED_EQUIPMENT").Should().Be(0);
    }

    [Fact]
    public async Task Workers_must_belong_to_the_bound_plant_and_be_active()
    {
        var asset = await Asset();
        await Error(() => Request(asset, worker: "SHARED-W2"), "EMPLOYEE_NOT_FOUND");
        Execute("UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID='SHARED-W1'");
        await Error(() => Request(asset), "EMPLOYEE_NOT_FOUND");
        Count("IVT_WORKER_IDENTITY").Should().Be(0);
    }

    [Fact]
    public async Task Worker_transfer_keeps_identity_but_denies_new_old_plant_bookings_and_allows_return()
    {
        var asset = await Asset(2, false); var booking = await Request(asset);
        _clock.Now = booking.Start;
        var checkedOut = await _bridge.CheckOutAsync("shared-user", _tenant, _organization, booking.Id, booking.Version);
        Execute("UPDATE MDM_WORKER SET PLANT_ID='SHARED-P2' WHERE WORKER_ID='SHARED-W1'");
        await Error(() => Request(asset), "EMPLOYEE_NOT_FOUND");
        await _bridge.ReturnAsync("shared-user", _tenant, _organization, booking.Id, checkedOut.Version);
        var other = Guid.NewGuid();
        await _bridge.BindScopeAsync("admin", _tenant, other, "SHARED-P2", null, true);
        (await _memberships.SaveMembershipAsync("admin", _tenant, other, "shared-user", new(0, true, Grants))).IsSuccess.Should().BeTrue();
        var otherAsset = await _bridge.CreateEquipmentAsync("shared-user", _tenant, other, Guid.NewGuid(), "OTHER", "Other", 1, false);
        var next = await _bridge.RequestBookingAsync("shared-user", _tenant, other, Guid.NewGuid(), otherAsset.Id,
            "SHARED-W1", _clock.Now.AddHours(2), _clock.Now.AddHours(3), 1);
        next.EmployeeId.Should().Be(booking.EmployeeId); Count("IVT_WORKER_IDENTITY").Should().Be(1);
        await Error(() => _bridge.GetEquipmentAsync("shared-user", _tenant, other, asset.Id), "EQUIPMENT_NOT_FOUND");
    }

    [Fact]
    public async Task Audit_failure_rolls_back_booking_and_new_worker_identity()
    {
        var asset = await Asset();
        Execute("CREATE TRIGGER shared_audit_failure BEFORE INSERT ON IVT_SHARED_EQUIPMENT_AUDIT BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => Request(asset));
        Count("IVT_SHARED_BOOKING").Should().Be(0); Count("IVT_WORKER_IDENTITY").Should().Be(0);
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(1);
    }

    [Fact]
    public async Task Capacity_uses_peak_overlap_and_touching_half_open_intervals()
    {
        var asset = await Asset(2, false); var start = _clock.Now.AddHours(1);
        await Request(asset, start: start, end: start.AddHours(1));
        await Request(asset, start: start.AddHours(1), end: start.AddHours(2));
        await Request(asset, start: start, end: start.AddHours(2));
        await Error(() => Request(asset, start: start, end: start.AddHours(2)), "EQUIPMENT_CAPACITY_UNAVAILABLE");
        Count("IVT_SHARED_BOOKING").Should().Be(3);
    }

    [Fact]
    public async Task Overdue_checkout_blocks_later_capacity_until_actual_return()
    {
        var asset = await Asset(1, false); var first = await Request(asset);
        _clock.Now = first.Start;
        first = await _bridge.CheckOutAsync("shared-user", _tenant, _organization, first.Id, first.Version);
        _clock.Now = first.End.AddHours(1);
        await Error(() => Request(asset), "EQUIPMENT_CAPACITY_UNAVAILABLE");
        await _bridge.ReturnAsync("shared-user", _tenant, _organization, first.Id, first.Version);
        await Request(asset);
    }

    [Fact]
    public async Task Competing_real_transactions_never_reserve_more_than_capacity()
    {
        var asset = await Asset(1, false);
        var requests = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try { await NewBridge().RequestBookingAsync("shared-user", _tenant, _organization, Guid.NewGuid(), asset.Id,
                "SHARED-W1", _clock.Now.AddHours(1), _clock.Now.AddHours(2), 1); return true; }
            catch (BusinessException error) when (error.Code == "EQUIPMENT_CAPACITY_UNAVAILABLE") { return false; }
            catch (DbException) { return false; }
        }));
        (await Task.WhenAll(requests)).Count(success => success).Should().Be(1);
        Count("IVT_SHARED_BOOKING").Should().Be(1); Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Binding_changes_require_live_admin_CAS_and_no_open_bookings()
    {
        await Error(() => _bridge.BindScopeAsync("shared-user", _tenant, _organization, "SHARED-P1", _binding.Version, false), "BUSINESS_ACCESS_DENIED");
        await Error(() => _bridge.BindScopeAsync("admin", _tenant, _organization, "SHARED-P1", null, true), "BUSINESS_VERSION_CONFLICT");
        await Error(() => _bridge.BindScopeAsync("admin", _tenant, _organization, "SHARED-P2", _binding.Version, true), "SCOPE_PLANT_IMMUTABLE");
        var asset = await Asset(); var booking = await Request(asset);
        await Error(() => _bridge.BindScopeAsync("admin", _tenant, _organization, "SHARED-P1", _binding.Version, false), "SCOPE_HAS_OPEN_BOOKINGS");
        await _bridge.CancelBookingAsync("shared-user", _tenant, _organization, booking.Id, booking.Version);
        var inactive = await _bridge.BindScopeAsync("admin", _tenant, _organization, "SHARED-P1", _binding.Version, false);
        inactive.Active.Should().BeFalse();
        (await _bridge.GetScopeBindingAsync("admin", _tenant, _organization)).Should().Be(inactive);
        await Error(() => _bridge.GetEquipmentAsync("shared-user", _tenant, _organization, asset.Id), "BUSINESS_ACCESS_DENIED");
        Count("IVT_BUSINESS_SCOPE_AUDIT").Should().Be(2);
    }

    [Fact]
    public async Task Owner_queries_use_the_callers_uncommitted_transaction_and_do_not_commit_it()
    {
        var masters = new BusinessMasterDirectory(DataSource());
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            connection.Execute("UPDATE SYS_BUSINESS_MEMBERSHIP SET IS_ACTIVE=0 WHERE USER_ID='shared-user'", transaction: transaction);
            connection.Execute("UPDATE MDM_WORKER SET PLANT_ID='SHARED-P2' WHERE WORKER_ID='SHARED-W1'", transaction: transaction);
            (await _memberships.GetAccessInTransactionAsync(transaction, "shared-user", _tenant, _organization)).Should().BeNull();
            (await masters.FindActiveWorkerAsync(transaction, "SHARED-W1", "SHARED-P1")).Should().BeNull();
            (await masters.FindActiveWorkerAsync(transaction, "SHARED-W1", "SHARED-P2")).Should().Be("SHARED-W1");
            (await _memberships.RequireAdministratorInTransactionAsync(transaction, "admin")).Should().Be("admin");
            transaction.Rollback();
        }
        (await _memberships.GetAccessAsync("shared-user", _tenant, _organization)).Should().NotBeNull();
        var booking = await Request(await Asset());
        booking.EmployeeId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Cancelled_request_has_no_persisted_identity_booking_or_audit()
    {
        var asset = await Asset(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Request(asset, ct: cancellation.Token));
        Count("IVT_SHARED_BOOKING").Should().Be(0); Count("IVT_WORKER_IDENTITY").Should().Be(0);
        Count("IVT_SHARED_EQUIPMENT_AUDIT").Should().Be(1);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
