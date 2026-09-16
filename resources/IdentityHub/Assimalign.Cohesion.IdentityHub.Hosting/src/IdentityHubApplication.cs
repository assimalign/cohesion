using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Hosts an IdentityHub application and its ordered service lifecycle.
/// </summary>
public sealed class IdentityHubApplication : Host<IdentityHubApplicationContext>, IIdentityHubApplication
{
    private readonly IdentityHubApplicationContext _context;

    internal IdentityHubApplication(
        IdentityHubApplicationOptions options,
        IdentityHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override IdentityHubApplicationContext Context => _context;

    /// <summary>
    /// Runs the application until shutdown, including a clean start and stop for an already-cancelled token.
    /// </summary>
    /// <param name="cancellationToken">The token that requests application shutdown.</param>
    /// <returns>A task that completes after the application stops.</returns>
    /// <exception cref="ObjectDisposedException">The application has been disposed.</exception>
    public new async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await ((IHost)this).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IHost)this).StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await base.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    IIdentityHubApplicationContext IIdentityHubApplication.Context => _context;

    Task IIdentityHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IIdentityHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for an identity hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the identity hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IdentityHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(IdentityHubApplication).Assembly;
        return new IdentityHubApplicationBuilder(args, resourceAssembly);
    }

    internal static IdentityHubApplicationBuilder CreateBuilder(
        string[] args,
        Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new IdentityHubApplicationBuilder(args, resourceAssembly);
    }
}
