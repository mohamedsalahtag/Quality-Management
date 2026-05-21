using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

public interface IArrivalService
{
    Task<IReadOnlyList<Arrival>> ListAsync(string? status, string? search);
    Task<Arrival?> GetAsync(long arrivalId);
    Task<IReadOnlyList<ArrivalItem>> GetItemsAsync(long arrivalId);

    /// <summary>
    /// Find an existing arrival matching the given (container, BOL) pair --
    /// case-insensitive. Used by the Create flow to block duplicate arrivals.
    /// Returns the most recent one if any (any status).
    /// </summary>
    Task<Arrival?> FindByContainerAndBolAsync(string containerNo, string bolNo);
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
