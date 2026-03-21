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
        var builder = new TransportPipelineBuilder<TestConnection, TestContext>();

        builder.Use(async (connection, context, next, cancellationToken) =>
        {
            calls.Add("first-before");
            await next(connection, context, cancellationToken).ConfigureAwait(false);
            calls.Add("first-after");
        });

        builder.Use(async (connection, context, next, cancellationToken) =>
        {
            calls.Add("second-before");
            await next(connection, context, cancellationToken).ConfigureAwait(false);
            calls.Add("second-after");
        });

        ITransportPipeline pipeline = ((ITransportPipelineBuilder)builder).Build();

        await pipeline.ExecuteAsync(new TestConnection(), new TestContext());

        Assert.Equal(new[]
        {
            "first-before",
            "second-before",
            "second-after",
            "first-after"
        }, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConnectionTypeDoesNotMatch_ShouldSkipTypedMiddleware()
    {
        bool invoked = false;
        var builder = new TransportPipelineBuilder<TestConnection, TestContext>();

        builder.Use((connection, context, next, cancellationToken) =>
        {
            invoked = true;
            return next(connection, context, cancellationToken);
        });

        ITransportPipeline pipeline = ((ITransportPipelineBuilder)builder).Build();

        await pipeline.ExecuteAsync(new AnotherConnection(), new TestContext());

        Assert.False(invoked);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationTokenIsProvided_ShouldFlowThroughMiddlewareChain()
    {
        var observedTokens = new List<CancellationToken>();
        var builder = new TransportPipelineBuilder<TestConnection, TestContext>();
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        builder.Use(async (connection, context, next, token) =>
        {
            observedTokens.Add(token);
            await next(connection, context, token).ConfigureAwait(false);
        });

        builder.Use((connection, context, next, token) =>
        {
            observedTokens.Add(token);
            return Task.CompletedTask;
        });

        ITransportPipeline pipeline = ((ITransportPipelineBuilder)builder).Build();

        await pipeline.ExecuteAsync(new TestConnection(), new TestContext(), cancellationToken);

        Assert.Equal(2, observedTokens.Count);
        Assert.All(observedTokens, token => Assert.Equal(cancellationToken, token));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationTokenIsCanceled_ShouldAllowMiddlewareToObserveCancellation()
    {
        var builder = new TransportPipelineBuilder<TestConnection, TestContext>();
        using var cancellationTokenSource = new CancellationTokenSource();

        cancellationTokenSource.Cancel();

        builder.Use((connection, context, next, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return next(connection, context, cancellationToken);
        });

        ITransportPipeline pipeline = ((ITransportPipelineBuilder)builder).Build();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pipeline.ExecuteAsync(new TestConnection(), new TestContext(), cancellationTokenSource.Token));
    }

    private sealed class TestConnection : ITransportConnection
    {
        public ConnectionId Id { get; } = ConnectionId.New();
        public TransportId TransportId { get; } = TransportId.New();
        public TransportProtocol Protocol { get; } = TransportProtocol.Tcp;
        public ConnectionState State { get; } = ConnectionState.Open;
        public void Abort() { }
        public ValueTask AbortAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AnotherConnection : ITransportConnection
    {
        public ConnectionId Id { get; } = ConnectionId.New();
        public TransportId TransportId { get; } = TransportId.New();
        public TransportProtocol Protocol { get; } = TransportProtocol.Tcp;
        public ConnectionState State { get; } = ConnectionState.Open;
        public void Abort() { }
        public ValueTask AbortAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestContext : ITransportConnectionContext
    {
        public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public ITransportConnectionPipe Pipe { get; } = new TransportConnectionPipe(new System.IO.MemoryStream());
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    }
}
