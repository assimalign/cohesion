using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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
    private readonly Lock _lifetimeLock = new();
    private readonly List<IDisposable> _lifetimeResources = new();
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _applicationServices = Array.Empty<IHostService>();
    private Lazy<IWebApplicationServer[]>? _servers;
    private Lazy<IHostService[]>? _serverServices;
    private int _isServiceProviderDisposed;

    internal List<X509Certificate2> EndpointCertificates { get; } = new();

    /// <summary>
    /// Gets or sets the number of application features the built pipeline stamps onto every
    /// exchange. It stays zero until the pipeline is built, and when a pipeline passed to
    /// <c>AddPipeline</c> replaces it, since that pipeline stamps none. The default server sizes
    /// each exchange's feature collection with it (#1381).
    /// </summary>
    internal int StampedFeatureCount { get; set; }

    internal WebApplicationContext()
    {
    }

    /// <summary>
    /// Gets the application's content root: the directory configuration files are read from.
    /// </summary>
    /// <remarks>
    /// <see cref="WebApplicationOptions.ContentRootPath"/> when set; otherwise the ambient resource
    /// context's content root for an orchestrated resource; otherwise
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </remarks>
    public FileSystemPath? ContentRootPath { get; init; }

    /// <summary>
    /// Gets the directory static web assets are served from, or <see langword="null"/> when the
    /// application has no web root.
    /// </summary>
    /// <remarks>
    /// <see cref="WebApplicationOptions.WebRootPath"/> resolved against
    /// <see cref="ContentRootPath"/> when set; otherwise <c>wwwroot</c> under the content root
    /// when that directory exists.
    /// </remarks>
    public FileSystemPath? WebRootPath { get; init; }

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

    // Pipeline composition owns these resources until final application teardown.
    internal void RegisterForDisposal(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isServiceProviderDisposed) != 0, this);
            foreach (IDisposable registered in _lifetimeResources)
            {
                if (ReferenceEquals(registered, resource))
                {
                    return;
                }
            }

            _lifetimeResources.Add(resource);
        }
    }

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
            .Select(static server => new WebApplicationServerHostService(server))
            .ToArray());
    }

    /// <summary>
    /// Disposes the service provider and then every resource registered by pipeline composition.
    /// </summary>
    internal async ValueTask DisposeServiceProviderAsync()
    {
        if (Interlocked.Exchange(ref _isServiceProviderDisposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        try
        {
            if (_serviceProvider is IAsyncDisposable serviceProvider)
            {
                await serviceProvider.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures = new List<Exception> { exception };
        }
        finally
        {
            IDisposable[] resources;
            lock (_lifetimeLock)
            {
                resources = _lifetimeResources.ToArray();
                _lifetimeResources.Clear();
            }

            for (int index = resources.Length - 1; index >= 0; index--)
            {
                try
                {
                    resources[index].Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
        }

        if (failures is not null)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            throw new AggregateException("One or more application resources failed to dispose.", failures);
        }
    }
}
