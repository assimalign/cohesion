using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// HTTP/1.1 request-body limits found after dispatch, served through the real Web server (#1339).
/// The application reads the body and the read fails, so the pipeline faults and the server's fault
/// boundary stages a <c>500</c>. The transport answers the limit instead: <c>413</c> for a body over
/// the size cap and <c>408</c> for one below the minimum data rate, with <c>Connection: close</c>,
/// and the connection ends. No <c>500</c> reaches the client, and nothing behind the rejected body
/// is served.
/// </summary>
public class WebApplicationServerRequestBodyLimitTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: A chunked HTTP/1.1 body over the cap should be answered with 413, not 500, and close the connection")]
    public async Task Server_OnChunkedBodyOverCap_ShouldAnswerContentTooLargeAndClose()
    {
        // Arrange — the chunk's data reads like a last chunk and then a second request.
        string request =
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "40\r\n"
            + "0\r\n\r\nGET /smuggled HTTP/1.1\r\nHost: localhost\r\n\r\n";

        // Act
        (string response, int served) = await ServeAsync(request, http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        response.ShouldStartWith("HTTP/1.1 413");
        response.ShouldContain("Connection: close");
        response.ShouldNotContain("500");
        served.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: A Content-Length HTTP/1.1 body over the cap should be answered with 413, not 500, and close the connection")]
    public async Task Server_OnContentLengthBodyOverCap_ShouldAnswerContentTooLargeAndClose()
    {
        // Arrange
        string request =
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 32\r\n\r\n"
            + new string('a', 32)
            + "GET /next HTTP/1.1\r\nHost: localhost\r\n\r\n";

        // Act
        (string response, int served) = await ServeAsync(request, http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        response.ShouldStartWith("HTTP/1.1 413");
        response.ShouldContain("Connection: close");
        response.ShouldNotContain("500");
        served.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: An HTTP/1.1 body below the minimum data rate should be answered with 408, not 500, and close the connection")]
    public async Task Server_OnBodyBelowMinimumDataRate_ShouldAnswerRequestTimeoutAndClose()
    {
        // Arrange — the head declares a body the client never sends.
        string request = "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100\r\n\r\n";

        // Act
        (string response, int served) = await ServeAsync(
            request,
            http1 => http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100)));

        // Assert
        response.ShouldStartWith("HTTP/1.1 408");
        response.ShouldContain("Connection: close");
        response.ShouldNotContain("500");
        served.ShouldBe(1);
    }

    /// <summary>
    /// Writes <paramref name="request"/> on one raw in-memory connection to a Web server whose
    /// pipeline reads every request body to its end, then reads until the server closes the
    /// connection. Returns everything the server wrote and the number of requests the pipeline ran.
    /// </summary>
    private static async Task<(string Response, int Served)> ServeAsync(string request, Action<Http1ConnectionListenerOptions> configure)
    {
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(transport, configure));
        FakePipeline pipeline = new(async (context, _) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, cancellationToken) > 0)
            {
            }

            context.Response.StatusCode = HttpStatusCode.Ok;
        });

        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = pipeline,
            Listener = listener,
        });

        await server.StartAsync(cancellationToken);

        try
        {
            await using Connection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
            Stream stream = client.AsStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            // The server closes the connection after the rejection, so the read ends.
            string response = await ReadToEndAsync(stream, cancellationToken);

            return (response, pipeline.Executed.Count);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder received = new();
        byte[] buffer = new byte[1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return received.ToString();
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }
}
