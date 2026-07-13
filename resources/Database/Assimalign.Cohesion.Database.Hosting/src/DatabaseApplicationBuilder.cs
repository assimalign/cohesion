using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// The default <see cref="IDatabaseApplicationBuilder"/> implementation: collects
/// engine and endpoint registrations — including those made by model verbs such as
/// <c>AddSqlDatabase(...)</c>, which compose against the root interface without
/// referencing this module — and builds the <see cref="DatabaseApplication"/> host.
/// Create one with <see cref="DatabaseApplication.CreateBuilder()"/>.
/// </summary>
/// <remarks>
/// The builder wraps a <see cref="DatabaseApplicationOptions"/> instance, exposed
/// through <see cref="Options"/> for the hosting-only composition surface the root
/// interface deliberately omits: worker-slot mapping (<c>Options.Workers</c>) and
/// additional host services (<c>Options.Services</c>). Engine registrations go to
/// <c>Options.Engines</c>; a deferred server factory runs at <see cref="Build"/>
/// time with the final engine list and lands on <c>Options.Server</c>.
/// </remarks>
public sealed class DatabaseApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly DatabaseApplicationOptions _options;

    private Func<IReadOnlyList<IDatabaseEngine>, IDatabaseServer>? _serverFactory;
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
    /// surface (worker-slot mapping, additional host services) that the root
    /// builder interface deliberately omits.
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
        ThrowIfServerRegistered();

        _options.Server = server;

        return this;
    }

    /// <inheritdoc cref="IDatabaseApplicationBuilder.AddServer(Func{IReadOnlyList{IDatabaseEngine}, IDatabaseServer})" />
    public DatabaseApplicationBuilder AddServer(Func<IReadOnlyList<IDatabaseEngine>, IDatabaseServer> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfServerRegistered();

        _serverFactory = configure;

        return this;
    }

    /// <summary>
    /// Builds the <see cref="DatabaseApplication"/> from the registered engines and
    /// endpoint. A deferred server factory runs here, with the final engine list.
    /// </summary>
    /// <returns>The composed application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">The application has already been built.</exception>
    public DatabaseApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The database application has already been built.");
        }

        if (_serverFactory is not null)
        {
            _options.Server = _serverFactory.Invoke(Engines);
        }

        _isBuilt = true;

        return new DatabaseApplication(_options);
    }

    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddEngine(IDatabaseEngine engine) => AddEngine(engine);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(IDatabaseServer server) => AddServer(server);
    IDatabaseApplicationBuilder IDatabaseApplicationBuilder.AddServer(Func<IReadOnlyList<IDatabaseEngine>, IDatabaseServer> configure) => AddServer(configure);
    IDatabaseApplication IDatabaseApplicationBuilder.Build() => Build();

    private void ThrowIfServerRegistered()
    {
        if (_options.Server is not null || _serverFactory is not null)
        {
            throw new InvalidOperationException("A server has already been registered on this builder.");
        }
    }
}
