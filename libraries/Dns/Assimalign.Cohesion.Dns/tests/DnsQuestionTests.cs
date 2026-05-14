using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Covers the <see cref="DnsQuestion"/> value-type contract.
/// </summary>
public class DnsQuestionTests
{
    [Fact(DisplayName = "Cohesion Test [Dns] - DnsQuestion: defaults class to IN")]
    public void DefaultClass_IsIN()
    {
        var question = new DnsQuestion("example.com", DnsRecordType.A);
        Assert.Equal(DnsClass.IN, question.Class);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsQuestion: equality compares all three fields")]
    public void Equality_AllFields()
    {
        var a = new DnsQuestion("example.com", DnsRecordType.A, DnsClass.IN);
        var b = new DnsQuestion("EXAMPLE.COM", DnsRecordType.A, DnsClass.IN);
        var differentType = new DnsQuestion("example.com", DnsRecordType.AAAA, DnsClass.IN);
        var differentClass = new DnsQuestion("example.com", DnsRecordType.A, DnsClass.CH);

        Assert.Equal(a, b);                // case-insensitive name match
        Assert.NotEqual(a, differentType);
        Assert.NotEqual(a, differentClass);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - DnsQuestion: ToString uses zone-file convention")]
    public void ToString_ZoneFileFormat()
    {
        var question = new DnsQuestion("example.com", DnsRecordType.MX);
        // Zone-file convention: NAME CLASS TYPE
        Assert.Equal("example.com IN MX", question.ToString());
    }
}
