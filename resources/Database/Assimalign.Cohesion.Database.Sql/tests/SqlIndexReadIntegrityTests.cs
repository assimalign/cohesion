using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using EventRecorder = Assimalign.Cohesion.Testing.EventSourceRecorder;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The rows an index seek fetches (#1342). An entry whose row was reclaimed beneath it (its slot
/// deleted, its page freed, the page reallocated as an index node) is stale and skipped. A row page
/// that cannot be read is not reclaimed: before the fix the executor read every storage error as
/// reclamation, so a page that failed its checksum dropped its rows from the result instead of
/// failing the statement. PostgreSQL's heap fetch makes the same split: a line pointer past the
/// page or no longer in use is "not found", and an invalid page is <c>ERRCODE_DATA_CORRUPTED</c>.
/// </summary>
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlIndexReadIntegrityTests
{
    private const string Seek = "SELECT id FROM t WHERE val = 20";

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: storage read failures abort transactions and keep the wire session (#1364)")]
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
    public async Task Execute_CorruptScanOverWire_ShouldReportExecutionFailureAndKeepSessionUsable(int failureKind, bool explicitTransaction, bool readCommitted)
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options => options.StorageStrategy = strategy);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        var pageId = database!.DataStorage.GetOwnerPages(Table(database, "users").ObjectId).ShouldHaveSingleItem();
        Evict(database, pageId);
        var faults = strategy.Faults(ServerTestHarness.DatabaseName);
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
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var engineSession = serverSession.DatabaseSession.ShouldBeOfType<SqlDatabaseSession>();
        var transaction = explicitTransaction ? await engineSession.BeginTransactionAsync(readCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, CancellationToken.None) : null;

        try
        {
            // Act
            await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users").Encode());
            var frame = await client.ExpectAsync(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);

            // Assert
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldStartWith(failureKind == 0 ? "CK06003: " : "COHSQLE041: ", Case.Sensitive);
            error.Message.ShouldNotContain("CK06003: CK06003:", Case.Sensitive);
            if (failureKind == 0)
            {
                error.Message.ShouldContain("page " + (long)pageId, Case.Sensitive);
                error.Message.ShouldContain(ServerTestHarness.DatabaseName, Case.Sensitive);
            }
            if (transaction is not null)
            {
                transaction.State.ShouldBe(TransactionState.Faulted);
                var refusal = await Should.ThrowAsync<DatabaseException>(async () =>
                    await engineSession.ExecuteAsync("SELECT 1", cancellationToken: CancellationToken.None));
                refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLT005");
                refusal.InnerException.ShouldNotBeNull().ShouldBeAssignableTo<DatabaseException>().Diagnostics.ShouldHaveSingleItem().Code
                    .ShouldBe(failureKind == 0 ? "CK06003" : "COHSQLE041");
            }
        }
        finally { faults.Clear(); }
        if (transaction is not null)
        {
            if (readCommitted)
            {
                await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("COMMIT").Encode());
                var commit = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
                commit.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
                commit.Message.ShouldStartWith("COHSQLT005: ", Case.Sensitive);
                engineSession.CurrentTransaction.ShouldBeNull();
            }
            else { await transaction.RollbackAsync(CancellationToken.None); }
        }
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(serverSession.Id);

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: corrupt pages fail scans and seeks with the database and page (#1160)")]
    [InlineData(Seek, 0)]
    [InlineData("SELECT id FROM t", 0)]
    [InlineData(Seek, 1)]
    [InlineData(Seek, 2)]
    public async Task Select_ReadsRottenRowPage_ShouldFailWithCodedDatabaseCorruption(string statement, int wrapper)
    {
        // Arrange: three rows on one page behind an index, read once through the seek, then the
        // page evicted so the next seek reads it from the data file.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync("rot-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        (await Ids(session, Seek)).ShouldBe([2]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        var rowPage = database.DataStorage.GetOwnerPages(Table(database, "t").ObjectId).ShouldHaveSingleItem();
        Evict(database, rowPage);

        // Act: the page decays on the device.
        var faults = strategy.Faults("rot-db");
        faults.RottenPage = rowPage;
        if (wrapper != 0)
        {
            faults.WrapCorruption = corruption =>
            {
                var coded = new DatabaseException("Storage read failed.", [new Diagnostic
                { Code = "COHSQLT004", Message = "Storage read failed.", Severity = DiagnosticSeverity.Error }], corruption);
                return wrapper == 1 ? coded : new AggregateException(new InvalidOperationException("Cleanup failed."), coded);
            };
        }
        var failure = await Should.ThrowAsync<DatabaseCorruptionException>(() => Ids(session, statement));
        long rottenReads = faults.RottenPageReads;
        faults.RottenPage = null;
        await using var next = await database.CreateSessionAsync();
        var healed = await Ids(next, Seek);

        // Assert: the statement failed with the page's checksum error. The failed read was not
        // cached, so once the device returns the page whole the same seek finds its row.
        failure.PageId.ShouldBe(rowPage);
        failure.Database.ShouldBe((DatabaseName)"rot-db");
        failure.Code.ShouldBe("CK06003");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failure.Code);
        failure.InnerException.ShouldBeOfType<StorageCorruptionException>().PageId.ShouldBe(rowPage);
        failure.Message.ShouldContain("rot-db", Case.Sensitive);
        failure.Message.ShouldContain("page " + (long)rowPage, Case.Sensitive);
        failure.Message.ShouldContain("verified backup", Case.Sensitive);
        failure.Message.ShouldContain("failed checksum verification", Case.Sensitive);
        rottenReads.ShouldBeGreaterThan(0);
        healed.ShouldBe([2]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row slot was reclaimed beneath it is skipped (#1342)")]
    public async Task Select_IndexEntryOverDeletedSlot_ShouldSkipTheEntry()
    {
        // Arrange: three rows share a key on one page. The middle row's slot is deleted beneath its
        // entry: the state a purge or an undo leaves under an entry a reader already holds.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("slot-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 20), (2, 20), (3, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);

        // Act
        var ids = await Ids(session, Seek);

        // Assert: the stale entry was examined and skipped; its page still holds the other rows.
        ids.Order().ShouldBe([1, 3]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row page was freed beneath it is skipped (#1342)")]
    public async Task Select_IndexEntryOverFreedPage_ShouldSkipTheEntry()
    {
        // Arrange: the table's only row is deleted beneath its entry, which frees its page.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("page-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeFalse();

        // Act
        var ids = await Ids(session, Seek);

        // Assert
        ids.ShouldBeEmpty();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row page now holds an index node is skipped, not reported as corruption (#1342)")]
    public async Task Select_IndexEntryOverPageReusedAsIndexNode_ShouldSkipTheEntry()
    {
        // Arrange: the freed row page is the next page the allocator hands out, and the next
        // CREATE INDEX takes it for its root.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("reuse-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);
        await session.ExecuteAsync("CREATE TABLE u (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_u ON u (id)");
        using (var reused = database.DataStorage.PageManager.GetPage(reclaimed.PageId))
        {
            reused.Page.Type.ShouldBe(PageType.Index);
        }

        // Act
        var ids = await Ids(session, Seek);

        // Assert
        ids.ShouldBeEmpty();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Abort: rollback failure preserves the exact coded read cause and emits cleanup evidence")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_AbortNotificationFails_ShouldRetainCodedCause(bool corruptRead, bool corruptCleanup)
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync("abort-failure", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)", cancellationToken: CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)", cancellationToken: CancellationToken.None);
        var pageId = database.DataStorage.GetOwnerPages(Table(database, "t").ObjectId).ShouldHaveSingleItem();
        Evict(database, pageId);
        var faults = strategy.Faults("abort-failure");
        faults.RottenPage = pageId;
        if (!corruptRead) { faults.WrapCorruption = _ => new StorageIOException("Injected read failure."); }
        int aborts = 0;
        var original = database.Coordinator.OnUndoDeferred;
        Exception abortFailure = corruptCleanup ? new StorageCorruptionException(pageId, "Injected cleanup corruption.")
            : new InvalidOperationException("Injected abort notification failure.");
        database.Coordinator.OnUndoDeferred = () => { Interlocked.Increment(ref aborts); throw abortFailure; };
        using var recorder = new EventRecorder(SqlDatabaseEventSource.Log, EventLevel.Warning);
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("SELECT id FROM t", cancellationToken: CancellationToken.None).AsTask());

            // Assert
            aborts.ShouldBe(1);
            faults.RottenPageReads.ShouldBeGreaterThan(0);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(corruptRead ? "CK06003" : "COHSQLE041");
            if (corruptRead) { failure.ShouldBeOfType<DatabaseCorruptionException>(); }
            transaction.CreateStatementRefusal().InnerException.ShouldBeSameAs(failure);
            var cleanup = recorder.Events.Where(item => item.EventId == 25).ShouldHaveSingleItem();
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

    private static SqlDatabaseEngine CreateEngine(FaultInjectingJournalSqlStorageStrategy? strategy)
        => SqlDatabaseEngine.Create("sql-engine", new SqlDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static async Task<int[]> Ids(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql);
        var resultSet = result.ShouldBeAssignableTo<DatabaseResultSet>();
        var ids = new List<int>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            ids.Add(Convert.ToInt32(row.GetValue(0)));
        }

        return ids.ToArray();
    }

    private static SqlCatalogTable Table(SqlDatabase database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table;
    }

    private static (PageId PageId, int SlotIndex) RowLocation(SqlDatabase database, string tableName, int id)
    {
        var table = Table(database, tableName);
        using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            if (Convert.ToInt32(SqlRowCodec.Decode(unit.Data.Span, table, out _, out _, out _)[0]) == id)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"Row {id} of '{tableName}' was not found.");
    }

    /// <summary>
    /// Deletes a row version's slot in its own storage bracket, leaving its index entry in place.
    /// </summary>
    private static void DeleteBeneathIndex(SqlDatabase database, (PageId PageId, int SlotIndex) location)
    {
        using var bracket = database.DataStorage.BeginTransaction();
        database.DataStorage.DeleteRow(bracket, location.PageId, location.SlotIndex);
        bracket.Commit();
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back.
    /// </summary>
    private static void Evict(SqlDatabase database, PageId pageId)
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
