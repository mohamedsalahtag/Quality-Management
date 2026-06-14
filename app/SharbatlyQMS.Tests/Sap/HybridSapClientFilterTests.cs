using System.Reflection;
using SharbatlyQMS.Web.Services.Sap;
using Xunit;

namespace SharbatlyQMS.Tests.Sap;

/// <summary>
/// Smoke tests for HybridSapClient input hardening (H4). The IsSafeKey
/// helper is private, so we invoke it via reflection to keep the
/// production surface untouched. If the helper is renamed these tests
/// will break -- intentional, so the refactor remembers the test.
/// </summary>
public class HybridSapClientFilterTests
{
    private static bool IsSafeKey(string s)
    {
        var m = typeof(HybridSapClient).GetMethod(
            "IsSafeKey",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridSapClient.IsSafeKey not found");
        return (bool)m.Invoke(null, new object[] { s })!;
    }

    [Theory]
    [InlineData("CMAU1234567")]
    [InlineData("MSCUAB1234")]
    [InlineData("4500001001")]
    [InlineData("100001")]
    [InlineData("ABC-123_XYZ")]
    public void Safe_key_accepts_natural_sap_codes(string key) =>
        Assert.True(IsSafeKey(key));

    [Theory]
    [InlineData("")]                              // empty
    [InlineData("   ")]                           // whitespace
    [InlineData("CMAU' or '1'='1")]               // single quote
    [InlineData("CMAU; drop table")]              // delimiter
    [InlineData("CMAU eq 'X' and 1 eq 1")]        // OData injection
    [InlineData("CMAU\"X")]                       // double quote
    [InlineData("CMAU\n1234")]                    // newline
    public void Safe_key_rejects_unsafe_input(string key) =>
        Assert.False(IsSafeKey(key));

    [Fact]
    public void Safe_key_caps_length_at_35() =>
        Assert.False(IsSafeKey(new string('A', 36)));
}
