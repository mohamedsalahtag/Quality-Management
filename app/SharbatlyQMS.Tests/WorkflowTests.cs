using System.Data;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Drives one whole inspection through the real services against the real
/// Sharbatly_MIS database: arrival -> quality order -> sample -> readings ->
/// defects -> photo upload -> PDF report, then removes everything it created.
///
/// This is the check that matters before go-live, because it exercises the
/// parts of the migration that a page-load cannot: writes into the [qms]
/// schema through the dbo synonyms, the defect and reading catalogues carried
/// over from the old database, material lookups resolving against dbo.Mara,
/// and image storage at its new location outside the publish output.
///
/// It writes to the live database on purpose and cleans up after itself. Audit
/// rows are append-only by design and are left behind.
/// </summary>
[Collection("workflow")]
public class WorkflowTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public WorkflowTests(QmsAppFactory factory) => _factory = factory;

    private const string TestUser = "qms.migration.test";

    [Fact]
    public async Task Full_inspection_workflow_succeeds_end_to_end()
    {
        using var scope = _factory.Services.CreateScope();
        var sp       = scope.ServiceProvider;
        var arrivals = sp.GetRequiredService<IArrivalService>();
        var qos      = sp.GetRequiredService<IQualityOrderService>();
        var images   = sp.GetRequiredService<IImageService>();
        var cs       = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;

        // ---- fixtures straight out of the migrated catalogues -----------------
        await using var db = new SqlConnection(cs);
        // NB: "Desc" is a reserved word, so the alias is spelled out.
        var material = await db.QuerySingleAsync<(string No, string Descr, string Grp)>(@"
            SELECT TOP 1 material_no AS No, material_desc AS Descr, material_group AS Grp
            FROM   qms_sap_material_cache
            WHERE  material_group = 'APPLE' AND material_desc NOT LIKE '%DELETED%'
            ORDER BY material_no");
        var defectId = await db.ExecuteScalarAsync<int>(
            "SELECT TOP 1 defect_id FROM qms_defect_catalog WHERE material_group='APPLE' ORDER BY defect_id");
        var readingCode = await db.ExecuteScalarAsync<string>(
            "SELECT TOP 1 reading_type_code FROM qms_reading_type WHERE material_group='APPLE' ORDER BY reading_type_id");

        Assert.False(string.IsNullOrWhiteSpace(material.No), "no APPLE material found in dbo.Mara via the catalogue view");
        Assert.True(defectId > 0, "no APPLE defect found in the migrated defect catalogue");

        long arrivalId = 0, qoId = 0, sampleId = 0;
        // 10 chars: qms_arrival.ebeln is varchar(10), so the stamp has to fit
        // a PO number exactly.
        var stamp = DateTime.UtcNow.ToString("MMddHHmmss");

        try
        {
            // ---- 1. arrival from a SAP shipment row --------------------------
            var row = new SapShipmentRow
            {
                ContainerNo       = $"TST{stamp}",
                BolNo             = $"BOL{stamp}",
                Ebeln             = stamp,
                Ebelp             = "10",
                PoType            = "NB",
                Bukrs             = "1000",
                VendorNo          = "10000",
                VendorName        = "MIGRATION TEST VENDOR",
                Carrier           = "TEST CARRIER",
                MaterialNo        = material.No,
                MaterialDesc      = material.Descr,
                MaterialGroup     = material.Grp,
                MaterialGroupDesc = material.Grp,
                MajorCategory     = "TEST",
                Origin            = "Test Origin",
                Variety           = "Test Variety",
                MaterialClass     = "Cat1",
                Brand             = "TESTBRAND",
                NetWeight         = 18.0m,
                MaterialSize      = "100",
            };
            arrivalId = await arrivals.CreateFromSapAsync(new[] { row }, TestUser);
            Assert.True(arrivalId > 0, "arrival was not created");

            var arrival = await arrivals.GetAsync(arrivalId);
            Assert.NotNull(arrival);
            var items = await arrivals.GetItemsAsync(arrivalId);
            Assert.NotEmpty(items);

            // ---- 2. container checklist + complete ---------------------------
            // A quality order can only be raised against a Completed arrival,
            // so this is part of the real path, not test scaffolding.
            await arrivals.SaveChecklistAsync(new ArrivalChecklist
            {
                ArrivalId            = arrivalId,
                SealNo               = "SEAL-TEST",
                CarrierName          = "TEST CARRIER",
                SealIntact           = true,
                SealMatchesDocuments = true,
                ExternalDamageExists = false,
                SetTemperature       = 2.0m,
                DisplayTemperature   = 2.4m,
                CargoSmellNormal     = true,
                PulpTempFront        = 2.1m,
                PulpTempMiddle       = 2.2m,
                PulpTempBack         = 2.3m,
                Notes                = "migration verification",
            }, TestUser);

            var checklist = await arrivals.GetChecklistAsync(arrivalId);
            Assert.NotNull(checklist);
            Assert.Equal("SEAL-TEST", checklist!.SealNo);

            var (compOk, compErr) = await arrivals.CompleteAsync(arrivalId, TestUser);
            Assert.True(compOk, $"could not complete the arrival: {compErr}");

            // ---- 3. quality order -------------------------------------------
            qoId = await qos.CreateForArrivalAsync(arrivalId, TestUser);
            Assert.True(qoId > 0, "quality order was not created");
            var (openOk, openErr) = await qos.OpenAsync(qoId, TestUser);
            Assert.True(openOk, $"could not open the quality order: {openErr}");

            var materials = await qos.GetMaterialsAsync(qoId);
            Assert.NotEmpty(materials);
            var qoMaterialId = materials[0].QoMaterialId;

            // ---- 3. sample ---------------------------------------------------
            sampleId = await qos.CreateSampleAsync(new Sample
            {
                QualityOrderId = qoId,
                QoMaterialId   = qoMaterialId,
                SampleNo       = 1,
                CartonCount    = 1,
                SampleScope    = "OneCarton",
                SampleSize     = 10,
                Grower         = "TEST GROWER",
                PalletNo       = "P1",
                CreatedBy      = TestUser,
            });
            Assert.True(sampleId > 0, "sample was not created");

            // ---- 4. readings (catalogue carried over from the old DB) --------
            if (!string.IsNullOrWhiteSpace(readingCode))
            {
                await qos.SaveReadingsAsync(sampleId, new[]
                {
                    new SampleReading
                    {
                        SampleId        = sampleId,
                        ReadingTypeCode = readingCode!,
                        NumericValue    = 12.5m,
                        ReadingSequence = 1,
                    }
                }, TestUser);
                var readings = await qos.GetReadingsAsync(sampleId);
                Assert.NotEmpty(readings);
            }

            // ---- 5. defects ---------------------------------------------------
            await qos.SaveDefectsAsync(sampleId, new[]
            {
                new SampleDefect
                {
                    SampleId    = sampleId,
                    DefectId    = defectId,
                    DefectValue = 2m,
                    Comment     = "migration verification",
                }
            }, TestUser);
            var defects = await qos.GetDefectsAsync(sampleId);
            Assert.NotEmpty(defects);

            // ---- 6. photo upload (lands outside the publish output) ----------
            var uploaded = await images.UploadAsync("Sample", sampleId, "General",
                new List<IFormFile> { MakeJpeg("test.jpg") }, TestUser);
            Assert.True(uploaded > 0, "image upload reported no files stored");
            var stored = await images.ListAsync("Sample", sampleId);
            Assert.NotEmpty(stored);

            // the bytes must be readable back over HTTP from the new location
            var web = _factory.CreateClient();
            var imgRes = await web.GetAsync(stored[0].StorageUrl);
            Assert.True(imgRes.IsSuccessStatusCode,
                $"uploaded photo not served from {stored[0].StorageUrl} ({(int)imgRes.StatusCode})");

            // ---- 7. submit + PDF report --------------------------------------
            var (subOk, subErr) = await qos.SubmitAsync(qoId, TestUser);
            Assert.True(subOk, $"could not submit the quality order: {subErr}");

            var pdf = await web.GetAsync($"/Reports/QualityOrderPdf?id={qoId}");
            Assert.True(pdf.IsSuccessStatusCode, $"PDF generation failed ({(int)pdf.StatusCode})");
            var bytes = await pdf.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000, $"PDF suspiciously small ({bytes.Length} bytes)");
            Assert.Equal(0x25, bytes[0]); // '%' - a real PDF starts %PDF
        }
        finally
        {
            // ---- cleanup, through the app's own delete paths ------------------
            // Images first: the app deliberately only detaches image LINKS when
            // an arrival is deleted, leaving the asset row and the file on disk
            // for a retention job, so the test removes its own.
            //
            // Deletion is by exact stored path. Removing the Sample/{id} folder
            // instead would be destructive: sample ids restart from 1 in this
            // database and collide with folders holding real production photos.
            if (sampleId > 0)
            {
                var env  = sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
                var cfg  = sp.GetRequiredService<IConfiguration>();
                var root = UploadStorage.Root(env, cfg);

                var urls = (await db.QueryAsync<string>(@"
                    SELECT storage_url FROM qms_image_asset WHERE uploaded_by = @TestUser
                    UNION ALL
                    SELECT thumbnail_url FROM qms_image_asset
                    WHERE uploaded_by = @TestUser AND thumbnail_url IS NOT NULL",
                    new { TestUser })).ToList();

                foreach (var url in urls)
                {
                    var rel  = url.TrimStart('/');
                    if (!rel.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Path.Combine(root, rel["uploads/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path)) File.Delete(path);
                }

                await db.ExecuteAsync(@"
                    DELETE FROM qms_image_link
                    WHERE image_id IN (SELECT image_id FROM qms_image_asset WHERE uploaded_by = @TestUser);
                    DELETE FROM qms_image_asset WHERE uploaded_by = @TestUser;",
                    new { TestUser });
            }

            if (qoId > 0) await qos.CancelAsync(qoId, TestUser, "migration verification cleanup");
            if (arrivalId > 0)
            {
                var (delOk, delErr) = await arrivals.DeleteAsync(arrivalId, TestUser);
                Assert.True(delOk, $"cleanup failed, test data left behind: {delErr}");

                var left = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM qms_arrival WHERE arrival_id=@arrivalId", new { arrivalId });
                Assert.Equal(0, left);
            }
        }
    }

    /// <summary>A small real JPEG; ImageService decodes and thumbnails it.</summary>
    private static IFormFile MakeJpeg(string name)
    {
        using var img = new Image<Rgba32>(120, 90);
        var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        ms.Position = 0;
        return new FormFile(ms, 0, ms.Length, "files", name)
        {
            Headers     = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };
    }
}
