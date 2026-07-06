using System.Net;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Tests for <see cref="HttpForwardedValues"/>: the ordered <c>X-Forwarded-For</c> /
/// <c>X-Forwarded-Proto</c> / <c>X-Forwarded-Host</c> list form — comma splitting, multiple header
/// occurrences, rightmost-first traversal, and interoperation with <see cref="HttpForwardedNode"/>
/// and the RFC 7239 <c>Forwarded</c> parser.
/// </summary>
public class HttpForwardedValuesTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should parse a comma-separated X-Forwarded-For chain in order")]
    public void TryParse_XForwardedFor_ShouldPreserveOrder()
    {
        bool ok = HttpForwardedValues.TryParse("203.0.113.195, 70.41.3.18, 150.172.238.178", out HttpForwardedValues values);

        ok.ShouldBeTrue();
        values.Count.ShouldBe(3);
        values[0].ShouldBe("203.0.113.195");
        values[2].ShouldBe("150.172.238.178");
        values.Nearest.ShouldBe("150.172.238.178");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should reverse into nearest-hop-first order")]
    public void Reverse_ShouldYieldNearestFirst()
    {
        HttpForwardedValues.TryParse("203.0.113.195, 70.41.3.18, 150.172.238.178", out HttpForwardedValues values).ShouldBeTrue();

        HttpForwardedValues reversed = values.Reverse();

        reversed[0].ShouldBe("150.172.238.178");
        reversed[2].ShouldBe("203.0.113.195");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should combine multiple header occurrences in arrival order")]
    public void TryParse_MultipleHeaderLines_ShouldCombine()
    {
        var headerValue = new HttpHeaderValue(new[] { "203.0.113.195", "70.41.3.18, 150.172.238.178" });

        bool ok = HttpForwardedValues.TryParse(headerValue, out HttpForwardedValues values);

        ok.ShouldBeTrue();
        values.Count.ShouldBe(3);
        values[0].ShouldBe("203.0.113.195");
        values[1].ShouldBe("70.41.3.18");
        values[2].ShouldBe("150.172.238.178");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should parse a single X-Forwarded-Proto value")]
    public void TryParse_XForwardedProto_ShouldExposeSingleEntry()
    {
        HttpForwardedValues.TryParse("https", out HttpForwardedValues values).ShouldBeTrue();

        values.Count.ShouldBe(1);
        values[0].ShouldBe("https");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should preserve an X-Forwarded-Host value verbatim")]
    public void TryParse_XForwardedHost_ShouldPreserveVerbatim()
    {
        HttpForwardedValues.TryParse("example.com:8080", out HttpForwardedValues values).ShouldBeTrue();

        values[0].ShouldBe("example.com:8080");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should drop empty comma slots")]
    public void TryParse_EmptySlots_ShouldBeDropped()
    {
        HttpForwardedValues.TryParse("203.0.113.195, , 70.41.3.18", out HttpForwardedValues values).ShouldBeTrue();

        values.Count.ShouldBe(2);
    }

    // ============================================================================
    // Interop with HttpForwardedNode (IPv6 / port edge cases in X-Forwarded-For)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should interpret X-Forwarded-For entries as nodes incl. bare IPv6")]
    public void Entries_ShouldParseAsNodes()
    {
        HttpForwardedValues.TryParse("203.0.113.195, 2001:db8::1, [2001:db8::2]:8443", out HttpForwardedValues values).ShouldBeTrue();

        HttpForwardedNode.TryParse(values[0], out HttpForwardedNode v4).ShouldBeTrue();
        v4.Address.ShouldBe(IPAddress.Parse("203.0.113.195"));

        HttpForwardedNode.TryParse(values[1], out HttpForwardedNode bareV6).ShouldBeTrue();
        bareV6.Address.ShouldBe(IPAddress.Parse("2001:db8::1"));

        HttpForwardedNode.TryParse(values[2], out HttpForwardedNode bracketV6).ShouldBeTrue();
        bracketV6.Address.ShouldBe(IPAddress.Parse("2001:db8::2"));
        bracketV6.PortNumber.ShouldBe(8443);
    }

    // ============================================================================
    // Mixed RFC 7239 + X-Forwarded-For — the two representations agree on the client
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should agree with the Forwarded header on the original client")]
    public void Mixed_ForwardedAndXForwardedFor_ShouldAgreeOnClient()
    {
        // Same chain expressed two ways; the left-most entry is the original client in both.
        HttpForwardedElementCollection.TryParse("for=203.0.113.195, for=70.41.3.18", out HttpForwardedElementCollection forwarded).ShouldBeTrue();
        HttpForwardedValues.TryParse("203.0.113.195, 70.41.3.18", out HttpForwardedValues xff).ShouldBeTrue();

        IPAddress? forwardedClient = forwarded[0].For?.Address;
        HttpForwardedNode.TryParse(xff[0], out HttpForwardedNode xffClient).ShouldBeTrue();

        forwardedClient.ShouldBe(IPAddress.Parse("203.0.113.195"));
        xffClient.Address.ShouldBe(forwardedClient);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should round-trip through Serialize")]
    public void TryParse_ThenSerialize_ShouldRoundTrip()
    {
        const string raw = "203.0.113.195, 70.41.3.18, 150.172.238.178";
        HttpForwardedValues.TryParse(raw, out HttpForwardedValues values).ShouldBeTrue();

        values.Serialize().ShouldBe(raw);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should fail on empty or content-free input")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void TryParse_NoEntries_ShouldFail(string raw)
    {
        HttpForwardedValues.TryParse(raw, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedValues: Should treat the static Empty as empty")]
    public void Empty_ShouldHaveNoEntries()
    {
        HttpForwardedValues.Empty.Count.ShouldBe(0);
        HttpForwardedValues.Empty.IsEmpty.ShouldBeTrue();
        HttpForwardedValues.Empty.Nearest.ShouldBeNull();
    }
}
