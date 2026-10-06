using System;
using System.IO;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Captures SQL engine options and deferred worker and server factories for one engine
/// construction attempt.
/// </summary>
/// <remarks>
/// <para>
/// Worker and server factories run after the engine exists, in registration order (every worker
/// before every server), and each receives the typed engine; a factory runs only when its product
/// is attached, so it sees the products attached before it. The products belong to the engine.
/// <see cref="Build"/> freezes the options and the factories: a second build, and any change after
/// the first, throws <see cref="InvalidOperationException"/>. A failed build disposes the product
/// it was attaching (unless the engine already owns it) and the engine, with everything the engine
/// owns.
/// </para>
/// <para>
/// The engine refuses a worker whose name another worker of the engine has (ordinal, ignoring
/// case; the built-in workers are named <c>{engine}/{role}</c>), a product a factory returned
/// twice, and a server that fronts another engine. A worker's blank name is refused by the
/// worker's own constructor, inside its factory.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, D5, phase 4, #1260).</b> Sealed, with an internal constructor:
/// <see cref="SqlDatabaseEngine.CreateBuilder"/> and the <c>AddSql</c> composition verb create
/// it. It replaces the former <c>ISqlDatabaseEngineBuilder</c> interface, which the 2026-10-02
/// ruling had kept "meant to be implemented elsewhere"; the owner's 2026-10-04 decision (D5)
/// reversed that ruling, its 2026-10-03 narrowing and #1232. Its factories are typed over the SQL
/// engine instead of the root interfaces.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class SqlDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState<SqlDatabaseEngine, DatabaseEngineWorker, DatabaseServer> _state = new();
    private readonly SqlDatabaseEngineOptions _options = new();

    internal SqlDatabaseEngineBuilder()
    {
    }

    /// <summary>Gets or sets the logical engine name; null selects <c>sql-engine</c>.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public string? EngineName
    {
        get => _options.EngineName;
        set { _state.EnsureMutable(); _options.EngineName = value; }
    }

    /// <summary>Gets or sets the optional directory for persistent storage.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public FileSystemPath? RootPath
    {
        get => _options.RootPath;
        set { _state.EnsureMutable(); _options.RootPath = value; }
    }

    /// <summary>Gets or sets the commit durability policy.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public StorageCommitDurability? Durability
    {
        get => _options.Durability;
        set { _state.EnsureMutable(); _options.Durability = value; }
    }

    /// <summary>Gets or sets the bounded grouped-commit flush window.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan GroupCommitWindow
    {
        get => _options.GroupCommitWindow;
        set { _state.EnsureMutable(); _options.GroupCommitWindow = value; }
    }

    /// <summary>Gets or sets the checkpoint time backstop (<see cref="SqlDatabaseEngineOptions.CheckpointInterval"/>).</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan CheckpointInterval
    {
        get => _options.CheckpointInterval;
        set { _state.EnsureMutable(); _options.CheckpointInterval = value; }
    }

    /// <summary>
    /// Gets or sets the journal size, in bytes, that triggers a checkpoint
    /// (<see cref="SqlDatabaseEngineOptions.CheckpointJournalSize"/>; 256 MiB by default, zero for time only).
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long CheckpointJournalSize
    {
        get => _options.CheckpointJournalSize;
        set { _state.EnsureMutable(); _options.CheckpointJournalSize = value; }
    }

    /// <summary>
    /// Gets or sets each database's buffer pool capacity, in bytes
    /// (<see cref="SqlDatabaseEngineOptions.BufferPoolCapacity"/>; 32 MiB by default). Build validates it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long BufferPoolCapacity
    {
        get => _options.BufferPoolCapacity;
        set { _state.EnsureMutable(); _options.BufferPoolCapacity = value; }
    }

    /// <summary>Gets or sets the page write-back cadence.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan PageWriteBackInterval
    {
        get => _options.PageWriteBackInterval;
        set { _state.EnsureMutable(); _options.PageWriteBackInterval = value; }
    }

    /// <summary>Gets or sets the maximum pages written per pass.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public int PageWriteBackBatchSize
    {
        get => _options.PageWriteBackBatchSize;
        set { _state.EnsureMutable(); _options.PageWriteBackBatchSize = value; }
    }

    /// <summary>Gets or sets the maintenance cadence.</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan MaintenanceInterval
    {
        get => _options.MaintenanceInterval;
        set { _state.EnsureMutable(); _options.MaintenanceInterval = value; }
    }

    /// <summary>
    /// Gets or sets how many levels a SQL expression may nest in a statement the built engine
    /// executes (#1151): the deepest expression tree, in which a chain of <c>AND</c> (or
    /// <c>OR</c>) terms is one level however many terms it has, and the deepest grouping
    /// parentheses. Until it is set, it reports the engine's default,
    /// <see cref="SqlQueryParserOptions.DefaultExpressionNestingLimit"/> (256). The value must
    /// lie within <see cref="SqlQueryParserOptions.MinimumExpressionNestingLimit"/> (32) and
    /// <see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/> (4096). Setting the
    /// property does not check it; <see cref="Build"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> for a value outside that range, before it
    /// creates the engine.
    /// </summary>
    /// <remarks>
    /// It is the builder's form of <see cref="SqlDatabaseEngineOptions.ExpressionNestingLimit"/>,
    /// which describes how the engine applies the limit; <see cref="Build"/> passes it to the
    /// engine it creates, and reports the range check <see cref="SqlDatabaseEngine.Create"/> makes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public int ExpressionNestingLimit
    {
        get => _options.ExpressionNestingLimit;
        set { _state.EnsureMutable(); _options.ExpressionNestingLimit = value; }
    }

    /// <summary>
    /// Gets or sets the storage strategy; internal with the strategy base (concrete-types plan, D9),
    /// for this assembly's tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    internal SqlStorageStrategy? StorageStrategy
    {
        get => _options.StorageStrategy;
        set { _state.EnsureMutable(); _options.StorageStrategy = value; }
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

    /// <summary>Freezes composition and constructs the engine, its workers and its servers.</summary>
    /// <returns>The operational engine, whose servers remain stopped until application startup.</returns>
    /// <exception cref="InvalidOperationException">
    /// A build was already attempted; a factory returned null; or the engine refused a product (a
    /// duplicate worker name, a product returned twice, a server that fronts another engine).
    /// </exception>
    /// <exception cref="ArgumentException">An option is invalid (see <see cref="SqlDatabaseEngine.Create"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ExpressionNestingLimit"/> is outside its range, or another option is (see
    /// <see cref="SqlDatabaseEngine.Create"/>); checked before the engine is created.
    /// </exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public SqlDatabaseEngine Build()
    {
        _state.BeginBuild();
        var engine = SqlDatabaseEngine.CreateUncomposed(_options);
        return _state.Complete(engine, engine.Compose);
    }

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards (the <c>AddSql</c> verb's compensation).
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    internal void Abort(Exception failure) => _state.Abort(failure);
}
