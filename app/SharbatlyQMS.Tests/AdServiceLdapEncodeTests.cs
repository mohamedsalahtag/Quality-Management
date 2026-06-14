using System.Reflection;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Smoke tests for the LDAP filter-encode helper (H1). RFC 4515 §3 lists
/// five characters that must be escaped before interpolation into an
/// LDAP filter string. The helper is private; reflection keeps the
/// production surface intact.
/// </summary>
public class AdServiceLdapEncodeTests
{
    private static string LdapEncode(string s)
    {
        var m = typeof(AdService).GetMethod(
            "LdapEncode",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AdService.LdapEncode not found");
        return (string)m.Invoke(null, new object[] { s })!;
    }

    [Theory]
    [InlineData("alice",           "alice")]
    [InlineData("",                "")]
    [InlineData("user*",           "user\\2a")]
    [InlineData("(uid=*)",         "\\28uid=\\2a\\29")]
    [InlineData("a\\b",            "a\\5cb")]
    [InlineData(")(uid=admin",     "\\29\\28uid=admin")]
    public void Encodes_per_rfc_4515(string input, string expected) =>
        Assert.Equal(expected, LdapEncode(input));
}
