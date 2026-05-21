namespace SharbatlyQMS.Web.Services.Sap;

/// <summary>
/// Read-only consumer for SAP S/4HANA CDS Views exposed via OData.
/// All upstream PO / BOL / container / shipment / material data flows
/// through this interface; QMS controllers MUST NOT call OData directly.
///
/// The current implementation (<see cref="StubSapClient"/>) returns
/// hard-coded sample rows so QMS development can proceed before the
/// SAP CDS endpoints are live. Swap to a real OData-backed implementation
/// in DI when the views are published.
/// </summary>
public interface ISapClient
{
    /// <summary>
    /// Search by container number, BOL, or PO. Empty filters are ignored.
    /// If multiple SAP rows share the same container across BOLs, the
    /// caller must force the user to select one (plan §5.3).
    /// </summary>
    Task<IReadOnlyList<SapShipmentRow>> SearchAsync(SapSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Health/availability probe -- maps to /api/sap/health in plan §8.1.
    /// </summary>
    Task<SapHealth> GetHealthAsync(CancellationToken ct = default);
}

public class SapSearchQuery
{
    public string? ContainerNo  { get; set; }
    public string? BolNo        { get; set; }
    public string? Ebeln        { get; set; }   // PO
    public string? MaterialNo   { get; set; }
}

public class SapHealth
{
    public bool   IsReachable { get; set; }
    public string Source      { get; set; } = "";   // e.g. "stub" | "odata"
    public string Message     { get; set; } = "";
}

/// <summary>
/// Flattened row covering everything QMS shows on the SAP search grid and
/// snapshots into <c>qms_arrival_item</c> + <c>qms_shipment_snapshot</c>.
/// One row = one PO line for one container in one BOL.
/// </summary>
public class SapShipmentRow
{
    // Container / BOL / PO identity
    public string  ContainerNo       { get; set; } = "";
    public string  BolNo             { get; set; } = "";
    public string  Ebeln             { get; set; } = "";   // PO header
    public string  Ebelp             { get; set; } = "";   // PO line
    public string  Bukrs             { get; set; } = "";   // company code

    // Vendor
    public string  VendorNo          { get; set; } = "";
    public string  VendorName        { get; set; } = "";

    // Material
    public string  MaterialNo        { get; set; } = "";
    public string  MaterialDesc      { get; set; } = "";
    public string  MaterialGroup     { get; set; } = "";
    public string  MaterialGroupDesc { get; set; } = "";
    public string  MajorCategory     { get; set; } = "";
    public string  Origin            { get; set; } = "";
    public string  Variety           { get; set; } = "";
    public string  MaterialClass     { get; set; } = "";
    public string  Brand             { get; set; } = "";
    public string  PackType          { get; set; } = "";
    public decimal NetWeight         { get; set; }
    public string  MaterialSize      { get; set; } = "";

    // Receiving
    public string  Plant             { get; set; } = "";
    public string  StorageLocation   { get; set; } = "";
    public string? BatchNo           { get; set; }
    public decimal Quantity          { get; set; }
    public string  Uom               { get; set; } = "";

    // Shipment
    public DateOnly? LoadingDate     { get; set; }
    public DateOnly? SailingDate     { get; set; }
    public DateOnly? ExaminationDate { get; set; }
    public DateOnly? ArrivalDate     { get; set; }
    public DateOnly? UnloadingDate   { get; set; }
    public short?    TransitDays     { get; set; }
    public string    LoadingPort     { get; set; } = "";
    public string    LoadingCountry  { get; set; } = "";
    public string    ArrivalPlace    { get; set; } = "";
    public string    VesselName      { get; set; } = "";
    public string    VoyageNumber    { get; set; } = "";
    public string    SealNo          { get; set; } = "";

    /// <summary>
    /// Stable selection key used when the user picks a row from the grid.
    /// Encoded so it can travel safely on a URL.
    /// </summary>
    public string SelectionKey =>
        $"{ContainerNo}|{BolNo}|{Ebeln}|{Ebelp}|{MaterialNo}|{Plant}|{StorageLocation}|{BatchNo}";
}
