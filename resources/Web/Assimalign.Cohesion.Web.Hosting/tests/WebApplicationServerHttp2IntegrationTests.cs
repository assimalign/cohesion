using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Per-stream dispatch (#1049) end to end: prior-knowledge HTTP/2 over the in-memory transport
/// through <see cref="WebApplicationTestFactory"/>, a real .NET client multiplexing its requests as
/// streams of one connection, and the real server receive loop. Every test pins that its requests
/// shared one connection, because the properties under test — concurrency and per-stream fault
/// isolation — are only meaningful between siblings.
/// </summary>
public class WebApplicationServerHttp2IntegrationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Http2: Streams on one connection should run concurrently (the first completes only after the second)")]
    public async Task Http2_ConcurrentStreams_ShouldCompleteTheFirstOnlyAfterTheSecond()
    {
        // Arrange — /first parks until /second's handler has finished. Serving the connection's
        // streams one at a time would never start /second, and the test would time out.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string?> connections = new();

        await using WebApplicationTestFactory factory = CreateHttp2Factory();

        factory.Application.Use(async (context, next) =>
        {
            connections.Enqueue(context.ConnectionInfo.RemoteEndPoint?.ToString());
            string path = context.Request.Path.ToString();

            if (path == "/first")
            {
                firstEntered.TrySetResult();
                await secondCompleted.Task.WaitAsync(cancellationToken);
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(path), cancellationToken);

            if (path == "/second")
            {
                secondCompleted.TrySetResult();
            }
        });

        using HttpClient client = factory.CreateClient();

        // Act
        Task<HttpResponseMessage> first = client.GetAsync("/first", cancellationToken);
        await firstEntered.Task.WaitAsync(cancellationToken);
        first.IsCompleted.ShouldBeFalse();

        using HttpResponseMessage second = await client.GetAsync("/second", cancellationToken);
        using HttpResponseMessage completedFirst = await first.WaitAsync(cancellationToken);

        // Assert
        second.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("/second");
        completedFirst.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await completedFirst.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("/first");
        completedFirst.Version.ShouldBe(NetHttpVersion.Version20);
        connections.Count.ShouldBe(2);
        connections.Distinct().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Http2: A faulting stream should be answered with 500 while a sibling stream on the same connection succeeds")]
    public async Task Http2_FaultingStream_ShouldAnswer500WhileTheSiblingSucceeds()
    {
        // Arrange — /sibling is still in flight on the connection when /faulty throws.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource siblingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSibling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string?> connections = new();

        await using WebApplicationTestFactory factory = CreateHttp2Factory();

        factory.Application.Use(async (context, next) =>
        {
            connections.Enqueue(context.ConnectionInfo.RemoteEndPoint?.ToString());

            if (context.Request.Path.ToString() == "/faulty")
            {
                context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
                throw new InvalidOperationException("Deliberate stream fault.");
            }

            siblingEntered.TrySetResult();
            await releaseSibling.Task.WaitAsync(cancellationToken);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("sibling"), cancellationToken);
        });

        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> sibling = client.GetAsync("/sibling", cancellationToken);
        await siblingEntered.Task.WaitAsync(cancellationToken);

        // Act
        using HttpResponseMessage faulty = await client.GetAsync("/faulty", cancellationToken);

        // Assert — the faulted stream got a bare 500; its sibling was not torn down with it.
        faulty.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        faulty.Content.Headers.ContentType.ShouldBeNull();
        (await faulty.Content.ReadAsByteArrayAsync(cancellationToken)).ShouldBeEmpty();
        sibling.IsCompleted.ShouldBeFalse();

        releaseSibling.TrySetResult();
        using HttpResponseMessage completedSibling = await sibling.WaitAsync(cancellationToken);
        completedSibling.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await completedSibling.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("sibling");
        connections.Distinct().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Http2: A stream that faults after its response started should be reset while a sibling stream succeeds")]
    public async Task Http2_FaultAfterResponseStarted_ShouldResetOnlyThatStream()
    {
        // Arrange — /stream commits its head and part of its body through the streaming feature,
        // then throws. A replacement 500 can no longer reach the wire, and finalizing the stream would
        // hand the client a truncated body as if it were whole, so the stream must be reset instead.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource siblingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSibling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string?> connections = new();

        await using WebApplicationTestFactory factory = CreateHttp2Factory();
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));

        factory.Application.Use(async (context, next) =>
        {
            connections.Enqueue(context.ConnectionInfo.RemoteEndPoint?.ToString());

            if (context.Request.Path.ToString() == "/stream")
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                IHttpResponseStreamingFeature streaming = context.Response.Streaming;
                await streaming.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
                await streaming.FlushAsync(cancellationToken);

                throw new InvalidOperationException("Deliberate fault after the response started.");
            }

            siblingEntered.TrySetResult();
            await releaseSibling.Task.WaitAsync(cancellationToken);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        });

        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> sibling = client.GetAsync("/sibling", cancellationToken);
        await siblingEntered.Task.WaitAsync(cancellationToken);

        // Act
        using HttpResponseMessage streamed = await client.GetAsync(
            "/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Assert — the committed head arrived, but the body ends in a reset, not a clean end of stream.
        streamed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Exception bodyFailure = await Should.ThrowAsync<Exception>(
            () => streamed.Content.ReadAsStringAsync(cancellationToken));
        (bodyFailure is HttpRequestException or IOException).ShouldBeTrue(bodyFailure.ToString());

        releaseSibling.TrySetResult();
        using HttpResponseMessage completedSibling = await sibling.WaitAsync(cancellationToken);
        completedSibling.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        connections.Distinct().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Http2: StopAsync should wait for an in-flight stream (graceful drain)")]
    public async Task Http2_StopAsync_ShouldWaitForAnInFlightStream()
    {
        // Arrange — the stream parks on a test-owned gate that ignores the server's cancellation, so
        // the only way StopAsync can complete is by waiting for it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WebApplicationTestFactory factory = CreateHttp2Factory();

        factory.Application.Use(async (context, next) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        });

        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> request = client.GetAsync("/parked", cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        // Act
        Task stopTask = factory.StopAsync(CancellationToken.None);

        await Task.Delay(100, cancellationToken);
        stopTask.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await stopTask.WaitAsync(cancellationToken);

        // Assert — the client's request completes rather than hanging. Whether it observes a
        // response or a reset stream is drain detail: the shutdown token governs the post-pipeline
        // send (see Web.Hosting docs/DESIGN.md, "Stop semantics").
        try
        {
            (await request.WaitAsync(cancellationToken)).Dispose();
        }
        catch (HttpRequestException)
        {
            // The drained stream was reset or its connection closed before a response was delivered.
        }
    }

    private static WebApplicationTestFactory CreateHttp2Factory()
    {
        return new WebApplicationTestFactory(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });
    }
}
