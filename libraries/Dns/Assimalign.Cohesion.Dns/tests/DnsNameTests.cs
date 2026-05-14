using System;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Covers the <see cref="DnsName"/> value-type contract: validation, label extraction, and
/// the ASCII-case-insensitive equality required by RFC 1035 &#167; 2.3.3.
/// </summary>
public class DnsNameTests
{
    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: default value is the root")]
    public void Default_IsRoot()
    {
        DnsName name = default;
        Assert.True(name.IsRoot);
        Assert.Equal(".", name.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: Root is the canonical root")]
    public void Root_Singleton()
    {
        Assert.True(DnsName.Root.IsRoot);
        Assert.Equal(".", DnsName.Root.Value);
    }

    [Theory(DisplayName = "Cohesion Test [Dns] - DnsName: equality is ASCII-case-insensitive")]
    [InlineData("example.com", "EXAMPLE.COM")]
    [InlineData("Example.Com", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("EXAMPLE.COM.", "example.com")]
    public void Equality_CaseInsensitive(string a, string b)
    {
        DnsName left = a;
        DnsName right = b;
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: labels enumerate most-specific first")]
    public void GetLabels_Order()
    {
        DnsName name = "www.example.com.";
        Assert.Equal(new[] { "www", "example", "com" }, name.GetLabels());
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: root yields no labels")]
    public void GetLabels_Root_Empty()
    {
        Assert.Empty(DnsName.Root.GetLabels());
        Assert.Empty(((DnsName)".").GetLabels());
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: empty input throws")]
    public void EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DnsName(""));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: rejects label longer than 63 octets")]
    public void LabelTooLong_Throws()
    {
        string longLabel = new string('a', 64);
        Assert.Throws<ArgumentException>(() => new DnsName(longLabel + ".example.com"));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: rejects total length above 253 chars")]
    public void TotalLengthTooLong_Throws()
    {
        // 254 chars of 'a' separated by dots produces a string above the RFC 1035 limit.
        string huge = new string('a', 64).Substring(0, 50);
        var labels = new string[10];
        for (int i = 0; i < labels.Length; i++) { labels[i] = huge; }
        var name = string.Join('.', labels); // 10 * 50 + 9 dots = 509 chars
        Assert.Throws<ArgumentException>(() => new DnsName(name));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: empty label (consecutive dots) throws")]
    public void EmptyLabel_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DnsName("foo..bar"));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsName: implicit string conversion round-trips")]
    public void ImplicitConversion_RoundTrips()
    {
        DnsName name = "example.com";
        string text = name;
        Assert.Equal("example.com", text);
    }
}
