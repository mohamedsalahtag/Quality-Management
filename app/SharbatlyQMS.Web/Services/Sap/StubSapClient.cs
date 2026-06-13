namespace SharbatlyQMS.Web.Services.Sap;

/// <summary>
/// Hard-coded SAP rows used during early development before CDS Views are
/// published. Includes the container-ambiguity case from plan §5.3 (one
/// container number under two different BOLs) so the search UI can be
/// exercised without a live SAP connection. Swap this in DI for a real
/// OData-backed client when SAP is wired up.
/// </summary>
public class StubSapClient : ISapClient
{
    private static readonly List<SapShipmentRow> Rows = BuildSampleRows();

    public Task<SapHealth> GetHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(new SapHealth
        {
            IsReachable = true,
            Source      = "stub",
            Message     = "Using in-memory stub data. Replace StubSapClient in DI to point at real SAP OData."
        });

    public Task<IReadOnlyList<SapShipmentRow>> SearchAsync(SapSearchQuery q, CancellationToken ct = default)
    {
        IEnumerable<SapShipmentRow> rs = Rows;

        if (!string.IsNullOrWhiteSpace(q.ContainerNo))
            rs = rs.Where(r => r.ContainerNo.Contains(q.ContainerNo, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.BolNo))
            rs = rs.Where(r => r.BolNo.Contains(q.BolNo, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Ebeln))
            rs = rs.Where(r => r.Ebeln.Contains(q.Ebeln, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.MaterialNo))
            rs = rs.Where(r => r.MaterialNo.Contains(q.MaterialNo, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<IReadOnlyList<SapShipmentRow>>(rs.ToList());
    }

    public async Task<int> FetchSinceAsync(
        DateOnly sinceDocDate,
        Func<IReadOnlyList<SapShipmentRow>, CancellationToken, Task> onPage,
        CancellationToken ct = default)
    {
        // The stub data has no TOC_DATE; use ArrivalDate as a proxy for
        // "document date" so dev mode still produces realistic rows.
        var matched = Rows
            .Where(r => r.ArrivalDate.HasValue && r.ArrivalDate.Value >= sinceDocDate)
            .ToList();
        if (matched.Count > 0)
            await onPage(matched, ct);
        return matched.Count;
    }

    private static List<SapShipmentRow> BuildSampleRows() => new()
    {
        // -- Apple shipment from South Africa, container CMAU1234567, BOL MSCUAB1234 ---
        new SapShipmentRow
        {
            ContainerNo = "CMAU1234567", BolNo = "MSCUAB1234",
            Ebeln = "4500001001", Ebelp = "00010", Bukrs = "1000",
            VendorNo = "V100001", VendorName = "Cape Fruit Exporters", Carrier = "MSC",
            MaterialNo = "100001", MaterialDesc = "Apple Royal Gala 18kg carton",
            MaterialGroup = "FRSH-APP", MaterialGroupDesc = "Fresh Apples",
            MajorCategory = "Apples", Origin = "South Africa", Variety = "Royal Gala",
            MaterialClass = "Class 1", Brand = "Sharbatly", PackType = "Carton",
            NetWeight = 18m, MaterialSize = "100",
            Plant = "1100", StorageLocation = "0001", BatchNo = "B240501",
            Quantity = 1320m, Uom = "CAR",
            LoadingDate = new DateOnly(2026, 4, 12), SailingDate = new DateOnly(2026, 4, 14),
            ExaminationDate = new DateOnly(2026, 4, 28), ArrivalDate = new DateOnly(2026, 5, 1),
            UnloadingDate = new DateOnly(2026, 5, 2), ReceiveDate = new DateOnly(2026, 5, 5), TransitDays = 19,
            LoadingPort = "Cape Town", LoadingCountry = "South Africa",
            ArrivalPlace = "Jeddah Islamic Port", VesselName = "MSC Ariadne",
            VoyageNumber = "104W", SealNo = "SL778812"
        },
        new SapShipmentRow
        {
            ContainerNo = "CMAU1234567", BolNo = "MSCUAB1234",
            Ebeln = "4500001001", Ebelp = "00020", Bukrs = "1000",
            VendorNo = "V100001", VendorName = "Cape Fruit Exporters", Carrier = "MSC",
            MaterialNo = "100002", MaterialDesc = "Apple Granny Smith 18kg carton",
            MaterialGroup = "FRSH-APP", MaterialGroupDesc = "Fresh Apples",
            MajorCategory = "Apples", Origin = "South Africa", Variety = "Granny Smith",
            MaterialClass = "Class 1", Brand = "Sharbatly", PackType = "Carton",
            NetWeight = 18m, MaterialSize = "120",
            Plant = "1100", StorageLocation = "0001", BatchNo = "B240502",
            Quantity = 660m, Uom = "CAR",
            LoadingDate = new DateOnly(2026, 4, 12), SailingDate = new DateOnly(2026, 4, 14),
            ExaminationDate = new DateOnly(2026, 4, 28), ArrivalDate = new DateOnly(2026, 5, 1),
            UnloadingDate = new DateOnly(2026, 5, 2), ReceiveDate = new DateOnly(2026, 5, 5), TransitDays = 19,
            LoadingPort = "Cape Town", LoadingCountry = "South Africa",
            ArrivalPlace = "Jeddah Islamic Port", VesselName = "MSC Ariadne",
            VoyageNumber = "104W", SealNo = "SL778812"
        },
        // -- Same container number reused under a DIFFERENT BOL (older shipment).
        //    Forces BOL/PO disambiguation per plan §5.3.
        new SapShipmentRow
        {
            ContainerNo = "CMAU1234567", BolNo = "OOLU2233445",
            Ebeln = "4500000871", Ebelp = "00010", Bukrs = "1000",
            VendorNo = "V100007", VendorName = "Mediterranean Fruits Co.", Carrier = "OOCL",
            MaterialNo = "100015", MaterialDesc = "Apple Pink Lady 16kg carton",
            MaterialGroup = "FRSH-APP", MaterialGroupDesc = "Fresh Apples",
            MajorCategory = "Apples", Origin = "Italy", Variety = "Pink Lady",
            MaterialClass = "Class 1", Brand = "Sharbatly", PackType = "Carton",
            NetWeight = 16m, MaterialSize = "90",
            Plant = "1100", StorageLocation = "0001", BatchNo = "B240115",
            Quantity = 980m, Uom = "CAR",
            LoadingDate = new DateOnly(2026, 1, 18), SailingDate = new DateOnly(2026, 1, 20),
            ExaminationDate = new DateOnly(2026, 2, 4), ArrivalDate = new DateOnly(2026, 2, 6),
            UnloadingDate = new DateOnly(2026, 2, 7), ReceiveDate = new DateOnly(2026, 2, 10), TransitDays = 17,
            LoadingPort = "Genoa", LoadingCountry = "Italy",
            ArrivalPlace = "Jeddah Islamic Port", VesselName = "OOCL Beijing",
            VoyageNumber = "045E", SealNo = "SL334411"
        },
        // -- Citrus shipment from Egypt (different container / BOL / PO)
        new SapShipmentRow
        {
            ContainerNo = "MSCU8765432", BolNo = "MSCUEG7777",
            Ebeln = "4500001088", Ebelp = "00010", Bukrs = "1000",
            VendorNo = "V100022", VendorName = "Nile Citrus Group", Carrier = "MSC",
            MaterialNo = "200005", MaterialDesc = "Orange Valencia 15kg carton",
            MaterialGroup = "FRSH-CIT", MaterialGroupDesc = "Fresh Citrus",
            MajorCategory = "Oranges", Origin = "Egypt", Variety = "Valencia",
            MaterialClass = "Class 1", Brand = "Sharbatly", PackType = "Carton",
            NetWeight = 15m, MaterialSize = "72",
            Plant = "1100", StorageLocation = "0001", BatchNo = "B240429",
            Quantity = 1480m, Uom = "CAR",
            LoadingDate = new DateOnly(2026, 4, 26), SailingDate = new DateOnly(2026, 4, 27),
            ExaminationDate = new DateOnly(2026, 5, 2), ArrivalDate = new DateOnly(2026, 5, 3),
            UnloadingDate = new DateOnly(2026, 5, 4), ReceiveDate = new DateOnly(2026, 5, 7), TransitDays = 7,
            LoadingPort = "Damietta", LoadingCountry = "Egypt",
            ArrivalPlace = "Jeddah Islamic Port", VesselName = "MSC Levante",
            VoyageNumber = "017E", SealNo = "SL908877"
        }
    };
}
