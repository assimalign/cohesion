using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Server;

/// <summary>
/// The endpoint host service: runs a <see cref="IDatabaseServer"/>'s accept loop as a
/// pool-scheduled <see cref="BackgroundService"/> and drains it gracefully on host stop.
/// </summary>
/// <remarks>
/// This adapter lives with the server, not in <c>Database.Hosting</c>: the resource
/// hosting-isolation rule (COHRES002) forbids the hosting module from referencing any
/// same-area library except the area root, so the module cannot name
/// <see cref="IDatabaseServer"/> to compose it. The server owns its own two-phase
/// drain; this service maps that lifecycle onto the hosting execution menu, and
/// <c>Database.Hosting</c> composes the resulting <see cref="IHostService"/>.
/// </remarks>
internal sealed class DatabaseServerHostService : BackgroundService
{
    private readonly IDatabaseServer _server;

    internal DatabaseServerHostService(IDatabaseServer server)
    {
        _server = server;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Begin accepting; the server's StartAsync launches its own accept loop and
        // returns synchronously, so a synchronous start failure surfaces to the host.
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping; fall through to the graceful drain.
        }

        // Drain on the server's own shutdown budget with a fresh token — the run token
        // is already cancelled, and passing it would pre-empt the graceful drain.
        await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
