using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// Composes one graph engine: its options, the databases it declares, and its deferred worker
/// and server factories, for one construction attempt that opens or creates every declared
/// database before it returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine level</b> of the three composition levels (B3 of the engine extensibility design,
/// the SQL builder's shape). The engine name is written once, as the first argument of
/// <see cref="GraphDatabaseEngine.CreateBuilder(string)"/> or the <c>AddGraph</c> verb. The engine's
/// settings are <see cref="Options"/>, values only, which <see cref="BuildAsync"/> copies, so a later
/// change cannot reach the running engine. The builder holds no container, configuration binding or
/// <see cref="IServiceProvider"/>.
/// </para>
/// <para>
/// <b>The build runs fixed phases</b>, each seeing only what earlier phases produced: (1) the options
/// are copied and the copy checked, before anything is created; (2) the engine is created and its
/// built-in workers start; (3) the <see cref="AddWorker"/> products are attached, then the
/// <see cref="AddServer(Action{GraphDatabaseServerOptions})"/> and
/// <see cref="AddServer(Func{GraphDatabaseEngine, DatabaseServer})"/> products (servers are created
/// stopped), and composition is frozen; (4) each
/// declared database is opened, or created when it does not exist, in declaration order; (5) the
/// engine is returned. A failure in phases 2 to 4 disposes the engine, with its servers, workers and
/// open databases, before the build throws.
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
/// <see cref="GraphDatabaseEngine.CreateBuilder"/> and the <c>AddGraph</c> composition verb
/// create it. It replaces the former <c>IGraphDatabaseEngineBuilder</c> interface, and its
/// factories are typed over the graph engine instead of the root interfaces. The properties
/// that mirrored the options type were replaced by <see cref="Options"/> in B3.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class GraphDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState<GraphDatabaseEngine> _state;
    private readonly GraphDatabaseEngineOptions _options = new();

    /// <summary>Initializes a builder for the engine of that name.</summary>
    /// <param name="name">The engine name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    internal GraphDatabaseEngineBuilder(string name)
    {
        _state = new(name, GraphDatabaseEngine.ModelName);
    }

    /// <summary>
    /// Gets the engine name: the first argument of <c>AddGraph</c> or
    /// <see cref="GraphDatabaseEngine.CreateBuilder(string)"/>, written once (owner decision 52 of
    /// 2026-10-09).
    /// </summary>
    public string Name => _state.Name;

    /// <summary>
    /// Gets the engine's settings: storage, durability, checkpoints, worker cadences and limits.
    /// </summary>
    /// <remarks>
    /// Values only, with no engine name: the engine is <see cref="Name"/>. <see cref="BuildAsync"/>
    /// checks them and keeps a copy, so a change made after the build began never reaches the
    /// engine.
    /// </remarks>
    public GraphDatabaseEngineOptions Options => _options;

    /// <summary>
    /// Declares a database the engine owns: the build opens it, or creates it when it does not
    /// exist, before the engine is returned.
    /// </summary>
    /// <param name="name">The database name, written once.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, or not a single file-name component (the
    /// database's files live in a directory named for it): refused here, before anything is created.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A build was attempted, or the engine already declares a database of that name (ignoring case,
    /// as database names compare).
    /// </exception>
    /// <remarks>
    /// The declaration owns the database: the built engine refuses to drop it
    /// (<see cref="DatabaseObjectLockedException"/>, owner decision 56 of 2026-10-09).
    /// </remarks>
    public GraphDatabaseEngineBuilder AddDatabase(string name)
    {
        _state.AddDatabase(name);
        return this;
    }

    /// <summary>
    /// Registers an engine-owned server the model creates: the build runs <paramref name="configure"/>
    /// on new server options and creates a <see cref="GraphDatabaseServer"/> over the engine with them.
    /// </summary>
    /// <param name="configure">Configures the server options, its listener included; invoked once, during the build.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <remarks>
    /// The server is created stopped and starts with the application, after its services; it keeps
    /// a copy of the options, so a later change to the object the callback configured never reaches
    /// it. When the server cannot be created, the listener the options carry is disposed.
    /// </remarks>
    public GraphDatabaseEngineBuilder AddServer(Action<GraphDatabaseServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _state.AddServer(engine =>
        {
            var options = new GraphDatabaseServerOptions();
            try
            {
                configure(options);
                return GraphDatabaseServer.Create(engine, options);
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
    /// <param name="factory">The factory, invoked once against the engine the server must front, after every worker.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public GraphDatabaseEngineBuilder AddServer(Func<GraphDatabaseEngine, DatabaseServer> factory)
    {
        _state.AddServer(factory);
        return this;
    }

    /// <summary>Registers a factory for an engine-owned background worker.</summary>
    /// <param name="factory">The factory, invoked once against the constructed engine, after every worker registered before it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public GraphDatabaseEngineBuilder AddWorker(Func<GraphDatabaseEngine, DatabaseEngineWorker> factory)
    {
        _state.AddWorker(factory);
        return this;
    }

    /// <summary>
    /// Builds the engine, then opens or creates every declared database: <see cref="BuildAsync"/>,
    /// bridged on the thread pool, so the caller's synchronization context is never captured.
    /// </summary>
    /// <returns>The operational engine, every declared database open and its servers still stopped.</returns>
    /// <exception cref="InvalidOperationException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="ArgumentException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="DatabaseException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="Assimalign.Cohesion.Database.Storage.StorageException">See <see cref="BuildAsync"/>.</exception>
    /// <exception cref="AggregateException">See <see cref="BuildAsync"/>.</exception>
    public GraphDatabaseEngine Build()
        => Task.Run(async () => await BuildAsync(CancellationToken.None).ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>
    /// Freezes composition, builds the engine with its workers and servers, and opens or creates
    /// every declared database before it returns.
    /// </summary>
    /// <param name="cancellationToken">Observed before the engine is created and before each declared database.</param>
    /// <returns>The operational engine, every declared database open and its servers still stopped.</returns>
    /// <exception cref="InvalidOperationException">
    /// A build was already attempted; a factory returned null; or the engine refused a product (a
    /// duplicate worker name, a product returned twice, a server that fronts another engine).
    /// </exception>
    /// <exception cref="ArgumentException">A server's options are invalid (see <see cref="GraphDatabaseServer.Create"/>), or a factory refused its arguments.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option is outside its range (see <see cref="GraphDatabaseEngine.Create"/>); checked before
    /// anything is created, and the refusal names the engine.
    /// </exception>
    /// <exception cref="DatabaseException">
    /// A declared database exists but its storage format was refused; the engine was disposed.
    /// </exception>
    /// <exception cref="Assimalign.Cohesion.Database.Storage.StorageException">
    /// A declared database's files could not be read (a damaged header, an I/O failure): the storage
    /// layer's own exception, which is not a <see cref="DatabaseException"/>; the engine was disposed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; a created engine was disposed.</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public async ValueTask<GraphDatabaseEngine> BuildAsync(CancellationToken cancellationToken = default)
    {
        _state.BeginBuild();

        // Phase 1: the options, copied, then the copy checked, before anything is created; a change
        // racing the build cannot reach the engine unchecked.
        GraphDatabaseEngineOptions options = _options.Snapshot();
        GraphDatabaseEngine.ValidateOptions(Name, options);
        cancellationToken.ThrowIfCancellationRequested();

        // Phases 2 and 3: the engine and its built-in workers, then the factories' products. A
        // failure here disposes what the engine and the composition hold.
        var engine = GraphDatabaseEngine.CreateUncomposed(Name, options);
        _state.Complete(engine, engine.Compose, GraphDatabaseEngine.ReleaseRefusedWorkerAsync);

        // Phase 4: each declared database, in declaration order; a failure disposes the engine.
        await _state.ProvisionDatabasesAsync(engine, engine.Declare, cancellationToken).ConfigureAwait(false);

        // Phase 5.
        return engine;
    }

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards (the <c>AddGraph</c> verb's compensation).
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    internal void Abort(Exception failure) => _state.Abort(failure);
}
