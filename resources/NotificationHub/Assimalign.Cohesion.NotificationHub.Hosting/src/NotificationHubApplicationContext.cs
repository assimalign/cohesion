using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

/// <summary>
/// Provides the NotificationHub application environment and runtime composition.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("NotificationHubApplicationContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class NotificationHubApplicationContext : HostContext, INotificationHubApplicationContext
{
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;
    private int _isServiceProviderDisposed;

    internal NotificationHubApplicationContext()
    {
        _environment = new HostEnvironment("production");
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

    private sealed class DebugView
    {
        private readonly NotificationHubApplicationContext _target;

        public DebugView(NotificationHubApplicationContext value)
        {
            _target = value;
        }

        public FileSystemPath? ContentRootPath => _target.ContentRootPath;
        public IHostEnvironment Environment => _target.Environment;
        public IEnumerable<IHostService> HostedServices => _target.HostedServices;
        public HostId HostId => _target.HostId;
        public global::Assimalign.Cohesion.Hosting.HostState State => _target.State;
    }
}
