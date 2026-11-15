using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal sealed class HealthIdentityEngine : DatabaseEngine
{
    private readonly DatabaseInstance _database;

    internal HealthIdentityEngine() : base("custom", EngineModel.Custom)
    {
        _database = new IdentityDatabase(this);
    }

    public override IReadOnlyList<DatabaseName> OfflineDatabases => [];
    protected override IReadOnlyList<DatabaseInstance> GetOpenDatabasesCore() => [_database];
    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, object? creationState, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    protected override IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken)
        => throw new NotSupportedException();
    protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        database = name == _database.Name ? _database : null;
        return database is not null;
    }
    protected override void ForgetClosedDatabaseCore(DatabaseInstance database) { }
    protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name) => null;
    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure) => false;
    protected override ValueTask DisposeAsyncCore() => _database.DisposeAsync();

    private sealed class IdentityDatabase : DatabaseInstance
    {
        internal IdentityDatabase(DatabaseEngine engine) : base("tenant 1", engine) { }
        protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override void DisposeCore() { }
        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
