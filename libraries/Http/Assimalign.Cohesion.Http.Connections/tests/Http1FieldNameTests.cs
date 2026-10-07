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
/// HTTP/1.1 field names (#1333). RFC 9112 §5.1 allows no whitespace between a field name and its
/// colon, and a field name is a token (RFC 9110 §5.1), so it is never empty. A field line that breaks
/// the rule in the header section or in a chunked trailer section is answered with 400, and the
/// connection is closed: an intermediary that reads the line differently could disagree with the
/// server about where the next request starts. Each request below has a second one pipelined behind
/// it, which must never be served.
/// </summary>
public class Http1FieldNameTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Names: A header field name that is not a token should be answered with 400 and close the connection")]
    [InlineData("X-Trace : abc")]  // a space before the colon
    [InlineData("X-Trace\t: abc")] // a tab before the colon
    [InlineData(": abc")]          // an empty name
    [InlineData(" : abc")]         // a name of whitespace alone
    public async Task ReceiveAsync_OnHeaderFieldNameNotToken_ShouldAnswerBadRequestAndClose(string fieldLine)
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"GET /malformed HTTP/1.1\r\nHost: api.test\r\n{fieldLine}\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — no request reaches the application.
        result.Paths.ShouldBeEmpty();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Names: A trailer field name that is not a token should fail the body read, be answered with 400, and close the connection")]
    [InlineData("X-Checksum : abc")]  // a space before the colon
    [InlineData("X-Checksum\t: abc")] // a tab before the colon
    [InlineData(": abc")]             // an empty name
    [InlineData(" : abc")]            // a name of whitespace alone
    public async Task ReceiveAsync_OnTrailerFieldNameNotToken_ShouldAnswerBadRequestAndClose(string fieldLine)
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Names: A malformed chunked body should never be drained into the next request")]
    public async Task ReceiveAsync_OnMalformedChunkFollowedByValidFraming_ShouldNotServeNextRequest()
    {
        // Arrange — after the bad chunk size, the octets read like a last chunk and a new request.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "zz\r\n0\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures.Single().ShouldBeOfType<InvalidDataException>();
        result.Output.ShouldStartWith("HTTP/1.1 400");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Field Names: Whitespace after the colon should be accepted in both sections")]
    public async Task ReceiveAsync_OnWhitespaceAfterColon_ShouldServeBothRequests()
    {
        // Arrange — OWS may follow the colon (RFC 9112 §5.1); only the name must be a bare token.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nX-Trace:\t abc \r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n0\r\nX-Checksum:  abc123\t\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/upload", "/next"]);
        result.ReadFailures.ShouldAllBe(failure => failure == null);
        result.Headers[0].ShouldBe("abc");
        result.Trailers[0].ShouldBe("abc123");
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
