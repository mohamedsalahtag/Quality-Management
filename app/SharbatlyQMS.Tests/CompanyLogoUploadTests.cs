using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Replacing the company logo.
///
/// It always failed on the server with an unhandled IOException: "the requested
/// operation cannot be performed on a file with a user-mapped section open".
/// The logo lives under wwwroot and is served by the static-file middleware,
/// which keeps a memory-mapped section open on a file it has served — Windows
/// then refuses to truncate or delete that file. Because the upload wrote to a
/// FIXED name, every attempt after the logo had been displayed once hit it.
///
/// Each upload now writes a name nobody has opened yet. These tests run against
/// the live installation, so they put the original logo setting back.
/// </summary>
[Collection("workflow")]
public class CompanyLogoUploadTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public CompanyLogoUploadTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>The smallest valid PNG: a single transparent pixel.</summary>
    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk" +
        "YPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent LogoForm(string token, string fileName)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(OnePixelPng());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "logo", fileName);
        return form;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin/Settings?activeTab=branding");
        var m = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on the branding tab.");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Upload twice in a row. The SECOND one is the regression: by then the
    /// first has been written and recorded, and under the old fixed-name scheme
    /// this is where the collision happened.
    /// </summary>
    [Fact]
    public async Task Uploading_a_logo_twice_in_a_row_succeeds()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var env      = scope.ServiceProvider.GetRequiredService<
                           Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var original = (await settings.GetBrandingConfigAsync()).LogoFilename;
        var written  = new List<string>();

        try
        {
            string? first = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var res = await client.PostAsync("/Admin/UploadCompanyLogo",
                                                 LogoForm(await TokenAsync(client), "logo.png"));
                _out.WriteLine($"attempt {attempt}: {(int)res.StatusCode}");
                Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

                var name = (await settings.GetBrandingConfigAsync()).LogoFilename;
                Assert.False(string.IsNullOrWhiteSpace(name), "No logo filename was recorded.");
                _out.WriteLine($"           stored as {name}");

                // A fresh name each time is what keeps the middleware's mapped
                // copy of the previous file out of the way -- and incidentally
                // stops a browser serving the old logo from cache.
                if (first is not null) Assert.NotEqual(first, name);
                first = name;
                written.Add(name);
            }
        }
        finally
        {
            await settings.SaveLogoFilenameAsync(original ?? "", null);

            // wwwroot is a DEPLOYED folder: anything left here is picked up by
            // the next publish and shipped to the server. A one-pixel test logo
            // reached production exactly once that way.
            foreach (var name in written)
            {
                var path = Path.Combine(env.WebRootPath, "branding", name);
                try { File.Delete(path); } catch { /* another run may hold it */ }
            }
        }
    }

    /// <summary>
    /// The rejections still reject, and none of them is a 500 either. A file
    /// type check that started throwing would be a worse bug than the one being
    /// fixed.
    /// </summary>
    [Theory]
    [InlineData("payload.exe")]
    [InlineData("script.js")]
    [InlineData("noextension")]
    public async Task An_unsupported_file_type_is_refused_politely(string fileName)
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var before = (await settings.GetBrandingConfigAsync()).LogoFilename;

        var res = await client.PostAsync("/Admin/UploadCompanyLogo",
                                         LogoForm(await TokenAsync(client), fileName));
        _out.WriteLine($"{fileName} -> {(int)res.StatusCode}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        // Refused means nothing changed.
        Assert.Equal(before, (await settings.GetBrandingConfigAsync()).LogoFilename);
    }
}
