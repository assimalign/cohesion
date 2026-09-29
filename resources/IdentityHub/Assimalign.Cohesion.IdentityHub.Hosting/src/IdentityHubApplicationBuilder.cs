using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Composes an IdentityHub application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>:
/// declared audiences and clients, and the lifecycle services (<see cref="IHostService"/>).
/// <see cref="Build"/> closes registration, creates the service provider once, validates the
/// declared clients against the declared audiences, and resolves the lifecycle services once, in
/// registration order; the identity endpoint is always the last of them.
/// </remarks>
public sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly string _environmentName;
    private readonly IdentityHubApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext _resourceContext;
    private bool _isBuilt;

    internal IdentityHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
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
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered IdentityHub control-plane factory returned null.");
            Services.AddSingleton<IResourceControlPlane>(_controlPlane);
        }

        _environmentName = _resourceContext.EnvironmentName;
        _context = new IdentityHubApplicationContext(
            _environmentName,
            System.IO.FileSystemPath.Parse(_resourceContext.ContentRootPath));
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order, ahead of the identity endpoint. Register
    /// factory or instance services: the provider is created without dynamic code. A
    /// factory-created service is owned by the application and disposed with it; an instance stays
    /// owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <remarks>
    /// The audience is registered in <see cref="Services"/>.
    /// </remarks>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared or the application has been built.</exception>
    public IdentityHubApplicationBuilder AddAudience(string audience)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is IdentityHubAudience declared &&
                string.Equals(declared.Value, audience, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"IdentityHub audience '{audience}' is already declared.");
            }
        }

        Services.AddSingleton(new IdentityHubAudience(audience));
        return this;
    }

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <remarks>
    /// The client registration is added to <see cref="Services"/>; its audiences are validated
    /// against the declared audiences when the application is built.
    /// </remarks>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered or the application has been built.</exception>
    public IdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(configure);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is IdentityHubClientRegistration registered &&
                string.Equals(registered.ClientId, clientId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"IdentityHub client '{clientId}' is already registered.");
            }
        }

        var options = new IdentityHubClientOptions();
        configure.Invoke(options);
        Services.AddSingleton(IdentityHubClientRegistration.Create(clientId, options));
        return this;
    }

    /// <summary>
    /// Registers a host service with the identity hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built.</exception>
    public IdentityHubApplicationBuilder AddService(IHostService service)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the identity hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, when the application is built, and receives the application's
    /// final host context. The application owns and disposes the service it returns, which follows
    /// registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built, or the factory returns <see langword="null"/> during build.</exception>
    public IdentityHubApplicationBuilder AddService(Func<IdentityHubApplicationContext, IHostService> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The identity hub application service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a registered client is invalid, a registered
    /// host service factory returns <see langword="null"/>, or the data mount has no filesystem path.
    /// </exception>
    /// <exception cref="ArgumentException">A command-line endpoint or data option is invalid or missing its value.</exception>
    public IdentityHubApplication Build()
    {
        EnsureNotBuilt();

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        Directory.CreateDirectory(dataPath);

        Services.AddSingleton<IdentityHubRegistrations>(serviceProvider => IdentityHubRegistrations.Create(
            serviceProvider.GetRequiredService<IEnumerable<IdentityHubAudience>>(),
            serviceProvider.GetRequiredService<IEnumerable<IdentityHubClientRegistration>>()));

        // Registered after every AddService call, so the endpoint starts last and stops first.
        Services.AddSingleton<IHostService>(serviceProvider =>
        {
            IdentityHubRegistrations registrations = serviceProvider.GetRequiredService<IdentityHubRegistrations>();
            return new IdentityEndpointService(
                endpoint,
                dataPath,
                registrations.Audiences,
                registrations.Clients,
                _controlPlane,
                _resourceContext,
                _context);
        });
        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<IIdentityHubApplicationContext>(_context);

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
            // The declared clients are validated before any lifecycle service is created.
            _ = _context.ServiceProvider.GetRequiredService<IdentityHubRegistrations>();
            _context.ResolveHostedServices();

            var application = new IdentityHubApplication(
                new IdentityHubApplicationOptions { Environment = _environmentName },
                _context);
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

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddAudience(string audience) => AddAudience(audience);

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure) => AddClient(clientId, configure);

    IIdentityHubApplication IIdentityHubApplicationBuilder.Build() => Build();

    private void EnsureNotBuilt()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The identity hub application has already been built.");
        }
    }

    private Uri ResolveEndpoint()
    {
        if (_controlPlane?.ObservedEndpoints.TryGetValue("https", out Uri? observed) is true)
        {
            return observed;
        }

        if (_resourceContext.Endpoints.TryGetValue("https", out Uri? supplied))
        {
            return supplied;
        }

        return ParseArgument("--endpoint") is string configured
            ? ParseEndpoint(configured)
            : new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private string ResolveDataPath()
    {
        if (_resourceContext.Mounts.TryGetValue("data", out ResourceMount? mount))
        {
            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                throw new InvalidOperationException(
                    "The IdentityHub 'data' volume must expose a file-system path.");
            }

            return Path.GetFullPath(mount.Path);
        }

        string? configured = ParseArgument("--data");
        return Path.GetFullPath(configured ?? Path.Combine(_resourceContext.ContentRootPath, "data"));
    }

    private string? ParseArgument(string name)
    {
        for (int index = 0; index < _args.Length; index++)
        {
            string argument = _args[index];
            if (argument.StartsWith(name + "=", StringComparison.Ordinal))
            {
                string value = argument[(name.Length + 1)..];
                ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
                return value;
            }

            if (string.Equals(argument, name, StringComparison.Ordinal))
            {
                if (index + 1 >= _args.Length)
                {
                    throw new ArgumentException($"Command-line option '{name}' requires a value.", name);
                }

                string value = _args[index + 1];
                ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
                return value;
            }
        }

        return null;
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.IsFile ||
            endpoint.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The IdentityHub endpoint must be an absolute HTTP or HTTPS endpoint with an explicit port.",
                nameof(value));
        }

        return endpoint;
    }
}
