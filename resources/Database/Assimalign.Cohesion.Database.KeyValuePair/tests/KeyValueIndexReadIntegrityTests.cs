using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using EventRecorder = Assimalign.Cohesion.Testing.EventSourceRecorder;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The entries a primary-index seek fetches (#1342). An index entry whose entry record was
/// reclaimed beneath it is stale and reads as absence. An entry page that cannot be read is not
/// reclaimed: before the fix every storage error read as absence, so a page that failed its
/// checksum made its keys disappear from Get, and a Put over such a key resolved it as missing and
/// failed with a retryable write-write conflict that no retry could clear.
/// </summary>
[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueIndexReadIntegrityTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: a Get over an entry page that fails its checksum fails instead of reading the key as missing (#1342)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Get_EntryPageRotten_ShouldFailWithStorageCorruption(int wrapper)
    {
        // Arrange: the entry page, read once, then evicted so the next read goes to the data file.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("bravo"), Bytes("two"), cancellationToken: TestTimeout.Token());
        var entryPage = database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).ShouldHaveSingleItem();
        Evict(database, entryPage);

        // Act: the page decays on the device.
        var faults = strategy.Faults(DatabaseName);
        faults.RottenPage = entryPage;
        if (wrapper != 0)
        {
            faults.WrapCorruption = corruption =>
            {
                var coded = new DatabaseException("Storage read failed.", [new Diagnostic
                { Code = "COHDBK002", Message = "Storage read failed.", Severity = DiagnosticSeverity.Error }], corruption);
                return wrapper == 1 ? coded : new AggregateException(new InvalidOperationException("Cleanup failed."), coded);
            };
        }
        var failure = await Should.ThrowAsync<DatabaseCorruptionException>(() => database.GetAsync(session, Bytes("bravo"), TestTimeout.Token()).AsTask());
        long rottenReads = faults.RottenPageReads;
        faults.RottenPage = null;
        var healed = await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token());

        // Assert
        failure.PageId.ShouldBe(entryPage);
        rottenReads.ShouldBeGreaterThan(0);
        Text(healed.ShouldNotBeNull().Value).ShouldBe("two");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: a Put over an entry page that fails its checksum fails without writing a version (#1342)")]
    public async Task Put_EntryPageRotten_ShouldFailWithoutWritingAVersion()
    {
        // Arrange: the key's record sits on the first entry page; later records filled it, so
        // the next version would land on another page and the Put reads the first one only to
        // resolve the key's current version.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        string padding = new('x', 3_000);
        var original = await database.PutAsync(session, Bytes("alpha"), Bytes("one" + padding), cancellationToken: TestTimeout.Token());
        for (int filler = 0; filler < 3; filler++)
        {
            await database.PutAsync(session, Bytes($"filler-{filler}"), Bytes(padding), cancellationToken: TestTimeout.Token());
        }

        var entryPage = EntryLocation(database, "alpha").PageId;
        database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).Count.ShouldBeGreaterThan(1);
        database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId)[^1].ShouldNotBe(entryPage);
        Evict(database, entryPage);

        // Act
        var faults = strategy.Faults(DatabaseName);
        faults.RottenPage = entryPage;
        await Should.ThrowAsync<DatabaseCorruptionException>(() => database.PutAsync(session, Bytes("alpha"), Bytes("replaced"), cancellationToken: TestTimeout.Token()).AsTask());
        faults.RottenPage = null;
        var entry = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());

        // Assert: the key kept its one version.
        Text(entry.ShouldNotBeNull().Value).ShouldBe("one" + padding);
        entry.Value.ETag.ShouldBe(original.ETag!.Value);
        EntryCount(database).ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: an entry whose record was reclaimed beneath it reads as absent (#1342)")]
    public async Task Get_EntryRecordReclaimedBeneathIndex_ShouldReadAsAbsent()
    {
        // Arrange: two keys on one page; one key's record is deleted beneath its index entry, the
        // state a purge or an undo leaves under an entry a reader already holds.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("bravo"), Bytes("two"), cancellationToken: TestTimeout.Token());
        var reclaimed = EntryLocation(database, "alpha");
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.DeleteEntry(bracket, reclaimed.PageId, reclaimed.SlotIndex);
            bracket.Commit();
        }

        // Act
        var alpha = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());
        var bravo = await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token());

        // Assert
        alpha.ShouldBeNull();
        Text(bravo.ShouldNotBeNull().Value).ShouldBe("two");
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeTrue();
    }

    /// <summary>
    /// The seek reads a record only from a data page of the key space, so a record that passes the
    /// page checksum and does not decode is damaged, not reclaimed. Before the fix it read as an
    /// absent key, the way a failed checksum used to.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: a Get over an entry record that does not decode fails instead of reading the key as missing (#1342)")]
    public async Task Get_EntryRecordDoesNotDecode_ShouldFailWithStorageCorruption()
    {
        // Arrange: two keys on one page; the first byte of one record's payload, the key's type
        // tag, overwritten in the buffer pool after the page passed its checksum.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("bravo"), Bytes("two"), cancellationToken: TestTimeout.Token());
        var damaged = EntryLocation(database, "alpha");
        using (var handle = database.DataStorage.PageManager.GetPage(damaged.PageId))
        {
            var page = handle.Page.AsSpan();
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(Page.Size - ((damaged.SlotIndex + 1) * 4), 2));
            page[offset + KeyValueRecordCodec.StampHeaderSize] = 0xFF;
        }

        // Act
        var failure = await Should.ThrowAsync<DatabaseCorruptionException>(() => database.GetAsync(session, Bytes("alpha"), TestTimeout.Token()).AsTask());
        var bravo = await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token());

        // Assert
        failure.PageId.ShouldBe(damaged.PageId);
        Text(bravo.ShouldNotBeNull().Value).ShouldBe("two");
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire: storage failures abort transactions and preserve the session (#1364)")]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    [InlineData(4, false, false)]
    [InlineData(4, true, false)]
    [InlineData(4, true, true)]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
    [InlineData(5, true, true)]
    [InlineData(6, false, false)]
    [InlineData(6, true, false)]
    [InlineData(6, true, true)]
    [InlineData(7, false, false)]
    [InlineData(7, true, false)]
    [InlineData(7, true, true)]
    public async Task Execute_ReadFailure_ShouldCodeAbortAndKeepWireSession(int failureKind, bool explicitTransaction, bool readCommitted)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var harness = await KeyValueServerHarness.StartAsync(configureEngine: options => options.StorageStrategy = strategy);
        harness.Engine.TryGetDatabase(KeyValueServerHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var embedded = await database!.CreateSessionAsync(CancellationToken.None);
        await database.PutAsync(embedded, Bytes("alpha"), Bytes("one"), cancellationToken: CancellationToken.None);
        var pageId = database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).ShouldHaveSingleItem();
        Evict(database, pageId);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var engineSession = serverSession.DatabaseSession.ShouldBeOfType<KeyValueDatabaseSession>();
        var transaction = explicitTransaction ? await engineSession.BeginTransactionAsync(readCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, CancellationToken.None) : null;
        var faults = strategy.Faults(KeyValueServerHarness.DatabaseName);
        faults.RottenPage = pageId;
        if (failureKind != 0)
        {
            faults.WrapCorruption = _ => failureKind switch
            {
                1 => new StorageIOException("Injected storage read failure."),
                2 => new IOException("Injected device read failure."),
                3 => new SlottedPageException("Injected slot failure."),
                4 => new JournalException("Injected journal failure."),
                5 => new StorageTransactionException("Injected storage transaction failure."),
                6 => new InvalidOperationException("Wrapped read.", new StorageTransactionException("Injected storage transaction failure.")),
                _ => new AggregateException(new InvalidOperationException("Other branch."), new IOException("Injected device read failure.")),
            };
        }
        try
        {
            // Act
            await client.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage("GET @key", new Dictionary<string, byte[]> { ["key"] = DatabaseValueCodec.EncodeComponent(Bytes("alpha")) }).Encode());
            var failure = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

            // Assert
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            failure.Message.ShouldStartWith(failureKind == 0 ? "COHDBS003: " : "COHDBK005: ", Case.Sensitive);
            faults.RottenPageReads.ShouldBeGreaterThan(0);
            if (transaction is not null)
            {
                transaction.State.ShouldBe(TransactionState.Faulted);
                var refusal = await Should.ThrowAsync<DatabaseException>(() => database.GetAsync(engineSession, Bytes("alpha"), CancellationToken.None).AsTask());
                refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBK001");
                refusal.InnerException.ShouldNotBeNull().ShouldBeAssignableTo<DatabaseException>().Diagnostics.ShouldHaveSingleItem().Code
                    .ShouldBe(failureKind == 0 ? "COHDBS003" : "COHDBK005");
            }
        }
        finally { faults.Clear(); }
        if (transaction is not null) { await transaction.RollbackAsync(CancellationToken.None); }
        await client.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage("GET @key", new Dictionary<string, byte[]> { ["key"] = DatabaseValueCodec.EncodeComponent(Bytes("alpha")) }).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(serverSession.Id);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Abort: rollback failure preserves the exact coded read cause and emits cleanup evidence")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_AbortNotificationFails_ShouldRetainCodedCause(bool corruptRead, bool corruptCleanup)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync("abort-failure", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await database.PutAsync(session, Bytes("pending"), Bytes("two"), cancellationToken: CancellationToken.None);
        var pageId = database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).ShouldHaveSingleItem();
        Evict(database, pageId);
        var faults = strategy.Faults("abort-failure");
        faults.RottenPage = pageId;
        if (!corruptRead) { faults.WrapCorruption = _ => new StorageIOException("Injected read failure."); }
        int aborts = 0;
        var original = database.Coordinator.OnUndoDeferred;
        Exception abortFailure = corruptCleanup ? new StorageCorruptionException(pageId, "Injected cleanup corruption.")
            : new InvalidOperationException("Injected abort notification failure.");
        database.Coordinator.OnUndoDeferred = () => { Interlocked.Increment(ref aborts); throw abortFailure; };
        using var recorder = new EventRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Warning);
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => database.GetAsync(session, Bytes("alpha"), CancellationToken.None).AsTask());

            // Assert
            aborts.ShouldBe(1);
            faults.RottenPageReads.ShouldBeGreaterThan(0);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(corruptRead ? "COHDBS003" : "COHDBK005");
            if (corruptRead) { failure.ShouldBeOfType<DatabaseCorruptionException>(); }
            transaction.CreateCommandRefusal().InnerException.ShouldBeSameAs(failure);
            var cleanup = recorder.Events.Where(item => item.EventId == 11).ShouldHaveSingleItem();
            cleanup.EventName.ShouldBe("TransactionCleanupFailed");
            cleanup.Level.ShouldBe(EventLevel.Warning);
            cleanup.PayloadNames.ShouldNotBeNull().ShouldBe(["transactionId", "exceptionType", "exceptionMessage"]);
            cleanup.Payload.ShouldNotBeNull()[0].ShouldBe(transaction.Id.ToString());
            cleanup.Payload[1].ShouldBe(corruptCleanup ? typeof(DatabaseCorruptionException).FullName : typeof(InvalidOperationException).FullName);
            cleanup.Payload[2].ShouldBeOfType<string>().ShouldContain(abortFailure.Message, Case.Sensitive);
            recorder.Events.ShouldNotContain(item => item.EventId == 0);
            transaction.State.ShouldBe(TransactionState.Faulted);
        }
        finally
        {
            faults.Clear();
            database.Coordinator.OnUndoDeferred = original;
        }
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static KeyValueDatabaseEngine CreateEngine(FaultInjectingJournalStorageStrategy? strategy)
        => KeyValueDatabaseEngine.Create("kv-index-reads", new KeyValueDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static (PageId PageId, int SlotIndex) EntryLocation(KeyValueDatabase database, string key)
    {
        using var iterator = database.DataStorage.GetUnitIterator(KeyValueOperationExecutor.KeySpaceObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            KeyValueRecordCodec.Decode(unit.Data.Span, out byte[] stored, out _, out _, out _);
            if (Text(stored) == key)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"The record of key '{key}' was not found.");
    }

    private static int EntryCount(KeyValueDatabase database)
    {
        using var iterator = database.DataStorage.GetUnitIterator(KeyValueOperationExecutor.KeySpaceObjectId);
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back.
    /// </summary>
    private static void Evict(KeyValueDatabase database, PageId pageId)
    {
        var storage = database.DataStorage;
        var other = Enumerable.Range(1, (int)storage.PageManager.PageCount - 1)
            .Select(page => (PageId)(long)page)
            .First(page => page != pageId && storage.FreeSpaceMap.IsAllocated(page));
        int capacity = storage.BufferPoolCapacity;
        storage.BufferPoolCapacity = 1;
        using (storage.PageManager.GetPage(other))
        {
        }

        storage.BufferPoolCapacity = capacity;
    }
}
