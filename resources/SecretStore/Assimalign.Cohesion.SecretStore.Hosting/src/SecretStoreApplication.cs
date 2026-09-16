using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

/// <summary>
/// Hosts a SecretStore application and its ordered service lifecycle.
/// </summary>
public sealed class SecretStoreApplication : Host<SecretStoreApplicationContext>, ISecretStoreApplication
{
    private readonly SecretStoreApplicationContext _context;
    private readonly SecretsEndpointService _endpointService;
    private bool _endpointServiceDisposed;

    internal SecretStoreApplication(
        SecretStoreApplicationOptions options,
        SecretStoreApplicationContext context,
        SecretsEndpointService endpointService)
        : base(options)
    {
        _context = context;
        _endpointService = endpointService;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override SecretStoreApplicationContext Context => _context;

    /// <summary>
    /// Disposes the host and its owned endpoint service.
    /// </summary>
    /// <param name="disposing">Whether to release managed resources.</param>
    /// <returns>A task representing asynchronous disposal.</returns>
    protected override async ValueTask DisposeAsync(bool disposing)
    {
        try
        {
            await base.DisposeAsync(disposing).ConfigureAwait(false);
        }
        finally
        {
            if (disposing && !_endpointServiceDisposed)
            {
                _endpointServiceDisposed = true;
                _endpointService.Dispose();
            }
        }
    }

    ISecretStoreApplicationContext ISecretStoreApplication.Context => _context;

    Task ISecretStoreApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task ISecretStoreApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a secret store application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the secret store application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static SecretStoreApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(SecretStoreApplication).Assembly;
        return new SecretStoreApplicationBuilder(args, resourceAssembly);
    }

    internal static SecretStoreApplicationBuilder CreateBuilder(
        string[] args,
        Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new SecretStoreApplicationBuilder(args, resourceAssembly);
    }
}
