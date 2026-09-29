using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The context of a built Web application: its service provider and the lifecycle composition
/// resolved from it.
/// </summary>
/// <remarks>
/// Lifecycle order is two phases, each in registration order: every <see cref="IHostService"/>
/// registration (the application services, resolved when the application is built), then every
/// <see cref="IWebApplicationServer"/> registration (the servers, resolved once at host start so
/// the default server captures the fully composed pipeline). Both snapshots are reused for every
/// start, stop, and rollback pass.
/// </remarks>
public sealed class WebApplicationContext : HostContext, IWebApplicationContext
{
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _applicationServices = Array.Empty<IHostService>();
    private Lazy<IWebApplicationServer[]>? _servers;
    private Lazy<IHostService[]>? _serverServices;
    private int _isServiceProviderDisposed;

    internal List<X509Certificate2> EndpointCertificates { get; } = new();

    internal WebApplicationContext()
    {
    }

    public FileSystemPath? ContentRootPath { get; init; }

    /// <summary>
    /// Gets the application's service provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application has not been built.</exception>
    public IServiceProvider ServiceProvider => _serviceProvider
        ?? throw new InvalidOperationException("The service provider is created when the web application is built.");

    public override IHostEnvironment Environment => ServiceProvider.GetRequiredService<IHostEnvironment>();

    public override IEnumerable<IHostService> HostedServices => _serverServices is null
        ? _applicationServices
        : _applicationServices.Concat(_serverServices.Value);

    public IEnumerable<IWebApplicationServer> Servers => _servers?.Value ?? Array.Empty<IWebApplicationServer>();

    public IEnumerable<IWebApplicationMiddleware> Middleware => ServiceProvider.GetRequiredService<IEnumerable<IWebApplicationMiddleware>>();

    public IEnumerable<IHttpFeature> Features => ServiceProvider.GetRequiredService<IEnumerable<IHttpFeature>>();

    internal void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    internal void ResolveApplicationServices()
    {
        IServiceProvider serviceProvider = ServiceProvider;

        _applicationServices = serviceProvider.GetRequiredService<IEnumerable<IHostService>>().ToArray();
        _servers = new Lazy<IWebApplicationServer[]>(() => serviceProvider
            .GetRequiredService<IEnumerable<IWebApplicationServer>>()
            .Where(static server => server is not InactiveWebApplicationServer)
            .ToArray());
        _serverServices = new Lazy<IHostService[]>(() => _servers.Value
            .Select(static server => server as IHostService ?? new WebApplicationServerLifecycleAdapter(server))
            .ToArray());
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
