using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A stream <see cref="DuplexConnectionListener"/> double whose every accept throws the supplied exception.
/// </summary>
internal sealed class ThrowingConnectionListener : DuplexConnectionListener
{
    private readonly Exception _exception;

    public ThrowingConnectionListener(Exception exception)
    {
        _exception = exception;
    }

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 17002);

    public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities;

    protected override ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken)
        => ValueTask.FromException<IDuplexConnection>(_exception);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
