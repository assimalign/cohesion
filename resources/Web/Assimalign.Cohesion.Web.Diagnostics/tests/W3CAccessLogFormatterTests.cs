using System.Text;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

using Shouldly;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Exact-output coverage for the access-log line renderer: W3C extended and NCSA
/// common/combined shapes, absent-value dashes, space encoding, and log-injection
/// sanitization.
/// </summary>
public class W3CAccessLogFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 11, 13, 45, 30, TimeSpan.Zero);

    private static LoggerEntry CreateEntry(Action<Dictionary<string, object?>>? mutate = null)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange,
            [HttpLoggingAttributes.ClientAddress] = "203.0.113.7",
            [HttpLoggingAttributes.RequestMethod] = "GET",
            [HttpLoggingAttributes.RequestPath] = "/orders/42",
            [HttpLoggingAttributes.RequestQuery] = "page=2",
            [HttpLoggingAttributes.ResponseStatusCode] = 200,
            [HttpLoggingAttributes.ResponseBodyBytes] = 1234L,
            [HttpLoggingAttributes.Duration] = 123.456d,
            [HttpLoggingAttributes.RequestProtocol] = "HTTP/1.1",
            [HttpLoggingAttributes.RequestHost] = "example.test",
            ["http.request.header.user-agent"] = "Unit Agent/1.0",
            ["http.request.header.referer"] = "https://ref.example/",
        };

        mutate?.Invoke(attributes);

        return new LoggerEntry(LogLevel.Information, "test", "msg", attributes: attributes, timestamp: Timestamp);
    }

    private static string Render(ILoggerEntry entry, AccessLogFormat format)
    {
        StringBuilder builder = new();
        W3CAccessLogFormatter.AppendLine(builder, entry, format);
        return builder.ToString();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: Extended line renders every field in declaration order")]
    public void Extended_FullEntry_ShouldRenderExactLine()
    {
        // Act — cs-bytes is absent from the entry, so it renders '-'; the user agent's space
        // becomes '+' per the extended-format convention.
        string line = Render(CreateEntry(), AccessLogFormat.W3CExtended);

        // Assert
        line.ShouldBe("2026-07-11 13:45:30 203.0.113.7 GET /orders/42 page=2 200 - 1234 0.123 HTTP/1.1 example.test Unit+Agent/1.0 https://ref.example/\n");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: Absent attributes render as dashes")]
    public void Extended_EmptyEntry_ShouldRenderDashes()
    {
        // Arrange
        LoggerEntry entry = new(
            LogLevel.Information,
            "test",
            "msg",
            attributes: new Dictionary<string, object?> { [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange },
            timestamp: Timestamp);

        // Act
        string line = Render(entry, AccessLogFormat.W3CExtended);

        // Assert
        line.ShouldBe("2026-07-11 13:45:30 - - - - - - - - - - - -\n");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: Control characters are stripped from text fields")]
    public void Extended_ControlCharacters_ShouldBeStripped()
    {
        // Arrange — a hostile user agent attempting to forge a second log line.
        LoggerEntry entry = CreateEntry(attributes => attributes["http.request.header.user-agent"] = "evil\r\n2026-01-01 injected");

        // Act
        string line = Render(entry, AccessLogFormat.W3CExtended);

        // Assert — the CR/LF never reach the file; the space is '+'-encoded.
        line.ShouldNotContain("\r");
        line.Count(c => c == '\n').ShouldBe(1);
        line.ShouldContain("evil2026-01-01+injected", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: Directives declare version, software, date, and the field list")]
    public void Directives_ShouldRenderHeaderBlock()
    {
        // Act
        StringBuilder builder = new();
        W3CAccessLogFormatter.AppendExtendedDirectives(builder, Timestamp);
        string directives = builder.ToString();

        // Assert
        directives.ShouldBe(
            "#Version: 1.0\n" +
            "#Software: Assimalign.Cohesion.Web.Diagnostics\n" +
            "#Date: 2026-07-11 13:45:30\n" +
            "#Fields: date time c-ip cs-method cs-uri-stem cs-uri-query sc-status cs-bytes sc-bytes time-taken cs-version cs-host cs(User-Agent) cs(Referer)\n");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: NCSA common line renders host, UTC timestamp, request, status, and bytes")]
    public void Common_FullEntry_ShouldRenderExactLine()
    {
        // Act
        string line = Render(CreateEntry(), AccessLogFormat.Common);

        // Assert
        line.ShouldBe("203.0.113.7 - - [11/Jul/2026:13:45:30 +0000] \"GET /orders/42?page=2 HTTP/1.1\" 200 1234\n");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: NCSA combined line appends quoted referer and user agent")]
    public void Combined_FullEntry_ShouldRenderExactLine()
    {
        // Act
        string line = Render(CreateEntry(), AccessLogFormat.Combined);

        // Assert — quoted fields keep their spaces (no '+' encoding in NCSA).
        line.ShouldBe("203.0.113.7 - - [11/Jul/2026:13:45:30 +0000] \"GET /orders/42?page=2 HTTP/1.1\" 200 1234 \"https://ref.example/\" \"Unit Agent/1.0\"\n");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - W3C formatter: NCSA quoted fields escape quotes and backslashes")]
    public void Combined_QuotedField_ShouldEscapeDelimiters()
    {
        // Arrange
        LoggerEntry entry = CreateEntry(attributes => attributes["http.request.header.user-agent"] = "agent \"quoted\" \\ tail");

        // Act
        string line = Render(entry, AccessLogFormat.Combined);

        // Assert
        line.ShouldContain("\"agent \\\"quoted\\\" \\\\ tail\"", Case.Sensitive);
    }
}
