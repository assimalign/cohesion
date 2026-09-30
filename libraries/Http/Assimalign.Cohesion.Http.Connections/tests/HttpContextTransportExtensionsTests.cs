using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Pins <see cref="HttpContextTransportExtensions"/>'s response-started probe: the host-facing view
/// of the state the exchange control reports to response interceptors. It must turn true exactly at
/// the transport's commit points — the first streamed write or flush, or the buffered send — so a
/// host finalizing a faulted exchange knows whether a replacement response can still reach the wire.
/// </summary>
public class HttpContextTransportExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HasResponseStarted/Http1: Should turn true at the first streamed write")]
    public async Task HasResponseStarted_Http1StreamedWrite_ShouldTurnTrueAtTheFirstWrite()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /stream HTTP/1.1\r\nHost: api.test\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.HasResponseStarted.ShouldBeFalse();

        // Act
        await context.Response.Streaming.WriteAsync(Encoding.UTF8.GetBytes("partial"));

        // Assert
        context.HasResponseStarted.ShouldBeTrue();

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HasResponseStarted/Http1: Should stay false until the buffered send commits the response")]
    public async Task HasResponseStarted_Http1BufferedResponse_ShouldTurnTrueAtTheSendCommitPoint()
    {
        // Arrange — a response staged in the buffered body has not started: it can still be replaced.
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("buffered"));
        context.HasResponseStarted.ShouldBeFalse();

        // Act
        await connectionContext.SendAsync(context);

        // Assert
        context.HasResponseStarted.ShouldBeTrue();

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HasResponseStarted/Http2: Should turn true at the first streamed flush")]
    public async Task HasResponseStarted_Http2StreamedFlush_ShouldTurnTrueAtTheFirstFlush()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/stream", "https", "api.test"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.HasResponseStarted.ShouldBeFalse();

        // Act
        await context.Response.Streaming.FlushAsync();

        // Assert
        context.HasResponseStarted.ShouldBeTrue();

        await connectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HasResponseStarted: Should report false for an exchange the transport did not produce")]
    public void HasResponseStarted_ForeignContext_ShouldBeFalse()
    {
        // Arrange
        IHttpContext context = new ForeignHttpContext();

        // Act & Assert
        context.HasResponseStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HasResponseStarted: Should reject a null exchange")]
    public void HasResponseStarted_NullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => context.HasResponseStarted);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    /// <summary>
    /// An exchange from outside the transport — every member throws, proving the probe answers
    /// without touching any of them.
    /// </summary>
    private sealed class ForeignHttpContext : IHttpContext
    {
        public HttpVersion Version => throw new NotSupportedException();

        public IHttpRequest Request => throw new NotSupportedException();

        public IHttpResponse Response => throw new NotSupportedException();

        public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();

        public IHttpFeatureCollection Features => throw new NotSupportedException();

        public IDictionary<string, object?> Items => throw new NotSupportedException();

        public CancellationToken RequestCancelled => throw new NotSupportedException();

        public void Cancel() => throw new NotSupportedException();

        public Task CancelAsync() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
