using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The subject of the internal "quality order finished" notification.
///
/// It used to be built entirely in code. It is now overridable from
/// Parameters → Mail Template, with the automatic line kept as the default —
/// a template cannot drop an empty field, shorten a long supplier name, or
/// stop growing before a mail client truncates it, so the code version has to
/// remain the fallback rather than being replaced by a stored string.
/// </summary>
public class NotifySubjectTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public NotifySubjectTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public void A_template_is_filled_from_the_order()
    {
        var s = QcSubjectLine.Render(
            "QC {QC_NO} | {CONTAINER} | {SUPPLIER}",
            "QO-2026-000956", "Finished", "MNBU3969264", "SQ FLORA B.V.", "065-49966700");
        _out.WriteLine(s);
        Assert.Equal("QC 956 | MNBU3969264 | SQ FLORA B.V.", s);
    }

    /// <summary>
    /// Every token, so a subject cannot silently keep printing "{PLANT}" at a
    /// supplier because one placeholder was wired to nothing.
    /// </summary>
    [Fact]
    public void Every_offered_placeholder_is_actually_substituted()
    {
        foreach (var (token, _) in QcSubjectLine.Tokens)
        {
            var s = QcSubjectLine.Render($"x {token} y", "QO-2026-000956", "Finished",
                "MNBU3969264", "SQ FLORA B.V.", "065-49966700", "JD01", "4700011395");
            _out.WriteLine($"{token} -> {s}");
            Assert.DoesNotContain(token, s);
        }
    }

    /// <summary>
    /// A missing value leaves a gap AND a dangling separator, which reads as a
    /// rendering fault: "QC 956 ·  · BOL X". Both go.
    /// </summary>
    [Theory]
    [InlineData("QC {QC_NO} · {CONTAINER} · BOL {BOL}", "QC 956 · BOL 065-49966700")]
    [InlineData("{CONTAINER} · QC {QC_NO}",             "QC 956")]
    [InlineData("QC {QC_NO} · {CONTAINER}",             "QC 956")]
    public void An_empty_placeholder_takes_its_separator_with_it(string template, string expected)
    {
        var s = QcSubjectLine.Render(template, "QO-2026-000956", null, null, null, "065-49966700");
        _out.WriteLine($"'{template}' -> '{s}'");
        Assert.Equal(expected, s);
    }

    /// <summary>
    /// The subject is now typed by a person, and a newline reaching a mail
    /// header is a header-injection bug. It is stripped, as it always was for
    /// the SAP-sourced values.
    /// </summary>
    [Fact]
    public void Control_characters_never_reach_the_header()
    {
        var s = QcSubjectLine.Render("QC {QC_NO}\r\nBcc: attacker@example.com",
                                     "QO-2026-000956");
        _out.WriteLine(s);
        Assert.DoesNotContain("\r", s);
        Assert.DoesNotContain("\n", s);
    }

    [Fact]
    public void A_long_template_is_capped()
    {
        var s = QcSubjectLine.Render(new string('x', 400) + " {QC_NO}", "QO-2026-000956");
        Assert.True(s.Length <= QcSubjectLine.MaxLength, $"{s.Length} characters");
    }

    /// <summary>Blank means the automatic line, not a blank subject.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_selects_the_automatic_line(string? template)
    {
        Assert.Equal("", QcSubjectLine.Render(template!, "QO-2026-000956", "Finished"));
    }

    /// <summary>Stored as text in a key/value table, so it has to round-trip.</summary>
    [Fact]
    public async Task The_subject_survives_a_round_trip_and_can_be_cleared()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetQoMailTemplateAsync();
        try
        {
            var cfg = await settings.GetQoMailTemplateAsync();
            cfg.NotifySubject = "QC {QC_NO} finished at {PLANT}";
            await settings.SaveQoMailTemplateAsync(cfg, null);
            Assert.Equal("QC {QC_NO} finished at {PLANT}",
                         (await settings.GetQoMailTemplateAsync()).NotifySubject);

            // Clearing it must not disturb the supplier template beside it.
            cfg.NotifySubject = "";
            await settings.SaveQoMailTemplateAsync(cfg, null);
            var after = await settings.GetQoMailTemplateAsync();
            Assert.Equal("", after.NotifySubject);
            Assert.Equal(original.Subject, after.Subject);
            Assert.Equal(original.Body,    after.Body);
        }
        finally { await settings.SaveQoMailTemplateAsync(original, null); }
    }
}
