using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Application.Internal;

/// <summary>
/// The composed parts of the standalone database host, owned as one unit: the
/// engine, the bound listener, the wire-protocol server, and the application that
/// hosts them. Disposal tears the parts down in dependency order (application →
/// server → listener → engine); the composition root creates and disposes the
/// listener — the server only accepts from it.
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

    /// <summary>Gets the bound TCP listener the endpoint accepts from.</summary>
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
        await Listener.DisposeAsync().ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
