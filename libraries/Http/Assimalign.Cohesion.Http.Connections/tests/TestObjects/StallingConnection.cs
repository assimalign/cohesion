using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="Connection"/> over another whose output is a <see cref="StallingPipeWriter"/>: the test
/// decides how fast the server's writes are accepted, standing in for a peer that stops reading the
/// connection (HTTP/1.1, HTTP/2) or grants no stream credit (an HTTP/3 request stream). Everything else is
/// the inner connection's, including the coded stream aborts of a multiplexed stream end.
/// </summary>
internal sealed class StallingConnection : MultiplexedStream, IMultiplexedStreamAbort
{
    private readonly Connection _inner;

    public StallingConnection(Connection inner)
    {
        _inner = inner;
        Writer = new StallingPipeWriter(inner.Output);
    }

    public string Name => IMultiplexedStreamAbort.FeatureName;

    protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

    protected override void ConfigureFeatures(IConnectionFeatureCollection features) => features.Set(this);

    /// <summary>Gets the stalling output, which the test stalls and credits.</summary>
    public StallingPipeWriter Writer { get; }

    /// <summary>Gets a value indicating whether the server aborted the connection.</summary>
    public bool IsAborted { get; private set; }

    /// <summary>Gets the code the server reset the sending direction with, or <see langword="null"/>.</summary>
    public long? WriteAbortCode { get; private set; }

    /// <inheritdoc />
    public override bool IsInputCompleted => (_inner as IMultiplexedStream)?.IsInputCompleted ?? false;

    public override ConnectionId Id => _inner.Id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    /// <inheritdoc />
    public override PipeReader Input => _inner.Input;

    /// <inheritdoc />
    public override PipeWriter Output => Writer;

    /// <inheritdoc />
    public override ConnectionDirection Direction => _inner.Direction;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    /// <inheritdoc />
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        WriteAbortCode ??= errorCode;
        IsAborted = true;
        if (_inner is IMultiplexedStream stream)
        {
            stream.Abort(errorCode, reason);
        }
        else
        {
            _inner.Abort(reason);
        }
    }

    public override void Abort(Exception? reason = null)
    {
        IsAborted = true;
        _inner.Abort(reason);
    }

    /// <inheritdoc />
    public void AbortRead(long errorCode)
    {
        (_inner as IMultiplexedStream)?.DirectionalAbort?.AbortRead(errorCode);
    }

    /// <inheritdoc />
    public void AbortWrite(long errorCode)
    {
        WriteAbortCode ??= errorCode;
        (_inner as IMultiplexedStream)?.DirectionalAbort?.AbortWrite(errorCode);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
