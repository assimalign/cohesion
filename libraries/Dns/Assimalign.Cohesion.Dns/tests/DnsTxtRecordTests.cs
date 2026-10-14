using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Dns.Tests;

public class DnsTxtRecordTests
{
    [Theory(DisplayName = "Cohesion Test [Dns] - TXT: source mutations cannot corrupt serialized strings")]
    [InlineData("replace")]
    [InlineData("append")]
    [InlineData("clear")]
    [InlineData("null")]
    public void WriteTo_MutatedSource_PreservesValidatedStrings(string mutation)
    {
        // Arrange
        var strings = new List<string> { "original", "" };
        var record = new DnsTxtRecord("example.com", strings, 60);
        var message = BuildMessage(record);
        byte[] original = new byte[1024];
        int originalLength = message.WriteTo(original);

        // Act
        switch (mutation)
        {
            case "replace": strings[0] = new string('x', 256); break;
            case "append": strings.Add(new string('x', 256)); break;
            case "clear": strings.Clear(); break;
            case "null": strings[0] = null!; break;
        }
        byte[] actual = new byte[1024];
        int actualLength = message.WriteTo(actual);

        // Assert
        actualLength.ShouldBe(originalLength);
        actual.AsSpan(0, actualLength).ToArray().ShouldBe(original.AsSpan(0, originalLength).ToArray());
        var parsed = DnsMessage.Parse(actual.AsSpan(0, actualLength));
        parsed.Answers[0].ShouldBeOfType<DnsTxtRecord>().Strings.ShouldBe(new[] { "original", "" });
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - TXT: exposed strings cannot be replaced through a mutable interface")]
    public void Strings_MutableInterface_RejectsReplacement()
    {
        // Arrange
        var record = new DnsTxtRecord("example.com", new[] { "original" }, 60);

        // Act
        var strings = (IList<string>)record.Strings;

        // Assert
        Should.Throw<NotSupportedException>(() => strings[0] = new string('x', 256));
        record.Strings[0].ShouldBe("original");
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - TXT: each UTF-8 string may occupy 255 octets independently")]
    public void Constructor_MaximumUtf8Strings_RoundTrips()
    {
        // Arrange
        string text = new string('\u00e9', 127) + "x";
        var record = new DnsTxtRecord("example.com", new[] { text, text, "" }, 60);
        byte[] buffer = new byte[1024];

        // Act
        int length = BuildMessage(record).WriteTo(buffer);
        var parsed = DnsMessage.Parse(buffer.AsSpan(0, length));

        // Assert
        parsed.Answers[0].ShouldBeOfType<DnsTxtRecord>().Strings.ShouldBe(new[] { text, text, "" });
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - TXT: the string limit counts UTF-8 octets")]
    public void Constructor_TooManyUtf8Octets_ThrowsArgumentException()
    {
        // Arrange
        string text = new string('\u00e9', 128);

        // Act
        var exception = Should.Throw<ArgumentException>(() => new DnsTxtRecord("example.com", text, 60));

        // Assert
        exception.ParamName.ShouldBe("strings");
    }

    private static DnsMessage BuildMessage(DnsTxtRecord record)
        => new(new DnsHeader(1, DnsHeaderFlags.Response, DnsOpCode.Query, DnsResponseCode.NoError, 0, 1, 0, 0),
            Array.Empty<DnsQuestion>(), new DnsRecord[] { record }, Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>());
}
