using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/1.1 field values (#1341). RFC 9112 §5.1 trims optional whitespace, which is SP and HTAB
/// only, so any other character at either end of a value belongs to it. RFC 9110 §5.5 makes a value
/// with NUL, CR, LF, or any other control character but HTAB invalid: the line reader ends a line only
/// at CRLF, so a bare CR or LF reaches the value, and an intermediary that ends the line there reads a
/// different message. Such a header is answered with 400 and such a trailer fails the body read, which
/// is then answered with 400; either way the connection closes. Values are decoded as Latin-1, so an
/// obs-text octet reaches the application intact, and every reader of a value trims SP and HTAB
/// only: a no-break space or a next-line octet stays in a framing field, a Host value, a connection
/// option, and a chunk-size line, where a Unicode trim would strip it. Each request below has a
/// second one pipelined behind it, which must never be served after a rejection.
/// </summary>
public class Http1FieldValueTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A header value with a control character other than HTAB should be answered with 400 and close the connection")]
    [InlineData("X-Trace: \u000Babc")]                    // a leading vertical tab
    [InlineData("X-Trace: abc\u000B")]                    // a trailing vertical tab
    [InlineData("X-Trace: \u000Cabc")]                    // a leading form feed
    [InlineData("X-Trace: abc\u000C")]                    // a trailing form feed
    [InlineData("X-Trace: abc\r")]                        // a trailing bare CR
    [InlineData("X-Trace: a\rb")]                         // a bare CR inside the value
    [InlineData("X-Trace: a\nTransfer-Encoding: chunked")] // a bare LF, read as two fields by a lenient hop
    [InlineData("X-Trace: \u0000abc")]                    // a leading NUL
    [InlineData("X-Trace: a\u0000b")]                     // a NUL inside the value
    [InlineData("X-Trace: a\u0001b")]                     // another C0 control
    [InlineData("X-Trace: a\u007Fb")]                     // DEL
    public async Task ReceiveAsync_OnHeaderValueWithControlCharacter_ShouldAnswerBadRequestAndClose(string fieldLine)
    {
        // Arrange
        byte[] payload = Encoding.Latin1.GetBytes(
            $"GET /malformed HTTP/1.1\r\nHost: api.test\r\n{fieldLine}\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — no request reaches the application.
        result.Paths.ShouldBeEmpty();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A trailer value with a control character other than HTAB should fail the body read, be answered with 400, and close the connection")]
    [InlineData("X-Checksum: \u000Babc")]  // a leading vertical tab
    [InlineData("X-Checksum: abc\u000B")]  // a trailing vertical tab
    [InlineData("X-Checksum: abc\u000C")]  // a trailing form feed
    [InlineData("X-Checksum: abc\r")]      // a trailing bare CR
    [InlineData("X-Checksum: a\nX-Other: b")] // a bare LF
    [InlineData("X-Checksum: a\u0000b")]   // a NUL inside the value
    [InlineData("X-Checksum: a\u007Fb")]   // DEL
    public async Task ReceiveAsync_OnTrailerValueWithControlCharacter_ShouldAnswerBadRequestAndClose(string fieldLine)
    {
        // Arrange
        byte[] payload = Encoding.Latin1.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"5\r\nhello\r\n0\r\n{fieldLine}\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — the trailer section is read with the body, after the head was dispatched.
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeOfType<InvalidDataException>();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: Only SP and HTAB should be trimmed from a value, in both sections")]
    [InlineData(" \t abc \t ", "abc")]                         // optional whitespace on both ends
    [InlineData("a\tb c", "a\tb c")]                           // HTAB and SP inside the value stay
    [InlineData(" abc ", " abc ")]         // a no-break space is obs-text, not OWS
    [InlineData("   abc   ", "  abc  ")]   // trimming stops at the no-break space
    [InlineData("café", "café")]                     // obs-text inside the value survives the decode
    public async Task ReceiveAsync_OnValueWithWhitespaceOrObsText_ShouldTrimOnlySpaceAndTab(string rawValue, string expected)
    {
        // Arrange — the same value as a header and as a trailer.
        byte[] payload = Encoding.Latin1.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nX-Trace:{rawValue}\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"5\r\nhello\r\n0\r\nX-Checksum:{rawValue}\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/upload", "/next"]);
        result.ReadFailures.ShouldAllBe(failure => failure == null);
        result.Headers[0].ShouldBe(expected);
        result.Trailers[0].ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A framing field padded with obs-text whitespace should not frame the body, and no request should be served")]
    [InlineData("Transfer-Encoding: chunked\u00A0")] // a trailing no-break space
    [InlineData("Transfer-Encoding: \u0085chunked")] // a leading next-line octet
    [InlineData("Transfer-Encoding: gzip, chunked\u00A0")] // the last coding of a list
    [InlineData("Content-Length: 5\u00A0")]          // a trailing no-break space
    [InlineData("Content-Length: \u00855")]          // a leading next-line octet
    [InlineData("Content-Length: 5, 5\u00A0")]       // a repeated length in a list
    public async Task ReceiveAsync_OnFramingFieldWithObsTextWhitespace_ShouldServeNoRequestAndClose(string framingLine)
    {
        // Arrange — the body matches both framings, so only a rejection keeps /upload from being served.
        string body = framingLine.StartsWith("Transfer-Encoding", StringComparison.Ordinal)
            ? "5\r\nhello\r\n0\r\n\r\n"
            : "hello";
        byte[] payload = Encoding.Latin1.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\n{framingLine}\r\n\r\n{body}"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — a Unicode trim read these as "chunked" and 5; an exact hop sees an unknown coding
        // and an invalid length, so the request is rejected before dispatch.
        result.Paths.ShouldBeEmpty();
        result.Output.ShouldNotContain("HTTP/1.1 200");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A Host value with an octet other than VCHAR should be answered with 400 and close the connection")]
    [InlineData("api.test\u00A0")]  // a trailing no-break space, which a Unicode trim strips
    [InlineData("\u0085api.test")]  // a leading next-line octet
    [InlineData("api.t\u00E9st")]   // obs-text inside the name
    [InlineData("api test")]        // an interior SP
    [InlineData("api\ttest")]       // an interior HTAB
    public async Task ReceiveAsync_OnHostValueWithNonVisibleOctet_ShouldAnswerBadRequestAndClose(string host)
    {
        // Arrange
        byte[] payload = Encoding.Latin1.GetBytes(
            $"GET /admin HTTP/1.1\r\nHost: {host}\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — RFC 9112 §3.2: a Host field with an invalid value is answered with 400.
        result.Paths.ShouldBeEmpty();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A Connection option padded with a no-break space should not be read as close")]
    public async Task ReceiveAsync_OnConnectionCloseWithObsTextWhitespace_ShouldKeepTheConnectionOpen()
    {
        // Arrange — "close\xA0" is an unknown connection option, not close (RFC 9110 §7.6.1).
        byte[] payload = Encoding.Latin1.GetBytes(
            "GET /first HTTP/1.1\r\nHost: api.test\r\nConnection: close\u00A0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/first", "/next"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A chunk-size line with a control character or a non-BWS pad should fail the body read, be answered with 400, and close the connection")]
    [InlineData("5;\nxx")]       // a bare LF in the extension: a lenient hop reads the size line "5;"
    [InlineData("5;x\r")]        // a bare CR in the extension
    [InlineData("5;\u0000")]     // NUL in the extension
    [InlineData("5\r;x")]        // a bare CR before the extension, which TrimEnd() stripped
    [InlineData("5\u000B;x")]    // a vertical tab before the extension
    [InlineData("5\u00A0;x")]    // a no-break space before the extension is not BWS
    public async Task ReceiveAsync_OnMalformedChunkSizeLine_ShouldAnswerBadRequestAndClose(string sizeLine)
    {
        // Arrange — the chunk data matches the size, so only a rejection of the line fails the read.
        byte[] payload = Encoding.Latin1.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"{sizeLine}\r\nhello\r\n0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeOfType<InvalidDataException>();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Values: A chunk-size line with SP or HTAB before its extension should still be read")]
    [InlineData("5 ;x")]
    [InlineData("5\t;x")]
    [InlineData("5 \t ;name=\"a\tb\"")] // HTAB is allowed inside a quoted extension value
    public async Task ReceiveAsync_OnChunkSizeLineWithBwsBeforeExtension_ShouldReadTheChunk(string sizeLine)
    {
        // Arrange
        byte[] payload = Encoding.Latin1.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"{sizeLine}\r\nhello\r\n0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/upload", "/next"]);
        result.ReadFailures.ShouldAllBe(failure => failure == null);
    }

    /// <summary>
    /// Serves every request on one connection the way a host would: reads each body to its end,
    /// then sends the response. Returns the paths served, each body read's failure, the X-Trace
    /// header and X-Checksum trailer of each request, and everything the server wrote.
    /// </summary>
    private static async Task<Served> ServeAsync(byte[] payload)
    {
        TestConnection transport = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport));
        Served served = new();

        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                served.Paths.Add(exchange.Request.Path.Value);
                served.ReadFailures.Add(await TryReadToEndAsync(exchange.Request.Body));
                served.Headers.Add(exchange.Request.Headers[new HttpHeaderKey("x-trace")].Value);
                served.Trailers.Add(exchange.Request.Trailers.IsSupported ? exchange.Request.Trailers[new HttpHeaderKey("x-checksum")].Value : null);
                await connectionContext.SendAsync(exchange);
            }
        }

        // The connection is disposed, so its output is complete: everything the server wrote is here.
        served.Output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return served;
    }

    private static async Task<Exception?> TryReadToEndAsync(Stream body)
    {
        try
        {
            byte[] buffer = new byte[256];
            while (await body.ReadAsync(buffer) > 0)
            {
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class Served
    {
        public List<string> Paths { get; } = new();

        public List<Exception?> ReadFailures { get; } = new();

        public List<string?> Headers { get; } = new();

        public List<string?> Trailers { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
