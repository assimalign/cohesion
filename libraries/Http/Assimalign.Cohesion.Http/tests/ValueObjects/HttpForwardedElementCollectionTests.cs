using System.Net;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Tests for <see cref="HttpForwardedElementCollection"/>: the RFC 7239 <c>Forwarded</c> element
/// list — quote-aware comma splitting, multi-hop ordering, rightmost-first traversal, multi-line
/// header combining, and strict (all-or-nothing) rejection of malformed lists.
/// </summary>
public class HttpForwardedElementCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should parse a multi-hop chain in wire order")]
    public void TryParse_MultiHop_ShouldPreserveOrder()
    {
        bool ok = HttpForwardedElementCollection.TryParse("for=192.0.2.43, for=\"[2001:db8:cafe::17]\"", out HttpForwardedElementCollection list);

        ok.ShouldBeTrue();
        list.Count.ShouldBe(2);
        list[0].For!.Value.Address.ShouldBe(IPAddress.Parse("192.0.2.43"));
        list[1].For!.Value.Address.ShouldBe(IPAddress.Parse("2001:db8:cafe::17"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should expose the nearest hop as the right-most element")]
    public void Nearest_ShouldBeRightmostElement()
    {
        HttpForwardedElementCollection.TryParse("for=192.0.2.43, for=198.51.100.17, for=203.0.113.9", out HttpForwardedElementCollection list).ShouldBeTrue();

        list.Nearest.ShouldNotBeNull();
        list.Nearest!.Value.For!.Value.Address.ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should reverse into nearest-hop-first order")]
    public void Reverse_ShouldYieldNearestFirst()
    {
        HttpForwardedElementCollection.TryParse("for=192.0.2.43, for=198.51.100.17, for=203.0.113.9", out HttpForwardedElementCollection list).ShouldBeTrue();

        HttpForwardedElementCollection reversed = list.Reverse();

        reversed[0].For!.Value.Address.ShouldBe(IPAddress.Parse("203.0.113.9"));
        reversed[2].For!.Value.Address.ShouldBe(IPAddress.Parse("192.0.2.43"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should not split on a comma inside a quoted value")]
    public void TryParse_QuotedComma_ShouldNotSplit()
    {
        bool ok = HttpForwardedElementCollection.TryParse("host=\"a,b\";proto=http", out HttpForwardedElementCollection list);

        ok.ShouldBeTrue();
        list.Count.ShouldBe(1);
        list[0].Host.ShouldBe("a,b");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should combine multiple header occurrences by comma")]
    public void TryParse_MultipleHeaderLines_ShouldCombine()
    {
        var headerValue = new HttpHeaderValue(new[] { "for=192.0.2.43", "for=198.51.100.17" });

        bool ok = HttpForwardedElementCollection.TryParse(headerValue, out HttpForwardedElementCollection list);

        ok.ShouldBeTrue();
        list.Count.ShouldBe(2);
        list[1].For!.Value.Address.ShouldBe(IPAddress.Parse("198.51.100.17"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should ignore empty list slots")]
    public void TryParse_EmptySlots_ShouldBeIgnored()
    {
        bool ok = HttpForwardedElementCollection.TryParse("for=192.0.2.43, , for=198.51.100.17", out HttpForwardedElementCollection list);

        ok.ShouldBeTrue();
        list.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should round-trip a chain through Serialize")]
    public void TryParse_ThenSerialize_ShouldRoundTrip()
    {
        const string raw = "for=192.0.2.43, for=\"[2001:db8:cafe::17]\"";
        HttpForwardedElementCollection.TryParse(raw, out HttpForwardedElementCollection list).ShouldBeTrue();

        list.Serialize().ShouldBe(raw);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should enumerate elements in wire order")]
    public void Enumerate_ShouldYieldWireOrder()
    {
        HttpForwardedElementCollection.TryParse("for=192.0.2.43, for=198.51.100.17", out HttpForwardedElementCollection list).ShouldBeTrue();

        var addresses = new System.Collections.Generic.List<IPAddress?>();
        foreach (HttpForwardedElement element in list)
        {
            addresses.Add(element.For?.Address);
        }

        addresses.ShouldBe(new IPAddress?[] { IPAddress.Parse("192.0.2.43"), IPAddress.Parse("198.51.100.17") });
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedList: Should reject the whole list when any element is malformed")]
    [InlineData("for=192.0.2.43, for=notanode")]
    [InlineData("for=bad, for=192.0.2.43")]
    [InlineData("for=192.0.2.43, forgot-an-equals")]
    public void TryParse_MalformedElement_ShouldRejectWholeList(string raw)
    {
        bool ok = true;
        Should.NotThrow(() => ok = HttpForwardedElementCollection.TryParse(raw, out _));

        ok.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedList: Should fail on empty or content-free input")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void TryParse_NoElements_ShouldFail(string raw)
    {
        HttpForwardedElementCollection.TryParse(raw, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should throw HttpException from Parse on malformed input")]
    public void Parse_Malformed_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpForwardedElementCollection.Parse("for=192.0.2.43, bad"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedList: Should treat the static Empty as empty")]
    public void Empty_ShouldHaveNoElements()
    {
        HttpForwardedElementCollection.Empty.Count.ShouldBe(0);
        HttpForwardedElementCollection.Empty.IsEmpty.ShouldBeTrue();
        HttpForwardedElementCollection.Empty.Nearest.ShouldBeNull();
    }
}
