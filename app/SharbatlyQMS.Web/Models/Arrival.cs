namespace SharbatlyQMS.Web.Models;

public class Arrival
{
    public long      ArrivalId    { get; set; }
    public string    ArrivalNo    { get; set; } = "";
    public string    SourceSystem { get; set; } = "S4HANA";
    public string?   BolNo        { get; set; }
    public string?   ContainerNo  { get; set; }
    public string?   Ebeln        { get; set; }
    public string?   Bukrs        { get; set; }
    public string?   VendorNo     { get; set; }
    public string?   VendorName   { get; set; }
    public string    StatusCode   { get; set; } = "Draft";
    public DateTime  CreatedAt    { get; set; }
    public string    CreatedBy    { get; set; } = "";
    public DateTime? CompletedAt  { get; set; }
    public string?   CompletedBy  { get; set; }

    // Joined from qms_quality_order (latest active QO for this arrival).
    // Populated by list/detail queries so the UI can switch between
    // "Create QO" and "View QO".
    public long?    QualityOrderId      { get; set; }
    public string?  QualityOrderNo      { get; set; }
    public string?  QualityOrderStatus  { get; set; }
}

public class ArrivalItem
{
    public long     ArrivalItemId     { get; set; }
    public long     ArrivalId         { get; set; }
    public string   Ebeln             { get; set; } = "";
    public string   Ebelp             { get; set; } = "";
    public string   MaterialNo        { get; set; } = "";
    public string?  MaterialDesc      { get; set; }
    public string?  Plant             { get; set; }
    public string?  StorageLocation   { get; set; }
    public string?  BatchNo           { get; set; }
    public decimal? Quantity          { get; set; }
    public string?  Uom               { get; set; }
    public string?  MaterialGroup     { get; set; }
    public string?  MaterialGroupDesc { get; set; }
    public string?  MajorCategory     { get; set; }
}

public static class ArrivalStatus
{
    public const string Draft     = "Draft";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Arrival checklist (one per arrival). Captures the external/internal
/// inspection answers and physical readings the inspector gathers when
/// the container is opened.
/// </summary>
public class ArrivalChecklist
{
    public long      ChecklistId                { get; set; }
    public long      ArrivalId                  { get; set; }
    public string?   SealNo                     { get; set; }
    public string?   CarrierName                { get; set; }
    public bool?     SealIntact                 { get; set; }
    public bool?     SealMatchesDocuments       { get; set; }
    public bool?     ExternalDamageExists       { get; set; }
    public decimal?  SetTemperature             { get; set; }
    public decimal?  DisplayTemperature         { get; set; }
    public bool?     CargoSmellNormal           { get; set; }
    public bool?     VisualCargoAcceptable      { get; set; }
    public bool?     CargoShiftedCollapsedWater { get; set; }
    public decimal?  PulpTempFront              { get; set; }
    public decimal?  PulpTempMiddle             { get; set; }
    public decimal?  PulpTempBack               { get; set; }
    public bool?     DataLoggerLocated          { get; set; }
    public string?   DataLoggerSerial           { get; set; }
    public bool?     DataLoggerPhotoTaken       { get; set; }
    public bool?     LoggerHandedOver           { get; set; }
    public bool?     LoggerActiveDataAvailable  { get; set; }
    public decimal?  LoggerTemperature          { get; set; }
    public string?   Notes                      { get; set; }
    public DateTime? UpdatedAt                  { get; set; }
    public string?   UpdatedBy                  { get; set; }

    // ---- Photo-taken flags (V05) ----
    // Pure booleans -- the actual images live in qms_image_asset/qms_image_link.
    // These let the inspector tick off the checklist of expected photos.
    public bool? DisplayTempPhotoTaken         { get; set; }
    public bool? InternalInspectionPhotoTaken  { get; set; }
    public bool? PulpTempPhotoTaken            { get; set; }
    public bool? ContainerSealPhotoTaken       { get; set; }
    public bool? ExternalContainerPhotoTaken   { get; set; }
    public bool? ExternalDamagePhotoTaken      { get; set; }
    public bool? FirstViewCargoPhotoTaken      { get; set; }
    public bool? InternalDamagePhotoTaken      { get; set; }
}

/// <summary>
/// Shipment snapshot (one per arrival) -- vessel, voyage, dates, ports.
/// Editable on the arrival page so inspectors can correct or fill any
/// values that SAP didn't carry across, and locked once the arrival is
/// completed.
/// </summary>
public class ShipmentSnapshot
{
    public long       ShipmentSnapshotId  { get; set; }
    public long       ArrivalId           { get; set; }
    public string     InternalShipmentNo  { get; set; } = "";
    public DateTime?  LoadingDate         { get; set; }
    public DateTime?  SailingDate         { get; set; }
    public DateTime?  ExaminationDate     { get; set; }
    public DateTime?  ArrivalDate         { get; set; }
    public DateTime?  UnloadingDate       { get; set; }
    public DateTime?  InspectionDate      { get; set; }
    public short?     TransitDays         { get; set; }
    public short?     TimeBar             { get; set; }
    public string?    LoadingPort         { get; set; }
    public string?    LoadingCountry      { get; set; }
    public string?    ArrivalPlace        { get; set; }
    public string?    VesselName          { get; set; }
    public string?    VoyageNumber        { get; set; }
    public DateTime?  PullOutDate         { get; set; }
    public DateTime?  ReceiveDate         { get; set; }
    public bool?      TimeBarExceeded     { get; set; }
    public string?    InspectionPoint     { get; set; }
    public bool?      JointSurvey         { get; set; }
    public string     StatusCode          { get; set; } = "Draft";
}
