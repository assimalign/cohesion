using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

/// <summary>
/// End-to-end integration: HTTP/1.1 served over a named-pipe listener via
/// <see cref="HttpConnectionListenerOptions.UseHttp1(IConnectionListener)"/>, with a client dialing the
/// same pipe through the driver factory. Proves the named-pipe driver composes with the HTTP stack with
/// no Http-layer changes — the capability-selected listener seam accepts any reliable, ordered stream.
/// </summary>
public class HttpOverNamedPipeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Http1: Should serve an HTTP/1.1 request over a named pipe")]
    public async Task Http1_OverNamedPipe_ShouldServeRequestAndResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        NamedPipeConnectionListener driverListener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);

        await using HttpConnectionListener httpListener = HttpConnectionListener.Create(
            options => options.UseHttp1(driverListener));

        Task serverTask = RunServerAsync(httpListener, cancellationToken);

        NamedPipeConnectionFactory factory = new();

        // Act — dial the pipe and speak a raw HTTP/1.1 request over the driver connection.
        await using Connection client = await factory.ConnectAsync(endPoint, cancellationToken);

        byte[] request = Encoding.ASCII.GetBytes(
            "GET /hello?name=pipe HTTP/1.1\r\nHost: local\r\nConnection: close\r\n\r\n");

        await client.Output.WriteAsync(request, cancellationToken);
        await client.Output.FlushAsync(cancellationToken);

        byte[] responseBytes = await client.Input.ReadToEndAsync(cancellationToken);
        await serverTask;

        // Assert
        string response = Encoding.ASCII.GetString(responseBytes);
        response.ShouldContain("HTTP/1.1 200 OK");
        response.ShouldContain("hello pipe");
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
}
