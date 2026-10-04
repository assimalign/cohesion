using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Storage operations of the graph engine: a failed journal fsync takes the database offline
/// until it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254),
/// and a deferred undo is retried on its own backoff (#1226).
/// </summary>
public sealed class GraphStorageOperationsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The commit's journal fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, a statement, BEGIN, COMMIT and ROLLBACK of an open
    /// transaction, over the wire too — is refused with COHDBG012, and nothing reaches the file
    /// set, through the workers' passes and the sessions' close included. Reopening the database
    /// runs recovery, which keeps the commit when its record's bytes survived and drops it when
    /// they were lost with the failed fsync.
    /// </summary>
    /// <param name="recordSurvives">False to reopen with only the journal bytes a durable flush confirmed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Offline: a failed journal fsync refuses every operation until the reopen, whose recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_JournalFsyncFails_ShouldRefuseEveryOperationUntilReopened(bool recordSurvives)
    {
        // Arrange: quiet workers, so none of them makes the commit record durable before the
        // committer's own fsync; the test runs their passes itself after the failure.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = !recordSurvives };
        await using var engine = GraphDatabaseEngine.Create(new()
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("graph", token);
        var listener = new InMemoryConnectionListener();
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var wire = await ConnectAsync(listener, token);
        await HandshakeAsync(wire, token);
        var session = await database.CreateSessionAsync(token);
        var other = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("INSERT (:Item {name: 'kept'})", cancellationToken: token);

        // A reader: the database has one writer at a time, so an open writer would block the
        // commit below.
        var open = await other.BeginTransactionAsync(token);
        await Names(other);

        // Act: the commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await session.ExecuteAsync("INSERT (:Item {name: 'unconfirmed'})", cancellationToken: token));
            failures.Remaining.ShouldBe(0);
        }

        var atTheFailure = strategy.Capture("graph");
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name", cancellationToken: token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("INSERT (:Item {name: 'late'})", cancellationToken: token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateNodeAsync(other, ["Item"], cancellationToken: token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync(token)),
        };

        // Over the wire: a statement on a session opened before the failure, and a new session.
        await WriteAsync(wire, (ProtocolMessageType)GraphProtocolMessageType.Execute,
            GraphProtocolExecuteMessage.Create("MATCH (n) RETURN n.name").Encode(), token);
        var statementError = await ReadErrorAsync(wire, token);
        await using var late = await ConnectAsync(listener, token);
        await WriteAsync(late, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "late").Encode(), token);
        (await ReadAsync(late, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(late, ProtocolMessageType.AuthenticateResponse, [], token);
        var handshakeError = await ReadErrorAsync(late, token);

        // Every worker runs a pass, with a checkpoint due by size; then the sessions close.
        database.DataStorage.CheckpointJournalSize = 1;
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        await other.DisposeAsync();
        await session.DisposeAsync();
        var beforeTheReopen = strategy.Capture("graph");

        var reopened = (GraphDatabaseInstance)await engine.OpenDatabaseAsync("graph", token);
        await using var observer = await reopened.CreateSessionAsync(token);
        var names = await Names(observer);

        // Assert
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBG012" && refusal.Message.StartsWith("COHDBG012", StringComparison.Ordinal));
        statementError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        statementError.Message.ShouldStartWith("COHDBG012", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("COHDBG012", Case.Sensitive);
        engine.State.ShouldBe(EngineState.Running);
        beforeTheReopen.Data.ShouldBe(atTheFailure.Data);
        beforeTheReopen.Journal.ShouldBe(atTheFailure.Journal);
        reopened.ShouldNotBeSameAs(database);
        reopened.IsOffline.ShouldBeFalse();
        names.ShouldBe(recordSurvives ? ["kept", "unconfirmed"] : ["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Buffer pool: a database gets the 32 MiB default, and the options are validated")]
    public async Task BufferPoolCapacity_DefaultAndInvalidOptions_ShouldSizeThePoolAndRefuseBadValues()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("pool");

        // Act & Assert
        database.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        database.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new GraphDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create(new() { BufferPoolCapacity = 512 * 1024 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create(new() { BufferPoolCapacity = 1024 * 1024 + 1 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create(new() { CheckpointJournalSize = -1 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.CheckpointJournalSize));
        await using var sized = GraphDatabaseEngine.Create(new() { BufferPoolCapacity = 2 * 1024 * 1024 });
        ((GraphDatabaseInstance)await sized.CreateDatabaseAsync("sized")).DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder();
        long defaultPool = builder.BufferPoolCapacity;
        long defaultSize = builder.CheckpointJournalSize;
        builder.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.CheckpointJournalSize = 8 * 1024 * 1024;

        // Act
        await using var engine = (GraphDatabaseEngine)builder.Build();
        var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    /// <summary>
    /// Under a sustained write load the journal-size trigger keeps the journal near its
    /// configured size (#1254). The bound is a ratio to the configured size.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        await using var engine = GraphDatabaseEngine.Create(new()
        {
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("bounded");
        using var stop = new CancellationTokenSource();
        string payload = new('x', 150);
        await using (var setup = await database.CreateSessionAsync())
        {
            // The label and property key exist before the writers race to use them.
            await database.CreateNodeAsync(setup, ["Item"], new Dictionary<string, object?> { ["payload"] = payload });
        }

        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            while (!stop.IsCancellationRequested)
            {
                await database.CreateNodeAsync(session, ["Item"], new Dictionary<string, object?> { ["payload"] = payload });
            }
        })).ToArray();

        // Act: sample the journal while the writers push well past the size many times over.
        long largest = 0;
        long written = 0;
        long previous = 0;
        var watch = Stopwatch.StartNew();
        while (written < 40 * size && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            long length = database.DataStorage.JournalLength;
            largest = Math.Max(largest, length);
            written += length >= previous ? length - previous : length;
            previous = length;
            await Task.Delay(1);
        }

        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);

        // Assert: tens of journal sizes were written, and the journal never held more than a few.
        written.ShouldBeGreaterThanOrEqualTo(40 * size);
        ((double)largest / size).ShouldBeLessThan(4.0);
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// A rollback's undo fails once. The undo is retried on its own backoff, about 100 ms later,
    /// so the writer waiting for the rolled-back transaction's writer lock proceeds within about a
    /// second although the maintenance interval is an hour (#1226 owner decision of 2026-10-04).
    /// The engine's wiring is checked exactly (the first retry is due within 100 ms of the
    /// deferral), and the release end to end as a ratio to that first delay.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        var options = new GraphDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            MaintenanceInterval = maintenance,
        };
        await using var engine = GraphDatabaseEngine.Create(options);
        var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Item {name: 'rolled'})");

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred with
        // the database writer lock held; the pages are released at once. (Until #1252 a failed journal
        // write was the transient fault; a journal write failure now takes the database offline.)
        var watch = Stopwatch.StartNew();
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = database.Coordinator.NextDeferredUndoRetry;

        await other.ExecuteAsync("INSERT (:Item {name: 'other'})").AsTask().WaitAsync(Timeout);
        watch.Stop();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await Names(other)).ShouldBe(["other"]);
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(options.DeferredUndoRetryDelay);
        (watch.Elapsed / options.DeferredUndoRetryDelay).ShouldBeLessThan(20);
        engine.State.ShouldBe(EngineState.Running);
    }

    private static async Task<List<string>> Names(IDatabaseSession session)
    {
        var names = new List<string>();
        var result = await session.ExecuteAsync("MATCH (n:Item) RETURN n.name");
        if (result is QueryResultSet set)
        {
            await using (set)
            {
                await foreach (var row in set.GetRowsAsync())
                {
                    names.Add(row.GetString(0) ?? "<null>");
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<ProtocolChannel> ConnectAsync(InMemoryConnectionListener listener, CancellationToken token)
    {
        var connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        return new ProtocolChannel(connection.AsStream(), GraphProtocol.Family, leaveOpen: false);
    }

    private static async Task HandshakeAsync(ProtocolChannel channel, CancellationToken token)
    {
        await WriteAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "test").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);
    }

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of graph exchange.");

    private static async Task<ProtocolErrorMessage> ReadErrorAsync(ProtocolChannel channel, CancellationToken token)
    {
        var frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe(ProtocolMessageType.Error);
        return ProtocolErrorMessage.Decode(frame.Payload.Span);
    }
}
