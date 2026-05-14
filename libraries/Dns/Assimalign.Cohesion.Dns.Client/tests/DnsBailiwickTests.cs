using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class DnsBailiwickTests
{
    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("www.example.com", "example.com", true)]
    [InlineData("a.b.c.example.com", "example.com", true)]
    [InlineData("example.com", "com", true)]
    [InlineData("example.com", ".", true)]
    [InlineData("example.org", "example.com", false)]
    [InlineData("notexample.com", "example.com", false)]
    [InlineData("example.com.evil.com", "example.com", false)]
    [InlineData("com", "example.com", false)]
    public void IsInBailiwick_ReturnsExpected(string name, string zone, bool expected)
    {
        Assert.Equal(expected, DnsBailiwick.IsInBailiwick(name, zone));
    }

    [Theory]
    [InlineData("example.com", "example.com", false)] // same name is NOT a strict subdomain
    [InlineData("www.example.com", "example.com", true)]
    [InlineData("example.com", "com", true)]
    [InlineData("example.com", ".", true)]
    [InlineData(".", ".", false)]
    [InlineData("example.org", "example.com", false)]
    public void IsStrictSubdomainOf_ReturnsExpected(string name, string parent, bool expected)
    {
        Assert.Equal(expected, DnsBailiwick.IsStrictSubdomainOf(name, parent));
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com", "com")]
    [InlineData("com", ".")]
    [InlineData(".", ".")]
    public void Parent_ReturnsImmediateParent(string name, string expectedParent)
    {
        Assert.Equal(new DnsName(expectedParent), DnsBailiwick.Parent(name));
    }

    [Theory]
    [InlineData(".", 0)]
    [InlineData("com", 1)]
    [InlineData("example.com", 2)]
    [InlineData("a.b.c.example.com", 5)]
    public void LabelCount_CountsLabelsExcludingRoot(string name, int expected)
    {
        Assert.Equal(expected, DnsBailiwick.LabelCount(name));
    }
}
