using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The connection <see cref="AmqpTestTlsLayer"/> returns: it runs over the same pipes as the connection it wraps,
/// reports <see cref="ConnectionSecurity.Tls"/>, adds the feature named <see cref="FeatureName"/> to the wrapped
/// connection's, and disposes the wrapped connection with itself, as a TLS connection does.
/// </summary>
internal sealed class TestSecuredConnection : DuplexConnection
{
    /// <summary>
    /// The name of the feature the connection adds, so a test can tell it runs over what the layer returned.
    /// </summary>
    public const string FeatureName = "test.tls";

    private readonly IDuplexConnection _inner;
    private volatile bool _isDisposed;

    public TestSecuredConnection(IDuplexConnection inner)
    {
        _inner = inner;
    }

    public bool IsDisposed => _isDisposed;

    public override ConnectionId Id => _inner.Id;

    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public override ConnectionCapabilities Capabilities => _inner.Capabilities with { Security = ConnectionSecurity.Tls };

    public override ConnectionState State => _inner.State;

    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public override PipeReader Input => _inner.Input;

    public override PipeWriter Output => _inner.Output;

    protected override IConnectionFeatureCollection? InnerFeatures => _inner.Features;

    public override void Abort(Exception? reason = null)
    {
        _inner.Abort(reason);
    }

    public override ValueTask DisposeAsync()
    {
        _isDisposed = true;
        return _inner.DisposeAsync();
    }

    protected override void ConfigureFeatures(IConnectionFeatureCollection features)
    {
        features.Set(new TestConnectionFeature(FeatureName));
    }
}
