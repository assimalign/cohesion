using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.CommandLine;
using Assimalign.Cohesion.Configuration.Json;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Collects engine intent and hosting registrations for one application Build attempt.</summary>
/// <remarks>Factories transfer ownership; instances are borrowed. Registration never materializes infrastructure.</remarks>
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;
    private readonly string[] _args;
    private readonly DatabaseConfigurationRegistrations _configuration;
    private readonly DatabaseServiceRegistrations _services;
    private readonly List<(IDatabaseEngine? Instance, string? Name, Func<DatabaseApplicationContext, IDatabaseEngine>? Factory)> _engines = [];
    private readonly List<(IHostService? Instance, Func<DatabaseApplicationContext, IHostService>? Factory)> _serviceRegistrations = [];
    private readonly List<IHealthContributor> _healthContributors = [];
    private readonly List<CompiledSchema> _schemas = [];
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IHostService? _telemetry;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private int _buildAttempted;

    /// <summary>Initializes a builder with borrowed legacy inputs and host settings.</summary>
    /// <param name="options">The options copied when Build begins.</param>
    public DatabaseApplicationBuilder(DatabaseApplicationOptions options) : this(options, null, []) { }

    internal DatabaseApplicationBuilder(DatabaseApplicationOptions options, Assembly? resourceAssembly, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _args = args is null ? [] : [.. args];
        _configuration = new DatabaseConfigurationRegistrations(EnsureMutable);
        _services = new DatabaseServiceRegistrations(EnsureMutable);
        if (resourceAssembly is not null && ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered database resource control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out _telemetry);
            _options.Environment = _resourceContext.EnvironmentName;
            _options.ContentRootPath = FileSystemPath.Parse(_resourceContext.ContentRootPath);
            ResourceRuntime.RegisterConnectionFactoryResolver(CreateConnectionFactory);
        }
    }

    /// <summary>Gets host settings and legacy borrowed input lists, copied at Build.</summary>
    public DatabaseApplicationOptions Options => _options;
    /// <summary>Gets configuration registrations; only application Build loads providers.</summary>
    public IConfigurationBuilder Configuration => _configuration;
    /// <summary>Gets closed factory and instance registrations; only application Build creates their provider.</summary>
    public IServiceProviderBuilder Services => _services;
    /// <summary>Gets compiled schema declarations registered for provisioning.</summary>
    public IReadOnlyList<CompiledSchema> Schemas => _schemas.AsReadOnly();
    internal IResourceControlPlane? ControlPlane => _controlPlane;

    /// <summary>Registers a named health contribution using the existing host integration.</summary>
    /// <param name="name">The contribution name.</param>
    /// <param name="check">The evaluator.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddHealthCheck(string name, ResourceHealthCheck check)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        _healthContributors.Add(new DelegateHealthContributor(name, check));
        return this;
    }

    /// <summary>Registers a caller-owned engine.</summary>
    /// <param name="engine">The borrowed engine.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(engine);
        ReserveName(engine.Name);
        _engines.Add((engine, engine.Name, null));
        return this;
    }

    /// <summary>Defers owned engine construction until Build.</summary>
    /// <param name="configure">The dependency-free factory, observing preceding engines.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _engines.Add((null, null, context => configure(context)));
        return this;
    }

    /// <summary>Reserves an engine name and defers construction using final configuration and services.</summary>
    /// <param name="name">The ordinal engine name, which must match the factory product.</param>
    /// <param name="factory">The ownership-transferring factory.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddEngine(string name, Func<DatabaseApplicationBuildContext, IDatabaseEngine> factory)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        ReserveName(name);
        _engines.Add((null, name, context => factory(new DatabaseApplicationBuildContext(context.Configuration, context.Services))));
        return this;
    }

    /// <summary>Registers a borrowed lifecycle service, started before servers and stopped after them.</summary>
    /// <param name="service">The caller-owned service.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddService(IHostService service)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(service);
        _serviceRegistrations.Add((service, null));
        return this;
    }

    /// <summary>Defers owned lifecycle service construction until the engine/server registry is complete.</summary>
    /// <param name="service">The ownership-transferring factory.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder AddService(Func<DatabaseApplicationContext, IHostService> service)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(service);
        _serviceRegistrations.Add((null, service));
        return this;
    }

    /// <summary>Registers before-accept provisioning for a borrowed registered engine.</summary>
    /// <param name="engine">The registered engine.</param>
    /// <param name="schema">The compiled schema.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder Provision(IDatabaseEngine engine, CompiledSchema schema)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(engine);
        ValidateSchema(engine, schema);
        _schemas.Add(schema);
        return AddService(context =>
        {
            if (!ReferenceEquals(context.GetEngine(engine.Name), engine))
            {
                throw new InvalidOperationException("The provisioning engine must belong to the application.");
            }

            return new DefaultDatabaseProvisioner(engine, schema);
        });
    }

    /// <summary>Registers existing compiled-schema provisioning against a deferred engine name.</summary>
    /// <param name="engineName">The registered engine name.</param>
    /// <param name="schema">The compiled schema.</param>
    /// <returns>This builder.</returns>
    public DatabaseApplicationBuilder Provision(string engineName, CompiledSchema schema)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(engineName);
        ArgumentNullException.ThrowIfNull(schema);
        _schemas.Add(schema);
        return AddService(context =>
        {
            IDatabaseEngine engine = context.GetEngine(engineName);
            ValidateSchema(engine, schema);
            return new DefaultDatabaseProvisioner(engine, schema);
        });
    }

    /// <summary>Registers a compiled logical database on a borrowed registered engine.</summary>
    /// <param name="engine">The registered engine.</param>
    /// <param name="name">The name matching the compiled schema.</param>
    /// <param name="schema">The compiled schema.</param>
    /// <returns>The same compiled schema.</returns>
    public CompiledSchema AddDatabase(IDatabaseEngine engine, string name, CompiledSchema schema)
    {
        ValidateDatabaseName(name, schema);
        Provision(engine, schema);
        return schema;
    }

    /// <summary>Registers a compiled logical database on a named deferred engine.</summary>
    /// <param name="engineName">The registered engine name.</param>
    /// <param name="name">The name matching the compiled schema.</param>
    /// <param name="schema">The compiled schema.</param>
    /// <returns>The same compiled schema.</returns>
    public CompiledSchema AddDatabase(string engineName, string name, CompiledSchema schema)
    {
        ValidateDatabaseName(name, schema);
        Provision(engineName, schema);
        return schema;
    }

    /// <summary>Consumes this builder and constructs one complete application.</summary>
    /// <returns>The application owning factory products and borrowing supplied instances.</returns>
    /// <exception cref="InvalidOperationException">Build was already attempted or composition is invalid.</exception>
    /// <exception cref="AggregateException">Construction and compensation both failed.</exception>
    public DatabaseApplication Build()
    {
        DatabaseApplicationComposition composition = BuildComposition();
        try
        {
            var application = new DatabaseApplication(composition);
            if (_controlPlane is not null)
            {
                ResourceRuntime.HostBuilt(application, _controlPlane);
            }

            return application;
        }
        catch (Exception exception)
        {
            var failures = new List<Exception>();
            Task.Run(() => composition.Ownership.DisposeAsync(failures)).GetAwaiter().GetResult();
            if (failures.Count > 0)
            {
                throw new AggregateException(new[] { exception }.Concat(failures));
            }

            throw;
        }
    }

    internal DatabaseApplicationComposition BuildComposition()
    {
        if (Interlocked.Exchange(ref _buildAttempted, 1) != 0)
        {
            throw new InvalidOperationException("The database application Build has already been attempted.");
        }

        var ownership = new DatabaseApplicationOwnership();
        try
        {
            DatabaseApplicationOptions options = SnapshotOptions(_options);
            var fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions
            {
                Root = options.ContentRootPath ?? FileSystemPath.Parse(AppContext.BaseDirectory),
                IsReadOnly = true,
            });
            ownership.Infrastructure.Add(fileSystem);
            IConfigurationBuilder configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddJsonFile(fileSystem, "appsettings.json", optional: true)
                .AddJsonFile(fileSystem, $"appsettings.{options.Environment ?? "production"}.json", optional: true)
                .AddEnvironmentVariables("COHESION_CONFIG__")
                .AddCommandLine(_args);
            _configuration.Apply(configurationBuilder);
            IConfiguration configuration = Task.Run(() => configurationBuilder.Build()).GetAwaiter().GetResult();
            ownership.Infrastructure.Add(configuration);
            IServiceProvider services = _services.Materialize(configuration);
            ownership.Infrastructure.Add(services);
            var context = new DatabaseApplicationContext(options, configuration, services);

            var products = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var registeredEngines = new HashSet<IDatabaseEngine>(ReferenceEqualityComparer.Instance);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var borrowedEngines = new List<IDatabaseEngine>(options.Engines);
            borrowedEngines.AddRange(_engines.Where(registration => registration.Instance is not null).Select(registration => registration.Instance!));
            borrowedEngines.AddRange(options.Servers.Select(server => server.Context.Engine));
            foreach (IDatabaseEngine engine in borrowedEngines)
            {
                products.Add(engine);
                foreach (IDatabaseServer server in engine.Servers)
                {
                    products.Add(server);
                }

                foreach (IDatabaseEngineWorker worker in engine.Workers)
                {
                    products.Add(worker);
                }
            }
            foreach (IDatabaseServer server in options.Servers)
            {
                products.Add(server);
            }

            foreach (IHostService service in options.Services)
            {
                products.Add(service);
            }

            foreach (var registration in _serviceRegistrations)
            {
                if (registration.Instance is not null)
                {
                    products.Add(registration.Instance);
                }
            }

            var inputEngines = options.Engines.ToArray();
            options.Engines.Clear();
            void RegisterEngine(IDatabaseEngine engine)
            {
                if (!registeredEngines.Add(engine))
                {
                    return;
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(engine.Name);
                if (engine.Model is not (EngineModel.Custom or EngineModel.Sql or EngineModel.Document or EngineModel.KeyValueStore or EngineModel.Blob or EngineModel.Graph))
                {
                    throw new InvalidOperationException($"Engine '{engine.Name}' has an unknown model.");
                }

                if (!names.Add(engine.Name))
                {
                    throw new InvalidOperationException($"Duplicate database engine name '{engine.Name}'.");
                }

                options.Engines.Add(engine);
                context.FreezeRegistries(options.Engines, []);
            }
            foreach (IDatabaseEngine engine in inputEngines)
            {
                RegisterEngine(engine);
            }

            foreach (var registration in _engines)
            {
                if (registration.Instance is not null) { RegisterEngine(registration.Instance); continue; }
                IDatabaseEngine engine = registration.Factory!(context) ?? throw new InvalidOperationException("An engine factory returned null.");
                if (!products.Add(engine))
                {
                    throw new InvalidOperationException("An engine factory returned an already registered product.");
                }

                ownership.Engines.Add(engine);
                foreach (IDatabaseServer server in engine.Servers)
                {
                    products.Add(server);
                }

                foreach (IDatabaseEngineWorker worker in engine.Workers)
                {
                    products.Add(worker);
                }

                if (registration.Name is not null && !string.Equals(registration.Name, engine.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Engine factory '{registration.Name}' returned engine '{engine.Name}'.");
                }

                RegisterEngine(engine);
            }
            foreach (IDatabaseServer server in options.Servers)
            {
                RegisterEngine(server.Context.Engine);
            }

            var borrowedServers = options.Servers.ToArray();
            options.Servers.Clear();
            var registeredServers = new HashSet<IDatabaseServer>(ReferenceEqualityComparer.Instance);
            foreach (IDatabaseEngine engine in options.Engines)
            {
                foreach (IDatabaseServer server in engine.Servers)
                {
                    if (!ReferenceEquals(server.Context.Engine, engine))
                    {
                        throw new InvalidOperationException("A nested server must front its owning engine.");
                    }

                    if (!registeredServers.Add(server))
                    {
                        throw new InvalidOperationException("A server was registered more than once.");
                    }

                    products.Add(server);
                    options.Servers.Add(server);
                }
            }
            foreach (IDatabaseServer server in borrowedServers)
            {
                if (!registeredServers.Add(server))
                {
                    throw new InvalidOperationException("A server was registered more than once.");
                }

                options.Servers.Add(server);
            }
            context.FreezeRegistries(options.Engines, options.Servers);
            if (_telemetry is not null)
            {
                options.Services.Insert(0, _telemetry);
                ownership.Services.Add(_telemetry);
            }
            var lifecycleProducts = new HashSet<object>(options.Servers, ReferenceEqualityComparer.Instance);
            foreach (IHostService service in options.Services)
            {
                if (!lifecycleProducts.Add(service))
                {
                    throw new InvalidOperationException("A lifecycle service was registered more than once.");
                }
            }

            foreach (var registration in _serviceRegistrations)
            {
                IHostService service = registration.Instance ?? registration.Factory!(context)
                    ?? throw new InvalidOperationException("A service factory returned null.");
                if (registration.Instance is null)
                {
                    if (!products.Add(service))
                    {
                        throw new InvalidOperationException("A service factory returned an already registered product.");
                    }

                    ownership.Services.Add(service);
                }
                if (!lifecycleProducts.Add(service))
                {
                    throw new InvalidOperationException("A lifecycle service was registered more than once.");
                }

                options.Services.Add(service);
            }
            ConfigureExistingResourceIntegration(options, context, ownership);
            return new DatabaseApplicationComposition(options, context, ownership);
        }
        catch (Exception exception)
        {
            var failures = new List<Exception>();
            Task.Run(() => ownership.DisposeAsync(failures)).GetAwaiter().GetResult();
            if (failures.Count > 0)
            {
                throw new AggregateException(new[] { exception }.Concat(failures));
            }

            throw;
        }
    }

    internal static DatabaseApplicationOptions SnapshotOptions(DatabaseApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartServicesConcurrently || options.StopServicesConcurrently)
        {
            throw new InvalidOperationException("Database applications require sequential service start and stop.");
        }

        var snapshot = new DatabaseApplicationOptions
        {
            Environment = options.Environment,
            ContentRootPath = options.ContentRootPath,
            StartupTimeout = options.StartupTimeout,
            ShutdownTimeout = options.ShutdownTimeout,
        };
        foreach (IDatabaseEngine engine in options.Engines)
        {
            snapshot.Engines.Add(engine);
        }

        foreach (IDatabaseServer server in options.Servers)
        {
            snapshot.Servers.Add(server);
        }

        foreach (IHostService service in options.Services)
        {
            snapshot.Services.Add(service);
        }

        return snapshot;
    }

    private void EnsureMutable()
    {
        if (Volatile.Read(ref _buildAttempted) != 0)
        {
            throw new InvalidOperationException("Registration is closed because application Build has begun.");
        }
    }

    private void ReserveName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_engines.Any(registration => string.Equals(registration.Name, name, StringComparison.Ordinal)) ||
            _options.Engines.Any(engine => string.Equals(engine.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Duplicate database engine name '{name}'.");
        }
    }

    private static void ValidateSchema(IDatabaseEngine engine, CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (engine.Model != schema.Model)
        {
            throw new ArgumentException($"Schema '{schema.Name}' targets {schema.Model}, but engine '{engine.Name}' uses {engine.Model}.", nameof(schema));
        }
    }

    private static void ValidateDatabaseName(string name, CompiledSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schema);
        if (!string.Equals(name, schema.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("The database name must match the compiled schema's name.", nameof(name));
        }
    }

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(IDatabaseEngine engine) => AddEngine(engine);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine> configure) => AddEngine(configure);
    IDatabaseApplication IDatabaseApplicationBuilder.Build() => Build();

    private static object? CreateConnectionFactory(string protocol) => string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase) ? new TcpConnectionFactory() : null;

    private void ConfigureExistingResourceIntegration(DatabaseApplicationOptions options, DatabaseApplicationContext context, DatabaseApplicationOwnership ownership)
    {
        if (_controlPlane is not null)
        {
            foreach (string kind in _controlPlane.AcceptedCommandKinds)
            {
                if (kind is "database.add-database" or "database.add-principal")
                {
                    _controlPlane.RegisterCommandHandler(new DatabaseResourceCommandHandler(kind, context));
                }
            }
            var controlPlaneContributors = new List<IHealthContributor> { context };
            var registeredContributors = new HashSet<IHealthContributor>(ReferenceEqualityComparer.Instance);
            registeredContributors.Add(context);

            foreach (IHealthContributor contributor in _healthContributors)
            {
                if (registeredContributors.Add(contributor))
                {
                    controlPlaneContributors.Add(contributor);
                }
            }

            foreach (IHealthContributor contributor in options.Services
                .Concat<object>(options.Engines)
                .Concat(options.Servers)
                .OfType<IHealthContributor>())
            {
                if (registeredContributors.Add(contributor))
                {
                    controlPlaneContributors.Add(contributor);
                }
            }

            foreach (IHealthContributor contributor in controlPlaneContributors)
            {
                _controlPlane.AddHealthContributor(contributor);
            }

            if ((_controlPlane.ObservedEndpoints.TryGetValue("admin", out Uri? endpoint) ||
                (_resourceContext is not null &&
                 _resourceContext.Endpoints.TryGetValue("admin", out endpoint))))
            {
                if (_resourceContext?.GatewayName is not null)
                {
                    ResourceControlPlaneMiddleware.Validate(_resourceContext);
                }
                _controlPlane.ObserveEndpoint("admin", endpoint);
                var adminService = new DatabaseAdminEndpointService(
                    endpoint,
                    _controlPlane,
                    _resourceContext!,
                    context,
                    controlPlaneContributors);
                options.Services.Add(adminService);
                ownership.Services.Add(adminService);
            }
        }


    }
}
