using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

/// <summary>
/// Composes a ConfigurationStore application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>:
/// declared namespaces, the configuration repository, and the lifecycle services
/// (<see cref="IHostService"/>). <see cref="Build"/> closes registration, creates the service
/// provider once, and resolves the lifecycle services once, in registration order; the
/// configuration endpoint is always the last of them.
/// </remarks>
public sealed class ConfigurationStoreApplicationBuilder : IConfigurationStoreApplicationBuilder
{
    private const string DefaultEndpoint = "http://127.0.0.1:8080";

    private readonly string[] _args;
    private readonly string _environmentName;
    private readonly ConfigurationStoreApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private bool _isBuilt;

    internal ConfigurationStoreApplicationBuilder(string[] args, Assembly resourceAssembly)
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
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered ConfigurationStore control-plane factory returned null.");
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

        _environmentName = _resourceContext?.EnvironmentName ?? "production";
        _context = new ConfigurationStoreApplicationContext(
            _environmentName,
            _resourceContext is null ? (System.IO.FileSystemPath?)null : System.IO.FileSystemPath.Parse(_resourceContext.ContentRootPath));
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order, ahead of the configuration endpoint.
    /// Register factory or instance services: the provider is created without dynamic code. A
    /// factory-created service is owned by the application and disposed with it; an instance
    /// stays owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Declares a named configuration namespace and its first-start values.
    /// </summary>
    /// <remarks>
    /// Declared values seed a namespace only when no durable namespace document exists.
    /// Later command mutations therefore survive application restarts. The declaration is
    /// registered in <see cref="Services"/>.
    /// </remarks>
    /// <param name="name">The namespace name.</param>
    /// <param name="configure">The callback that declares initial key/value entries.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A namespace with the same name is already declared.</exception>
    public ConfigurationStoreApplicationBuilder AddNamespace(
        string name,
        Action<IConfigurationNamespaceBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is ConfigurationNamespaceDeclaration declared &&
                string.Equals(declared.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Configuration namespace '{name}' is already declared.");
            }
        }

        var declaration = new ConfigurationNamespaceBuilder();
        configure.Invoke(declaration);
        Services.AddSingleton(new ConfigurationNamespaceDeclaration(name, declaration.Snapshot()));
        return this;
    }

    /// <summary>
    /// Registers a host service with the configuration store application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public ConfigurationStoreApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the configuration store application.
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
    public ConfigurationStoreApplicationBuilder AddService(Func<ConfigurationStoreApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The configuration store application service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the configuration store application.
    /// </summary>
    /// <returns>The configured configuration store application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a registered host service factory returns
    /// <see langword="null"/>, or the data mount has no filesystem path.
    /// </exception>
    /// <exception cref="ArgumentException">A command-line endpoint or data option is invalid or missing its value.</exception>
    public ConfigurationStoreApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The configuration store application has already been built.");

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();

        Services.AddSingleton<ConfigurationStoreRepository>(serviceProvider =>
        {
            var namespaces = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
            foreach (ConfigurationNamespaceDeclaration declaration in
                serviceProvider.GetRequiredService<IEnumerable<ConfigurationNamespaceDeclaration>>())
            {
                if (!namespaces.TryAdd(declaration.Name, declaration.Values))
                {
                    throw new InvalidOperationException(
                        $"Configuration namespace '{declaration.Name}' is already declared.");
                }
            }

            return new ConfigurationStoreRepository(dataPath, namespaces);
        });

        // Registered after every AddService call, so the endpoint starts last and stops first.
        Services.AddSingleton<IHostService>(serviceProvider => new ConfigurationEndpointService(
            endpoint,
            serviceProvider.GetRequiredService<ConfigurationStoreRepository>(),
            _controlPlane,
            _resourceContext,
            _context));
        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<IConfigurationStoreApplicationContext>(_context);

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

            var application = new ConfigurationStoreApplication(
                new ConfigurationStoreApplicationOptions { Environment = _environmentName },
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

    IConfigurationStoreApplicationBuilder IConfigurationStoreApplicationBuilder.AddNamespace(
        string name,
        Action<IConfigurationNamespaceBuilder> configure) => AddNamespace(name, configure);

    IConfigurationStoreApplication IConfigurationStoreApplicationBuilder.Build() => Build();

    private Uri ResolveEndpoint()
    {
        if (_controlPlane?.ObservedEndpoints.TryGetValue("api", out Uri? observed) is true)
        {
            return observed;
        }

        if (_resourceContext?.Endpoints.TryGetValue("api", out Uri? supplied) is true)
        {
            return supplied;
        }

        return ParseArgument("--endpoint") is string configured
            ? ParseEndpoint(configured)
            : new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private string ResolveDataPath()
    {
        if (_resourceContext?.Mounts.TryGetValue("data", out ResourceMount? mount) is true)
        {
            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                throw new InvalidOperationException(
                    "The ConfigurationStore 'data' volume must expose a file-system path.");
            }

            return Path.GetFullPath(mount.Path);
        }

        string? configured = ParseArgument("--data");
        string path = configured ?? Path.Combine(
            _resourceContext?.ContentRootPath ?? AppContext.BaseDirectory,
            "data");
        return Path.GetFullPath(path);
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
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            endpoint.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The ConfigurationStore endpoint must be an absolute HTTP endpoint with an explicit port.",
                nameof(value));
        }

        return endpoint;
    }
}
