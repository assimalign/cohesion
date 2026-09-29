using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

/// <summary>
/// Provides the NotificationHub application environment and runtime composition.
/// </summary>
public sealed class NotificationHubApplicationContext : HostContext, INotificationHubApplicationContext, IHealthContributor
{
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;
    private int _isServiceProviderDisposed;

    internal NotificationHubApplicationContext(ResourceContext? resourceContext = null)
    {
        _environment = new HostEnvironment(resourceContext?.EnvironmentName ?? "production")
        {
            ContentRootPath = resourceContext is null ? (FileSystemPath?)null : FileSystemPath.Parse(resourceContext.ContentRootPath),
        };
    }

    /// <summary>
    /// Gets the name used for this application health contribution.
    /// </summary>
    public string Name => "NotificationHub";

    /// <summary>
    /// Reports the health of the application.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the health check.</param>
    /// <returns>The current application health contribution.</returns>
    /// <exception cref="OperationCanceledException">The cancellation token is cancelled.</exception>
    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State is HostState.Failed
            ? HealthContribution.Unhealthy("The NotificationHub host failed.")
            : HealthContribution.Healthy());
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
    /// <remarks>
    /// The <see cref="IHostService"/> registrations, resolved once when the application is built.
    /// </remarks>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <summary>
    /// Gets the application's service provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application has not been built.</exception>
    public IServiceProvider ServiceProvider => _serviceProvider
        ?? throw new InvalidOperationException("The service provider is created when the notification hub application is built.");

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
