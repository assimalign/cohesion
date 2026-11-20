using Shouldly;
using Xunit;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Cases the official suite does not pin: dialect differences, IDNA mapping and A-label edge cases,
/// and grammar corners of the other formats.
/// </summary>
public class BuiltInFormatTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Built-in formats: ";

    [Theory(DisplayName = DisplayPrefix + "hostname: Should apply A-label rules from draft 7 on and RFC 1123 alone before")]
    [InlineData("xn--bfa", true, false)]
    [InlineData("xn--abc", true, false)]
    [InlineData("ab--cd", true, false)]
    [InlineData("XN--FSQU00A.XN--0ZWM56D", true, true)]
    [InlineData("xn--fsqu00a.xn--0zwm56d", true, true)]
    [InlineData("xn--Strae-oqa.de", true, true)]
    [InlineData("a--b.example", true, true)]
    [InlineData("例子.测试", false, false)]
    [InlineData("a..b", false, false)]
    [InlineData("-a.example", false, false)]
    [InlineData("a-.example", false, false)]
    [InlineData("example.com.", false, false)]
    public void Hostname_DraftDifferences_ShouldMatch(string value, bool draft06, bool draft07)
    {
        // Act & Assert
        IsValid("hostname", value, JsonSchemaVersion.Draft06).ShouldBe(draft06);
        IsValid("hostname", value, JsonSchemaVersion.Draft07).ShouldBe(draft07);
        IsValid("hostname", value, JsonSchemaVersion.Draft202012).ShouldBe(draft07);
    }

    [Fact(DisplayName = DisplayPrefix + "hostname: Should limit labels to 63 characters and names to 253")]
    public void Hostname_Lengths_ShouldBeLimited()
    {
        // Arrange
        string label63 = new('a', 63);
        string name253 = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));

        // Act & Assert
        IsValid("hostname", label63).ShouldBeTrue();
        IsValid("hostname", label63 + "a").ShouldBeFalse();
        name253.Length.ShouldBe(253);
        IsValid("hostname", name253).ShouldBeTrue();
        IsValid("hostname", name253 + "d").ShouldBeFalse();
    }

    [Theory(DisplayName = DisplayPrefix + "idn-hostname: Should map, then validate, as UTS 46 and IDNA2008 do")]
    [InlineData("İstanbul.com", true)]
    [InlineData("ᾀ.com", true)]
    [InlineData("ꭰ.com", true)]
    [InlineData("ϲ.com", true)]
    [InlineData("a\u3164b.com", true)]
    [InlineData("Ⅻ.com", true)]
    [InlineData("e\u0301xample.com", true)]
    [InlineData("faß.de", true)]
    [InlineData("Bücher.example", true)]
    [InlineData("例子。测试", true)]
    [InlineData("XN--FSQU00A.XN--0ZWM56D", true)]
    [InlineData("\uFFFE.com", false)]
    [InlineData("⒈.com", false)]
    [InlineData("xn--bfa.com", false)]
    [InlineData("xn--abc.com", false)]
    [InlineData("ab\u200Dc.com", false)]
    [InlineData("1ب.com", false)]
    [InlineData("xn--ü.com", false)]
    [InlineData("", false)]
    public void IdnHostname_Mapping_ShouldMatchReference(string value, bool expected)
    {
        // Act & Assert
        IsValid("idn-hostname", value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "relative-json-pointer: Should accept index manipulation from 2020-12 on")]
    [InlineData("0+1", false, true)]
    [InlineData("1-1/a", false, true)]
    [InlineData("0#", true, true)]
    [InlineData("0/a~0b", true, true)]
    [InlineData("12", true, true)]
    [InlineData("01", false, false)]
    [InlineData("-1", false, false)]
    [InlineData("0+01", false, false)]
    [InlineData("0#/a", false, false)]
    [InlineData("/a", false, false)]
    public void RelativeJsonPointer_DraftDifferences_ShouldMatch(string value, bool draft201909, bool draft202012)
    {
        // Act & Assert
        IsValid("relative-json-pointer", value, JsonSchemaVersion.Draft201909).ShouldBe(draft201909);
        IsValid("relative-json-pointer", value, JsonSchemaVersion.Draft202012).ShouldBe(draft202012);
    }

    [Theory(DisplayName = DisplayPrefix + "date-time, date and time: Should follow RFC 3339 and the Gregorian calendar")]
    [InlineData("date-time", "1998-12-31T23:59:60Z", true)]
    [InlineData("date-time", "1998-12-31T15:59:60.123-08:00", true)]
    [InlineData("date-time", "1998-12-31T23:59:60+01:00", false)]
    [InlineData("date-time", "2020-02-29t00:00:00z", true)]
    [InlineData("date-time", "2021-02-29T00:00:00Z", false)]
    [InlineData("date-time", "2020-01-01 00:00:00Z", false)]
    [InlineData("date-time", "2020-01-01T00:00:00", false)]
    [InlineData("date-time", "2020-01-01T00:00:00.Z", false)]
    [InlineData("date", "2000-02-29", true)]
    [InlineData("date", "1900-02-29", false)]
    [InlineData("date", "2020-04-31", false)]
    [InlineData("date", "2020-13-01", false)]
    [InlineData("date", "20200101", false)]
    [InlineData("date", "２０２０-01-01", false)]
    [InlineData("time", "23:59:60Z", true)]
    [InlineData("time", "12:00:00.5+05:30", true)]
    [InlineData("time", "12:00:00", false)]
    [InlineData("time", "24:00:00Z", false)]
    [InlineData("time", "12:00:00+5:30", false)]
    public void DateAndTime_Values_ShouldMatchRfc3339(string format, string value, bool expected)
    {
        // Act & Assert
        IsValid(format, value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "duration: Should follow the RFC 3339 Appendix A grammar")]
    [InlineData("P1D", true)]
    [InlineData("PT1H30M", true)]
    [InlineData("P1Y2M3DT4H5M6S", true)]
    [InlineData("P1W", true)]
    [InlineData("P1W1D", false)]
    [InlineData("P1Y2D", false)]
    [InlineData("PT1H2S", false)]
    [InlineData("P", false)]
    [InlineData("PT", false)]
    [InlineData("P1DT", false)]
    [InlineData("P1.5D", false)]
    [InlineData("-P1D", false)]
    [InlineData("P1M1Y", false)]
    public void Duration_Values_ShouldMatchGrammar(string value, bool expected)
    {
        // Act & Assert
        IsValid("duration", value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "email and idn-email: Should follow the RFC 5321 Mailbox rule")]
    [InlineData("email", "joe@example.com", true)]
    [InlineData("email", "\"joe bloggs\"@example.com", true)]
    [InlineData("email", "joe@[127.0.0.1]", true)]
    [InlineData("email", "joe@[IPv6:::1]", true)]
    [InlineData("email", "joe@[general:literal]", false)]
    [InlineData("email", "joe.@example.com", false)]
    [InlineData("email", ".joe@example.com", false)]
    [InlineData("email", "jo..e@example.com", false)]
    [InlineData("email", "joe@-example.com", false)]
    [InlineData("email", "Joe <joe@example.com>", false)]
    [InlineData("email", "jöe@example.com", false)]
    [InlineData("idn-email", "jöe@example.com", true)]
    [InlineData("idn-email", "실례@실례.테스트", true)]
    [InlineData("idn-email", "joe@\uFFFE.com", false)]
    public void Email_Values_ShouldMatchMailboxRule(string format, string value, bool expected)
    {
        // Act & Assert
        IsValid(format, value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "ipv4 and ipv6: Should accept only canonical address text")]
    [InlineData("ipv4", "192.168.0.1", true)]
    [InlineData("ipv4", "192.168.0.01", false)]
    [InlineData("ipv4", "256.0.0.1", false)]
    [InlineData("ipv4", "1.2.3", false)]
    [InlineData("ipv4", "1.2.3.4\n", false)]
    [InlineData("ipv4", "١٢٣.1.1.1", false)]
    [InlineData("ipv6", "::1", true)]
    [InlineData("ipv6", "::ffff:192.168.0.1", true)]
    [InlineData("ipv6", "1:2:3:4:5:6:7:8", true)]
    [InlineData("ipv6", "1:2:3:4:5:6:7:8:9", false)]
    [InlineData("ipv6", "::1%eth0", false)]
    [InlineData("ipv6", "1::2::3", false)]
    [InlineData("ipv6", "12345::", false)]
    [InlineData("ipv6", "[::1]", false)]
    public void IPAddress_Values_ShouldMatchGrammar(string format, string value, bool expected)
    {
        // Act & Assert
        IsValid(format, value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "uri, uri-reference, iri and iri-reference: Should follow RFC 3986 and RFC 3987")]
    [InlineData("uri", "https://example.com/a?b#c", true)]
    [InlineData("uri", "urn:isbn:0451450523", true)]
    [InlineData("uri", "http://[::1]:80/", true)]
    [InlineData("uri", "http://999.999.999.999/", true)]
    [InlineData("uri", "//example.com", false)]
    [InlineData("uri", "http://example.com/a b", false)]
    [InlineData("uri", "http://example.com/%zz", false)]
    [InlineData("uri", "http://example.com:8o/", false)]
    [InlineData("uri", "http://exämple.com/", false)]
    [InlineData("uri-reference", "//example.com", true)]
    [InlineData("uri-reference", "#fragment", true)]
    [InlineData("uri-reference", "", true)]
    [InlineData("uri-reference", "a:b/c", true)]
    [InlineData("uri-reference", "\\\\server\\share", false)]
    [InlineData("iri", "http://exämple.com/ƒøø?∂éœ=πîx#πîüx", true)]
    [InlineData("iri", "http://[::1ä]/", false)]
    [InlineData("iri-reference", "ƒøø/bar", true)]
    public void Uri_Values_ShouldMatchGrammar(string format, string value, bool expected)
    {
        // Act & Assert
        IsValid(format, value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "uri-template: Should follow RFC 6570 at any level")]
    [InlineData("http://example.com/{id}", true)]
    [InlineData("{+path}/here{?q,lang}", true)]
    [InlineData("{var:30}{list*}{#a.b}", true)]
    [InlineData("it's/{x}", true)]
    [InlineData("{var:9999}", true)]
    [InlineData("{var:10000}", false)]
    [InlineData("{var:0}", false)]
    [InlineData("{var:01}", false)]
    [InlineData("http://example.com/{id", false)]
    [InlineData("{}", false)]
    [InlineData("{a b}", false)]
    [InlineData("a}b", false)]
    [InlineData("{var*:3}", false)]
    public void UriTemplate_Values_ShouldMatchGrammar(string value, bool expected)
    {
        // Act & Assert
        IsValid("uri-template", value).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "json-pointer, regex and uuid: Should check syntax only")]
    [InlineData("json-pointer", "/a~0b/~1", true)]
    [InlineData("json-pointer", "a", false)]
    [InlineData("json-pointer", "/~2", false)]
    [InlineData("regex", "^[a-z]+$", true)]
    [InlineData("regex", "\\p{L}+", true)]
    [InlineData("regex", "(?<n>a)\\k<n>", true)]
    [InlineData("regex", "(", false)]
    [InlineData("regex", "a{2,1}", false)]
    [InlineData("regex", "\\a", false)]
    [InlineData("uuid", "2eb8aa08-aa98-11ea-b4aa-73b441d16380", true)]
    [InlineData("uuid", "2EB8AA08-AA98-11EA-B4AA-73B441D16380", true)]
    [InlineData("uuid", "2eb8aa08aa9811eab4aa73b441d16380", false)]
    [InlineData("uuid", "{2eb8aa08-aa98-11ea-b4aa-73b441d16380}", false)]
    [InlineData("uuid", "2eb8aa08-aa98-11ea-b4aa-73b441d1638g", false)]
    public void SyntaxFormats_Values_ShouldMatch(string format, string value, bool expected)
    {
        // Act & Assert
        IsValid(format, value).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Every format: Should pass instances that are not strings")]
    public void EveryFormat_NonString_ShouldPass()
    {
        // Arrange
        string[] names = ["date-time", "date", "time", "duration", "email", "idn-email", "hostname", "idn-hostname", "ipv4", "ipv6", "uri", "uri-reference", "iri", "iri-reference", "uri-template", "json-pointer", "relative-json-pointer", "regex", "uuid"];

        foreach (string name in names)
        {
            // Act
            BuiltInFormat? format = BuiltInFormats.Find(name);

            // Assert
            format.ShouldNotBeNull(name);
            format.IsValid(TestJson.Parse("12"), JsonSchemaVersion.Draft202012).ShouldBeTrue(name);
            format.IsValid(TestJson.Parse("{\"a\": 1}"), JsonSchemaVersion.Draft202012).ShouldBeTrue(name);
        }

        BuiltInFormats.Find("Date").ShouldBeNull();
    }

    private static bool IsValid(string format, string value, JsonSchemaVersion version = JsonSchemaVersion.Draft202012) =>
        BuiltInFormats.Find(format)!.IsValid(TestJson.String(value), version);
}
