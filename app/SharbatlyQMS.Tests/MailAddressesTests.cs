using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Address-list parsing behind the supplier report mail. Merge is what folds
/// the standing CC (Parameters → Mail Template) into whatever the sender typed,
/// so a bug here either drops the standing CC from every outgoing report or
/// copies somebody twice on all of them — silently, in both directions.
/// </summary>
public class MailAddressesTests
{
    [Theory]
    [InlineData("a@x.com,b@x.com")]
    [InlineData("a@x.com; b@x.com")]
    [InlineData("a@x.com\nb@x.com")]
    [InlineData("  a@x.com ,, b@x.com  ")]
    public void Split_handles_every_separator_and_trims(string input) =>
        Assert.Equal(new[] { "a@x.com", "b@x.com" }, MailAddresses.Split(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",;")]
    public void Split_of_nothing_is_empty(string? input) =>
        Assert.Empty(MailAddresses.Split(input));

    [Fact]
    public void Merge_appends_the_standing_cc()
    {
        var merged = MailAddresses.Merge("typed@x.com", "always@y.com, boss@y.com");
        Assert.Equal(new[] { "typed@x.com", "always@y.com", "boss@y.com" }, merged);
    }

    [Fact]
    public void Merge_keeps_the_standing_cc_when_the_sender_typed_none()
    {
        Assert.Equal(new[] { "always@y.com" }, MailAddresses.Merge("", "always@y.com"));
        Assert.Equal(new[] { "always@y.com" }, MailAddresses.Merge(null, "always@y.com"));
    }

    [Fact]
    public void Merge_does_not_copy_the_same_address_twice()
    {
        // Case difference must not defeat the dedupe -- typing an address that
        // is already on the standing CC is the obvious way to hit this.
        var merged = MailAddresses.Merge("Always@Y.com, other@x.com", "always@y.com");
        Assert.Equal(new[] { "Always@Y.com", "other@x.com" }, merged);
    }

    [Fact]
    public void Merge_preserves_first_seen_order()
    {
        var merged = MailAddresses.Merge("c@x.com, a@x.com", "b@x.com, a@x.com");
        Assert.Equal(new[] { "c@x.com", "a@x.com", "b@x.com" }, merged);
    }
}
