using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Embedded;

/// <summary>
/// The in-process consumption facade for Cohesion database engines: composes one or
/// more model engines inside the consumer's process — no server, no wire protocol,
/// no host — with the same engines and the same ACID guarantees as the hosted mode.
/// </summary>
/// <remarks>
/// This is how other Cohesion resources (configuration stores, secret stores,
/// schedulers, hubs) embed a data layer. Engines are self-sufficient libraries —
/// they own their internal background workers (WAL flushing, checkpointing)
/// whether embedded or hosted — so the facade only composes and disposes them.
/// Disposal stops engines in reverse registration order.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("EmbeddedDatabase")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Embedded.EmbeddedDatabase.DebugView))]
public sealed class EmbeddedDatabase : IAsyncDisposable
{
    private readonly IReadOnlyList<DatabaseEngine> _engines;
    private bool _disposed;

    private EmbeddedDatabase(IReadOnlyList<DatabaseEngine> engines)
    {
        _engines = engines;
    }

    /// <summary>
    /// Gets the engines composed into this embedded database, in registration order.
    /// </summary>
    public IReadOnlyList<DatabaseEngine> Engines => _engines;

    /// <summary>
    /// Creates an embedded database from configured options.
    /// </summary>
    /// <param name="configure">Configures the engines to embed.</param>
    /// <returns>The composed embedded database.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is null.</exception>
    /// <exception cref="DatabaseException">Thrown when no engine is registered or engine names collide.</exception>
    public static EmbeddedDatabase Create(Action<EmbeddedDatabaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new EmbeddedDatabaseOptions();
        configure(options);

        if (options.Engines.Count == 0)
        {
            throw new DatabaseException(ErrorMessages.EmbeddedDatabaseCreateDatabase);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engine in options.Engines)
        {
            if (engine is null)
            {
                throw new DatabaseException(ErrorMessages.EmbeddedDatabaseCreateDatabase2);
            }
            if (!names.Add(engine.Name))
            {
                throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EmbeddedDatabaseCreateDatabase3, engine.Name));
            }
        }

        return new EmbeddedDatabase(new List<DatabaseEngine>(options.Engines));
    }

    /// <summary>
    /// Attempts to find an engine by its logical name.
    /// </summary>
    /// <param name="name">The engine name.</param>
    /// <param name="engine">When this method returns true, the matching engine.</param>
    /// <returns>True when an engine with the name exists; otherwise false.</returns>
    public bool TryGetEngine(string name, [MaybeNullWhen(false)] out DatabaseEngine engine)
    {
        foreach (var candidate in _engines)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                engine = candidate;
                return true;
            }
        }
        engine = null;
        return false;
    }

    /// <summary>
    /// Attempts to find the first engine implementing the specified model.
    /// </summary>
    /// <param name="model">The database model.</param>
    /// <param name="engine">When this method returns true, the first matching engine.</param>
    /// <returns>True when an engine of the model exists; otherwise false.</returns>
    public bool TryGetEngine(EngineModel model, [MaybeNullWhen(false)] out DatabaseEngine engine)
    {
        foreach (var candidate in _engines)
        {
            if (candidate.Model == model)
            {
                engine = candidate;
                return true;
            }
        }
        engine = null;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        List<Exception>? failures = null;
        for (var i = _engines.Count - 1; i >= 0; i--)
        {
            try
            {
                await _engines[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }
        if (failures is not null)
        {
            throw new AggregateException(ErrorMessages.EmbeddedDatabaseDisposeAsyncAggregate, failures);
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Embedded.EmbeddedDatabase _target;

        public DebugView(global::Assimalign.Cohesion.Database.Embedded.EmbeddedDatabase value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.DatabaseEngine> Engines => _target.Engines;
    }
}
