using System;
using System.Reflection;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting.Internal;

namespace Assimalign.Cohesion.NatGateway.Hosting;

/// <summary>
/// Composes a NatGateway application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>.
/// <see cref="Build"/> closes registration, creates the service provider once, and resolves the
/// lifecycle services (<see cref="IHostService"/>) once, in registration order.
/// </remarks>
public sealed class NatGatewayApplicationBuilder : INatGatewayApplicationBuilder
{
    private readonly NatGatewayApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    private bool _isBuilt;

    internal NatGatewayApplicationBuilder(string[] args, Assembly resourceAssembly)
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
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered NatGateway control-plane factory returned null.");
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
        }

        _context = new NatGatewayApplicationContext(_resourceContext);
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
    /// Registers a host service with the NAT gateway application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public NatGatewayApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the NAT gateway application.
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
    public NatGatewayApplicationBuilder AddService(Func<NatGatewayApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The NAT gateway application service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the NAT gateway application.
    /// </summary>
    /// <returns>The configured NAT gateway application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The application was already built, or a registered host service factory returns <see langword="null"/>.
    /// </exception>
    public NatGatewayApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The NAT gateway application has already been built.");

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(_context);
            if (_resourceContext!.Endpoints.TryGetValue("http", out Uri? endpoint))
            {
                _controlPlane.ObserveEndpoint("http", endpoint);

                // Registered after every AddService call, so the endpoint starts last and stops first.
                Services.AddSingleton<IHostService>(_ => new NatGatewayControlPlaneEndpointService(
                    endpoint, _controlPlane, _resourceContext, _context));
            }
        }

        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<INatGatewayApplicationContext>(_context);

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

            var application = new NatGatewayApplication(new NatGatewayApplicationOptions(), _context);
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

    INatGatewayApplication INatGatewayApplicationBuilder.Build() => Build();
}
