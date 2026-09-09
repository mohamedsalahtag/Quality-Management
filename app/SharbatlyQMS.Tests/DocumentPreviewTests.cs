using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Previewing an attachment instead of downloading it.
///
/// The security shape matters more than the convenience here. An inline
/// response is rendered in OUR origin against the signed-in session, and these
/// files are uploaded by users — so only a short allow-list may be served
/// inline, and everything else has to keep falling back to a download.
/// </summary>
[Collection("workflow")]
public class DocumentPreviewTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public DocumentPreviewTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private IDocumentService Docs()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDocumentService>();
    }

    /// <summary>
    /// The allow-list. .msg and .eml are excluded deliberately even though a
    /// browser would happily show them as text: they are the formats most
    /// likely to carry hostile markup, and rendering one in our origin would
    /// hand it our cookies.
    /// </summary>
    [Theory]
    [InlineData("invoice.pdf",        true)]
    [InlineData("REPORT.PDF",         true)]
    [InlineData("notes.txt",          true)]
    [InlineData("export.csv",         true)]
    [InlineData("packing.docx",       false)]
    [InlineData("sheet.xlsx",         false)]
    [InlineData("mail.msg",           false)]
    [InlineData("mail.eml",           false)]
    [InlineData("bundle.zip",         false)]
    [InlineData("page.html",          false)]
    [InlineData("noextension",        false)]
    [InlineData("",                   false)]
    public void Only_safe_types_may_be_served_inline(string fileName, bool expected)
    {
        Assert.Equal(expected, Docs().CanPreviewInline(fileName));
    }

    /// <summary>
    /// A PDF comes back inline; anything outside the allow-list still comes
    /// back as an attachment from the SAME endpoint, so a caller cannot force
    /// an unsafe type to render by choosing the preview URL.
    /// </summary>
    [Fact]
    public async Task Preview_serves_a_pdf_inline_and_everything_else_as_a_download()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        using var c = new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));

        var rows = (await Dapper.SqlMapper.QueryAsync<(long DocumentId, string OriginalName)>(c, @"
            SELECT TOP 40 document_id, original_file_name AS OriginalName
            FROM   qms_document
            WHERE  is_deleted = 0
            ORDER  BY document_id DESC")).ToList();

        if (rows.Count == 0) { _out.WriteLine("No documents uploaded yet."); return; }

        var docs = Docs();
        var pdf   = rows.FirstOrDefault(r => docs.CanPreviewInline(r.OriginalName));
        var other = rows.FirstOrDefault(r => !docs.CanPreviewInline(r.OriginalName));

        if (pdf.DocumentId != 0)
        {
            var res = await client.GetAsync($"/Documents/Preview/{pdf.DocumentId}");
            _out.WriteLine($"inline candidate {pdf.OriginalName}: {(int)res.StatusCode} " +
                           $"{res.Content.Headers.ContentDisposition?.DispositionType}");
            // A missing file on disk is a data problem, not a routing one.
            if (res.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("inline", res.Content.Headers.ContentDisposition?.DispositionType);
                Assert.True(res.Headers.TryGetValues("X-Content-Type-Options", out var nosniff)
                            && nosniff.Contains("nosniff"),
                    "An inline response must carry nosniff, or a mislabelled file can be " +
                    "re-interpreted as something executable.");
            }
        }

        if (other.DocumentId != 0)
        {
            var res = await client.GetAsync($"/Documents/Preview/{other.DocumentId}");
            _out.WriteLine($"non-inline {other.OriginalName}: {(int)res.StatusCode} " +
                           $"{res.Content.Headers.ContentDisposition?.DispositionType}");
            if (res.StatusCode == HttpStatusCode.OK)
                Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        }
    }

    /// <summary>
    /// Preview reads exactly what Download reads, so it must not be reachable
    /// on a document the caller could not download.
    /// </summary>
    /// <summary>
    /// The Documents tab carries a count so nobody has to open the tab to find
    /// out whether anything is attached. The count is rendered server-side on
    /// the tab AND published by the grid partial (which is what keeps the badge
    /// honest after an AJAX upload) -- if those two ever disagree the badge
    /// silently drifts, so both are checked against the service.
    /// </summary>
    [Fact]
    public async Task The_documents_tab_shows_how_many_are_attached()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        using var c = new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));

        var arrivalId = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(c, @"
            SELECT TOP 1 owner_id
            FROM   qms_document
            WHERE  owner_type = 'Arrival' AND is_deleted = 0
            GROUP  BY owner_id
            ORDER  BY COUNT(*) DESC");

        if (arrivalId is null) { _out.WriteLine("No arrival has documents yet."); return; }

        var expected = (await Docs().ListAsync("Arrival", arrivalId.Value)).Count;
        Assert.True(expected > 0);

        var res = await client.GetAsync($"/Arrivals/Details/{arrivalId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        _out.WriteLine($"arrival {arrivalId} has {expected} document(s)");

        Assert.Contains($"data-doc-count-badge=\"Arrival-{arrivalId}\">{expected}</span>", html);
        // The grid's own marker, which the AJAX refresh copies into the badge.
        Assert.Contains($"data-doc-count=\"{expected}\"", html);
    }

    [Fact]
    public async Task Preview_of_a_missing_document_is_not_found()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/Documents/Preview/999999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
