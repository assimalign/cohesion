using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportPipelineBuilderTests
{
    [Fact]
    public async Task ExecuteAsync_WhenMiddlewareIsConfigured_ShouldExecuteInRegistrationOrder()
    {
        var calls = new List<string>();
        var builder = new TransportPipelineBuilder<TestContext>();

        builder.Use(async (context, next) =>
        {
            calls.Add("first-before");
            await next(context).ConfigureAwait(false);
            calls.Add("first-after");
        });

        builder.Use(async (context, next) =>
        {
            calls.Add("second-before");
            await next(context).ConfigureAwait(false);
            calls.Add("second-after");
        });

        TransportPipeline<TestContext> pipeline = builder.Build();

        await pipeline.ExecuteAsync(new TestContext());

        Assert.Equal(new[]
        {
            "first-before",
            "second-before",
            "second-after",
            "first-after"
        }, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenContextTypeDoesNotMatch_ShouldThrow()
    {
        var builder = new TransportPipelineBuilder<TestContext>();

        builder.Use((context, next) => next(context));

        ITransportPipeline pipeline = ((ITransportPipelineBuilder)builder).Build();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            pipeline.ExecuteAsync(new AnotherContext()));
    }

    [Fact]
    public async Task ExecuteAsync_WhenMiddlewareThrows_ShouldPropagateException()
    {
        var builder = new TransportPipelineBuilder<TestContext>();
        var sentinel = new InvalidOperationException("middleware boom");

        builder.Use((ctx, next) => throw sentinel);

        TransportPipeline<TestContext> pipeline = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(new TestContext()));

        Assert.Same(sentinel, thrown);
    }

    private sealed class TestContext : TransportConnectionContext
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public override EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override ITransportConnectionPipe Pipe { get; } = new TransportConnectionPipe(new System.IO.MemoryStream());

        public override CancellationToken PipelineCancelled => _cancellationTokenSource.Token;

        public new void Cancel() => _cancellationTokenSource.Cancel();
    }

    private sealed class AnotherContext : TransportConnectionContext
    {
        public override EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override ITransportConnectionPipe Pipe { get; } = new TransportConnectionPipe(new System.IO.MemoryStream());
    }
}
