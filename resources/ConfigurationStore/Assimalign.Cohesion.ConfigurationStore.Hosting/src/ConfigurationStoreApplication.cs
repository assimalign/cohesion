using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

/// <summary>
/// Hosts a ConfigurationStore application and its ordered service lifecycle.
/// </summary>
public sealed class ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>, IConfigurationStoreApplication
{
    private readonly ConfigurationStoreApplicationContext _context;

    internal ConfigurationStoreApplication(
        ConfigurationStoreApplicationOptions options,
        ConfigurationStoreApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override ConfigurationStoreApplicationContext Context => _context;

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

    IConfigurationStoreApplicationContext IConfigurationStoreApplication.Context => _context;

    Task IConfigurationStoreApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IConfigurationStoreApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a configuration store application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the configuration store application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ConfigurationStoreApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(ConfigurationStoreApplication).Assembly;
        return new ConfigurationStoreApplicationBuilder(args, resourceAssembly);
    }

    internal static ConfigurationStoreApplicationBuilder CreateBuilder(
        string[] args,
        Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new ConfigurationStoreApplicationBuilder(args, resourceAssembly);
    }
}
