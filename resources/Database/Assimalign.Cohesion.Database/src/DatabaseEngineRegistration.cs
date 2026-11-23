using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Names one engine of a database application and says how the application gets it: a borrowed
/// engine the caller already built, or a factory the application runs, once, when it is built.
/// </summary>
/// <remarks>
/// <para>
/// A registration is what the application's service registrations carry for an engine. The hosting
/// builder's <c>AddEngine</c> verbs and the root <see cref="IDatabaseApplicationBuilder"/> shims
/// register one as a singleton instance, and so do the model packages' component integrations
/// (<c>builder.Services.AddSql(name, ...)</c>), which compose it without a container reference.
/// The name travels with the registration so the hosting layer can refuse a duplicate before any
/// engine factory runs (owner decision 52 of 2026-10-09): a factory can open, recover and provision
/// databases.
/// </para>
/// <para>
/// Constructing a registration does no I/O. A borrowed engine stays owned by its caller; a factory's
/// product is owned by the application, which disposes it after every lifecycle service.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseEngineRegistration {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.DatabaseEngineRegistration.DebugView))]
public sealed class DatabaseEngineRegistration
{
    private readonly string _name;
    private readonly DatabaseEngine? _engine;
    private readonly Func<IDatabaseApplicationContext, DatabaseEngine>? _factory;

    private DatabaseEngineRegistration(string name, DatabaseEngine? engine, Func<IDatabaseApplicationContext, DatabaseEngine>? factory)
    {
        _name = name;
        _engine = engine;
        _factory = factory;
    }

    /// <summary>Gets the engine name, which the engine the registration yields must carry (ordinal).</summary>
    public string Name => _name;

    /// <summary>Gets the borrowed engine, or <see langword="null"/> when the registration holds a factory.</summary>
    public DatabaseEngine? Engine => _engine;

    /// <summary>Gets a value indicating whether the application owns the engine, because a factory creates it.</summary>
    public bool IsOwned => _factory is not null;

    /// <summary>Creates a registration for an engine the caller built and keeps owning.</summary>
    /// <param name="engine">The borrowed engine; its name is the registration's name.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    public static DatabaseEngineRegistration Create(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new DatabaseEngineRegistration(engine.Name, engine, null);
    }

    /// <summary>Creates a registration whose engine the application builds, once, when it is built.</summary>
    /// <param name="name">The engine name, which the factory's product must carry (ordinal).</param>
    /// <param name="factory">
    /// The ownership-transferring factory. It receives the application's context, whose engines are
    /// the ones registered before this one, and cleans up its own allocations if it throws.
    /// </param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public static DatabaseEngineRegistration Create(string name, Func<IDatabaseApplicationContext, DatabaseEngine> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        return new DatabaseEngineRegistration(name, null, factory);
    }

    /// <summary>Returns the borrowed engine, or runs the factory and returns its product.</summary>
    /// <param name="context">The application's context.</param>
    /// <returns>The engine; a factory may return <see langword="null"/>, which the hosting layer refuses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// The hosting layer calls this once per application Build. A factory's exception propagates
    /// unchanged.
    /// </remarks>
    public DatabaseEngine CreateEngine(IDatabaseApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _factory is null ? _engine! : _factory(context);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.DatabaseEngineRegistration _target;

        public DebugView(global::Assimalign.Cohesion.Database.DatabaseEngineRegistration value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.DatabaseEngine? Engine => _target.Engine;
        public bool IsOwned => _target.IsOwned;
    }
}
