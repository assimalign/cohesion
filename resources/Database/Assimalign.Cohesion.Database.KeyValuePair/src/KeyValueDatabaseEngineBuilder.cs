using System;
using System.IO;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Captures key-value engine options and deferred worker and server factories for one engine
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
/// <see cref="KeyValueDatabaseEngine.CreateBuilder"/> and the <c>AddKeyValue</c> composition verb
/// create it. It replaces the former <c>IKeyValueDatabaseEngineBuilder</c> interface, and its
/// factories are typed over the key-value engine instead of the root interfaces.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class KeyValueDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState<KeyValueDatabaseEngine> _state = new();
    private readonly KeyValueDatabaseEngineOptions _options = new();

    internal KeyValueDatabaseEngineBuilder()
    {
    }

    /// <summary>Gets or sets the logical engine name; null selects <c>keyvalue-engine</c>.</summary>
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

    /// <summary>Gets or sets the checkpoint time backstop (<see cref="KeyValueDatabaseEngineOptions.CheckpointInterval"/>).</summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan CheckpointInterval
    {
        get => _options.CheckpointInterval;
        set { _state.EnsureMutable(); _options.CheckpointInterval = value; }
    }

    /// <summary>
    /// Gets or sets the journal size, in bytes, that triggers a checkpoint
    /// (<see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/>; 256 MiB by default, zero for time only).
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long CheckpointJournalSize
    {
        get => _options.CheckpointJournalSize;
        set { _state.EnsureMutable(); _options.CheckpointJournalSize = value; }
    }

    /// <summary>
    /// Gets or sets how long a worker's failures of one database must persist before the engine
    /// takes it offline (<see cref="KeyValueDatabaseEngineOptions.WorkerFailureWindow"/>; one hundred seconds by
    /// default). Build validates it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan WorkerFailureWindow
    {
        get => _options.WorkerFailureWindow;
        set { _state.EnsureMutable(); _options.WorkerFailureWindow = value; }
    }

    /// <summary>
    /// Gets or sets how many failed passes in a row a worker's failures of one database must span
    /// before the engine takes it offline (<see cref="KeyValueDatabaseEngineOptions.WorkerFailureMinimumPasses"/>;
    /// three by default). Build validates it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public int WorkerFailureMinimumPasses
    {
        get => _options.WorkerFailureMinimumPasses;
        set { _state.EnsureMutable(); _options.WorkerFailureMinimumPasses = value; }
    }

    /// <summary>
    /// Gets or sets the hard cap, in bytes, on a journal whose checkpoints keep failing
    /// (<see cref="KeyValueDatabaseEngineOptions.JournalSizeLimit"/>; zero for four times the
    /// checkpoint journal size). Build validates it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long JournalSizeLimit
    {
        get => _options.JournalSizeLimit;
        set { _state.EnsureMutable(); _options.JournalSizeLimit = value; }
    }

    /// <summary>
    /// Gets or sets each database's buffer pool capacity, in bytes
    /// (<see cref="KeyValueDatabaseEngineOptions.BufferPoolCapacity"/>; 32 MiB by default). Build validates it.
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
    /// Gets or sets the storage strategy; internal with the strategy base (concrete-types plan, D9),
    /// for this assembly's tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    internal KeyValueStorageStrategy? StorageStrategy
    {
        get => _options.StorageStrategy;
        set { _state.EnsureMutable(); _options.StorageStrategy = value; }
    }

    /// <summary>Registers a factory for an engine-owned background worker.</summary>
    /// <param name="configure">The factory, invoked once against the constructed engine, after every worker registered before it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public KeyValueDatabaseEngineBuilder AddWorker(Func<KeyValueDatabaseEngine, DatabaseEngineWorker> configure)
    {
        _state.AddWorker(configure);
        return this;
    }

    /// <summary>Registers a factory for an engine-owned server.</summary>
    /// <param name="configure">The factory, invoked once against the engine the server must front, after every worker.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public KeyValueDatabaseEngineBuilder AddServer(Func<KeyValueDatabaseEngine, DatabaseServer> configure)
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
    /// <exception cref="ArgumentException">An option is invalid (see <see cref="KeyValueDatabaseEngine.Create"/>).</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public KeyValueDatabaseEngine Build()
    {
        _state.BeginBuild();
        var engine = KeyValueDatabaseEngine.CreateUncomposed(_options);
        return _state.Complete(engine, engine.Compose, KeyValueDatabaseEngine.ReleaseRefusedWorkerAsync);
    }

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards (the <c>AddKeyValue</c> verb's compensation).
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    internal void Abort(Exception failure) => _state.Abort(failure);
}
