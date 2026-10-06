using System;
using System.IO;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Captures dependency-free blob engine options and deferred worker and server factories for one
/// engine construction attempt.
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
/// case; the built-in workers are named <c>{engine}/wal-flush</c>, <c>{engine}/page-writeback</c>,
/// <c>{engine}/checkpoint</c> and <c>{engine}/version-purge</c>), a product a factory returned
/// twice, and a server that fronts another engine. A worker's blank name is refused by the
/// worker's own constructor, inside its factory. Each worker's pump thread is named for the worker.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, D5, phase 4, #1260).</b> Sealed, with an internal constructor:
/// <see cref="BlobDatabaseEngine.CreateBuilder"/> and the <c>AddBlob</c> composition verb
/// create it. It replaces the former <c>IBlobDatabaseEngineBuilder</c> interface, and its
/// factories are typed over the blob engine instead of the root interfaces.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class BlobDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState<BlobDatabaseEngine, DatabaseEngineWorker, DatabaseServer> _state = new();
    private readonly BlobDatabaseEngineOptions _options = new();

    internal BlobDatabaseEngineBuilder()
    {
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.EngineName" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public string? EngineName
    {
        get => _options.EngineName;
        set { _state.EnsureMutable(); _options.EngineName = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.RootPath" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public FileSystemPath? RootPath
    {
        get => _options.RootPath;
        set { _state.EnsureMutable(); _options.RootPath = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.Durability" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public StorageCommitDurability? Durability
    {
        get => _options.Durability;
        set { _state.EnsureMutable(); _options.Durability = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.GroupCommitWindow" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan GroupCommitWindow
    {
        get => _options.GroupCommitWindow;
        set { _state.EnsureMutable(); _options.GroupCommitWindow = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.CheckpointInterval" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan CheckpointInterval
    {
        get => _options.CheckpointInterval;
        set { _state.EnsureMutable(); _options.CheckpointInterval = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.CheckpointJournalSize" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long CheckpointJournalSize
    {
        get => _options.CheckpointJournalSize;
        set { _state.EnsureMutable(); _options.CheckpointJournalSize = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.BufferPoolCapacity" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public long BufferPoolCapacity
    {
        get => _options.BufferPoolCapacity;
        set { _state.EnsureMutable(); _options.BufferPoolCapacity = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.PageWriteBackInterval" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan PageWriteBackInterval
    {
        get => _options.PageWriteBackInterval;
        set { _state.EnsureMutable(); _options.PageWriteBackInterval = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.PageWriteBackBatchSize" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public int PageWriteBackBatchSize
    {
        get => _options.PageWriteBackBatchSize;
        set { _state.EnsureMutable(); _options.PageWriteBackBatchSize = value; }
    }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.MaintenanceInterval" />
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public TimeSpan MaintenanceInterval
    {
        get => _options.MaintenanceInterval;
        set { _state.EnsureMutable(); _options.MaintenanceInterval = value; }
    }

    /// <summary>
    /// Gets or sets the storage strategy, which overrides <see cref="RootPath"/>; internal with the
    /// strategy base (concrete-types plan, D9), for this assembly's tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    internal BlobStorageStrategy? StorageStrategy
    {
        get => _options.StorageStrategy;
        set { _state.EnsureMutable(); _options.StorageStrategy = value; }
    }

    /// <summary>Registers a factory for an engine-owned background worker.</summary>
    /// <param name="configure">The factory, invoked once against the constructed engine, after every worker registered before it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public BlobDatabaseEngineBuilder AddWorker(Func<BlobDatabaseEngine, DatabaseEngineWorker> configure)
    {
        _state.AddWorker(configure);
        return this;
    }

    /// <summary>Registers a factory for an engine-owned server.</summary>
    /// <param name="configure">The factory, invoked once against the engine the server must front, after every worker.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public BlobDatabaseEngineBuilder AddServer(Func<BlobDatabaseEngine, DatabaseServer> configure)
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
    /// <exception cref="ArgumentException">An option is invalid (see <see cref="BlobDatabaseEngine.Create"/>).</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public BlobDatabaseEngine Build()
    {
        _state.BeginBuild();
        var engine = BlobDatabaseEngine.CreateUncomposed(_options);
        return _state.Complete(engine, engine.Compose);
    }

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards (the <c>AddBlob</c> verb's compensation).
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    internal void Abort(Exception failure) => _state.Abort(failure);
}
