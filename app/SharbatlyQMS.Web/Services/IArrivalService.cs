using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

public interface IArrivalService
{
    Task<IReadOnlyList<Arrival>> ListAsync(string? status, string? search, string? plant = null);
    Task<Arrival?> GetAsync(long arrivalId);
    Task<IReadOnlyList<ArrivalItem>> GetItemsAsync(long arrivalId);

    /// <summary>Plant code denormalized on the arrival header. Null when no row
    /// exists. Cheap single-column read used by the plant-scope gate.</summary>
    Task<string?> GetPlantAsync(long arrivalId);

    /// <summary>
    /// Find an existing arrival matching the given (container, BOL, PO) triple --
    /// case-insensitive. Used by the Create flow to block duplicate arrivals.
    /// The same container number can legitimately recur under a different BOL or
    /// PO, so all three must match. Returns the most recent one if any (any status).
    /// </summary>
    Task<Arrival?> FindByShipmentAsync(string containerNo, string bolNo, string? po);

    /// <summary>
    /// Bulk lookup of existing arrivals for a set of container numbers, so the
    /// SAP search grid can pre-flag (disable) shipments that already have an
    /// arrival. Caller matches the (container, BOL, PO) triple in memory.
    /// </summary>
    Task<IReadOnlyList<Arrival>> FindByContainersAsync(IReadOnlyCollection<string> containers);
    Task<ArrivalChecklist?> GetChecklistAsync(long arrivalId);
    Task<ShipmentSnapshot?> GetShipmentAsync(long arrivalId);

    Task<long> CreateFromSapAsync(IReadOnlyList<SapShipmentRow> rows, string createdBy);
    Task SaveChecklistAsync(ArrivalChecklist cl, string updatedBy);
    Task SaveShipmentAsync(ShipmentSnapshot ss, string updatedBy);
    Task<(bool ok, string? error)> CompleteAsync(long arrivalId, string user);

    /// <summary>Admin: take a Completed arrival back to Draft so the checklist
    /// can be edited again. Refuses if an active Quality Order exists.</summary>
    Task<(bool ok, string? error)> ReopenForEditAsync(long arrivalId, string user, string? reason);

    /// <summary>Admin: delete an arrival and all its child rows. Refuses if an
    /// active Quality Order exists.</summary>
    Task<(bool ok, string? error)> DeleteAsync(long arrivalId, string user);
}
