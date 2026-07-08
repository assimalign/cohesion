using System.Net;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 7239 &#167; 6 compliance tests for <see cref="HttpForwardedNode"/>: nodename/node-port
/// parsing across IPv4, bracketed and bare IPv6, <c>unknown</c>, and obfuscated forms, plus the
/// deterministic rejection of malformed input.
/// </summary>
public class HttpForwardedNodeTests
{
    // ============================================================================
    // IPv4
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse a bare IPv4 literal")]
    public void TryParse_IPv4_ShouldExposeAddress()
    {
        bool ok = HttpForwardedNode.TryParse("192.0.2.60", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Name.ShouldBe("192.0.2.60");
        node.Address.ShouldBe(IPAddress.Parse("192.0.2.60"));
        node.Port.ShouldBeNull();
        node.PortNumber.ShouldBeNull();
        node.IsUnknown.ShouldBeFalse();
        node.IsObfuscatedName.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse an IPv4 literal with a numeric port")]
    public void TryParse_IPv4WithPort_ShouldExposeAddressAndPort()
    {
        bool ok = HttpForwardedNode.TryParse("192.0.2.60:4711", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Address.ShouldBe(IPAddress.Parse("192.0.2.60"));
        node.Port.ShouldBe("4711");
        node.PortNumber.ShouldBe(4711);
    }

    // ============================================================================
    // IPv6 (bracketed = RFC 7239, bare = X-Forwarded-For de-facto)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse a bracketed IPv6 literal")]
    public void TryParse_BracketedIPv6_ShouldStripBracketsForAddress()
    {
        bool ok = HttpForwardedNode.TryParse("[2001:db8:cafe::17]", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Name.ShouldBe("[2001:db8:cafe::17]");
        node.Address.ShouldBe(IPAddress.Parse("2001:db8:cafe::17"));
        node.Port.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse a bracketed IPv6 literal with a port")]
    public void TryParse_BracketedIPv6WithPort_ShouldExposeAddressAndPort()
    {
        bool ok = HttpForwardedNode.TryParse("[2001:db8:cafe::17]:4711", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Address.ShouldBe(IPAddress.Parse("2001:db8:cafe::17"));
        node.PortNumber.ShouldBe(4711);
        node.ToString().ShouldBe("[2001:db8:cafe::17]:4711");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should accept a bare IPv6 literal as the whole nodename")]
    [InlineData("2001:db8::1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void TryParse_BareIPv6_ShouldExposeAddressWithoutPort(string raw)
    {
        bool ok = HttpForwardedNode.TryParse(raw, out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Address.ShouldBe(IPAddress.Parse(raw));
        node.Port.ShouldBeNull();
    }

    // ============================================================================
    // unknown + obfuscated identifiers
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse the unknown sentinel case-insensitively")]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("UNKNOWN")]
    public void TryParse_Unknown_ShouldFlagUnknown(string raw)
    {
        bool ok = HttpForwardedNode.TryParse(raw, out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.IsUnknown.ShouldBeTrue();
        node.Address.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse an obfuscated node identifier")]
    public void TryParse_ObfuscatedName_ShouldFlagObfuscated()
    {
        bool ok = HttpForwardedNode.TryParse("_gazonk", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.IsObfuscatedName.ShouldBeTrue();
        node.Address.ShouldBeNull();
        node.IsUnknown.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse an obfuscated node and obfuscated port")]
    public void TryParse_ObfuscatedNameAndPort_ShouldFlagBoth()
    {
        bool ok = HttpForwardedNode.TryParse("_gazonk:_secret", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.IsObfuscatedName.ShouldBeTrue();
        node.HasObfuscatedPort.ShouldBeTrue();
        node.PortNumber.ShouldBeNull();
        node.Port.ShouldBe("_secret");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should parse an IPv4 literal with an obfuscated port")]
    public void TryParse_IPv4WithObfuscatedPort_ShouldExposeObfuscatedPort()
    {
        bool ok = HttpForwardedNode.TryParse("192.0.2.60:_obf", out HttpForwardedNode node);

        ok.ShouldBeTrue();
        node.Address.ShouldBe(IPAddress.Parse("192.0.2.60"));
        node.HasObfuscatedPort.ShouldBeTrue();
    }

    // ============================================================================
    // FromIPAddress + round-trip
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should emit canonical brackets from an IPv6 address")]
    public void FromIPAddress_IPv6_ShouldBracket()
    {
        HttpForwardedNode node = HttpForwardedNode.FromIPAddress(IPAddress.Parse("2001:db8::1"), 8080);

        node.ToString().ShouldBe("[2001:db8::1]:8080");
        node.Address.ShouldBe(IPAddress.Parse("2001:db8::1"));
        node.PortNumber.ShouldBe(8080);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should emit an IPv4 address without brackets")]
    public void FromIPAddress_IPv4_ShouldNotBracket()
    {
        HttpForwardedNode node = HttpForwardedNode.FromIPAddress(IPAddress.Parse("203.0.113.7"));

        node.ToString().ShouldBe("203.0.113.7");
        node.Port.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should round-trip through ToString")]
    [InlineData("192.0.2.60")]
    [InlineData("192.0.2.60:4711")]
    [InlineData("[2001:db8:cafe::17]")]
    [InlineData("[2001:db8:cafe::17]:4711")]
    [InlineData("unknown")]
    [InlineData("_gazonk")]
    [InlineData("_gazonk:_secret")]
    public void TryParse_ThenToString_ShouldRoundTrip(string raw)
    {
        HttpForwardedNode.TryParse(raw, out HttpForwardedNode node).ShouldBeTrue();

        node.ToString().ShouldBe(raw);
    }

    // ============================================================================
    // Equality
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should compare equal for the same node text")]
    public void Equals_SameText_ShouldBeEqual()
    {
        HttpForwardedNode.TryParse("192.0.2.60:4711", out HttpForwardedNode left).ShouldBeTrue();
        HttpForwardedNode.TryParse("192.0.2.60:4711", out HttpForwardedNode right).ShouldBeTrue();

        left.ShouldBe(right);
        (left == right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should not compare equal for different ports")]
    public void Equals_DifferentPort_ShouldNotBeEqual()
    {
        HttpForwardedNode.TryParse("192.0.2.60:1", out HttpForwardedNode left).ShouldBeTrue();
        HttpForwardedNode.TryParse("192.0.2.60:2", out HttpForwardedNode right).ShouldBeTrue();

        (left != right).ShouldBeTrue();
    }

    // ============================================================================
    // Malformed — deterministic rejection, never throws
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should reject malformed input without throwing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[2001:db8::1")]      // unterminated bracket
    [InlineData("[]")]                 // empty brackets
    [InlineData("[notipv6]")]
    [InlineData("[1.2.3.4]")]          // IPv4 is not valid inside brackets
    [InlineData("[2001:db8::1]x")]     // junk after bracket
    [InlineData("[2001:db8::1]:")]     // empty port after bracket
    [InlineData("[2001:db8::1]:x")]    // non-numeric, non-obf port
    [InlineData("192.0.2.60:")]        // empty port
    [InlineData("192.0.2.60:abc")]     // non-numeric, non-obf port
    [InlineData("192.0.2.60:999999")]  // > 5 digits
    [InlineData(":4711")]              // empty name
    [InlineData("256.1.1.1")]          // not a valid IPv4
    [InlineData("example.com")]        // hostnames are not nodes
    [InlineData("example.com:80")]
    [InlineData("_")]                  // obf token needs at least one char after '_'
    [InlineData("_bad$char")]          // invalid obf char
    [InlineData("unknown:")]           // empty port
    [InlineData("nota node")]
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        bool ok = false;
        Should.NotThrow(() => ok = HttpForwardedNode.TryParse(raw, out _));

        ok.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should throw HttpException from Parse on malformed input")]
    public void Parse_Malformed_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpForwardedNode.Parse("not-a-node"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedNode: Should treat the default instance as empty")]
    public void Default_ShouldBeEmpty()
    {
        HttpForwardedNode node = default;

        node.IsEmpty.ShouldBeTrue();
        node.Name.ShouldBe(string.Empty);
        node.ToString().ShouldBe(string.Empty);
    }
}
