using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Provides the IdentityHub application environment and runtime composition.
/// </summary>
public sealed class IdentityHubApplicationContext : HostContext, IIdentityHubApplicationContext
{
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;
    private int _isServiceProviderDisposed;

    internal IdentityHubApplicationContext(string environmentName, System.IO.FileSystemPath? contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        // The opt-in resource runner asserts that the host content root equals the ambient resource content root,
        // so an enabled resource seeds it from the ambient context; a plain application keeps it unset.
        _environment = new HostEnvironment(environmentName) { ContentRootPath = contentRootPath };
    }

    /// <summary>
    /// Gets the configured application content root.
    /// </summary>
    public FileSystemPath? ContentRootPath => Environment.ContentRootPath;

    /// <summary>
    /// Gets the host environment for this application.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services in registration and startup order.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <summary>
    /// Gets the application's service provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application has not been built.</exception>
    public IServiceProvider ServiceProvider => _serviceProvider
        ?? throw new InvalidOperationException("The service provider is created when the identity hub application is built.");

    internal void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    internal void ResolveHostedServices()
    {
        _hostedServices = ServiceProvider.GetRequiredService<IEnumerable<IHostService>>().ToArray();
    }

    /// <summary>
    /// Disposes the service provider, which disposes every service a registered factory created.
    /// </summary>
    internal ValueTask DisposeServiceProviderAsync()
    {
        if (_serviceProvider is IAsyncDisposable serviceProvider &&
            Interlocked.Exchange(ref _isServiceProviderDisposed, 1) == 0)
        {
            return serviceProvider.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
