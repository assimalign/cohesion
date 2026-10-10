using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A minimal engine of the root base — a data machine with no databases — for composition tests
/// that only need an engine-shaped registration. It implements only the protected cores
/// (<c>database-area.md</c>, "Test doubles"); the base owns its state, its workers and its servers.
/// </summary>
/// <remarks>
/// The double never completes its composition, so a test can attach a server after the
/// application was built (a fake engine that mutates). Its state is the base's: a test makes it
/// <see cref="EngineState.Faulted"/> by failing a pass of an attached
/// <see cref="RecordingEngineWorker"/>, and <see cref="EngineState.Disposed"/> by disposing it.
/// </remarks>
internal sealed class RecordingEngine : DatabaseEngine
{
    internal RecordingEngine(string name = "recording-engine")
        : base(name, EngineModel.Sql)
    {
    }

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseName> OfflineDatabases => Offline;

    /// <summary>Gets or sets the databases the engine reports offline.</summary>
    internal IReadOnlyList<DatabaseName> Offline { get; set; } = [];

    /// <summary>Gets how many times the base ran the engine's own disposal core.</summary>
    internal int DisposeCount { get; private set; }

    /// <summary>Gets or sets a failure the engine's disposal core throws.</summary>
    internal Exception? DisposeException { get; set; }

    /// <summary>Attaches the server the factory creates over this engine.</summary>
    internal void AddServer(Func<RecordingEngine, DatabaseServer> factory) => AttachServer(factory(this));

    /// <summary>Attaches a worker, whose pump starts at once.</summary>
    internal void AddWorker(DatabaseEngineWorker worker) => AttachWorker(worker);

    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        database = null;
        return false;
    }

    protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
    {
    }

    // The double holds no storage, so it never says what took a database offline.
    protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name) => null;

    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
        => false;

    protected override ValueTask DisposeAsyncCore()
    {
        DisposeCount++;
        return DisposeException is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
    }
}
