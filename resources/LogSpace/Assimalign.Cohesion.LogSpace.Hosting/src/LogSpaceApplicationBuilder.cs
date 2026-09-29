using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.LogSpace.Hosting;

/// <summary>
/// Composes a LogSpace application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>.
/// <see cref="Build"/> closes registration, creates the service provider once, and resolves the
/// lifecycle services (<see cref="IHostService"/>) once, in registration order.
/// </remarks>
public sealed class LogSpaceApplicationBuilder : ILogSpaceApplicationBuilder
{
    private readonly LogSpaceApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    private bool _isBuilt;

    internal LogSpaceApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered LogSpace control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            ILoggerFactory? loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (loggerFactory is not null)
            {
                Services.AddSingleton<ILoggerFactory>(loggerFactory);
            }
            if (telemetry is not null)
            {
                // Registered ahead of every AddService call, so telemetry starts first and stops last.
                Services.AddSingleton<IHostService>(telemetry);
            }
            Services.AddSingleton<IResourceControlPlane>(_controlPlane);

            if (_resourceContext.Endpoints.ContainsKey("otlp"))
            {
                // One segment store backs the flush service, the OTLP receiver, and the query endpoint.
                ResourceContext resourceContext = _resourceContext;
                Services.AddSingleton<LogSegmentStore>(_ => new LogSegmentStore(
                    resourceContext.GetMount("data", Path.Combine(resourceContext.ContentRootPath, "logs")).Path
                        ?? throw new InvalidOperationException("LogSpace data requires a filesystem mount.")));

                // Registered ahead of every AddService call, so segments flush for the whole lifetime
                // of the other services.
                Services.AddSingleton<IHostService>(serviceProvider =>
                    new SegmentFlushService(serviceProvider.GetRequiredService<LogSegmentStore>()));
            }
        }

        _context = new LogSpaceApplicationContext(_resourceContext);
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order. Register factory or instance services: the
    /// provider is created without dynamic code. A factory-created service is owned by the
    /// application and disposed with it; an instance stays owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Registers a host service with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public LogSpaceApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, when the application is built, and receives the application's
    /// final host context. The application owns and disposes the service it returns, which follows
    /// registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    public LogSpaceApplicationBuilder AddService(Func<LogSpaceApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The LogSpace application service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the LogSpace application.
    /// </summary>
    /// <returns>The configured LogSpace application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The application was already built, or a registered host service factory returns <see langword="null"/>.
    /// </exception>
    public LogSpaceApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The LogSpace application has already been built.");

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(_context);

            // Registered after every AddService call, so the endpoints start last and stop first.
            if (_resourceContext!.Endpoints.TryGetValue("query", out Uri? queryEndpoint))
            {
                _controlPlane.ObserveEndpoint("query", queryEndpoint);
                Services.AddSingleton<IHostService>(serviceProvider => new LogSpaceControlPlaneEndpointService(
                    queryEndpoint, _controlPlane, _resourceContext, _context, serviceProvider.GetService<LogSegmentStore>()));
            }
            if (_resourceContext.Endpoints.TryGetValue("otlp", out Uri? otlpEndpoint))
            {
                _controlPlane.ObserveEndpoint("otlp", otlpEndpoint);
                Services.AddSingleton<IHostService>(serviceProvider => new OtlpReceiverEndpointService(
                    otlpEndpoint, serviceProvider.GetRequiredService<LogSegmentStore>(), _resourceContext, _context));
            }
        }

        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<ILogSpaceApplicationContext>(_context);

        _isBuilt = true;

        // Registration closes here. The provider copies the registrations, so one added after
        // Build would silently never reach it; the read-only container turns that into an error.
        if (Services.Container is ServiceContainer container)
        {
            container.MakeReadOnly();
        }

        _context.SetServiceProvider(((IServiceProviderBuilder)Services).Build());
        try
        {
            _context.ResolveHostedServices();

            var application = new LogSpaceApplication(new LogSpaceApplicationOptions(), _context);
            if (_controlPlane is not null)
            {
                ResourceRuntime.HostBuilt(application, _controlPlane);
            }

            return application;
        }
        catch (Exception exception)
        {
            // The provider owns every service a factory created before the failure.
            try
            {
                Task.Run(() => _context.DisposeServiceProviderAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(exception, disposalException);
            }

            throw;
        }
    }

    ILogSpaceApplication ILogSpaceApplicationBuilder.Build() => Build();
}
