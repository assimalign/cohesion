using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

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
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("kv", TestTimeout.Token()));
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

            failure.Message.ShouldContain("uses entry-space format 99, but this engine supports only format 2", Case.Sensitive);
            failure.Message.ShouldContain("newer engine");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: A new database is stamped with entry-space format 2 and reopens (#1194)")]
    public async Task Create_ShouldStampCurrentEntrySpaceFormat()
    {
        // Arrange / Act
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            var catalog = ((Internal.KeyValueDatabaseInstance)database).Catalog;
            catalog.EntrySpaceFormatVersion.ShouldBe(2);
            catalog.GetIndexRegistrations().ShouldHaveSingleItem();
            await using var session = await database.CreateSessionAsync();
            await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        }

        // Assert
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath });
        var reopenedDatabase = (IKeyValueDatabase)await reopened.OpenDatabaseAsync("kv", TestTimeout.Token());
        ((Internal.KeyValueDatabaseInstance)reopenedDatabase).Catalog.EntrySpaceFormatVersion.ShouldBe(2);
        await using var reader = await reopenedDatabase.CreateSessionAsync();
        Text((await reopenedDatabase.GetAsync(reader, Bytes("alpha"), TestTimeout.Token()))!.Value.Value).ShouldBe("one");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: A database on entry-space format 1 is refused at open, its files untouched (#1194)")]
    public async Task Open_OlderEntrySpaceFormat_ShouldBeRefusedWithoutTouchingFiles()
    {
        // Arrange: a closed database whose marker reads 1, as every engine before #1194
        // stamped it (their primary index trees are in B-tree page format 1).
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();
            await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
            await ((Internal.KeyValueDatabaseInstance)database).Catalog.SetEntrySpaceFormatVersionAsync(1, TestTimeout.Token());
        }

        var before = Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath });
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("kv", TestTimeout.Token()));

        // Assert: the refusal names the database, both formats and the remedy, and the
        // gate read only the catalog: nothing was written.
        failure.Message.ShouldStartWith("Database 'kv' uses entry-space format 1, but this engine supports only format 2.", Case.Sensitive);
        failure.Message.ShouldContain("#1152");
        reopened.TryGetDatabase("kv", out _).ShouldBeFalse();
        await reopened.DisposeAsync();

        foreach (var (path, bytes) in before)
        {
            File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: A database whose catalog registers no primary index (an interrupted creation) opens on entry-space format 2")]
    public async Task Open_CatalogWithoutRegistration_ShouldBootstrapOnCurrentFormat()
    {
        // Arrange: a database whose creation stopped before its catalog held anything —
        // modelled by a created, empty database whose catalog file set is gone. The
        // reopened catalog reads as format 1 with no registration.
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
        }

        Directory.Delete(Path.Combine(_rootPath, "kv" + KeyValueDatabaseEngine.CatalogSuffix), recursive: true);

        // Act
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath });
        var database = (IKeyValueDatabase)await reopened.OpenDatabaseAsync("kv", TestTimeout.Token());

        // Assert: the primary index was bootstrapped and the current marker stamped.
        var catalog = ((Internal.KeyValueDatabaseInstance)database).Catalog;
        catalog.EntrySpaceFormatVersion.ShouldBe(2);
        catalog.GetIndexRegistrations().ShouldHaveSingleItem();
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        Text((await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token()))!.Value.Value).ShouldBe("one");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: A database whose primary index is in B-tree page format 1 is refused at open with COHDBI001, its files untouched (#1194)")]
    public async Task Open_PrimaryIndexInFormatOne_ShouldBeRefused()
    {
        // Arrange: a closed database whose index pages are rewritten into the layout
        // engines before #1194 wrote (entries ordered by key alone, no page stamp).
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();
            await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        }

        LegacyBTreePages.DowngradeDataFiles(_rootPath).ShouldBeGreaterThan(0);
        var before = Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath });
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("kv", TestTimeout.Token()));

        // Assert: the coded refusal names the database and both formats, and the open
        // wrote nothing — it failed before recovery's scrub and checkpoint.
        failure.Message.ShouldStartWith("Database 'kv' cannot be opened. " + IndexFormatException.ErrorCode + ": ", Case.Sensitive);
        failure.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<IndexFormatException>().FoundVersion.ShouldBe(1);

        foreach (var (path, bytes) in before)
        {
            File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
        }
    }

    /// <summary>
    /// The storage refuses a file set in another storage format with <c>COHDBS001</c> (#1251).
    /// A database has a data and a catalog file set, so the engine names the database and the
    /// one that was refused.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Format: a file set in storage format 2 (the format before #1253) is refused at open with COHDBS001 naming the database and the file set, its files untouched (#1251, #1253)")]
    [InlineData("data")]
    [InlineData("catalog")]
    public async Task Open_FileSetInStorageFormatTwo_ShouldBeRefusedNamingTheDatabaseAndTheFileSet(string role)
    {
        // Arrange: a closed database whose page 0 in one file set names storage format 2, the format before #1253.
        await using (var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath }))
        {
            var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
            await using var session = await database.CreateSessionAsync();
            await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        }

        string storageName = role == "catalog" ? "kv" + KeyValueDatabaseEngine.CatalogSuffix : "kv";
        StorageFormatFiles.WriteVersion(Path.Combine(_rootPath, storageName, storageName + ".dat"), version: 2);
        var before = Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { RootPath = _rootPath });
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("kv", TestTimeout.Token()));

        // Assert
        failure.Message.ShouldStartWith(
            $"Database 'kv' cannot be opened: its {role} file set '{storageName}' was refused. {StorageFormatException.ErrorCode}: ",
            Case.Sensitive);
        failure.Message.ShouldContain("uses storage format 2, but this engine supports only storage format 3", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<StorageFormatException>().FoundVersion.ShouldBe(2);
        reopened.TryGetDatabase("kv", out _).ShouldBeFalse();
        Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).Length.ShouldBe(before.Count);
        foreach (var (path, bytes) in before)
        {
            File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
        }
    }
}
