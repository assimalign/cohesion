using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;

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
/// through <see cref="Options"/> for the hosting-only composition surface the root
/// interface deliberately omits (additional host services on
/// <c>Options.Services</c>). Engine registrations go to <c>Options.Engines</c>;
/// server registrations go to <c>Options.Servers</c>, with deferred factories
/// resolved at <see cref="Build"/> in registration order against the application
/// context — mirroring the Web area's context-receiving
/// <c>AddServer(Func&lt;IWebApplicationContext, IWebApplicationServer&gt;)</c>.
/// </remarks>
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private readonly List<IHealthContributor> _healthContributors = new();
    private readonly List<IDatabaseSchema> _schemas = new();

    // Server registrations resolve in registration order at Build: instances are
    // wrapped as trivial factories so an instance registered after a deferred
    // factory still lands after it in the context's Servers list.
    private readonly List<Func<IDatabaseApplicationContext, IDatabaseServer>> _serverRegistrations = new();
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
            _options.Environment = _resourceContext.EnvironmentName;
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
    /// Gets the schema declarations retained for later compilation and migration planning.
    /// </summary>
    public IReadOnlyList<IDatabaseSchema> Schemas => _schemas.AsReadOnly();

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
    /// Registers before-accept provisioning for a database declared by the application.
    /// </summary>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="databaseName">The logical database name to open or create.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="databaseName"/> is empty or whitespace.</exception>
    /// <remarks>
    /// Provisioning is registered as an additional host service. The built application always
    /// starts all additional services before its server wrappers, so provisioning completes
    /// before any server accepts connections regardless of the order in which composition verbs
    /// were called.
    /// </remarks>
    public DatabaseApplicationBuilder Provision(IDatabaseEngine engine, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        _options.Services.Add(new DefaultDatabaseProvisioner(engine, databaseName));
        return this;
    }

    /// <summary>
    /// Declares a logical database in C#, retains its schema for later compilation, and registers
    /// the database for before-accept provisioning.
    /// </summary>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="configure">The callback that declares the database schema.</param>
    /// <returns>The completed schema declaration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="engine"/> or <paramref name="configure"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <remarks>
    /// Schema compilation and migrations consume the retained declaration in their owning work
    /// items. This registration already enforces the durable code-first boundary: the database
    /// is opened or created before any registered server starts accepting connections.
    /// </remarks>
    public IDatabaseSchema AddDatabase(
        IDatabaseEngine engine,
        string name,
        Action<IDatabaseSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        IDatabaseSchema schema = DatabaseSchema.Create(name, configure);
        _schemas.Add(schema);
        Provision(engine, name);
        return schema;
    }

    /// <inheritdoc cref="IDatabaseApplicationBuilder.AddEngine" />
    public DatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _options.Engines.Add(engine);

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
    /// Builds the <see cref="DatabaseApplication"/> from the registered engines and
    /// servers. Deferred server factories run here, in registration order, each
    /// receiving the application context (final engine list plus every server
    /// registered ahead of it).
    /// </summary>
    /// <returns>The composed application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">The application has already been built, or a deferred server factory returned null.</exception>
    public DatabaseApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The database application has already been built.");
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

        if (_controlPlane is not null)
        {
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

            if ((_controlPlane.ObservedEndpoints.TryGetValue("admin", out EndpointAddress endpoint) ||
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
