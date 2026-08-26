using NexaOne.EMS.Application.SpareParts;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.UnitTests.Services;

public sealed class SparePartServiceTests
{
    [Fact]
    public async Task Same_policy_create_replays_but_changed_payload_conflicts()
    {
        var repository = MemoryRepository.Ready();
        var bridge = new SparePartBridge(new SparePartService(repository));
        var command = Policy();

        var first = await bridge.SaveStockPolicyAsync(command);
        var replay = await bridge.SaveStockPolicyAsync(command);
        var conflict = await bridge.SaveStockPolicyAsync(command with { TargetStock = 21m });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(first.Value);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.SparePart.IdempotencyConflict");
    }

    [Fact]
    public async Task Policy_update_uses_expected_version_and_preserves_the_winner()
    {
        var repository = MemoryRepository.Ready();
        var bridge = new SparePartBridge(new SparePartService(repository));
        (await bridge.SaveStockPolicyAsync(Policy())).IsSuccess.Should().BeTrue();

        var winner = await bridge.SaveStockPolicyAsync(
            Policy("policy-update") with { ExpectedVersion = 1, TargetStock = 25m });
        var stale = await bridge.SaveStockPolicyAsync(
            Policy("policy-stale") with { ExpectedVersion = 1, TargetStock = 30m });

        winner.IsSuccess.Should().BeTrue();
        winner.Value.Version.Should().Be(2);
        stale.IsFailure.Should().BeTrue();
        stale.Error.Code.Should().Be("EMS.SparePart.VersionConflict");
        repository.Policy!.TargetStock.Should().Be(25m);
    }

    [Fact]
    public async Task Supplier_and_bom_reject_missing_or_ambiguous_master_scope()
    {
        var bridge = new SparePartBridge(new SparePartService(MemoryRepository.Ready()));

        var missingVendor = await bridge.SaveSupplierAsync(Supplier() with { VendorId = "UNKNOWN" });
        var bothScopes = await bridge.SaveEquipmentBomAsync(
            Bom() with { EquipmentClassId = "EQC01" });

        missingVendor.IsFailure.Should().BeTrue();
        missingVendor.Error.Code.Should().Be("Error.NotFound");
        bothScopes.IsFailure.Should().BeTrue();
        bothScopes.Error.Code.Should().Be("EquipmentScope");
    }

    [Fact]
    public async Task Recommendation_uses_primary_supplier_lead_demand_reserved_stock_and_moq()
    {
        var repository = MemoryRepository.Ready(currentStock: 7m);
        var bridge = new SparePartBridge(new SparePartService(repository));
        (await bridge.SaveStockPolicyAsync(Policy())).IsSuccess.Should().BeTrue();
        (await bridge.SaveSupplierAsync(Supplier("SUP-FAST", "VENDOR-FAST", 1, false, 2m))).IsSuccess.Should().BeTrue();
        (await bridge.SaveSupplierAsync(Supplier("SUP-PRIMARY", "VENDOR01", 4, true, 10m))).IsSuccess.Should().BeTrue();

        var result = await bridge.RecommendReplenishmentAsync("PART01");

        result.IsSuccess.Should().BeTrue();
        result.Value.AvailableQuantity.Should().Be(4m);
        result.Value.LeadTimeDemand.Should().Be(8m);
        result.Value.EffectiveReorderPoint.Should().Be(13m);
        result.Value.EffectiveTargetStock.Should().Be(20m);
        result.Value.RecommendedOrderQuantity.Should().Be(16m);
        result.Value.PartSupplierId.Should().Be("SUP-PRIMARY");
        result.Value.ShouldOrder.Should().BeTrue();
    }

    private static SparePartStockPolicyCommand Policy(string key = "policy-create") => new(
        "PART01", 5m, 8m, 20m, 3m, 2m, 0.95m, 7, true, 0, key, "operator");

    private static SparePartSupplierCommand Supplier(
        string id = "SUP01",
        string vendor = "VENDOR01",
        int lead = 4,
        bool primary = true,
        decimal? moq = 10m) => new(
        id, "PART01", vendor, lead, moq, 12.5m, "KRW", primary, true,
        0, $"supplier:{id}", "VP-01", "operator");

    private static EquipmentPartBomCommand Bom() => new(
        "BOM01", "PART01", 2m, "EQ01", null, "Critical", 90, 1000m,
        "P01", true, 0, "bom-create", "operator");

    private sealed class MemoryRepository : ISparePartManagementRepository
    {
        public HashSet<string> Parts { get; } = ["PART01"];
        public HashSet<string> Vendors { get; } = ["VENDOR01", "VENDOR-FAST"];
        public HashSet<string> Equipment { get; } = ["EQ01"];
        public HashSet<string> EquipmentClasses { get; } = ["EQC01"];
        public decimal CurrentStock { get; private init; }
        public SparePartStockPolicyRecord? Policy { get; private set; }
        public List<SparePartSupplierRecord> Suppliers { get; } = [];
        public EquipmentPartBomRecord? BomItem { get; private set; }

        public static MemoryRepository Ready(decimal currentStock = 50m) => new() { CurrentStock = currentStock };

        public Task<bool> PartExistsAsync(string partId, CancellationToken ct = default)
            => Task.FromResult(Parts.Contains(partId));
        public Task<bool> VendorExistsAsync(string vendorId, CancellationToken ct = default)
            => Task.FromResult(Vendors.Contains(vendorId));
        public Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default)
            => Task.FromResult(Equipment.Contains(equipmentId));
        public Task<bool> EquipmentClassExistsAsync(string equipmentClassId, CancellationToken ct = default)
            => Task.FromResult(EquipmentClasses.Contains(equipmentClassId));
        public Task<SparePartStockPolicyRecord?> GetStockPolicyAsync(string partId, CancellationToken ct = default)
            => Task.FromResult(Policy);
        public Task<SparePartStockPolicyRecord?> GetStockPolicyByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Policy?.LastIdempotencyKey == key ? Policy : null);
        public Task<SparePartSupplierRecord?> GetSupplierAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Suppliers.SingleOrDefault(x => x.PartSupplierId == id));
        public Task<SparePartSupplierRecord?> GetSupplierByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Suppliers.SingleOrDefault(x => x.LastIdempotencyKey == key));
        public Task<EquipmentPartBomRecord?> GetEquipmentBomAsync(string id, CancellationToken ct = default)
            => Task.FromResult(BomItem?.BomItemId == id ? BomItem : null);
        public Task<EquipmentPartBomRecord?> GetEquipmentBomByIdempotencyKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult(BomItem?.LastIdempotencyKey == key ? BomItem : null);
        public Task<bool> HasOtherActivePrimarySupplierAsync(string partId, string supplierId, CancellationToken ct = default)
            => Task.FromResult(Suppliers.Any(x => x.PartId == partId && x.PartSupplierId != supplierId && x.IsPrimary && x.IsActive));

        public Task<bool> TryCreateStockPolicyAsync(SparePartStockPolicyRecord record, CancellationToken ct = default)
        {
            if (Policy is not null) return Task.FromResult(false);
            Policy = record;
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateStockPolicyAsync(SparePartStockPolicyRecord record, int expectedVersion, CancellationToken ct = default)
        {
            if (Policy?.Version != expectedVersion) return Task.FromResult(false);
            Policy = record;
            return Task.FromResult(true);
        }

        public Task<bool> TryCreateSupplierAsync(SparePartSupplierRecord record, CancellationToken ct = default)
        {
            if (Suppliers.Any(x => x.PartSupplierId == record.PartSupplierId)) return Task.FromResult(false);
            Suppliers.Add(record);
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateSupplierAsync(SparePartSupplierRecord record, int expectedVersion, CancellationToken ct = default)
        {
            var index = Suppliers.FindIndex(x => x.PartSupplierId == record.PartSupplierId && x.Version == expectedVersion);
            if (index < 0) return Task.FromResult(false);
            Suppliers[index] = record;
            return Task.FromResult(true);
        }

        public Task<bool> TryCreateEquipmentBomAsync(EquipmentPartBomRecord record, CancellationToken ct = default)
        {
            if (BomItem is not null) return Task.FromResult(false);
            BomItem = record;
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateEquipmentBomAsync(EquipmentPartBomRecord record, int expectedVersion, CancellationToken ct = default)
        {
            if (BomItem?.Version != expectedVersion) return Task.FromResult(false);
            BomItem = record;
            return Task.FromResult(true);
        }

        public Task<SparePartReplenishmentInput?> GetReplenishmentInputAsync(string partId, CancellationToken ct = default)
            => Task.FromResult(Policy is null || !Parts.Contains(partId)
                ? null
                : new SparePartReplenishmentInput(partId, CurrentStock, Policy, Suppliers.Where(x => x.PartId == partId).ToArray()));
    }
}
