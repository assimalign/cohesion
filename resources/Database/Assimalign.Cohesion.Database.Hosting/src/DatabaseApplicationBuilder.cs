using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.CommandLine;
using Assimalign.Cohesion.Configuration.Json;
using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// Composes a database application. Every dependency the application runs with is a registration in
/// <see cref="Services"/>: engines (<see cref="DatabaseEngineRegistration"/>, one per engine name) and
/// lifecycle services (<see cref="IHostService"/>).
/// </summary>
/// <remarks>
/// <para>
/// The host-level pieces are the ones <c>WebApplicationBuilder</c> exposes, created the same way when
/// the builder is created: <see cref="Environment"/>, the <see cref="Configuration"/> manager (which
/// loads each provider as it is added), the <see cref="Logging"/> builder and the
/// <see cref="Services"/> registrations. <see cref="Build"/> closes registration, builds the logger
/// factory and the service provider once, and resolves every engine and then every lifecycle service
/// from it, in registration order. The provider owns what its factories create and disposes it in
/// reverse creation order, so services dispose before the engines they use.
/// </para>
/// <para>
/// The root <see cref="IDatabaseApplicationBuilder"/> verbs are implemented explicitly as shims over
/// those registrations, so model packages compose against the dependency-free root contract while
/// this module owns the container. A model package also projects its verb onto
/// <see cref="Services"/> as a component integration (<c>builder.Services.AddSql(name, ...)</c>),
/// which registers the same <see cref="DatabaseEngineRegistration"/>.
/// </para>
/// <para>
/// <b>The application level</b> of the three composition levels (B1 of the engine extensibility
/// design, owner decisions 52 and 54 of 2026-10-09): host infrastructure, host services, health,
/// the reopen policy, and which engines exist. It holds no database, schema,
/// function or model type: a model's engine builder declares and provisions its databases while
/// it is built. Every engine is registered by name, and a duplicate name is refused before any
/// engine factory runs: when the engine is registered through this builder, and again when Build
/// begins, for registrations added to <see cref="Services"/> directly.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseApplicationBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationBuilder.DebugView))]
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;
    private readonly PhysicalFileSystem? _contentRoot;
    private Uri? _healthEndpoint;
    private DatabaseApplicationContext? _context;
    private int _buildAttempted;

    /// <summary>Initializes a builder with host settings.</summary>
    /// <param name="options">
    /// The host settings: their <c>Environment</c> and
    /// <see cref="DatabaseApplicationOptions.ContentRootPath"/> are read now, for
    /// <see cref="Environment"/>; the rest is copied when Build begins.
    /// </param>
    /// <remarks>
    /// The configuration starts empty, as <c>new WebApplicationBuilder(options)</c>'s does:
    /// <see cref="DatabaseApplication.CreateBuilder(string[])"/> adds the default sources.
    /// </remarks>
    public DatabaseApplicationBuilder(DatabaseApplicationOptions options) : this(options, null) { }

    internal DatabaseApplicationBuilder(DatabaseApplicationOptions options, string[]? args)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        Environment = new HostEnvironment(_options.Environment ?? "production")
        {
            ContentRootPath = _options.ContentRootPath,
        };
        Configuration = new ConfigurationManager();
        if (args is not null)
        {
            _contentRoot = AddDefaultConfiguration(args);
        }

        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Gets the host settings, host policy only (timeouts and the reopen policy):
    /// <c>Environment</c> and <see cref="DatabaseApplicationOptions.ContentRootPath"/> are read when
    /// the builder is created (see <see cref="Environment"/>); the rest is copied at Build.
    /// </summary>
    /// <remarks>
    /// Setting the environment or the content root here after the builder exists changes nothing:
    /// Build's snapshot takes both from <see cref="Environment"/>, so the application's options and
    /// its context never disagree.
    /// </remarks>
    public DatabaseApplicationOptions Options => _options;

    /// <summary>
    /// Gets the application's host environment: its name and content root, read from
    /// <see cref="Options"/> when the builder was created.
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
    /// <c>COHESION_CONFIG__</c>, then the command-line arguments;
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
    /// disposes it after every service and engine. The builder refuses changes after Build.
    /// <see cref="Build"/> creates the application's one logger factory; building this builder
    /// directly yields a separate factory the application never uses, and makes the application's
    /// Build fail, since the builder builds once. <c>WebApplicationBuilder</c>'s <c>Logging</c> is
    /// the same.
    /// </remarks>
    public LoggerFactoryBuilder Logging { get; }


    /// <summary>
    /// Gets the application's service registrations: every engine and lifecycle service the
    /// application runs with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration closes when Build begins; a later registration throws
    /// <see cref="InvalidOperationException"/>. The provider is created without dynamic code, so the
    /// application accepts closed factory and instance registrations only, and reserves
    /// <see cref="IHostEnvironment"/>, <see cref="IConfiguration"/>, <see cref="ILoggerFactory"/>, the
    /// application context and <see cref="DatabaseEngine"/> for its own pieces.
    /// </para>
    /// <para>
    /// An engine is a <see cref="DatabaseEngineRegistration"/> singleton instance, as
    /// <see cref="AddEngine(DatabaseEngine)"/>, the root verbs (<c>builder.AddSql(...)</c>) and the
    /// model packages' component integrations (<c>builder.Services.AddSql(...)</c>) register it. A
    /// registration added through a factory is refused, because its name must be known before any
    /// engine factory runs. Build creates the engines first, in registration order; each engine
    /// factory sees the engines registered before it.
    /// </para>
    /// <para>
    /// Unkeyed <see cref="IHostService"/> registrations must be singletons. They start in registration
    /// order, <see cref="AddService(IHostService)"/> and <see cref="AddService(Func{DatabaseApplicationContext, IHostService})"/>
    /// included, followed by the health listener when configured, the reopen service and the servers;
    /// stop reverses that order, draining servers first. Build resolves them once, after every engine
    /// exists. Factory-created services and engines are owned by the provider, which disposes them in
    /// reverse creation order; instances stay owned by their callers. A forwarding factory
    /// (sp => sp.GetRequiredService&lt;X&gt;()) hands the provider an object it already owns, which it
    /// disposes once. Build creates the application's one provider; building these registrations
    /// directly yields a separate provider the application never uses, which creates its singletons a
    /// second time and is the caller's to dispose. <c>WebApplicationBuilder</c>'s <c>Services</c> is
    /// the same.
    /// </para>
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>Registers a caller-owned engine.</summary>
    /// <param name="engine">The borrowed engine.</param>
    /// <returns>This builder.</returns>
    /// <remarks>Registered in <see cref="Services"/> as a <see cref="DatabaseEngineRegistration"/> instance.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="ArgumentException">The engine name is empty, white space or invalid UTF-16.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    public DatabaseApplicationBuilder AddEngine(DatabaseEngine engine)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(DatabaseEngineRegistration.Create(engine));
    }

    /// <summary>
    /// Reserves an engine name and defers construction until Build, handing the factory the
    /// application's environment, configuration, built service provider and logger factory.
    /// </summary>
    /// <param name="name">The ordinal engine name, which must match the factory product.</param>
    /// <param name="factory">The ownership-transferring factory.</param>
    /// <returns>This builder.</returns>
    /// <remarks>Registered in <see cref="Services"/> as a <see cref="DatabaseEngineRegistration"/> instance.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, white space or invalid UTF-16.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    public DatabaseApplicationBuilder AddEngine(string name, Func<DatabaseApplicationBuildContext, DatabaseEngine> factory)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        ValidateName(name);
        return AddEngine(DatabaseEngineRegistration.Create(name, context => factory(((DatabaseApplicationContext)context).BuildContext)));
    }

    /// <summary>Registers a borrowed lifecycle service.</summary>
    /// <param name="service">The caller-owned service.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Registered in <see cref="Services"/> as an <see cref="IHostService"/> instance. Lifecycle
    /// services start in registration order before the reopen service and the servers; stop reverses
    /// the complete order, draining servers first.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Build has begun.</exception>
    public DatabaseApplicationBuilder AddService(IHostService service)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(service);
        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>Adds a lifecycle service created from the final application context.</summary>
    /// <param name="service">The ownership-transferring factory.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Registered in <see cref="Services"/> as an <see cref="IHostService"/> factory, which Build
    /// invokes once, after every engine and server exists. Lifecycle services start in registration
    /// order before the reopen service and the servers; stop reverses that order. The application
    /// disposes the product before the engines.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Build has begun.</exception>
    public DatabaseApplicationBuilder AddService(Func<DatabaseApplicationContext, IHostService> service)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(service);
        Services.AddSingleton<IHostService>(_ => service(_context!));
        return this;
    }

    /// <summary>Configures the application's HTTP health listener.</summary>
    /// <param name="endpoint">A plain HTTP endpoint with a literal IP address or localhost.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The last call replaces the previous endpoint. When the runtime environment names a gateway
    /// and assigns a plain HTTP endpoint with a bindable host, that platform endpoint wins.
    /// Unsupported platform assignments are reported and skipped. Without either configuration,
    /// no health listener is created. The listener starts before model servers and stops after
    /// they drain. Namespaced health routes accept loopback peers only.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is not an endpoint URI.</exception>
    /// <exception cref="InvalidOperationException">Build has begun, or the scheme or host is unsupported.</exception>
    public DatabaseApplicationBuilder AddHealthEndpoint(Uri endpoint)
    {
        EnsureMutable();
        _ = DatabaseAdminEndpointService.GetBindEndPoint(endpoint);
        _healthEndpoint = endpoint;
        return this;
    }

    /// <summary>Consumes this builder and constructs one complete application.</summary>
    /// <param name="cancellationToken">Cancels construction, recovery and inline provisioning through engine factories.</param>
    /// <returns>The application owning factory products and borrowing supplied instances.</returns>
    /// <exception cref="InvalidOperationException">Build was already attempted or composition is invalid.</exception>
    /// <exception cref="AggregateException">Construction and compensation both failed.</exception>
    /// <exception cref="OperationCanceledException">Construction was canceled; owned products were disposed.</exception>
    /// <remarks>
    /// Build runs each owned engine factory synchronously, in registration order, through the
    /// service provider. A SQL engine's
    /// factory provisions its declared databases here: open, recovery and schema migration run
    /// inside Build with no timeout (<see cref="DatabaseApplicationOptions.StartupTimeout"/> bounds
    /// service start only). Decision 49a passes the caller's cancellation token and defers
    /// data-scaling SQL migration steps to the engine's worker. The parameterless root
    /// <see cref="IDatabaseApplicationBuilder.Build"/> forwards here without a token; callers that
    /// need cancellation retain the concrete builder. A factory's
    /// exception, for example a SQL schema migration failure led by <c>CS03101</c> to
    /// <c>CS03105</c>, propagates unchanged after the products built before it are disposed.
    /// </remarks>
    public DatabaseApplication Build(CancellationToken cancellationToken = default)
    {
        DatabaseApplicationComposition composition = BuildComposition(cancellationToken);
        try
        {
            var application = new DatabaseApplication(composition);
            DatabaseHostingEventSource.Log.ApplicationBuilt(application.Context, composition.Services.Count);
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

    internal DatabaseApplicationComposition BuildComposition(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _buildAttempted, 1) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.BuilderBuildCompositionFailure);
        }

        // The application owns the host-level pieces from here, the content root's file system and
        // the configuration first, so a failed Build disposes them with everything else. The provider
        // joins them once it exists; it owns every engine and service its factories create.
        var ownership = new DatabaseApplicationOwnership();
        if (_contentRoot is not null)
        {
            ownership.Infrastructure.Add(_contentRoot);
        }

        ownership.Infrastructure.Add(Configuration);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Disposed after the provider, so the services it disposes can still log.
            var loggerFactory = (LoggerFactory)Logging.Build();
            ownership.Infrastructure.Add(loggerFactory);
            DatabaseApplicationOptions options = SnapshotOptions(_options);

            // The environment was read when the builder was created; a later change to the options
            // must not leave the snapshot disagreeing with the context.
            options.Environment = Environment.Name;
            options.ContentRootPath = Environment.ContentRootPath;

            Uri? healthEndpoint = ResolveHealthEndpoint();
            var composer = new EngineComposer(this, cancellationToken);
            ServiceProvider services = BuildServiceProvider(loggerFactory, composer, healthEndpoint, options);
            ownership.Infrastructure.Add(services);

            _context = new DatabaseApplicationContext(Environment, Configuration, services, loggerFactory, cancellationToken);
            DatabaseApplicationContext context = _context;

            // Engines first, in registration order: the provider invokes each owned factory once and
            // owns its product, so every service resolved below is created, and disposed, after them.
            IReadOnlyList<DatabaseEngine> engines = composer.Compose(services, context);
            cancellationToken.ThrowIfCancellationRequested();

            var servers = new List<DatabaseServer>();
            foreach (DatabaseEngine engine in engines)
            {
                // The engine base attaches only a server that fronts it, and each server once, so
                // a nested server belongs to exactly one registered engine (phase 6 of the
                // concrete-types plan retired the checks the root interface needed here).
                servers.AddRange(engine.Servers);
            }

            context.FreezeRegistries(engines, servers);
            IReadOnlyList<IHostService> hostServices = ResolveLifecycleServices(services, composer.Products, servers);
            return new DatabaseApplicationComposition(options, context, ownership, hostServices);
        }
        catch (Exception exception)
        {
            // Validation may fail before the provider closes registration. Build is still consumed,
            // so the container must refuse late mutation on this path too.
            ((ServiceContainer)Services.Container).MakeReadOnly();
            var failures = new List<Exception>();
            Task.Run(() => ownership.DisposeAsync(failures)).GetAwaiter().GetResult();
            if (failures.Count > 0)
            {
                throw new AggregateException(new[] { exception }.Concat(failures));
            }

            throw;
        }
    }

    // The configured endpoint, unless the runtime environment names a gateway and assigns a bindable
    // plain HTTP endpoint, which wins.
    private Uri? ResolveHealthEndpoint()
    {
        Uri? healthEndpoint = _healthEndpoint;
        if (!string.IsNullOrWhiteSpace(AppEnvironment.GetValue(AppEnvironment.Variables.Gateway))
            && AppEnvironment.TryGetEndpoint("http", out Uri? platformEndpoint))
        {
            if (DatabaseAdminEndpointService.CanBindPlatformEndpoint(platformEndpoint))
            {
                if (healthEndpoint is not null && healthEndpoint != platformEndpoint)
                {
                    DatabaseHostingEventSource.Log.HealthEndpointOverridden(healthEndpoint, platformEndpoint);
                }
                healthEndpoint = platformEndpoint;
            }
            else
            {
                DatabaseHostingEventSource.Log.HealthEndpointAssignmentSkipped(platformEndpoint);
            }
        }

        if (healthEndpoint is not null)
        {
            _ = DatabaseAdminEndpointService.GetBindEndPoint(healthEndpoint);
        }

        return healthEndpoint;
    }

    // Resolves the IHostService aggregate once, in registration order, and checks what the container
    // cannot: a null product, one object registered twice or also fronting an engine as a server, and a
    // factory product that is already another registered product. The descriptors are read in the same
    // order the provider resolves them, so each product is paired with the registration that made it.
    private IReadOnlyList<IHostService> ResolveLifecycleServices(ServiceProvider services, HashSet<object> products, List<DatabaseServer> servers)
    {
        var descriptors = new List<ServiceDescriptor>();
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ServiceType == typeof(IHostService))
            {
                descriptors.Add(descriptor);
            }
        }

        IHostService?[] resolved = [.. services.GetServices<IHostService>()];
        var lifecycleProducts = new HashSet<object>(servers, ReferenceEqualityComparer.Instance);
        var hostServices = new List<IHostService>(resolved.Length);
        for (int index = 0; index < resolved.Length; index++)
        {
            IHostService service = resolved[index] ?? throw new InvalidOperationException(ErrorMessages.BuilderBuildCompositionFailure6);
            if (!lifecycleProducts.Add(service))
            {
                throw new InvalidOperationException(ErrorMessages.BuilderBuildCompositionFailure8);
            }

            if (descriptors[index].ImplementationInstance is null && !products.Add(service))
            {
                throw new InvalidOperationException(ErrorMessages.BuilderBuildCompositionFailure7);
            }

            products.Add(service);
            hostServices.Add(service);
        }

        return hostServices.AsReadOnly();
    }

    internal static DatabaseApplicationOptions SnapshotOptions(DatabaseApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartServicesConcurrently || options.StopServicesConcurrently)
        {
            throw new InvalidOperationException(ErrorMessages.BuilderSnapshotOptionsFailure);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReopenInitialDelay, TimeSpan.Zero, nameof(options.ReopenInitialDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReopenMaximumDelay, options.ReopenInitialDelay, nameof(options.ReopenMaximumDelay));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ReopenMaximumDelay, DatabaseApplicationOptions.MaximumReopenDelay, nameof(options.ReopenMaximumDelay));

        return new DatabaseApplicationOptions
        {
            Environment = options.Environment,
            ContentRootPath = options.ContentRootPath,
            StartupTimeout = options.StartupTimeout,
            ShutdownTimeout = options.ShutdownTimeout,
            ReopenOfflineDatabases = options.ReopenOfflineDatabases,
            ReopenInitialDelay = options.ReopenInitialDelay,
            ReopenMaximumDelay = options.ReopenMaximumDelay,
        };
    }

    private void EnsureMutable()
    {
        if (Volatile.Read(ref _buildAttempted) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.BuilderEnsureMutableFailure);
        }
    }

    /// <summary>
    /// Closes service registration and builds the application's provider once: checks every
    /// registration and every engine name, registers the environment, the configuration, the logger
    /// factory and the application context, one <see cref="DatabaseEngine"/> per engine registration,
    /// the health listener and the reopen service, makes the container read-only, and builds.
    /// </summary>
    /// <returns>The provider, which the application owns.</returns>
    /// <exception cref="InvalidOperationException">
    /// A registration needs constructor activation or is open generic, registers a reserved service
    /// type, registers an engine through a factory, or repeats an engine name.
    /// </exception>
    private ServiceProvider BuildServiceProvider(LoggerFactory loggerFactory, EngineComposer composer, Uri? healthEndpoint, DatabaseApplicationOptions options)
    {
        var container = (ServiceContainer)Services.Container;
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ServiceDescriptor descriptor in container)
            {
                if (descriptor.ServiceType == typeof(IHostService) && descriptor.Lifetime != ServiceLifetime.Singleton)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.LifecycleServiceLifetimeInvalid, descriptor));
                }

                if (descriptor.ServiceType == typeof(IConfiguration)
                    || descriptor.ServiceType == typeof(IHostEnvironment)
                    || descriptor.ServiceType == typeof(DatabaseApplicationContext)
                    || descriptor.ServiceType == typeof(IDatabaseApplicationContext)
                    || descriptor.ServiceType == typeof(ILoggerFactory)
                    || descriptor.ServiceType == typeof(DatabaseEngine))
                {
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.BuilderBuildServiceProviderFailure, descriptor.ServiceType.Name, Describe(descriptor.ServiceType)));
                }

                if (descriptor.ImplementationType is not null || descriptor.ServiceType.ContainsGenericParameters)
                {
                    throw new InvalidOperationException(ErrorMessages.BuilderBuildServiceProviderFailure2);
                }

                // Every engine name is checked here, before any engine factory runs (owner decision 52):
                // a registration added to Services directly, as the model packages' component
                // integrations add theirs, was not checked when it was registered.
                if (descriptor.ServiceType == typeof(DatabaseEngineRegistration))
                {
                    if (descriptor.ImplementationInstance is not DatabaseEngineRegistration registration)
                    {
                        throw new InvalidOperationException(ErrorMessages.EngineRegistrationNotInstance);
                    }

                    ValidateName(registration.Name);
                    if (!names.Add(registration.Name))
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationBuilderReserveNameFailure, registration.Name));
                    }

                    composer.Registrations.Add(registration);
                }
            }

            Services.AddSingleton<IHostEnvironment>(Environment);
            Services.AddSingleton<IConfiguration>(Configuration);
            Services.AddSingleton<ILoggerFactory>(loggerFactory);
            // Created before engine factories run; lifecycle factories resolve it after registries freeze.
            // The application owns the context, which is not disposable; the provider must never own it.
            Func<IServiceProvider, DatabaseApplicationContext> contextFactory = _ => _context!;
            Services.AddSingleton<DatabaseApplicationContext>(contextFactory);
            Services.AddSingleton<IDatabaseApplicationContext>(contextFactory);

            // One DatabaseEngine per registration, in registration order: a borrowed engine as an
            // instance, which the provider never disposes, and an owned one as a factory, whose product
            // the provider owns and disposes after every service created later.
            for (int index = 0; index < composer.Registrations.Count; index++)
            {
                DatabaseEngineRegistration registration = composer.Registrations[index];
                if (registration.IsOwned)
                {
                    int position = index;
                    Services.AddSingleton<DatabaseEngine>(_ => composer.CreateOwned(position));
                }
                else
                {
                    Services.AddSingleton<DatabaseEngine>(registration.Engine!);
                }
            }

            // The module's own lifecycle services follow the application's, so they start after them
            // and before the servers, and stop after the servers drained: the health listener, then
            // the reopen service (owner decision 22), which supervises every engine of the frozen
            // registry, the server-fronted ones included. Their factories run after the registry froze.
            if (healthEndpoint is not null)
            {
                Services.AddSingleton<IHostService>(_ => new DatabaseAdminEndpointService(healthEndpoint, _context!));
            }

            if (options.ReopenOfflineDatabases)
            {
                Services.AddSingleton<IHostService>(_ =>
                {
                    var reopen = new DatabaseReopenService(_context!.Engines, options.ReopenInitialDelay, options.ReopenMaximumDelay);
                    _context.ReopenService = reopen;
                    return reopen;
                });
            }
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
                : reserved == typeof(DatabaseApplicationContext) || reserved == typeof(IDatabaseApplicationContext) ? "application context"
                : reserved == typeof(DatabaseEngine) ? "engine registrations (register a DatabaseEngineRegistration)"
                : "logger factory";
    }

    /// <summary>
    /// Adds the default configuration sources, in order: optional <c>appsettings.json</c> and
    /// <c>appsettings.{Environment}.json</c> under the content root (the options' content root, or
    /// the application base directory), environment variables prefixed <c>COHESION_CONFIG__</c>,
    /// then the command-line arguments. Later sources win.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The content root's read-only file system, which the application owns from Build.</returns>
    private PhysicalFileSystem AddDefaultConfiguration(string[] args)
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
                .AddCommandLine([.. args]);
        }
        catch
        {
            contentRoot.Dispose();
            throw;
        }

        return contentRoot;
    }

    private DatabaseApplicationBuilder AddEngine(DatabaseEngineRegistration registration)
    {
        ValidateName(registration.Name);
        ReserveName(registration.Name);
        Services.AddSingleton<DatabaseEngineRegistration>(registration);
        return this;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        for (int index = 0; index < name.Length; index++)
        {
            if (!char.IsSurrogate(name[index])) { continue; }
            if (!char.IsSurrogatePair(name, index))
            {
                throw new ArgumentException(ErrorMessages.EngineNameInvalidUtf16, nameof(name));
            }
            index++;
        }
    }

    // Every engine registered through this builder is refused here when its name is taken, before any
    // factory runs (owner decision 52): a factory can open and provision databases. The duplicate check
    // reads the registrations in Services, never a second collection.
    private void ReserveName(string name)
    {
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is DatabaseEngineRegistration registration
                && string.Equals(registration.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationBuilderReserveNameFailure, name));
            }
        }
    }

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(DatabaseEngine engine) => AddEngine(engine);

    // The root seam's named factory, which every model verb calls: the name is reserved now, and the
    // factory receives the application's context (it implements the root's), never the container.
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(string name, Func<IDatabaseApplicationContext, DatabaseEngine> factory)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(factory);
        ValidateName(name);
        return AddEngine(DatabaseEngineRegistration.Create(name, factory));
    }

    IDatabaseApplication IDatabaseApplicationBuilder.Build() => Build();

    // Creates the owned engines as the provider resolves them, in registration order, and checks what the
    // container cannot: a null product, a product that is another registered object, a name that is not
    // the registered one, and an unknown model. A factory sees the engines registered before it.
    private sealed class EngineComposer
    {
        private readonly DatabaseApplicationBuilder _builder;
        private readonly CancellationToken _cancellationToken;
        private DatabaseEngine?[] _engines = [];

        internal EngineComposer(DatabaseApplicationBuilder builder, CancellationToken cancellationToken)
        {
            _builder = builder;
            _cancellationToken = cancellationToken;
        }

        internal List<DatabaseEngineRegistration> Registrations { get; } = [];

        // Every engine, server and worker, and every lifecycle service, so a factory that returns one of
        // them as a new product is refused.
        internal HashSet<object> Products { get; } = new(ReferenceEqualityComparer.Instance);

        internal IReadOnlyList<DatabaseEngine> Compose(ServiceProvider services, DatabaseApplicationContext context)
        {
            _engines = new DatabaseEngine?[Registrations.Count];
            for (int index = 0; index < Registrations.Count; index++)
            {
                if (Registrations[index].Engine is { } borrowed)
                {
                    _engines[index] = borrowed;
                    AddProducts(Products, borrowed);
                }
            }

            // The provider invokes the factories in registration order; each one fills its slot.
            _ = services.GetServices<DatabaseEngine>().ToArray();
            var engines = new List<DatabaseEngine>(_engines.Length);
            foreach (DatabaseEngine? engine in _engines)
            {
                engines.Add(engine!);
            }

            foreach (DatabaseEngine engine in engines)
            {
                if (engine.Model is not (EngineModel.Custom or EngineModel.Sql or EngineModel.Document or EngineModel.KeyValueStore or EngineModel.Blob or EngineModel.Graph))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.BuilderBuildCompositionFailure5, engine.Name));
                }
            }

            context.FreezeRegistries(engines, []);
            return engines.AsReadOnly();
        }

        internal DatabaseEngine CreateOwned(int position)
        {
            DatabaseApplicationContext context = _builder._context!;
            DatabaseEngineRegistration registration = Registrations[position];
            _cancellationToken.ThrowIfCancellationRequested();
            context.FreezeRegistries(_engines.Take(position).Select(engine => engine!), []);
            DatabaseEngine engine = registration.CreateEngine(context)
                ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.BuilderBuildCompositionFailure2, registration.Name));
            if (!Products.Add(engine))
            {
                // Another registration's object: not this factory's to dispose.
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.BuilderBuildCompositionFailure3, registration.Name));
            }

            try
            {
                AddProducts(Products, engine);
                if (!string.Equals(registration.Name, engine.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.BuilderBuildCompositionFailure4, registration.Name, engine.Name));
                }
            }
            catch (Exception exception)
            {
                // The provider captures only what a factory returns, so a refused product is disposed here.
                try
                {
                    Task.Run(() => engine.DisposeAsync().AsTask()).GetAwaiter().GetResult();
                }
                catch (Exception disposal)
                {
                    throw new AggregateException(exception, disposal);
                }

                throw;
            }

            _engines[position] = engine;
            return engine;
        }

        // A borrowed engine's own products, and a factory engine's, so a factory that returns one of them
        // as a new product is refused.
        private static void AddProducts(HashSet<object> products, DatabaseEngine engine)
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
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationBuilder _target;

        public DebugView(global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationBuilder value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationOptions Options => _target.Options;
        public global::Assimalign.Cohesion.Hosting.HostEnvironment Environment => _target.Environment;
        public global::Assimalign.Cohesion.Configuration.ConfigurationManager Configuration => _target.Configuration;
        public global::Assimalign.Cohesion.Logging.LoggerFactoryBuilder Logging => _target.Logging;
        public global::Assimalign.Cohesion.DependencyInjection.ServiceProviderBuilder Services => _target.Services;
    }
}
