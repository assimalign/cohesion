using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

internal sealed class FeatureConnectionProbe
{
    private int _innerReads;
    private int _configurationCount;

    public IConnectionFeatureCollection? Inner { get; set; }

    public Action<IConnectionFeatureCollection>? Configure { get; set; }

    public int InnerReads => Volatile.Read(ref _innerReads);

    public int ConfigurationCount => Volatile.Read(ref _configurationCount);

    public IConnectionFeatureCollection? ReadInner()
    {
        Interlocked.Increment(ref _innerReads);
        return Inner;
    }

    public void ConfigureFeatures(IConnectionFeatureCollection features)
    {
        Interlocked.Increment(ref _configurationCount);
        Configure?.Invoke(features);
    }
}

internal sealed class FeatureProbeConnection : Connection
{
    private readonly FeatureConnectionProbe _probe;

    public FeatureProbeConnection(FeatureConnectionProbe probe)
    {
        _probe = probe;
    }

    public override ConnectionId Id => ConnectionId.Empty;
    public override EndPoint? LocalEndPoint => null;
    public override EndPoint? RemoteEndPoint => null;
    public override PipeReader Input => throw new NotSupportedException();
    public override PipeWriter Output => throw new NotSupportedException();
    public override ConnectionCapabilities Capabilities => default;
    public override ConnectionState State => ConnectionState.Open;
    public override CancellationToken ConnectionClosed => CancellationToken.None;
    protected override IConnectionFeatureCollection? InnerFeatures => _probe.ReadInner();
    protected override void ConfigureFeatures(IConnectionFeatureCollection features) => _probe.ConfigureFeatures(features);
    public override void Abort(Exception? reason = null) { }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FeatureProbeMultiplexedConnection : MultiplexedConnection
{
    private readonly FeatureConnectionProbe _probe;
    private readonly CancellationTokenSource _closedSource = new();
    private ConnectionState _state = ConnectionState.Open;

    public FeatureProbeMultiplexedConnection(FeatureConnectionProbe probe)
    {
        _probe = probe;
    }

    public override ConnectionId Id => ConnectionId.Empty;
    public override EndPoint? LocalEndPoint => null;
    public override EndPoint? RemoteEndPoint => null;
    public override ConnectionCapabilities Capabilities => default;
    public override ConnectionState State => _state;
    public override CancellationToken ConnectionClosed => _closedSource.Token;
    protected override IConnectionFeatureCollection? InnerFeatures => _probe.ReadInner();
    protected override void ConfigureFeatures(IConnectionFeatureCollection features) => _probe.ConfigureFeatures(features);
    public override void Abort(Exception? reason = null)
    {
        _state = ConnectionState.Aborted;
        _closedSource.Cancel();
    }
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        Abort(reason);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class FeatureProbeDatagramConnection : DatagramConnection
{
    private readonly FeatureConnectionProbe _probe;

    public FeatureProbeDatagramConnection(FeatureConnectionProbe probe)
    {
        _probe = probe;
    }

    public override EndPoint LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 0);
    public override EndPoint? RemoteEndPoint => null;
    public override ConnectionCapabilities Capabilities => default;
    protected override IConnectionFeatureCollection? InnerFeatures => _probe.ReadInner();
    protected override void ConfigureFeatures(IConnectionFeatureCollection features) => _probe.ConfigureFeatures(features);
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public override ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
