using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Runs a Web server as a host service without taking ownership of the server.
/// </summary>
/// <remarks>
/// The caller or service provider retains disposal ownership. Each wrapper has its own service identity.
/// </remarks>
public sealed class WebApplicationServerHostService : IHostService
{
    /// <summary>Creates a non-owning host service for a server.</summary>
    /// <param name="server">The server whose lifecycle is forwarded.</param>
    /// <exception cref="ArgumentNullException">The server is null.</exception>
    public WebApplicationServerHostService(IWebApplicationServer server)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>Gets the wrapped server.</summary>
    public IWebApplicationServer Server { get; }

    /// <summary>Gets this wrapper's host service identity.</summary>
    public ServiceId Id { get; } = ServiceId.New();

    /// <summary>Binds and starts the wrapped server.</summary>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>The server startup task.</returns>
    /// <exception cref="HostStartupException">The default server could not bind its listener.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Server is WebApplicationServer &&
            exception is not HostStartupException &&
            (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            // This hosting boundary translates the default engine's bind failure. Custom
            // servers forward unchanged, and the engine requires no Hosting reference.
            throw new HostStartupException(ErrorMessages.ApplicationServerBindFailed, exception);
        }
    }

    /// <summary>Stops and drains the wrapped server.</summary>
    /// <param name="cancellationToken">The graceful shutdown budget.</param>
    /// <returns>The server shutdown task.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Server.StopAsync(cancellationToken);
    }
}
