using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportExtensionsTests
{
    [Fact]
    public async Task EnumerateAsync_WhenCanceled_ShouldStopYieldingConnections()
    {
        var transport = new TestTransport();
        using var cancellationTokenSource = new CancellationTokenSource();

        int count = 0;

        await foreach (ITransportConnection connection in transport.EnumerateAsync(cancellationTokenSource.Token))
        {
            count++;

            if (count == 3)
            {
                cancellationTokenSource.Cancel();
            }
        }

        Assert.Equal(3, count);
        Assert.Equal(3, transport.InitializeCalls);
    }

    [Fact]
    public void AddItem_WhenValueIsStored_ShouldBeReturnedByGetItem()
    {
        var context = new TestContext();

        context.AddItem("key", "value");

        string? value = context.GetItem<string>("key");

        Assert.Equal("value", value);
    }

    [Fact]
    public void GetItem_WhenKeyDoesNotExist_ShouldReturnNull()
    {
        var context = new TestContext();

        string? value = context.GetItem<string>("missing");

        Assert.Null(value);
    }

    [Fact]
    public void IsOpen_WhenStateIsOpen_ShouldReturnTrue()
    {
        var connection = new TestConnection(ConnectionState.Open);

        bool isOpen = connection.IsOpen();

        Assert.True(isOpen);
    }

    [Fact]
    public async Task WriteAsync_WhenPayloadIsProvided_ShouldWriteToPipeOutput()
    {
        var pipe = new TestPipe();
        byte[] payload = Encoding.UTF8.GetBytes("hello");

        _ = await pipe.WriteAsync(payload);

        ReadResult result = await pipe.Input.ReadAsync();

        Assert.Equal(payload, result.Buffer.ToArray());

        pipe.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task PeekAsync_WhenDataExists_ShouldNotConsumePipeBuffer()
    {
        var pipe = new TestPipe();
        byte[] payload = Encoding.UTF8.GetBytes("peek");

        await pipe.Output.WriteAsync(payload);

        ReadResult peekResult = await pipe.PeekAsync();
        Assert.Equal(payload, peekResult.Buffer.ToArray());

        ReadResult readResult = await pipe.Input.ReadAsync();
        Assert.Equal(payload, readResult.Buffer.ToArray());

        pipe.Input.AdvanceTo(readResult.Buffer.End);
    }

    [Fact]
    public async Task ReadAsync_WhenDataExists_ShouldConsumePipeBuffer()
    {
        var pipe = new TestPipe();
        byte[] payload = Encoding.UTF8.GetBytes("read");

        await pipe.Output.WriteAsync(payload);
        await pipe.Output.CompleteAsync();

        ReadResult readResult = await pipe.ReadAsync();
        Assert.Equal(payload, readResult.Buffer.ToArray());

        ReadResult secondRead = await pipe.Input.ReadAsync();
        Assert.Equal(0, secondRead.Buffer.Length);
        Assert.True(secondRead.IsCompleted);

        pipe.Input.AdvanceTo(secondRead.Buffer.End);
    }

    [Fact]
    public async Task PeekAsync_WhenCanceled_ShouldHonorCancellationToken()
    {
        var pipe = new TestPipe();
        using var cancellationTokenSource = new CancellationTokenSource();

        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await pipe.PeekAsync(cancellationTokenSource.Token);
        });
    }

    [Fact]
    public async Task ReadAsync_WhenCanceled_ShouldHonorCancellationToken()
    {
        var pipe = new TestPipe();
        using var cancellationTokenSource = new CancellationTokenSource();

        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await pipe.ReadAsync(cancellationTokenSource.Token);
        });
    }

    private sealed class TestTransport : ITransport
    {
        private int _initializeCalls;

        public TransportId Id { get; } = TransportId.New();
        public TransportKind Kind { get; } = TransportKind.Client;
        public TransportProtocol Protocol { get; } = TransportProtocol.Tcp;
        public int InitializeCalls => _initializeCalls;

        public ITransportConnection Initialize()
        {
            Interlocked.Increment(ref _initializeCalls);
            return new TestConnection(ConnectionState.Open);
        }

        public Task<ITransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _initializeCalls);
            return Task.FromResult<ITransportConnection>(new TestConnection(ConnectionState.Open));
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestConnection : ITransportConnection
    {
        public TestConnection(ConnectionState state)
        {
            State = state;
        }

        public ConnectionId Id { get; } = ConnectionId.New();
        public TransportId TransportId { get; } = TransportId.New();
        public TransportProtocol Protocol { get; } = TransportProtocol.Tcp;
        public ConnectionState State { get; }

        public void Abort() { }

        public ValueTask AbortAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestContext : ITransportConnectionContext
    {
        public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public ITransportConnectionPipe Pipe { get; } = new TestPipe();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    }

    private sealed class TestPipe : ITransportConnectionPipe
    {
        private readonly Pipe _pipe;

        public TestPipe()
        {
            _pipe = new Pipe();
        }

        public PipeReader Input => _pipe.Reader;
        public PipeWriter Output => _pipe.Writer;

        public Stream GetStream() => new PipeStream(this);
    }
}
