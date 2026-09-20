using NexaFramework.Service;
using NexaFramework.Service.Inventory;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>IVT-owned shared assets. Actor identity comes from the authenticated MES principal;
/// scope, current grants, worker membership and writes are checked within one database transaction.</summary>
public interface IEquipmentSharingBridge : INexaModuleBridge
{
    Task<InventoryScopeBinding> GetScopeBindingAsync(string administratorId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default);
    Task<InventoryScopeBinding> BindScopeAsync(string administratorId, Guid tenantId, Guid organizationId,
        string plantId, Guid? expectedVersion, bool active, CancellationToken ct = default);
    /// <summary>Creates once per caller-chosen operation ID. Exact retries by the same actor return the current asset.</summary>
    Task<SharedEquipment> CreateEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, string code, string name, int capacity, bool requiresApproval, CancellationToken ct = default);
    Task<SharedEquipment> UpdateEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        string code, string name, int capacity, bool requiresApproval, Guid id,
        Guid expectedVersion, CancellationToken ct = default);
    Task<SharedEquipment> GetEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    /// <summary>Lists scoped assets and the matching total under the caller's current read grant.</summary>
    Task<BusinessPage<SharedEquipment>> ListEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default);
    /// <summary>Finds equipment by its current scope-unique code. Creation recovery uses the original operation ID.</summary>
    Task<SharedEquipment> GetEquipmentByCodeAsync(string userId, Guid tenantId, Guid organizationId,
        string code, CancellationToken ct = default);
    Task<SharedEquipment> SetEquipmentActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default);
    Task<EquipmentBooking> RequestBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid bookingId, Guid equipmentId, string workerId, DateTimeOffset start, DateTimeOffset end,
        int quantity, CancellationToken ct = default);
    Task<EquipmentBooking> GetBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    /// <summary>Lists scoped booking history with optional equipment/state filters and the matching total.
    /// Historical bookings remain readable when the linked worker is inactive.</summary>
    Task<BusinessPage<EquipmentBooking>> ListBookingsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? equipmentId = null, EquipmentBookingState? state = null, int offset = 0, int limit = 50,
        CancellationToken ct = default);
    Task<EquipmentBooking> DecideBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool approve, CancellationToken ct = default);
    Task<EquipmentBooking> CancelBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<EquipmentBooking> CheckOutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<EquipmentBooking> ReturnAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
}

/// <summary>Explicit product binding to an existing MDM plant; this is not an external Gauzy organization registry.</summary>
public sealed record InventoryScopeBinding(Guid TenantId, Guid OrganizationId, string PlantId, Guid Version, bool Active);
