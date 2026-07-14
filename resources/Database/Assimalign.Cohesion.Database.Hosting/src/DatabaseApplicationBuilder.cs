using System;
using System.Collections.Generic;

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
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>
    /// Gets the application options the builder composes into — the hosting-side
    /// surface (additional host services) that the root builder interface
    /// deliberately omits.
    /// </summary>
    public DatabaseApplicationOptions Options => _options;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngine> Engines => _options.Engines as IReadOnlyList<IDatabaseEngine> ?? [.. _options.Engines];

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

        _isBuilt = true;

        return new DatabaseApplication(_options, context);
    }

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(IDatabaseEngine engine) => AddEngine(engine);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(IDatabaseServer server) => AddServer(server);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure) => AddServer(configure);
    IDatabaseApplication IDatabaseApplicationBuilder.Build() => Build();
}
