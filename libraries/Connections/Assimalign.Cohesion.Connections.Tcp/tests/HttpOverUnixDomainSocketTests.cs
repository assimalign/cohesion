using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// End-to-end integration: HTTP/1.1 served over a Unix domain socket listener via
/// <see cref="HttpConnectionListenerOptions.UseHttp1(IConnectionListener)"/>, with a client dialing the
/// same socket through the driver factory. Proves the hardened UDS path composes with the HTTP stack with
/// no Http-layer changes — the capability-selected listener seam accepts any reliable, ordered stream.
/// </summary>
public class HttpOverUnixDomainSocketTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Http1: Should serve an HTTP/1.1 request over a Unix domain socket")]
    public async Task Http1_OverUnixDomainSocket_ShouldServeRequestAndResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        string path = UnixSocketPath.Create();

        try
        {
            TcpConnectionListener driverListener = TcpConnectionListener.Create(
                options => options.EndPoint = new UnixDomainSocketEndPoint(path));

            await using HttpConnectionListener httpListener = HttpConnectionListener.Create(
                options => options.UseHttp1(driverListener));

            Task serverTask = RunServerAsync(httpListener, cancellationToken);

            TcpConnectionFactory factory = new();

            // Act — dial the socket (retrying until the server has lazily bound) and speak a raw request.
            await using Connection client = await ConnectWithRetryAsync(factory, path, cancellationToken);

            byte[] request = Encoding.ASCII.GetBytes(
                "GET /hello?name=uds HTTP/1.1\r\nHost: local\r\nConnection: close\r\n\r\n");

            await client.Output.WriteAsync(request, cancellationToken);
            await client.Output.FlushAsync(cancellationToken);

            byte[] responseBytes = await client.Input.ReadToEndAsync(cancellationToken);
            await serverTask;

            // Assert
            string response = Encoding.ASCII.GetString(responseBytes);
            response.ShouldContain("HTTP/1.1 200 OK");
            response.ShouldContain("hello uds");
        }
        finally
        {
            SafeDelete(path);
        }
    }

    private static async Task<Connection> ConnectWithRetryAsync(
        TcpConnectionFactory factory,
        string path,
        CancellationToken cancellationToken)
    {
        // The HTTP listener binds the underlying Unix socket lazily on its first accept, and a UDS
        // connect (unlike a named-pipe connect) does not wait for the socket to appear, so retry until
        // the server is listening.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await factory.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
            }
            catch (SocketException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static async Task RunServerAsync(HttpConnectionListener httpListener, CancellationToken cancellationToken)
    {
        await using IHttpConnection connection = await httpListener.AcceptOrListenAsync(cancellationToken);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);

        await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken))
        {
            string name = context.Request.Query["name"].Value ?? string.Empty;
            byte[] body = Encoding.UTF8.GetBytes($"hello {name}");

            context.Response.StatusCode = HttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync(body, 0, body.Length, cancellationToken);
            await connectionContext.SendAsync(context, cancellationToken);
            await context.DisposeAsync();

            break;
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
