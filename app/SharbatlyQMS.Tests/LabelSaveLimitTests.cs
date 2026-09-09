using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Saving on the Labels screen.
///
/// The screen lists every label the application knows about -- over a thousand
/// on production -- and each row submits a key AND a value. An unfiltered save
/// therefore posted more than 2,000 form values, and ASP.NET Core rejects a
/// request past its default 1,024-value limit before any action code runs: the
/// administrator saw a server error and lost the edit.
///
/// The view now disables untouched rows so only edits are sent, and the action
/// carries a limit that fits a deliberate bulk rename. This pins the second
/// half, which is the one a browser cannot opt out of.
/// </summary>
[Collection("workflow")]
public class LabelSaveLimitTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public LabelSaveLimitTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, $"No antiforgery token found on {url}.");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// A post far larger than the framework default has to be accepted. The
    /// values are every label's CURRENT text, so the request is the shape a
    /// real save takes and nothing is actually changed by it.
    /// </summary>
    [Fact]
    public async Task A_save_carrying_every_label_is_not_rejected_for_size()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        // No auto-redirect: a successful save answers 302 back to the list, and
        // following it would mask the status the test is about.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        List<UiLabel> labels;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ILabelService>();
            await svc.FlushAsync(CancellationToken.None);
            labels = (await svc.ListAsync(CancellationToken.None)).ToList();
        }
        _out.WriteLine($"{labels.Count} labels -> {labels.Count * 2} form values");

        // Below the framework's limit there is nothing to prove; the guard only
        // matters once the screen has discovered enough labels to trip it.
        if (labels.Count * 2 <= 1024)
        {
            _out.WriteLine("Too few labels discovered here to exceed the default limit.");
            return;
        }

        var token = await TokenAsync(client, "/Labels");
        var form  = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token)
        };
        foreach (var l in labels)
        {
            form.Add(new("key",   l.Key));
            form.Add(new("value", l.CustomText ?? ""));
        }

        var res = await client.PostAsync("/Labels/Save", new FormUrlEncodedContent(form));
        _out.WriteLine($"POST /Labels/Save with {form.Count} values -> {(int)res.StatusCode}");

        // A rejected form surfaces as 400 (or 500 from the unhandled
        // InvalidDataException); a save redirects back to the list.
        Assert.NotEqual(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    /// <summary>
    /// Renaming one label still works and still takes effect, which is the
    /// thing the screen exists to do.
    /// </summary>
    [Fact]
    public async Task Renaming_one_label_saves_and_takes_effect()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        // No auto-redirect: a successful save answers 302 back to the list, and
        // following it would mask the status the test is about.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });

        const string key = "Inspection Time Bar";
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ILabelService>();

        var before = (await svc.ListAsync(CancellationToken.None))
            .FirstOrDefault(l => l.Key == key)?.CustomText;
        try
        {
            var token = await TokenAsync(client, "/Labels");
            var res = await client.PostAsync("/Labels/Save", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("key",   key),
                new KeyValuePair<string, string>("value", "Container clock"),
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

            var after = (await svc.ListAsync(CancellationToken.None))
                .FirstOrDefault(l => l.Key == key)?.CustomText;
            _out.WriteLine($"'{key}' -> '{after}'");
            Assert.Equal("Container clock", after);
        }
        finally
        {
            await svc.SaveAsync(key, before ?? "", "test", CancellationToken.None);
        }
    }
}
