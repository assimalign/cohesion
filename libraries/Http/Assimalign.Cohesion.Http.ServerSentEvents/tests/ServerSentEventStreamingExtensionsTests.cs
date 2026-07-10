using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.ServerSentEvents.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests;

public class ServerSentEventStreamingExtensionsTests
{
    // ------------------------------------------------------------ bridge unit tests

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Bridge: WriteEventAsync serializes the event and flushes")]
    public async Task WriteEventAsync_ShouldSerializeAndFlush()
    {
        RecordingStreamingFeature feature = new();

        await feature.WriteEventAsync(new ServerSentEvent("hello") { EventType = "greeting", Id = "1" });

        Encoding.UTF8.GetString(feature.WrittenBytes).ShouldBe("event: greeting\nid: 1\ndata: hello\n\n");
        feature.FlushCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Bridge: WriteKeepAliveAsync writes a comment heartbeat and flushes")]
    public async Task WriteKeepAliveAsync_ShouldWriteCommentAndFlush()
    {
        RecordingStreamingFeature feature = new();

        await feature.WriteKeepAliveAsync();

        Encoding.UTF8.GetString(feature.WrittenBytes).ShouldBe(": keep-alive\n\n");
        feature.FlushCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Bridge: WriteEventAsync throws on a null feature")]
    public async Task WriteEventAsync_OnNullFeature_ShouldThrow()
    {
        IHttpResponseStreamingFeature feature = null!;

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await feature.WriteEventAsync(ServerSentEvent.Message("x")));
    }

    // ------------------------------------------------ transport-backed round-trip

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Transport: Should stream SSE events over the HTTP/1.1 streaming write path")]
    public async Task ServerSentEvents_OverHttp1_ShouldStreamEventsBeforeCompletion()
    {
        await using InMemoryHttp1Loopback loopback = new("GET /events HTTP/1.1\r\nHost: api.test\r\n\r\n");
        IHttpContext context = await loopback.ReadRequestAsync();

        context.Response.Headers[HttpHeaderKey.ContentType] = ServerSentEvent.MediaType;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-cache";
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        // First event — observed before the response is completed.
        await streaming.WriteEventAsync(new ServerSentEvent("hello") { EventType = "greeting", Id = "1" });
        string firstObservation = Encoding.UTF8.GetString(await loopback.ReadResponseAsync());

        // Head committed at first flush, then the SSE fields ride inside a chunk.
        firstObservation.ShouldContain("HTTP/1.1 200 OK");
        firstObservation.ShouldContain("Content-Type: text/event-stream");
        firstObservation.ShouldContain("Transfer-Encoding: chunked", Case.Insensitive);
        firstObservation.ShouldContain("event: greeting\nid: 1\ndata: hello\n\n");

        // Keep-alive heartbeat — a comment the client ignores.
        await streaming.WriteKeepAliveAsync();
        string keepAlive = Encoding.UTF8.GetString(await loopback.ReadResponseAsync());
        keepAlive.ShouldContain(": keep-alive\n\n");

        // Finalizing emits the terminating chunk.
        await loopback.FinalizeResponseAsync(context);
        string completion = Encoding.UTF8.GetString(await loopback.ReadResponseAsync());
        completion.ShouldContain("0\r\n\r\n");

        await context.DisposeAsync();
    }

    /// <summary>
    /// An <see cref="IHttpResponseStreamingFeature"/> that records what the SSE bridge writes, so the
    /// extension methods can be asserted without a live transport.
    /// </summary>
    private sealed class RecordingStreamingFeature : IHttpResponseStreamingFeature
    {
        private readonly List<byte> _written = new();

        public int FlushCount { get; private set; }
        public byte[] WrittenBytes => _written.ToArray();

        public string Name => "Assimalign.Cohesion.Http.ResponseStreaming";
        public bool HasStarted { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            HasStarted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            HasStarted = true;
            _written.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            HasStarted = true;
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
