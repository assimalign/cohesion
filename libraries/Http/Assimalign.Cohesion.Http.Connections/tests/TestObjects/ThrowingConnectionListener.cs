using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A stream <see cref="ConnectionListener"/> double whose every accept throws the supplied exception.
/// </summary>
internal sealed class ThrowingConnectionListener : ConnectionListener
{
    private readonly Exception _exception;

    public ThrowingConnectionListener(Exception exception)
    {
        _exception = exception;
    }

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 17002);

    public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities;

    public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<Connection>(_exception);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
