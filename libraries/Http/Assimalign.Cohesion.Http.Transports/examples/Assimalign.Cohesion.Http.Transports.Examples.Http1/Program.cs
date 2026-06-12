using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Transports;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Transports.Examples.Http1;

internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(15));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = LoopbackPortAllocator.AllocateTcpPort();
        Uri serverUri = new($"http://127.0.0.1:{port}/hello?name=http1");

        // Plain (no TLS) HTTP/1.1: a TCP connection listener is composed
        // directly into the HTTP listener. The TCP listener's capabilities
        // (reliable, ordered byte stream) satisfy the HTTP/1.1 gate.
        TcpConnectionListener tcpListener = TcpConnectionListener.Create(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        });

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp1(tcpListener);
        });

        Task serverTask = RunServerAsync(listener, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        using HttpClient client = new();
        using HttpRequestMessage request = new(ClientHttpMethod.Get, serverUri)
        {
            Version = NetHttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"HTTP/1.1 status: {(int)response.StatusCode}");
        Console.WriteLine($"HTTP/1.1 negotiated version: {response.Version}");
        Console.WriteLine(body);

        await serverTask.ConfigureAwait(false);

        return 0;
    }

    private static async Task RunServerAsync(HttpConnectionListener listener, CancellationToken cancellationToken)
    {
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            string payload = $"Hello from HTTP/1.1. Method={context.Request.Method}, Path={context.Request.Path}, Name={context.Request.Query["name"].Value}";
            byte[] buffer = Encoding.UTF8.GetBytes(payload);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
            break;
        }
    }
}
