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
/// <remarks>
/// <para>
/// Factories transfer ownership; instances are borrowed. The host-level pieces are the ones
/// <c>WebApplicationBuilder</c> exposes, created the same way when the builder is created:
/// <see cref="Environment"/>, the <see cref="Configuration"/> manager (which loads each provider as
/// it is added), the <see cref="Logging"/> builder and the <see cref="Services"/> registrations.
/// <see cref="Build"/> builds the logger factory and the service provider once, registers the
/// environment, the configuration and the logger factory in the provider, and hands all four to
/// the engine factories (<see cref="DatabaseApplicationBuildContext"/>).
/// </para>
/// <para>
/// The root <see cref="IDatabaseApplicationBuilder"/> carries none of them: model packages compose
/// against it without the container, configuration or logging (COHRES004).
/// </para>
/// </remarks>
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;
    private readonly List<(DatabaseEngine? Instance, string? Name, Func<DatabaseApplicationContext, DatabaseEngine>? Factory)> _engines = [];
    private readonly List<(IHostService? Instance, Func<DatabaseApplicationContext, IHostService>? Factory)> _serviceRegistrations = [];
    private readonly List<IHealthContributor> _healthContributors = [];
    private readonly List<CompiledSchema> _schemas = [];
    private readonly PhysicalFileSystem? _contentRoot;
    private readonly IHostService? _telemetry;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private int _buildAttempted;

    /// <summary>Initializes a builder with borrowed legacy inputs and host settings.</summary>
    /// <param name="options">
    /// The host settings: their <c>Environment</c> and
    /// <see cref="DatabaseApplicationOptions.ContentRootPath"/> are read now, for
    /// <see cref="Environment"/>; the rest is copied when Build begins.
    /// </param>
    /// <remarks>
    /// The configuration starts empty, as <c>new WebApplicationBuilder(options)</c>'s does:
    /// <see cref="DatabaseApplication.CreateBuilder(string[])"/> adds the default sources.
    /// </remarks>
    public DatabaseApplicationBuilder(DatabaseApplicationOptions options) : this(options, null, null) { }

    internal DatabaseApplicationBuilder(DatabaseApplicationOptions options, Assembly? resourceAssembly, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ResourceContext? resourceContext = null;
        if (resourceAssembly is not null && ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered database resource control-plane factory returned null.");
            resourceContext = ResourceRuntime.Current;
            _resourceContext = resourceContext;
            _options.Environment = resourceContext.EnvironmentName;
            _options.ContentRootPath = FileSystemPath.Parse(resourceContext.ContentRootPath);
            ResourceRuntime.RegisterConnectionFactoryResolver(CreateConnectionFactory);
        }

        Environment = new HostEnvironment(_options.Environment ?? "production")
        {
            ContentRootPath = _options.ContentRootPath,
        };
        Configuration = new ConfigurationManager();
        if (args is not null)
        {
            _contentRoot = AddDefaultConfiguration(args, resourceContext);
        }

        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        if (_controlPlane is not null && resourceContext is not null
            && ResourceTelemetry.Configure(resourceContext, Logging, out IHostService? telemetry))
        {
            // Started first and stopped last (Build puts it ahead of every other service).
            _telemetry = telemetry;
        }
    }

    /// <summary>
    /// Gets host settings and legacy borrowed input lists: <c>Environment</c> and
    /// <see cref="DatabaseApplicationOptions.ContentRootPath"/> are read when the builder is created
    /// (see <see cref="Environment"/>); the rest is copied at Build.
    /// </summary>
    /// <remarks>
    /// Setting the environment or the content root here after the builder exists changes nothing:
    /// Build's snapshot takes both from <see cref="Environment"/>, so the application's options and
    /// its context never disagree.
    /// </remarks>
    public DatabaseApplicationOptions Options => _options;

    /// <summary>
    /// Gets the application's host environment: its name and content root, read from
    /// <see cref="Options"/> (or the ambient resource context) when the builder was created.
    /// </summary>
    /// <remarks>
    /// Registered in <see cref="Services"/> as <see cref="IHostEnvironment"/> at Build; the
    /// application's <see cref="DatabaseApplicationContext.Environment"/> is this instance.
    /// </remarks>
    public HostEnvironment Environment { get; }

    /// <summary>
    /// Gets the application's configuration: a manager that loads each provider as it is added.
    /// </summary>
    /// <remarks>
    /// <see cref="DatabaseApplication.CreateBuilder(string[])"/> adds the default sources when it
    /// creates the builder: optional <c>appsettings.json</c> and
    /// <c>appsettings.{Environment}.json</c> under the content root, environment variables prefixed
    /// <c>COHESION_CONFIG__</c>, then the ambient resource settings and the command-line arguments;
    /// later sources win. Registered in <see cref="Services"/> as <see cref="IConfiguration"/> at
    /// Build and owned by the application, which disposes it. A provider added after Build is
    /// loaded into the running configuration, but recomposes nothing: engine factories read the
    /// configuration once, at Build.
    /// </remarks>
    public ConfigurationManager Configuration { get; }

    /// <summary>
    /// Gets the application's logging composition: providers, filter rules, enrichers and
    /// forwarders for the logger factory Build creates.
    /// </summary>
    /// <remarks>
    /// Build builds the factory once, registers it in <see cref="Services"/> as
    /// <see cref="ILoggerFactory"/>, and hands it to the engine factories; the application owns and
    /// disposes it after every service and engine. The builder refuses changes after Build. An
    /// enabled resource's telemetry adds its exporter here when the builder is created.
    /// <see cref="Build"/> creates the application's one logger factory; building this builder
    /// directly yields a separate factory the application never uses, and makes the application's
    /// Build fail, since the builder builds once. <c>WebApplicationBuilder</c>'s <c>Logging</c> is
    /// the same.
    /// </remarks>
    public LoggerFactoryBuilder Logging { get; }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when Build begins; a later registration throws
    /// <see cref="InvalidOperationException"/>. The provider is created without dynamic code, so the
    /// application accepts closed factory and instance registrations only, and reserves
    /// <see cref="IHostEnvironment"/>, <see cref="IConfiguration"/> and <see cref="ILoggerFactory"/>
    /// for its own pieces. A factory-created service is owned by the provider and disposed with the
    /// application; an instance stays owned by its caller. <see cref="Build"/> creates the
    /// application's one provider; building these registrations directly yields a separate provider
    /// the application never uses, which creates its singletons a second time and is the caller's to
    /// dispose. <c>WebApplicationBuilder</c>'s <c>Services</c> is the same.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

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
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    public DatabaseApplicationBuilder AddEngine(DatabaseEngine engine)
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
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    public DatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, DatabaseEngine> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _engines.Add((null, null, context => configure(context)));
        return this;
    }

    /// <summary>
    /// Reserves an engine name and defers construction until Build, handing the factory the
    /// application's environment, configuration, built service provider and logger factory.
    /// </summary>
    /// <param name="name">The ordinal engine name, which must match the factory product.</param>
    /// <param name="factory">The ownership-transferring factory.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    public DatabaseApplicationBuilder AddEngine(string name, Func<DatabaseApplicationBuildContext, DatabaseEngine> factory)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        ReserveName(name);
        _engines.Add((null, name, context => factory(context.BuildContext)));
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
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> or <paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> targets another data model than <paramref name="engine"/>.</exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    /// <remarks>Build refuses an engine the application did not register.</remarks>
    public DatabaseApplicationBuilder Provision(DatabaseEngine engine, CompiledSchema schema)
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
    /// <exception cref="ArgumentNullException"><paramref name="engineName"/> or <paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="engineName"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    /// <remarks>Build refuses a name no engine of the application has, or a schema of another data model.</remarks>
    public DatabaseApplicationBuilder Provision(string engineName, CompiledSchema schema)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(engineName);
        ArgumentNullException.ThrowIfNull(schema);
        _schemas.Add(schema);
        return AddService(context =>
        {
            DatabaseEngine engine = context.GetEngine(engineName);
            ValidateSchema(engine, schema);
            return new DefaultDatabaseProvisioner(engine, schema);
        });
    }

    /// <summary>Registers a compiled logical database on a borrowed registered engine.</summary>
    /// <param name="engine">The registered engine.</param>
    /// <param name="name">The name matching the compiled schema.</param>
    /// <param name="schema">The compiled schema.</param>
    /// <returns>The same compiled schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/>, <paramref name="name"/> or <paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, white space or not the schema's name, or <paramref name="schema"/> targets
    /// another data model than <paramref name="engine"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    /// <remarks>Build refuses an engine the application did not register.</remarks>
    public CompiledSchema AddDatabase(DatabaseEngine engine, string name, CompiledSchema schema)
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
    /// <exception cref="ArgumentNullException"><paramref name="engineName"/>, <paramref name="name"/> or <paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="engineName"/> or <paramref name="name"/> is empty or white space, or <paramref name="name"/> is
    /// not the schema's name.
    /// </exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    /// <remarks>Build refuses a name no engine of the application has, or a schema of another data model.</remarks>
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

        // The application owns the host-level pieces from here, the content root's file system and
        // the configuration first, so a failed Build disposes them with everything else.
        var ownership = new DatabaseApplicationOwnership();
        if (_contentRoot is not null)
        {
            ownership.Infrastructure.Add(_contentRoot);
        }

        ownership.Infrastructure.Add(Configuration);
        try
        {
            // Disposed after the provider, so the services it disposes can still log. The provider
            // is built first, so service registration closes whatever fails later.
            var loggerFactory = (LoggerFactory)Logging.Build();
            ownership.Infrastructure.Add(loggerFactory);
            ServiceProvider services = BuildServiceProvider(loggerFactory);
            ownership.Infrastructure.Add(services);
            DatabaseApplicationOptions options = SnapshotOptions(_options);

            // The environment was read when the builder was created; a later change to the options
            // must not leave the snapshot disagreeing with the context.
            options.Environment = Environment.Name;
            options.ContentRootPath = Environment.ContentRootPath;
            var context = new DatabaseApplicationContext(options, Environment, Configuration, services, loggerFactory);

            var products = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var registeredEngines = new HashSet<DatabaseEngine>(ReferenceEqualityComparer.Instance);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var borrowedEngines = new List<DatabaseEngine>(options.Engines);
            borrowedEngines.AddRange(_engines.Where(registration => registration.Instance is not null).Select(registration => registration.Instance!));
            borrowedEngines.AddRange(options.Servers.Select(server => server.Engine));
            foreach (DatabaseEngine engine in borrowedEngines)
            {
                products.Add(engine);
                foreach (DatabaseServer server in engine.Servers)
                {
                    products.Add(server);
                }

                foreach (DatabaseEngineWorker worker in engine.Workers)
                {
                    products.Add(worker);
                }
            }
            foreach (DatabaseServer server in options.Servers)
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
            void RegisterEngine(DatabaseEngine engine)
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
            foreach (DatabaseEngine engine in inputEngines)
            {
                RegisterEngine(engine);
            }

            foreach (var registration in _engines)
            {
                if (registration.Instance is not null) { RegisterEngine(registration.Instance); continue; }
                DatabaseEngine engine = registration.Factory!(context) ?? throw new InvalidOperationException("An engine factory returned null.");
                if (!products.Add(engine))
                {
                    throw new InvalidOperationException("An engine factory returned an already registered product.");
                }

                ownership.Engines.Add(engine);
                foreach (DatabaseServer server in engine.Servers)
                {
                    products.Add(server);
                }

                foreach (DatabaseEngineWorker worker in engine.Workers)
                {
                    products.Add(worker);
                }

                if (registration.Name is not null && !string.Equals(registration.Name, engine.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Engine factory '{registration.Name}' returned engine '{engine.Name}'.");
                }

                RegisterEngine(engine);
            }
            foreach (DatabaseServer server in options.Servers)
            {
                RegisterEngine(server.Engine);
            }

            var borrowedServers = options.Servers.ToArray();
            options.Servers.Clear();
            var registeredServers = new HashSet<DatabaseServer>(ReferenceEqualityComparer.Instance);
            foreach (DatabaseEngine engine in options.Engines)
            {
                // The engine base attaches only a server that fronts it, and each server once, so
                // a nested server belongs to exactly one registered engine (phase 6 of the
                // concrete-types plan retired the checks the root interface needed here).
                foreach (DatabaseServer server in engine.Servers)
                {
                    registeredServers.Add(server);
                    products.Add(server);
                    options.Servers.Add(server);
                }
            }
            foreach (DatabaseServer server in borrowedServers)
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

            // The module's own reopen service (owner decision 22): started with the other services,
            // before the servers, and stopped after the servers drained. It supervises every engine
            // of the frozen registry, the server-fronted ones included.
            if (options.ReopenOfflineDatabases)
            {
                var reopen = new DatabaseReopenService(context.Engines, options.ReopenInitialDelay, options.ReopenMaximumDelay);
                ownership.Services.Add(reopen);
                options.Services.Add(reopen);
                context.ReopenService = reopen;
            }

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

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReopenInitialDelay, TimeSpan.Zero, nameof(options.ReopenInitialDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReopenMaximumDelay, options.ReopenInitialDelay, nameof(options.ReopenMaximumDelay));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ReopenMaximumDelay, DatabaseApplicationOptions.MaximumReopenDelay, nameof(options.ReopenMaximumDelay));

        var snapshot = new DatabaseApplicationOptions
        {
            Environment = options.Environment,
            ContentRootPath = options.ContentRootPath,
            StartupTimeout = options.StartupTimeout,
            ShutdownTimeout = options.ShutdownTimeout,
            ReopenOfflineDatabases = options.ReopenOfflineDatabases,
            ReopenInitialDelay = options.ReopenInitialDelay,
            ReopenMaximumDelay = options.ReopenMaximumDelay,
        };
        foreach (DatabaseEngine engine in options.Engines)
        {
            snapshot.Engines.Add(engine);
        }

        foreach (DatabaseServer server in options.Servers)
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

    /// <summary>
    /// Closes service registration and builds the application's provider once: checks every
    /// registration, registers the environment, the configuration and the logger factory as
    /// borrowed instances, makes the container read-only, and builds.
    /// </summary>
    /// <param name="loggerFactory">The application's logger factory.</param>
    /// <returns>The provider, which the application owns.</returns>
    /// <exception cref="InvalidOperationException">
    /// A registration needs constructor activation or is open generic, or registers a reserved
    /// service type.
    /// </exception>
    private ServiceProvider BuildServiceProvider(LoggerFactory loggerFactory)
    {
        var container = (ServiceContainer)Services.Container;
        try
        {
            foreach (ServiceDescriptor descriptor in container)
            {
                if (descriptor.ServiceType == typeof(IConfiguration)
                    || descriptor.ServiceType == typeof(IHostEnvironment)
                    || descriptor.ServiceType == typeof(ILoggerFactory))
                {
                    throw new InvalidOperationException(
                        $"{descriptor.ServiceType.Name} is reserved for the application's own {Describe(descriptor.ServiceType)}.");
                }

                if (descriptor.ImplementationType is not null || descriptor.ServiceType.ContainsGenericParameters)
                {
                    throw new InvalidOperationException("Database hosting requires closed factory or instance service registrations.");
                }
            }

            Services.AddSingleton<IHostEnvironment>(Environment);
            Services.AddSingleton<IConfiguration>(Configuration);
            Services.AddSingleton<ILoggerFactory>(loggerFactory);
        }
        finally
        {
            // Registration closes here, whatever Build does next: the provider copies the
            // registrations, so one added later would silently never reach it.
            container.MakeReadOnly();
        }

        return (ServiceProvider)((IServiceProviderBuilder)Services).Build();

        static string Describe(Type reserved)
            => reserved == typeof(IConfiguration) ? "configuration"
                : reserved == typeof(IHostEnvironment) ? "host environment"
                : "logger factory";
    }

    /// <summary>
    /// Adds the default configuration sources, in order: optional <c>appsettings.json</c> and
    /// <c>appsettings.{Environment}.json</c> under the content root (the options' content root, or
    /// the application base directory), environment variables prefixed <c>COHESION_CONFIG__</c>,
    /// then the ambient resource settings and the command-line arguments. Later sources win.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="resourceContext">The ambient resource context, or null for a plain application.</param>
    /// <returns>The content root's read-only file system, which the application owns from Build.</returns>
    private PhysicalFileSystem AddDefaultConfiguration(string[] args, ResourceContext? resourceContext)
    {
        var contentRoot = new PhysicalFileSystem(new PhysicalFileSystemOptions
        {
            Root = _options.ContentRootPath ?? FileSystemPath.Parse(AppContext.BaseDirectory),
            IsReadOnly = true,
        });

        try
        {
            Configuration
                .AddJsonFile(contentRoot, "appsettings.json", optional: true)
                .AddJsonFile(contentRoot, $"appsettings.{Environment.Name}.json", optional: true)
                .AddEnvironmentVariables("COHESION_CONFIG__")
                .AddCommandLine(PrependAmbientSettings(args, resourceContext));
        }
        catch
        {
            contentRoot.Dispose();
            throw;
        }

        return contentRoot;
    }

    // The ambient resource settings as command-line arguments ahead of the caller's, so the
    // caller's win, as WebApplicationBuilder adds them.
    private static string[] PrependAmbientSettings(string[] args, ResourceContext? resourceContext)
    {
        if (resourceContext is null || resourceContext.Settings.Count == 0)
        {
            return [.. args];
        }

        var combined = new string[resourceContext.Settings.Count + args.Length];
        int index = 0;
        foreach ((string key, string value) in resourceContext.Settings)
        {
            combined[index++] = $"--{key}={value}";
        }

        args.CopyTo(combined, index);
        return combined;
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

    private static void ValidateSchema(DatabaseEngine engine, CompiledSchema schema)
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

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(DatabaseEngine engine) => AddEngine(engine);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(Func<IDatabaseApplicationContext, DatabaseEngine> configure) => AddEngine(configure);
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
