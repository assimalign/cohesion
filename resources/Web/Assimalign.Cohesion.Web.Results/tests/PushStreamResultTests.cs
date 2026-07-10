using System;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the push-stream built-in — the thin adapter over the #769 response-streaming feature:
/// loud failure when streaming is absent, head state applied before the first write, incremental
/// writes, completion, and the no-<c>Content-Length</c> rule.
/// </summary>
public class PushStreamResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - PushStream: fails loudly when streaming is not enabled on the exchange")]
    public async Task ExecuteAsync_WithoutStreamingFeature_ThrowsNotSupported()
    {
        // Arrange — no streaming feature installed on the context.
        TestHttpContext context = new();
        IResult result = Results.PushStream((_, _) => Task.CompletedTask);

        // Act + Assert
        await Should.ThrowAsync<NotSupportedException>(() => result.ExecuteAsync(context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PushStream: applies status and content type before the first write, then completes")]
    public async Task ExecuteAsync_WithFeature_AppliesHeadBeforeFirstWriteAndCompletes()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        IResult result = Results.PushStream(
            async (feature, cancellationToken) =>
            {
                await feature.WriteAsync(Encoding.UTF8.GetBytes("chunk-1"), cancellationToken);
                await feature.WriteAsync(Encoding.UTF8.GetBytes("chunk-2"), cancellationToken);
            },
            contentType: "application/x-ndjson",
            statusCode: HttpStatusCode.Accepted);

        // Act
        await result.ExecuteAsync(context);

        // Assert — the head was fully shaped before the response started.
        streaming.ContentTypeAtFirstWrite.ShouldBe("application/x-ndjson");
        context.Response.StatusCode.Value.ShouldBe(202);
        streaming.WrittenText.ShouldBe("chunk-1chunk-2");
        streaming.CompleteCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PushStream: never sets Content-Length")]
    public async Task ExecuteAsync_StreamingBody_NeverSetsContentLength()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        IResult result = Results.PushStream(
            (feature, cancellationToken) => feature.WriteAsync(Encoding.UTF8.GetBytes("body"), cancellationToken).AsTask());

        // Act
        await result.ExecuteAsync(context);

        // Assert
        streaming.ContentLengthPresentAtFirstWrite.ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PushStream: a callback that only completes still commits an empty streamed response")]
    public async Task ExecuteAsync_EmptyCallback_StillCompletes()
    {
        // Arrange
        TestHttpContext context = new();
        TestStreamingFeature streaming = new(context.Response);
        context.Features.Set<IHttpResponseStreamingFeature>(streaming);

        // Act
        await Results.PushStream((_, _) => Task.CompletedTask).ExecuteAsync(context);

        // Assert — CompleteAsync starts the response even when nothing was written.
        streaming.HasStarted.ShouldBeTrue();
        streaming.CompleteCount.ShouldBe(1);
        streaming.WrittenText.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - PushStream: a null callback is rejected at the factory")]
    public void Factory_NullCallback_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.PushStream(null!));
        Should.Throw<ArgumentNullException>(() => TypedResults.PushStream(null!));
    }
}
