using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class Http2TransportTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should parse request headers and write response frames")]
    public async Task Http2_OnRequest_ShouldParseHeadersAndWriteResponseFrames()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/items?id=7", "https", "api.test");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert request
        httpContext.Version.ShouldBe(HttpVersion.Http20);
        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/items");
        httpContext.Request.Query["id"].Value.ShouldBe("7");
        httpContext.Request.Host.Value.ShouldBe("api.test");
        httpContext.Request.Scheme.ShouldBe(HttpScheme.Https);

        // Act response
        httpContext.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        httpContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}"));
        await httpConnectionContext.SendAsync(httpContext);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(await transportContext.ReadOutputAsync());

        // Assert response
        frames.Count.ShouldBeGreaterThanOrEqualTo(4);
        frames[0].FrameType.ShouldBe(4);
        frames[1].FrameType.ShouldBe(4);
        frames[2].FrameType.ShouldBe(1);
        frames[3].FrameType.ShouldBe(0);

        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[2].Payload);
        headers[":status"].ShouldBe("200");
        headers["content-type"].ShouldBe("application/json");
        headers["content-length"].ShouldBe("11");
        Encoding.UTF8.GetString(frames[3].Payload).ShouldBe("{\"ok\":true}");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should yield multiple streams in sequence")]
    public async Task Http2_OnMultipleStreams_ShouldYieldRequestsInSequence()
    {
        // Arrange
        byte[] first = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test");
        byte[] second = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/two", "https", "api.test");
        byte[] secondWithoutPreface = new byte[second.Length - 24];
        Array.Copy(second, 24, secondWithoutPreface, 0, secondWithoutPreface.Length);
        byte[] payload = Combine(first, secondWithoutPreface);
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        string firstPath = enumerator.Current.Request.Path.Value;
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        string secondPath = enumerator.Current.Request.Path.Value;

        // Assert
        firstPath.ShouldBe("/one");
        secondPath.ShouldBe("/two");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should keep a localhost connection open for sequential requests")]
    public async Task Http2_OnSequentialLocalhostRequests_ShouldKeepConnectionOpen()
    {
        // Arrange
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = GetAvailablePort();
        List<string> observedPaths = new();

        // Synchronizes the server task's connection teardown with the client side.
        // Without this, the server breaks out of the receive loop as soon as it has
        // dispatched both responses; the resulting `await using IHttpConnection`
        // disposal closes the H2 connection and can race the client's final frame
        // read on slow runners (predominantly macOS), surfacing as
        // "response ended prematurely". The signal lets the test code release the
        // server only after both response bodies have been fully consumed.
        TaskCompletionSource clientFinishedReading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp2(transport =>
            {
                transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            });
        });

        Task serverTask = Task.Run(async () =>
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken);
            IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);

            await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken))
            {
                try
                {
                    observedPaths.Add(context.Request.Path.Value);

                    byte[] body = Encoding.UTF8.GetBytes(context.Request.Path.Value);
                    context.Response.StatusCode = HttpStatusCode.Ok;
                    context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
                    await context.Response.Body.WriteAsync(body, 0, body.Length, cancellationToken);
                    await connectionContext.SendAsync(context, cancellationToken);
                }
                finally
                {
                    await context.DisposeAsync();
                }

                if (observedPaths.Count >= 2)
                {
                    // Hold the receive loop open until the client confirms both
                    // responses were fully read. This blocks the `await using
                    // IHttpConnection` disposal until the H2 conversation has
                    // wound down on the client side.
                    await clientFinishedReading.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }, cancellationToken);

        await Task.Delay(200, cancellationToken);

        using SocketsHttpHandler handler = new();
        using HttpClient client = new(handler);

        // Act
        string firstBody = await SendHttp2RequestAsync(client, new Uri($"http://127.0.0.1:{port}/one"), cancellationToken);
        string secondBody = await SendHttp2RequestAsync(client, new Uri($"http://127.0.0.1:{port}/two"), cancellationToken);

        // Both response bodies fully consumed — release the server task so it can
        // tear down the connection cleanly.
        clientFinishedReading.SetResult();
        await serverTask;

        // Assert
        firstBody.ShouldBe("/one");
        secondBody.ShouldBe("/two");
        observedPaths.Count.ShouldBe(2);
        observedPaths[0].ShouldBe("/one");
        observedPaths[1].ShouldBe("/two");
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static async Task<string> SendHttp2RequestAsync(HttpClient client, Uri requestUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, requestUri)
        {
            Version = System.Net.HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.Version.ShouldBe(System.Net.HttpVersion.Version20);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
