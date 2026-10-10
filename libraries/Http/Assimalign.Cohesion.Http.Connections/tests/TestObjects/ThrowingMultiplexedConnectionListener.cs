using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="MultiplexedConnectionListener"/> double whose every accept throws the supplied exception.
/// </summary>
internal sealed class ThrowingMultiplexedConnectionListener : MultiplexedConnectionListener
{
    private readonly Exception _exception;

    public ThrowingMultiplexedConnectionListener(Exception exception)
    {
        _exception = exception;
    }

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 17003);

    public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true,
        Security = ConnectionSecurity.Tls
    };

    public override ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<MultiplexedConnection>(_exception);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
