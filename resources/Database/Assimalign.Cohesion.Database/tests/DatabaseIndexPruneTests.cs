using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseIndexPruneTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Index prune: every B-tree model uses the coordinator pass and its current manager (#1195)")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    public async Task RunVersionPurgePass_EachModel_ShouldPruneCurrentIndexManager(string model)
    {
        // Arrange
        await using DatabaseEngine engine = model switch
        {
            "Sql" => SqlDatabaseEngine.Create("prune", new() { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) }),
            "KeyValuePair" => KeyValueDatabaseEngine.Create("prune", new() { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) }),
            "Documents" => DocumentDatabaseEngine.Create("prune", new() { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) }),
            _ => GraphDatabaseEngine.Create("prune", new() { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) }),
        };
        var database = await engine.CreateDatabaseAsync("db");
        TransactionCoordinator coordinator;
        BTreeIndexManager indexes;
        BTreeIndex? index = null;
        switch (database)
        {
            case SqlDatabase sql:
                coordinator = Coordinator(sql);
                await using (var session = await sql.CreateSessionAsync())
                {
                    await session.ExecuteAsync("CREATE TABLE prune_probe(v BIGINT)");
                    await session.ExecuteAsync("CREATE INDEX prune_probe ON prune_probe(v)");
                }
                // Attach a probe handle to the published tree. The owning SQL
                // directory must prune these same pages through its callback;
                // this does not depend on that directory's private CLR type.
                var registrations = Catalog(sql).GetIndexRegistrations();
                indexes = BTreeIndexManager.Create(new BTreeIndexManagerOptions
                {
                    Storage = Storage(coordinator), ExistingIndexes = registrations,
                    TransactionSource = _ => throw new InvalidOperationException("The probe uses explicit storage brackets."),
                });
                indexes.TryGetIndex(registrations.Single().ObjectId, "prune_probe", out index).ShouldBeTrue();
                break;
            case KeyValueDatabase keyValue:
                coordinator = Coordinator(keyValue);
                indexes = Indexes(keyValue);
                break;
            case DocumentDatabase documents:
                coordinator = Coordinator(documents);
                var catalog = Catalog(documents);
                ReloadIndexes(catalog, scheduleReclamation: true);
                indexes = Indexes(catalog);
                break;
            case GraphDatabase graph:
                coordinator = Coordinator(graph);
                var store = Store(graph);
                ReloadIndexes(store);
                indexes = Indexes(store);
                break;
            default: throw new InvalidOperationException("Unknown B-tree model.");
        }

        if (index is null)
        {
            var setup = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            index = await coordinator.ApplyStatementAsync<BTreeIndex>(setup,
                _ => indexes.CreateIndexAsync(setup, ulong.MaxValue - 1, new IndexDefinition("prune-probe")));
            await coordinator.CommitAsync(setup);
        }
        var creator = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.CommitAsync(creator);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.CommitAsync(deleter);
        using (var bracket = Storage(coordinator).BeginTransaction())
        {
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(1), 42, creator.Sequence, deleter.Sequence);
            bracket.Commit();
        }
        (await ExaminedAsync(coordinator, index)).ShouldBe(1);

        // Act
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBeGreaterThanOrEqualTo(1);

        // Assert: the current manager, including one reloaded after composition, was swept.
        (await ExaminedAsync(coordinator, index)).ShouldBe(0);
    }

    private static async Task<long> ExaminedAsync(TransactionCoordinator coordinator, BTreeIndex index)
    {
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
            (await cursor.MoveNextAsync()).ShouldBeFalse();
            return Examined(cursor);
        }
        finally { await coordinator.RollbackAsync(reader); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(KeyValueDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(DocumentDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Catalog")]
    private static extern SqlCatalog Catalog(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IndexManager")]
    private static extern BTreeIndexManager Indexes(KeyValueDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Catalog")]
    private static extern DocumentCatalog Catalog(DocumentDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Store")]
    private static extern GraphStore Store(GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_indexes")]
    private static extern ref BTreeIndexManager Indexes(DocumentCatalog catalog);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_indexes")]
    private static extern ref BTreeIndexManager Indexes(GraphStore store);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_storage")]
    private static extern ref Storage.Storage Storage(TransactionCoordinator coordinator);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OpenIndexes")]
    private static extern void ReloadIndexes(DocumentCatalog catalog, bool scheduleReclamation);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OpenIndexes")]
    private static extern void ReloadIndexes(GraphStore store);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EntriesExamined")]
    private static extern long Examined(BTreeCursor cursor);
}
