using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Composes one SQL engine: its options, the databases it declares, and its deferred worker and
/// server factories, for one construction attempt that provisions every declared database before
/// it returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine level</b> of the three composition levels (B1 of the engine extensibility design,
/// owner decision 52 of 2026-10-09). The engine name is written once, as the first argument of
/// <see cref="SqlDatabaseEngine.CreateBuilder(string)"/> or the <c>AddSql</c> verb. The engine's
/// settings are <see cref="Options"/>, values only, which <see cref="BuildAsync"/> copies, so a
/// later change cannot reach the running engine. The builder holds no container, configuration
/// binding or <see cref="IServiceProvider"/>.
/// </para>
/// <para>
/// <b>The build runs fixed phases</b>, each seeing only what earlier phases produced (the design's
/// §5.2): (1) the options are checked and copied; (3) each declared database's schema is compiled,
/// and a declaration the engine cannot provision (a principal, a custom type) is refused before
/// any file is touched; (4) the engine is created and its
/// built-in workers start; (5) the <see cref="AddWorker"/> products are attached, then the
/// <see cref="AddServer(Func{SqlDatabaseEngine, DatabaseServer})"/> products (servers are created
/// stopped), and composition is frozen; (6) each declared database is provisioned in declaration
/// order: opened, or created when it does not exist, its collation checked, then its schema applied
/// or verified; (7) the engine is returned. A failure in phases 4 to 6 disposes the engine, with its
/// servers, workers and open databases, before the build throws. Phase 2, the function and type
/// catalog, and phase 3's binding of declared CHECK and DEFAULT expressions to it arrive with the
/// engine's function abstraction.
/// </para>
/// <para>
/// Worker and server factories run after the engine exists, in registration order (every worker
/// before every server), and each receives the typed engine; a factory runs only when its product
/// is attached, so it sees the products attached before it. The products belong to the engine. The
/// engine refuses a worker whose name another worker of the engine has (ordinal, ignoring case; the
/// built-in workers are named <c>{engine}/{role}</c>), a product a factory returned twice, and a
/// server that fronts another engine. A worker's blank name is refused by the worker's own
/// constructor, inside its factory.
/// </para>
/// <para>
/// The builder supports one build attempt: a second, and any change after the first has begun,
/// throws <see cref="InvalidOperationException"/>, except a change to <see cref="Options"/>, a
/// plain options object, which no longer reaches the engine.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, D5, phase 4, #1260).</b> Sealed, with an internal constructor:
/// <see cref="SqlDatabaseEngine.CreateBuilder(string)"/> and the <c>AddSql</c> composition verb
/// create it. It replaces the former <c>ISqlDatabaseEngineBuilder</c> interface, which the
/// 2026-10-02 ruling had kept "meant to be implemented elsewhere"; the owner's 2026-10-04 decision
/// (D5) reversed that ruling, its 2026-10-03 narrowing and #1232. The fourteen properties that
/// mirrored the options type were replaced by <see cref="Options"/> in B1.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class SqlDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState<SqlDatabaseEngine> _state;
    private readonly SqlDatabaseEngineOptions _options = new();
    private readonly List<SqlDatabaseBuilder> _databases = [];

    /// <summary>Initializes a builder for the engine of that name.</summary>
    /// <param name="name">The engine name, which <see cref="Options"/>' engine name starts as.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    internal SqlDatabaseEngineBuilder(string name)
    {
        _state = new(name);
        _options.EngineName = name;
    }

    /// <summary>
    /// Gets the engine name: the first argument of <c>AddSql</c> or
    /// <see cref="SqlDatabaseEngine.CreateBuilder(string)"/>, written once (owner decision 52 of
    /// 2026-10-09).
    /// </summary>
    public string Name => _state.Name;

    /// <summary>
    /// Gets the engine's settings: storage, durability, checkpoints, worker cadences and limits.
    /// </summary>
    /// <remarks>
    /// Values only. <see cref="BuildAsync"/> checks them and keeps a copy, so a change made after
    /// the build began never reaches the engine. Their <see cref="SqlDatabaseEngineOptions.EngineName"/>
    /// starts as <see cref="Name"/>, and the build refuses any other value; it leaves the options
    /// when the engine is named only by its builder.
    /// </remarks>
    public SqlDatabaseEngineOptions Options => _options;

    /// <summary>
    /// Declares a database the engine owns: the build opens it, or creates it when it does not
    /// exist, and provisions it before the engine is returned.
    /// </summary>
    /// <param name="name">The database name, written once.</param>
    /// <param name="configure">
    /// Declares the database's collation, schema and provisioning mode; invoked now. Without it the
    /// build only ensures the database exists.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">
    /// A build was attempted, or the engine already declares a database of that name (ignoring case,
    /// as database names compare).
    /// </exception>
    /// <remarks>
    /// The declaration owns the database: the built engine refuses to drop it
    /// (<see cref="DatabaseObjectLockedException"/>, owner decision 56 of 2026-10-09).
    /// </remarks>
    public SqlDatabaseEngineBuilder AddDatabase(string name, Action<SqlDatabaseBuilder>? configure = null)
    {
        _state.EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var databaseName = new DatabaseName(name);
        foreach (SqlDatabaseBuilder declared in _databases)
        {
            if (declared.Name == databaseName)
            {
                throw new InvalidOperationException($"SQL engine '{Name}' already declares database '{declared.Name}'.");
            }
        }

        var database = new SqlDatabaseBuilder(this, databaseName);
        configure?.Invoke(database);
        _databases.Add(database);
        return this;
    }

    /// <summary>
    /// Declares a database with a reusable schema declaration: the short form of
    /// <c>AddDatabase(schema.Name, database =&gt; database.Schema(schema))</c>, the database named by the
    /// schema.
    /// </summary>
    /// <param name="schema">The schema declaration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A build was attempted, or the engine already declares a database of the schema's name.
    /// </exception>
    public SqlDatabaseEngineBuilder AddDatabase(SqlSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return AddDatabase(schema.Name, database => database.Schema(schema));
    }

    /// <summary>
    /// Registers an engine-owned server the model creates: the build runs <paramref name="configure"/>
    /// on new server options and creates a <see cref="SqlDatabaseServer"/> over the engine with them.
    /// </summary>
    /// <param name="configure">Configures the server options, its listener included; invoked once, during the build.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <remarks>
    /// The server is created stopped and starts with the application, after its services. When the
    /// server cannot be created, the listener the options carry is disposed.
    /// </remarks>
    public SqlDatabaseEngineBuilder AddServer(Action<SqlDatabaseServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _state.AddServer(engine =>
        {
            var options = new SqlDatabaseServerOptions();
            try
            {
                configure(options);
                return SqlDatabaseServer.Create(engine, options);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Nothing owns the listener the callback created until the server does.
                if (options.Listener is { } listener)
                {
                    try
                    {
                        Task.Run(async () => await listener.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
                    }
                    catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                    {
                        throw new AggregateException(failure, cleanup);
                    }
                }

                throw;
            }
        });
        return this;
    }

    /// <summary>Registers a factory for an engine-owned server.</summary>
    /// <param name="configure">The factory, invoked once against the engine the server must front, after every worker.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public SqlDatabaseEngineBuilder AddServer(Func<SqlDatabaseEngine, DatabaseServer> configure)
    {
        _state.AddServer(configure);
        return this;
    }

    /// <summary>Registers a factory for an engine-owned background worker.</summary>
    /// <param name="configure">The factory, invoked once against the constructed engine, after every worker registered before it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public SqlDatabaseEngineBuilder AddWorker(Func<SqlDatabaseEngine, DatabaseEngineWorker> configure)
    {
        _state.AddWorker(configure);
        return this;
    }

    /// <summary>
    /// Builds the engine, then provisions every declared database: <see cref="BuildAsync"/>, bridged
    /// on the thread pool, so the caller's synchronization context is never captured.
    /// </summary>
    /// <returns>The operational engine, every declared database provisioned and its servers still stopped.</returns>
    /// <exception cref="InvalidOperationException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="ArgumentException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="SqlSchemaMigrationException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="AggregateException">See <see cref="BuildAsync"/>.</exception>
    public SqlDatabaseEngine Build()
        => Task.Run(async () => await BuildAsync(CancellationToken.None).ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>
    /// Freezes composition, builds the engine with its workers and servers, and provisions every
    /// declared database before it returns.
    /// </summary>
    /// <param name="cancellationToken">Observed between the phases and by each provisioning step.</param>
    /// <returns>The operational engine, every declared database provisioned and its servers still stopped.</returns>
    /// <exception cref="InvalidOperationException">
    /// A build was already attempted; <see cref="Options"/> name another engine than
    /// <see cref="Name"/>; a factory returned null; or the engine refused a product (a duplicate
    /// worker name, a product returned twice, a server that fronts another engine).
    /// </exception>
    /// <exception cref="ArgumentException">An option is invalid (see <see cref="SqlDatabaseEngine.Create"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option is outside its range (see <see cref="SqlDatabaseEngine.Create"/>); checked before
    /// anything is created.
    /// </exception>
    /// <exception cref="SqlSchemaMigrationException">
    /// A declared database was refused before anything was created (<c>COHSQLP001</c>), or its
    /// provisioning failed after the engine was created (<c>COHSQLP002</c> to <c>COHSQLP004</c>, or
    /// an ownership or destructive-step refusal); the engine was disposed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; a created engine was disposed.</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public async ValueTask<SqlDatabaseEngine> BuildAsync(CancellationToken cancellationToken = default)
    {
        _state.BeginBuild();

        // Phase 1: the options, checked and copied before anything is compiled or created.
        _state.ThrowIfRenamed(_options.EngineName);
        SqlDatabaseEngineOptions options = _options.Snapshot();
        SqlDatabaseEngine.ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 3: every declaration compiled; a refusal touches no file.
        var declarations = new SqlDeclaredDatabase[_databases.Count];
        for (int index = 0; index < declarations.Length; index++)
        {
            declarations[index] = SqlDeclaredDatabase.Compile(Name, _databases[index]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Phases 4 and 5: the engine and its built-in workers, then the factories' products. A
        // failure here disposes what the engine and the composition hold.
        var engine = SqlDatabaseEngine.CreateUncomposed(options);
        _state.Complete(engine, engine.Compose, SqlDatabaseEngine.ReleaseRefusedWorkerAsync);

        // Phase 6: each declared database, in declaration order.
        try
        {
            engine.Declare(declarations);
            foreach (SqlDeclaredDatabase declaration in declarations)
            {
                await declaration.ProvisionAsync(engine, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            await _state.AbortAsync(failure).ConfigureAwait(false);
            throw;
        }

        // Phase 7.
        return engine;
    }

    /// <summary>
    /// Throws once a build was attempted: the declarations are frozen from then on.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    internal void EnsureMutable() => _state.EnsureMutable();

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards (the <c>AddSql</c> verb's compensation).
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    internal void Abort(Exception failure) => _state.Abort(failure);
}
