using System;
using System.Collections.Generic;
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
public sealed class EmbeddedDatabase : IAsyncDisposable
{
    private readonly IReadOnlyList<IDatabaseEngine> _engines;
    private bool _disposed;

    private EmbeddedDatabase(IReadOnlyList<IDatabaseEngine> engines)
    {
        _engines = engines;
    }

    /// <summary>
    /// Gets the engines composed into this embedded database, in registration order.
    /// </summary>
    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

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
            throw new DatabaseException("An embedded database requires at least one engine.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engine in options.Engines)
        {
            if (engine is null)
            {
                throw new DatabaseException("Registered engines cannot be null.");
            }
            if (!names.Add(engine.Name))
            {
                throw new DatabaseException($"An engine named '{engine.Name}' is already registered.");
            }
        }

        return new EmbeddedDatabase(new List<IDatabaseEngine>(options.Engines));
    }

    /// <summary>
    /// Attempts to find an engine by its logical name.
    /// </summary>
    /// <param name="name">The engine name.</param>
    /// <param name="engine">When this method returns true, the matching engine.</param>
    /// <returns>True when an engine with the name exists; otherwise false.</returns>
    public bool TryGetEngine(string name, out IDatabaseEngine engine)
    {
        foreach (var candidate in _engines)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                engine = candidate;
                return true;
            }
        }
        engine = null!;
        return false;
    }

    /// <summary>
    /// Attempts to find the first engine implementing the specified model.
    /// </summary>
    /// <param name="model">The database model.</param>
    /// <param name="engine">When this method returns true, the first matching engine.</param>
    /// <returns>True when an engine of the model exists; otherwise false.</returns>
    public bool TryGetEngine(EngineModel model, out IDatabaseEngine engine)
    {
        foreach (var candidate in _engines)
        {
            if (candidate.Model == model)
            {
                engine = candidate;
                return true;
            }
        }
        engine = null!;
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
            throw new AggregateException("One or more embedded engines failed to dispose.", failures);
        }
    }
}
