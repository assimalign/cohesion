using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// The default <see cref="IDatabaseApplicationBuilder"/> implementation: collects
/// engine and server registrations — including those made by model verbs such as
/// <c>AddSqlDatabase(...)</c> and <c>AddSqlServer(...)</c>, which compose against
/// the root interface without referencing this module — and builds the
/// <see cref="DatabaseApplication"/> host. Create one with
/// <see cref="DatabaseApplication.CreateBuilder()"/>.
/// </summary>
/// <remarks>
/// The builder wraps a <see cref="DatabaseApplicationOptions"/> instance, exposed
/// through <see cref="Options"/> for hosting-specific settings and fully manual
/// composition. Engine registrations go to <c>Options.Engines</c>; service and
/// server registrations preserve their respective registration order, with
/// deferred factories resolved once at <see cref="Build"/> against the final
/// application context. All services are materialized ahead of the server
/// lifecycle adapters, regardless of fluent call order.
/// </remarks>
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IHostService? _telemetry;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private readonly List<IHealthContributor> _healthContributors = new();
    private readonly List<CompiledSchema> _schemas = new();

    // Server registrations resolve in registration order at Build: instances are
    // wrapped as trivial factories so an instance registered after a deferred
    // factory still lands after it in the context's Servers list.
    private readonly List<Func<IDatabaseApplicationContext, IDatabaseServer>> _serverRegistrations = new();
    private readonly List<Func<IDatabaseApplicationContext, IHostService>> _serviceRegistrations = new();
    private bool _isBuilt;

    /// <summary>
    /// Initializes a new builder over the specified options.
    /// </summary>
    /// <param name="options">The application options the builder composes into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public DatabaseApplicationBuilder(DatabaseApplicationOptions options)
        : this(options, resourceAssembly: null)
    {
    }

    internal DatabaseApplicationBuilder(DatabaseApplicationOptions options, Assembly? resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;

        if (resourceAssembly is not null &&
            ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered database resource control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out _telemetry);
            _options.Environment = _resourceContext.EnvironmentName;
            _options.ContentRootPath = FileSystemPath.Parse(_resourceContext.ContentRootPath);
            ResourceRuntime.RegisterConnectionFactoryResolver(CreateConnectionFactory);
        }
    }

    /// <summary>
    /// Gets the application options the builder composes into — the hosting-side
    /// surface (additional host services) that the root builder interface
    /// deliberately omits.
    /// </summary>
    public DatabaseApplicationOptions Options => _options;

    /// <summary>
    /// Gets the generated resource control plane, or null when this is a plain application.
    /// </summary>
    internal IResourceControlPlane? ControlPlane => _controlPlane;

    /// <summary>
    /// Gets the immutable compiled schemas registered for provisioning.
    /// </summary>
    public IReadOnlyList<CompiledSchema> Schemas => _schemas.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngine> Engines => _options.Engines as IReadOnlyList<IDatabaseEngine> ?? [.. _options.Engines];

    /// <summary>
    /// Adds a named contribution to the enabled resource's aggregate health, readiness,
    /// and liveness reports.
    /// </summary>
    /// <param name="name">The stable contribution name.</param>
    /// <param name="check">The health evaluator.</param>
    /// <returns>The same builder for chaining.</returns>
    public DatabaseApplicationBuilder AddHealthCheck(string name, ResourceHealthCheck check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);

        _healthContributors.Add(new DelegateHealthContributor(name, check));
        return this;
    }

    /// <summary>
    /// Registers before-accept provisioning for a compiled database schema.
    /// </summary>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="schema">The validated schema to open, create, and reconcile.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="engine"/> or <paramref name="schema"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">The compiled schema targets a different engine model.</exception>
    /// <remarks>
    /// Provisioning is registered as an additional host service. The built application always
    /// starts all additional services before its server wrappers, so provisioning completes
    /// before any server accepts connections regardless of the order in which composition verbs
    /// were called.
    /// </remarks>
    public DatabaseApplicationBuilder Provision(IDatabaseEngine engine, CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schema);
        if (engine.Model != schema.Model)
        {
            throw new ArgumentException(
                $"Compiled schema '{schema.Name}' targets {schema.Model}, but engine '{engine.Name}' uses {engine.Model}.",
                nameof(schema));
        }

        _schemas.Add(schema);
        AddService(new DefaultDatabaseProvisioner(engine, schema));
        return this;
    }

    /// <summary>
    /// Declares a logical database in C#, compiles and validates its schema, and registers the
    /// compiled schema for before-accept provisioning.
    /// </summary>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="configure">The callback that declares the database schema.</param>
    /// <returns>The immutable compiled schema.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="engine"/> or <paramref name="configure"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="DatabaseSchemaValidationException">The declaration is not valid for the engine's model.</exception>
    public CompiledSchema AddDatabase(
        IDatabaseEngine engine,
        string name,
        Action<IDatabaseSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        IDatabaseSchema declaration = DatabaseSchema.Create(name, configure);
        CompiledSchema schema = DatabaseSchemaCompiler.Compile(declaration, engine.Model);
        Provision(engine, schema);
        return schema;
    }

    /// <inheritdoc cref="IDatabaseApplicationBuilder.AddEngine" />
    public DatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _options.Engines.Add(engine);

        return this;
    }

    /// <summary>
    /// Registers a host service on the application lifecycle. Services start in
    /// registration order before any database server and stop in reverse
    /// registration order after every server has drained.
    /// </summary>
    /// <param name="service">The service to start and stop with the application.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public DatabaseApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);

        return this;
    }

    /// <summary>
    /// Registers a host service factory that receives the final database
    /// application context. The factory is invoked once when the application is
    /// built, and the resulting service follows service registration order.
    /// Services start before any database server and stop after every server has
    /// drained.
    /// </summary>
    /// <param name="service">The factory that creates the service from the application context.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory returns <see langword="null"/> when the application is built.
    /// </exception>
    public DatabaseApplicationBuilder AddService(Func<IDatabaseApplicationContext, IHostService> service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(service);

        return this;
    }

    /// <inheritdoc cref="IDatabaseApplicationBuilder.AddServer(IDatabaseServer)" />
    public DatabaseApplicationBuilder AddServer(IDatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        _serverRegistrations.Add(_ => server);

        return this;
    }

    /// <inheritdoc cref="IDatabaseApplicationBuilder.AddServer(Func{IDatabaseApplicationContext, IDatabaseServer})" />
    public DatabaseApplicationBuilder AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _serverRegistrations.Add(configure);

        return this;
    }

    /// <summary>
    /// Builds the <see cref="DatabaseApplication"/> from the registered engines,
    /// lifecycle services, and servers. Deferred factories run here in their
    /// respective registration order against the final application context.
    /// </summary>
    /// <returns>The composed application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">The application has already been built, or a deferred server factory returned null.</exception>
    public DatabaseApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The database application has already been built.");
        }

        if (_telemetry is not null)
        {
            // Options may already contain services; telemetry must stop after those producers too.
            _options.Services.Insert(0, _telemetry);
        }

        // The context wraps the live option lists, so each factory sees every
        // registration made before it runs.
        var context = new DatabaseApplicationContext(_options);

        foreach (Func<IDatabaseApplicationContext, IDatabaseServer> registration in _serverRegistrations)
        {
            IDatabaseServer server = registration.Invoke(context)
                ?? throw new InvalidOperationException("A deferred server factory returned null.");

            _options.Servers.Add(server);
        }

        foreach (Func<IDatabaseApplicationContext, IHostService> registration in _serviceRegistrations)
        {
            IHostService service = registration.Invoke(context)
                ?? throw new InvalidOperationException("A deferred host service factory returned null.");

            _options.Services.Add(service);
        }

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

            foreach (IHealthContributor contributor in _options.Services
                .Concat<object>(_options.Engines)
                .Concat(_options.Servers)
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
                _controlPlane.ObserveEndpoint("admin", endpoint);
                _options.Services.Add(new DatabaseAdminEndpointService(
                    endpoint,
                    _controlPlane,
                    _resourceContext?.BootstrapCredential ?? ReadOnlyMemory<byte>.Empty,
                    _resourceContext?.GatewayName is not null,
                    context,
                    controlPlaneContributors));
            }
        }

        _isBuilt = true;

        var application = new DatabaseApplication(_options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }

        return application;
    }

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(IDatabaseEngine engine) => AddEngine(engine);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(IDatabaseServer server) => AddServer(server);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure) => AddServer(configure);
    IDatabaseApplication IDatabaseApplicationBuilder.Build() => Build();

    private static object? CreateConnectionFactory(string protocol)
    {
        return string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            ? new TcpConnectionFactory()
            : null;
    }
}
