using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Examples.Sse;

/// <summary>
/// End-to-end Server-Sent Events sample over plain HTTP/1.1 on localhost. It composes
/// three decoupled pieces: the <c>Assimalign.Cohesion.Http.Connections</c> transport
/// (the streaming write path), the <c>Assimalign.Cohesion.Http</c> streaming feature
/// (<see cref="IHttpResponseStreamingFeature"/>), and the
/// <c>Assimalign.Cohesion.Http.ServerSentEvents</c> primitives (<see cref="ServerSentEvent"/>
/// + <c>WriteEventAsync</c>). The server commits a <c>text/event-stream</c> head, then
/// streams several flushed events plus a keep-alive comment; the client reads and prints
/// each event as it arrives — before the response completes.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(15));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = LoopbackPortAllocator.AllocateTcpPort();
        Uri serverUri = new($"http://127.0.0.1:{port}/events");

        // Plain (no TLS) HTTP/1.1 — a TCP listener composed directly into the HTTP
        // listener. Server-Sent Events ride plain HTTP, so no TLS is required.
        TcpConnectionListener tcpListener = TcpConnectionListener.Create(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        });

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp1(tcpListener);

            // Opt into incremental response streaming by registering its response interceptor. The
            // transport exposes its raw response body sink to the feature package through this seam;
            // it has no streaming or SSE dependency of its own.
            options.ResponseInterceptors.Add(HttpResponseStreaming.CreateInterceptor());
        });

        Task serverTask = RunServerAsync(listener, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        using HttpClient client = new();
        using HttpRequestMessage request = new(ClientHttpMethod.Get, serverUri)
        {
            Version = NetHttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        // ResponseHeadersRead returns as soon as the head is committed, so the
        // client can read events incrementally instead of buffering the whole body.
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"SSE status: {(int)response.StatusCode}");
        Console.WriteLine($"SSE content-type: {response.Content.Headers.ContentType}");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            // The blank line between events is the dispatch boundary; print only the
            // field lines so the incremental arrival of each event is visible.
            if (line.Length > 0)
            {
                Console.WriteLine($"[event-stream] {line}");
            }
        }

        await serverTask.ConfigureAwait(false);

        return 0;
    }

    private static async Task RunServerAsync(HttpConnectionListener listener, CancellationToken cancellationToken)
    {
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = ServerSentEvent.MediaType;
            context.Response.Headers[HttpHeaderKey.CacheControl] = "no-cache";

            // The streaming feature commits the head on the first write and frames
            // each event as its own chunked write, flushed through to the client.
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;

            for (int tick = 1; tick <= 5; tick++)
            {
                ServerSentEvent @event = new($"server time tick {tick}")
                {
                    EventType = "tick",
                    Id = tick.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                await streaming.WriteEventAsync(@event, cancellationToken).ConfigureAwait(false);

                // Space the events out so the incremental streaming is observable.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }

            // A comment-only heartbeat keeps an otherwise-idle stream (and any
            // intermediaries) alive; the client ignores it.
            await streaming.WriteKeepAliveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // The connection loop's SendAsync finalizes the response — for a
            // streamed response that means emitting the terminating zero-length
            // chunk rather than writing a buffered body.
            await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
            break;
        }
    }
}
