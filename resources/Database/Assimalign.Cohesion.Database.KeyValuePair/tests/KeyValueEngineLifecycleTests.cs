using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The engine as a data machine: operational from creation with its worker
/// inventory pumping, database lifecycle (create/open/drop), durable disposal,
/// and restart recovery over both file sets (data + catalog) — entries AND the
/// primary key index must survive a reopen.
/// </summary>
public sealed class KeyValueEngineLifecycleTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-kv-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Engine: Create returns an operational data machine with the five-worker inventory")]
    public void Create_NewEngine_ShouldBeOperationalWithWorkerInventory()
    {
        // Arrange / Act
        using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-inventory" });

        // Assert
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.KeyValueStore);
        engine.Workers.Count.ShouldBe(5);
        engine.Workers.Select(worker => worker.Kind).ShouldBe(
        [
            DatabaseEngineWorkerKind.WriteAheadFlush,
            DatabaseEngineWorkerKind.PageWriteBack,
            DatabaseEngineWorkerKind.Checkpoint,
            DatabaseEngineWorkerKind.VersionPurge,
            DatabaseEngineWorkerKind.IndexMaintenance,
        ]);
        engine.Workers.ShouldAllBe(worker => worker.Name.StartsWith("kv-inventory/"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Engine: Databases create, enumerate, and drop")]
    public async Task DatabaseLifecycle_CreateEnumerateDrop_ShouldRoundTrip()
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions());

        // Act
        var database = await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
        database.ShouldBeAssignableTo<IKeyValueDatabase>();

        var names = new List<string>();
        await foreach (var found in engine.GetDatabasesAsync(TestTimeout.Token()))
        {
            names.Add(found.Name);
        }

        await engine.DropDatabaseAsync("kv", TestTimeout.Token());

        // Assert
        names.ShouldBe(["kv"]);
        engine.TryGetDatabase("kv", out _).ShouldBeFalse();
        await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("kv", TestTimeout.Token()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Engine: Disposal is idempotent and terminal")]
    public async Task Dispose_Twice_ShouldBeIdempotentAndTerminal()
    {
        // Arrange
        var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions());
        await engine.CreateDatabaseAsync("kv", TestTimeout.Token());

        // Act
        await engine.DisposeAsync();
        await engine.DisposeAsync();

        // Assert
        engine.State.ShouldBe(EngineState.Disposed);
        Should.Throw<ObjectDisposedException>(() => engine.TryGetDatabase("kv", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Recovery: Committed entries and the primary index survive a restart over both file sets")]
    public async Task Restart_CommittedEntries_ShouldRecoverDataAndIndex()
    {
        // Arrange: a file-backed engine — the restart reopens the REAL file sets
        // (data + catalog), so this proves journal recovery, catalog reload, and
        // primary-index re-attachment together.
        var putETags = new Dictionary<string, long>();

        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();

            foreach (string key in new[] { "alpha", "bravo", "charlie" })
            {
                var put = await database.PutAsync(session, Bytes(key), Bytes("v-" + key), cancellationToken: TestTimeout.Token());
                putETags[key] = put.ETag!.Value;
            }

            await database.TryDeleteAsync(session, Bytes("bravo"), cancellationToken: TestTimeout.Token());
        }

        // Act: a fresh engine over the same root.
        await using (var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await reopened.OpenDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();

            // Assert: point reads ride the recovered primary index (a get IS an
            // index seek — a broken re-attachment cannot pass this).
            var alpha = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());
            Text(alpha!.Value.Value).ShouldBe("v-alpha");
            alpha.Value.ETag.ShouldBe(putETags["alpha"]);

            (await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token())).ShouldBeNull();

            var keys = new List<string>();
            await foreach (var entry in database.ScanAsync(session, null, TestTimeout.Token()))
            {
                keys.Add(Text(entry.Key));
            }

            keys.ShouldBe(["alpha", "charlie"]);

            // And the recovered database accepts new writes.
            (await database.PutAsync(session, Bytes("delta"), Bytes("post-restart"), cancellationToken: TestTimeout.Token()))
                .Applied.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Recovery: A transaction never committed is scrubbed from data and index on reopen")]
    public async Task Restart_UncommittedTransaction_ShouldScrubUnprovenWriter()
    {
        // Arrange: an explicit transaction writes but never commits; the engine
        // disposes underneath it (the abort path) — and, decisively, the reopen
        // must classify + scrub whatever reached the journal.
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();
            await database.PutAsync(session, Bytes("committed"), Bytes("stays"), cancellationToken: TestTimeout.Token());

            await session.BeginTransactionAsync(TestTimeout.Token());
            await database.PutAsync(session, Bytes("uncommitted"), Bytes("goes"), cancellationToken: TestTimeout.Token());
            // No commit: engine disposal aborts the in-flight transaction.
        }

        // Act
        await using (var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await reopened.OpenDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();

            // Assert
            (await database.GetAsync(session, Bytes("uncommitted"), TestTimeout.Token())).ShouldBeNull();
            (await database.ExistsAsync(session, Bytes("uncommitted"), TestTimeout.Token())).ShouldBeFalse();
            Text((await database.GetAsync(session, Bytes("committed"), TestTimeout.Token()))!.Value.Value).ShouldBe("stays");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: A database on a newer entry-space format is rejected at open")]
    public async Task Open_NewerEntrySpaceFormat_ShouldBeRejected()
    {
        // Arrange: create a database, then forge its catalog's format marker to a
        // future version (the compatibility gate is the catalog marker).
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            var instance = (Internal.KeyValueDatabaseInstance)database;
            await instance.Catalog.SetEntrySpaceFormatVersionAsync(99, TestTimeout.Token());
        }

        // Act / Assert
        await using (var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await reopened.OpenDatabaseAsync("kv", TestTimeout.Token()));

            failure.Message.ShouldContain("format", Case.Insensitive);
        }
    }
}
