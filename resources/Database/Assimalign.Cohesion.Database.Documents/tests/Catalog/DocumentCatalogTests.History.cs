using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents.Catalog] - History: seeded operations match the physical highest-visible-writer oracle (#1281)")]
    [InlineData(17)]
    [InlineData(1281)]
    [InlineData(907)]
    public async Task FindDocument_SeededWriterHandoff_ShouldMatchPhysicalVisibilityOracle(int seed)
    {
        // Arrange: one model writer holds publication through commit or completed undo.
        await using var database = new CatalogHistoryDatabase();
        var random = new Random(seed);
        var parent = Guid.NewGuid();
        var readers = new List<TransactionContext>();
        TransactionContext? writer = null;
        ulong version = 0;
        int verified = 0;
        int deferred = 0;
        int reopened = 0;

        void Verify()
        {
            var snapshots = readers.Select(reader => reader.Snapshot).ToList();
            if (writer is { State: TransactionState.Active }) { snapshots.Add(writer.Snapshot); }
            foreach (var snapshot in snapshots)
            {
                var expected = new Dictionary<string, (DocumentCatalogEntry Entry, TransactionSequence Writer)>(StringComparer.Ordinal);
                using (var iterator = database.Storage.GetUnitIterator(0))
                {
                    while (iterator.MoveNext())
                    {
                        var unit = iterator.Current;
                        var record = DocumentCatalogCodec.Decode(unit.Data.Span);
                        var (created, deleted) = RecordVersionStamp.ReadStamps(unit.Data.Span);
                        if (record.Document is not { } entry || entry.CollectionId != parent
                            || !snapshot.IsVisible(created)
                            || (deleted != TransactionSequence.None && snapshot.IsVisible(deleted))) { continue; }
                        if (!expected.TryGetValue(entry.Name, out var prior) || created >= prior.Writer)
                        {
                            expected[entry.Name] = (entry, created);
                        }
                    }
                }
                foreach (string name in Enumerable.Range(0, 6).Select(index => $"n{index}").Append("missing"))
                {
                    var actual = database.Catalog.FindDocument(parent, name, snapshot);
                    actual.ShouldBe(expected.TryGetValue(name, out var found) ? found.Entry : null,
                        $"seed={seed}, verification={verified}, name={name}, owner={snapshot.Owner.Value}");
                }
                database.Catalog.GetDocuments(parent, null, snapshot)
                    .ShouldBe(expected.Values.Select(value => value.Entry).OrderBy(entry => entry.Name, StringComparer.Ordinal));
                database.Catalog.GetDocuments(parent, "n1", snapshot)
                    .ShouldBe(expected.Values.Select(value => value.Entry).Where(entry => entry.Name.StartsWith("n1", StringComparison.Ordinal)));
            }
            verified++;
        }

        async Task Begin(IsolationLevel isolation = IsolationLevel.Snapshot)
        {
            writer = await database.Coordinator.BeginAsync(isolation);
            Verify();
        }

        async Task Save(string name)
        {
            var entry = new DocumentCatalogEntry(parent, name, ++version, 1, 2, 0);
            await database.Catalog.SaveDocumentAsync(entry, writer!);
            Verify();
        }

        async Task Delete(string name)
        {
            await database.Catalog.DeleteDocumentAsync(parent, name, writer!);
            Verify();
        }

        async Task End(bool commit)
        {
            if (commit) { await database.Coordinator.CommitAsync(writer!); }
            else { await database.Coordinator.RollbackAsync(writer!); }
            writer = null;
            Verify();
        }

        void Reopen()
        {
            database.Catalog = DocumentCatalog.Open(database.Storage, database.Coordinator);
            reopened++;
            Verify();
        }

        // Mandatory recovered all-tombstoned case, with a retained reader of v2.
        await Begin();
        await Save("n0");
        await End(true);
        await Begin();
        await Save("n0");
        await End(true);
        readers.Add(await database.Coordinator.BeginAsync(IsolationLevel.Snapshot));
        Verify();
        await Begin();
        await Delete("n0");
        await End(true);
        Reopen();

        // Mandatory late ReadCommitted publication followed by one invisible version.
        await Begin(IsolationLevel.ReadCommitted);
        var early = writer;
        writer = null;
        for (int index = 0; index < 32; index++)
        {
            await Begin();
            await Save("n1");
            await End(true);
        }
        writer = early;
        await Save("n1");
        await End(true);
        await Begin();
        await Save("n1");
        readers.Add(await database.Coordinator.BeginAsync(IsolationLevel.Snapshot));
        Verify();
        Reopen();
        await End(false);

        // Mandatory recreate over a physically purged deletion, then rollback.
        foreach (var reader in readers) { await database.Coordinator.RollbackAsync(reader); }
        readers.Clear();
        await Begin();
        await Delete("n0");
        await End(true);
        await Begin();
        int retainedBeforePurge = database.Catalog.CachedReferenceCount;
        // Leave cached deleted heads in place so Save itself must handle reclamation.
        database.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        database.Catalog.CachedReferenceCount.ShouldBe(retainedBeforePurge);
        await Save("n0");
        await End(false);
        readers.Add(await database.Coordinator.BeginAsync(IsolationLevel.Snapshot));
        Verify();

        // Act: random histories include deferred undo, physical purge, retained readers,
        // same-writer saves/deletes/recreates, both isolation levels and repeated Open.
        for (int step = 0; step < 400; step++)
        {
            if (database.Coordinator.VersionStore.PendingAbortedPurges.Count != 0)
            {
                Reopen();
                database.Catalog.RunVersionPurgePass(default);
                Verify();
                continue;
            }
            string name = $"n{random.Next(6)}";
            switch (random.Next(11))
            {
                case 0:
                case 1:
                case 2:
                    if (writer is null) { await Begin(random.Next(2) == 0 ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted); }
                    await Save(name);
                    break;
                case 3:
                    if (writer is null) { await Begin(); }
                    await Delete(name);
                    break;
                case 4:
                    if (writer is not null) { await End(true); }
                    break;
                case 5:
                    if (writer is not null) { await End(false); }
                    break;
                case 6:
                    if (writer is null) { await Begin(); }
                    await Save(name);
                    database.FailUndo = true;
                    var aborted = writer!.Sequence.Value;
                    await End(false);
                    database.Coordinator.VersionStore.PendingAbortedPurges.ShouldContain(aborted);
                    database.FailUndo = false;
                    deferred++;
                    break;
                case 7:
                    if (readers.Count < 12)
                    {
                        readers.Add(await database.Coordinator.BeginAsync(random.Next(3) == 0 ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot));
                    }
                    break;
                case 8:
                    if (readers.Count != 0)
                    {
                        int index = random.Next(readers.Count);
                        await database.Coordinator.RollbackAsync(readers[index]);
                        readers.RemoveAt(index);
                    }
                    break;
                case 9:
                    database.Catalog.RunVersionPurgePass(default);
                    break;
                case 10:
                    Reopen();
                    break;
            }
            var fresh = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            readers.Add(fresh);
            Verify();
            readers.Remove(fresh);
            await database.Coordinator.RollbackAsync(fresh);
        }

        // Assert: every operation was checked, including the injected deferred-undo window.
        verified.ShouldBeGreaterThan(400);
        deferred.ShouldBeGreaterThan(0);
        reopened.ShouldBeGreaterThan(2);
        if (writer is not null) { await End(false); }
        foreach (var reader in readers) { await database.Coordinator.RollbackAsync(reader); }
        database.Catalog.RunVersionPurgePass(default);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        _output.WriteLine($"Seed {seed}: {verified} checks, {deferred} deferred undos, {reopened} reopens");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Purge: bounded maintenance retires idle histories without lookups (#1281)")]
    public async Task RunVersionPurgePass_IdleNames_ShouldRetirePurgedReferencesWithoutReads()
    {
        // Arrange
        await using var database = new CatalogHistoryDatabase();
        var parent = Guid.NewGuid();
        var pinned = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        const int names = 32;
        const int versions = 17;
        for (int version = 1; version <= versions; version++)
        {
            var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            for (int name = 0; name < names; name++)
            {
                var entry = new DocumentCatalogEntry(parent, $"n{name}", (ulong)version, 1, 2, 0);
                await database.Catalog.SaveDocumentAsync(entry, writer);
            }
            await database.Coordinator.CommitAsync(writer);
        }
        database.Catalog.CachedReferenceCount.ShouldBe(names * versions);
        database.Catalog.RunVersionPurgePass(default).ShouldBe(0);
        database.Catalog.CachedReferenceCount.ShouldBe(names * versions);
        await database.Coordinator.RollbackAsync(pinned);

        // Act: no Find or enumeration after releasing the snapshot.
        for (int pass = 0; pass < 16; pass++)
        {
            long before = database.Catalog.RecordReadCount;
            database.Catalog.RunVersionPurgePass(default);
            (database.Catalog.RecordReadCount - before).ShouldBeLessThanOrEqualTo(128);
        }

        // Assert
        database.Catalog.CachedReferenceCount.ShouldBe(names);
    }
}
