using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// An in-memory listener whose accepted connections throw from <see cref="Connection.DisposeAsync"/>
/// once they have released the in-memory pair, so a server session's cleanup meets a disposal that
/// fails. Clients dial <see cref="Inner"/>.
/// </summary>
internal sealed class DisposeFailingConnectionListener : ConnectionListener
{
    /// <summary>The message of the failure every accepted connection's disposal throws.</summary>
    public const string FailureMessage = "The connection failed to close.";

    public DisposeFailingConnectionListener(InMemoryConnectionListener inner)
    {
        Inner = inner;
    }

    /// <summary>Gets the in-memory listener clients dial.</summary>
    public InMemoryConnectionListener Inner { get; }

    public override EndPoint EndPoint => Inner.EndPoint;

    public override ConnectionCapabilities Capabilities => Inner.Capabilities;

    public override ValueTask BindAsync(CancellationToken cancellationToken = default) => Inner.BindAsync(cancellationToken);

    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
        => new DisposeFailingConnection(await Inner.AcceptAsync(cancellationToken).ConfigureAwait(false));

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();

    private sealed class DisposeFailingConnection : Connection
    {
        private readonly Connection _inner;

        public DisposeFailingConnection(Connection inner)
        {
            _inner = inner;
        }

        public override ConnectionId Id => _inner.Id;

        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

        public override PipeReader Input => _inner.Input;

        public override PipeWriter Output => _inner.Output;

        public override ConnectionCapabilities Capabilities => _inner.Capabilities;

        public override ConnectionState State => _inner.State;

        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

        public override void Abort(Exception? reason = null) => _inner.Abort(reason);

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(FailureMessage);
        }
    }
}
