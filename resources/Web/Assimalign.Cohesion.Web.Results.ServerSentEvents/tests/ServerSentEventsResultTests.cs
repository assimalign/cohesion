using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results;
using Assimalign.Cohesion.Web.Results.ServerSentEvents.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.ServerSentEvents.Tests;

/// <summary>
/// Covers the Server-Sent Events result adapter: the grafted <c>Results.ServerSentEvents</c> /
/// <c>TypedResults.ServerSentEvents</c> factories, the event-stream head
/// (<c>text/event-stream</c> + <c>Cache-Control: no-cache</c> before the first write), per-event
/// write/flush over the streaming feature, the loud failure when streaming is absent, and the
/// no-<c>Content-Length</c> rule.
/// </summary>
public class ServerSentEventsResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Factory: the extension factories graft onto Results and TypedResults")]
    public void Factory_GraftedOntoBothFactories_CreatesResult()
    {
        // Arrange + Act — calling through the core factory names proves the static extension
        // members compose across assemblies.
        IResult result = Results.ServerSentEvents(EventsAsync());
        ServerSentEventsHttpResult typed = TypedResults.ServerSentEvents(EventsAsync());

        // Assert
        result.ShouldBeOfType<ServerSentEventsHttpResult>();
        typed.ContentType.ShouldBe(ServerSentEvent.MediaType);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: fails loudly when streaming is not enabled on the exchange")]
    public async Task ExecuteAsync_WithoutStreamingFeature_ThrowsNotSupported()
    {
        // Arrange — no streaming feature installed on the context.
        TestHttpContext context = new();
        IResult result = Results.ServerSentEvents(EventsAsync());

        // Act + Assert
        await Should.ThrowAsync<NotSupportedException>(() => result.ExecuteAsync(context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: sets the event-stream head before the first write")]
    public async Task ExecuteAsync_WithEvents_SetsHeadBeforeFirstWrite()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        // Act
        await Results.ServerSentEvents(EventsAsync("one", "two")).ExecuteAsync(context);

        // Assert — both head fields were locked in before the response started.
        streaming.ContentTypeAtFirstWrite.ShouldBe("text/event-stream");
        streaming.CacheControlAtFirstWrite.ShouldBe("no-cache");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: writes each event in wire format, flushing per event, then completes")]
    public async Task ExecuteAsync_WithEvents_WritesAndFlushesEachEventThenCompletes()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        // Act
        await Results.ServerSentEvents(EventsAsync("one", "two")).ExecuteAsync(context);

        // Assert — one write + one flush per event, then a single completion.
        streaming.WrittenText.ShouldBe("data: one\n\ndata: two\n\n");
        streaming.WriteLengths.Count.ShouldBe(2);
        streaming.FlushCount.ShouldBe(2);
        streaming.CompleteCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: never sets Content-Length")]
    public async Task ExecuteAsync_StreamingBody_NeverSetsContentLength()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        // Act
        await Results.ServerSentEvents(EventsAsync("tick")).ExecuteAsync(context);

        // Assert
        streaming.ContentLengthPresentAtFirstWrite.ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: an empty sequence still commits and completes the stream")]
    public async Task ExecuteAsync_EmptySequence_StillCompletes()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        // Act
        await Results.ServerSentEvents(EventsAsync()).ExecuteAsync(context);

        // Assert — CompleteAsync starts the response even when no event was produced.
        streaming.HasStarted.ShouldBeTrue();
        streaming.CompleteCount.ShouldBe(1);
        streaming.WrittenText.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Execute: cancellation stops the enumeration")]
    public async Task ExecuteAsync_CancelledToken_StopsEnumeration()
    {
        // Arrange — the token is already cancelled before execution starts.
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        // Act + Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => Results.ServerSentEvents(SlowEventsAsync()).ExecuteAsync(context, cancelled.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results.ServerSentEvents] - Factory: a null sequence is rejected")]
    public void Factory_NullSequence_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.ServerSentEvents(null!));
        Should.Throw<ArgumentNullException>(() => TypedResults.ServerSentEvents(null!));
    }

    private static async IAsyncEnumerable<ServerSentEvent> EventsAsync(params string[] payloads)
    {
        foreach (string payload in payloads)
        {
            yield return ServerSentEvent.Message(payload);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ServerSentEvent> SlowEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            yield return ServerSentEvent.Message("tick");
        }
    }
}
