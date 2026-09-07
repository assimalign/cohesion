using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Application.Internal;

/// <summary>
/// The composed parts of the standalone database host, owned as one unit: the
/// engine, the configured listener, the wire-protocol server, and the application
/// that hosts them. The server binds and releases the listener through the
/// application's lifecycle; composition disposal then releases the engine.
/// </summary>
internal sealed class DatabaseApplicationComposition : IAsyncDisposable
{
    internal DatabaseApplicationComposition(
        SqlDatabaseEngine engine,
        TcpConnectionListener listener,
        SqlDatabaseServer server,
        DatabaseApplication application)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Application = application;
    }

    /// <summary>Gets the SQL engine the host serves.</summary>
    internal SqlDatabaseEngine Engine { get; }

    /// <summary>Gets the TCP listener whose endpoint is acquired when the application starts.</summary>
    internal TcpConnectionListener Listener { get; }

    /// <summary>Gets the SQL model's wire-protocol server fronting the engine.</summary>
    internal SqlDatabaseServer Server { get; }

    /// <summary>Gets the hosting application composing the parts.</summary>
    internal DatabaseApplication Application { get; }

    /// <summary>
    /// Runs the application until <paramref name="cancellationToken"/> signals
    /// shutdown, then drains gracefully (endpoint first, engines last).
    /// </summary>
    /// <param name="cancellationToken">The shutdown signal.</param>
    /// <returns>A task that completes once the application has stopped.</returns>
    internal Task RunAsync(CancellationToken cancellationToken = default)
        => Application.RunAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)Application).DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
