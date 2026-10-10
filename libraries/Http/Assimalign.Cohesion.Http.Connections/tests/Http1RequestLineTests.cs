using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/1.1 request-line octets (#1341). A request line is <c>method SP request-target SP
/// HTTP-version</c> (RFC 9112 §3), so every octet in it is a VCHAR or SP. The line used to be decoded
/// as ASCII, which turned an octet above 0x7F into <c>?</c>: <c>GET /admin\xFFx</c> was routed as the
/// path <c>/admin</c> with the query <c>x</c>, while an intermediary that forwards the raw octets saw
/// another path. Any other octet is now answered with 400 and the connection closes. Each request
/// below has a second one pipelined behind it, which must never be served.
/// </summary>
public class Http1RequestLineTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Request Line: A request line with an octet other than VCHAR and SP should be answered with 400 and close the connection")]
    [InlineData("GET /adminÿx HTTP/1.1")]          // obs-text, once decoded to '?' and split into a query
    [InlineData("GET /café HTTP/1.1")]             // obs-text in the path
    [InlineData("GET /search?q=\u0080 HTTP/1.1")]       // obs-text in the query
    [InlineData("GET /a\tb HTTP/1.1")]                  // a tab, which a lenient parser treats as a separator
    [InlineData("GET\t/ HTTP/1.1")]                     // a tab as the separator
    [InlineData("GET /a\rb HTTP/1.1")]                  // a bare CR
    [InlineData("GET /a HTTP/1.1\nX-Smuggled: 1")]      // a bare LF, a line end to a lenient parser
    [InlineData("GET /a\u0000 HTTP/1.1")]               // a NUL
    [InlineData("GET /a\u007F HTTP/1.1")]               // DEL
    public async Task ReceiveAsync_OnRequestLineWithInvalidOctet_ShouldAnswerBadRequestAndClose(string requestLine)
    {
        // Arrange
        byte[] payload = Encoding.Latin1.GetBytes(
            $"{requestLine}\r\nHost: api.test\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert — no request reaches the application.
        result.Paths.ShouldBeEmpty();
        result.Output.ShouldStartWith("HTTP/1.1 400");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Request Line: A request line of VCHAR and SP should be served with its path and query intact")]
    public async Task ReceiveAsync_OnVisibleAsciiRequestLine_ShouldServeBothRequests()
    {
        // Arrange — a non-ASCII character reaches the target only percent-encoded.
        byte[] payload = Encoding.Latin1.GetBytes(
            "GET /admin%C3%BFx?id=42 HTTP/1.1\r\nHost: api.test\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload);

        // Assert
        result.Paths.ShouldBe(["/adminÿx", "/next"]);
        result.Ids.ShouldBe(["42", ""]);
        result.Output.ShouldStartWith("HTTP/1.1 200");
    }

    /// <summary>
    /// Serves every request on one connection and returns the paths and <c>id</c> query values
    /// served and everything the server wrote.
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
                served.Ids.Add(exchange.Request.Query["id"].Value);
                await connectionContext.SendAsync(exchange);
            }
        }

        // The connection is disposed, so its output is complete: everything the server wrote is here.
        served.Output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return served;
    }

    private sealed class Served
    {
        public List<string> Paths { get; } = new();

        public List<string?> Ids { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
