using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IArrivalService
{
    /// <summary>The Arrivals list, filtered by the quick bar + "More filters"
    /// panel. <paramref name="plantScope"/> forces the plant for plant-restricted
    /// operators and overrides whatever the panel posted.</summary>
    /// <summary>
    /// One server-side page of arrivals plus the total matching count, so the
    /// view can draw a pager. Paged rather than returning everything: see
    /// <see cref="ArrivalListFilter.Page"/>.
    /// </summary>
    Task<ArrivalPage> ListAsync(ArrivalListFilter filter, Models.PlantScope scope);

    /// <summary>Dropdown sources for the Arrivals filter panel (plants, storage
    /// locations, creators), drawn only from arrivals that exist.</summary>
    Task<ArrivalFilterOptions> GetArrivalFilterOptionsAsync(Models.PlantScope scope);

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

    /// <summary>
    /// Days in transit as the SAP cache currently holds them for this arrival's
    /// (container, BOL, PO) triplet — the same figure the container list shows.
    /// Null when the container is not in the cache at all, which is the case for
    /// arrivals created through /Arrivals/Search.
    ///
    /// The shipment snapshot carries its own copy, written once at arrival
    /// creation and deliberately excluded from every later UPDATE, so a SAP
    /// correction never reaches it: six arrivals were printing 0 transit days
    /// while the cache held 31. Anything that displays transit days to a user
    /// should read it from here.
    /// </summary>
    Task<short?> GetCachedTransitDaysAsync(long arrivalId);

    /// <summary>
    /// Refuses a container that arrived in bad condition: the arrival goes to
    /// Rejected and a quality order is raised already Closed, carrying a
    /// potential claim, so the damage still reaches the supplier without an
    /// inspection there is nothing to inspect for.
    ///
    /// Draft only. A Completed arrival can already have an active quality order,
    /// and rejecting around one would either strand it or destroy inspection
    /// data; the caller is told to reopen for edit first.
    ///
    /// <paramref name="reason"/> is mandatory and validated here, not only in
    /// the dialog -- a crafted POST bypasses a required attribute trivially, and
    /// this text is printed on the report the supplier receives.
    /// </summary>
    Task<(bool ok, string? error, long? qoId)> RejectAsync(long arrivalId, string reason, string user);

    /// <summary>
    /// Undoes a rejection: cancels the rejection order and returns the arrival
    /// to Draft. The only route back -- a Closed quality order cannot be
    /// cancelled or deleted by any other path in the application.
    /// </summary>
    Task<(bool ok, string? error)> CancelRejectionAsync(long arrivalId, string reason, string user);

    /// <summary>V36: the active custom fields applicable to this arrival
    /// (definition's material group is present among the arrival's line
    /// items), each carrying this arrival's stored value when one exists.</summary>
    Task<IReadOnlyList<ArrivalCustomField>> GetCustomFieldsAsync(long arrivalId);

    /// <summary>V36: upsert the custom-field values posted from the Details
    /// page. Only fields applicable to the arrival are accepted; blank
    /// values delete the stored row. Raw strings are parsed per the field's
    /// value kind.</summary>
    Task SaveCustomFieldValuesAsync(long arrivalId, IReadOnlyDictionary<int, string?> rawValues, string updatedBy);

    /// <summary>Promotes a pending SAP shipment to a Draft arrival. When
    /// <paramref name="overridePlant"/> is supplied (a QC Manager/Admin has
    /// reassigned the container), the arrival header plant — which every plant
    /// scope and the Quality Order inherit — is set to it instead of the SAP
    /// row's plant. Item lines keep their original SAP plant as a record.</summary>
    Task<long> CreateFromSapAsync(IReadOnlyList<SapShipmentRow> rows, string createdBy, string? overridePlant = null);
    Task SaveChecklistAsync(ArrivalChecklist cl, string updatedBy);
    Task SaveShipmentAsync(ShipmentSnapshot ss, string updatedBy);
    Task<(bool ok, string? error)> CompleteAsync(long arrivalId, string user);

    /// <summary>Every registry field's effective policy: the stored override
    /// when present, otherwise the code default. Keyed by field_key.</summary>
    Task<IReadOnlyDictionary<string, ArrivalFieldPolicy>> GetArrivalFieldPoliciesAsync();

    /// <summary>Upserts the per-field policy rows edited on the admin page.</summary>
    Task SaveArrivalFieldPoliciesAsync(IEnumerable<ArrivalFieldPolicy> policies, string user);

    /// <summary>Admin: take a Completed arrival back to Draft so the checklist
    /// can be edited again. Refuses if an active Quality Order exists.</summary>
    Task<(bool ok, string? error)> ReopenForEditAsync(long arrivalId, string user, string? reason);

    /// <summary>Admin: delete an arrival and all its child rows. Refuses if an
    /// active Quality Order exists.</summary>
    Task<(bool ok, string? error)> DeleteAsync(long arrivalId, string user);
}
