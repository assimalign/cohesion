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
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Storage operations of the graph engine: a failed journal fsync takes the database offline
/// until it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254),
/// and a deferred undo is retried on its own backoff (#1226).
/// </summary>
[Collection(nameof(GraphWorkerResilienceCollection))]
public sealed class GraphStorageOperationsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The commit's journal fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, a statement, BEGIN, COMMIT and ROLLBACK of an open
    /// transaction, over the wire too — is refused with CG06012, and nothing reaches the file
    /// set, through the workers' passes and the sessions' close included. The root bases' checks
    /// come first (concrete-types plan §6.4): BEGIN on the session holding the open transaction is
    /// refused as already active, a canceled token is refused before the offline refusal of a new
    /// session, both execute seams and BEGIN, and the transaction whose session closed reports
    /// Faulted. Reopening the database runs recovery, which keeps the commit when its record's
    /// bytes survived and drops it when they were lost with the failed fsync.
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
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new()
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("graph", token);
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

        // The root bases' order (concrete-types plan §6.4): BEGIN on the session that holds the
        // open transaction is refused as already active before the offline refusal, and a canceled
        // token is refused before the offline refusal of a new session, both execute seams and
        // BEGIN. Before the bases, each of these was refused with CG06012. The typed node and
        // relationship operations do not pass the session's execute seams, so their order holds.
        var activeBegin = await Should.ThrowAsync<DatabaseException>(async () => await other.BeginTransactionAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledCalls = new List<OperationCanceledException>
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await database.CreateSessionAsync(canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name"), canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name", null, canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.BeginTransactionAsync(canceled.Token)),
        };
        var typedRefusal = await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateNodeAsync(session, ["Item"], cancellationToken: canceled.Token));

        // The execute seams' argument checks are the base's too, ahead of the offline refusal the
        // model made first.
        var nullRequest = await Should.ThrowAsync<ArgumentNullException>(async () => await session.ExecuteAsync((GraphRequest)null!, token));
        var blankStatement = await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" ", null, token));

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

        // The open transaction's teardown rolls nothing back on the offline database, and the
        // transaction reports Faulted (it reported Active before the bases).
        await other.DisposeAsync();
        var closedState = open.State;
        await session.DisposeAsync();
        var beforeTheReopen = strategy.Capture("graph");

        var reopened = await engine.OpenDatabaseAsync("graph", token);
        await using var observer = await reopened.CreateSessionAsync(token);
        var names = await Names(observer);

        // Assert
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        unconfirmed.Diagnostics.ShouldContain(d => d.Code == "CG06012");
        unconfirmed.Message.ShouldStartWith("Database 'graph' went offline while a transaction was committing", Case.Sensitive);
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "CG06012" && refusal.Diagnostics.Count == 1 && refusal.Diagnostics[0].Code == "CG06012" && refusal.Message.StartsWith("Database", StringComparison.Ordinal));
        activeBegin.Message.ShouldBe("A transaction or operation is already active on this session.");
        canceledCalls.ShouldAllBe(refusal => refusal.CancellationToken == canceled.Token);
        typedRefusal.Code.ShouldBe("CG06012");
        nullRequest.ParamName.ShouldBe("request");
        blankStatement.ParamName.ShouldBe("statement");
        closedState.ShouldBe(TransactionState.Faulted);
        statementError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        statementError.Message.ShouldStartWith("CG06012", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("CG06012", Case.Sensitive);
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
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("pool");

        // Act & Assert
        database.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        database.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new GraphDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { BufferPoolCapacity = 512 * 1024 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { BufferPoolCapacity = 1024 * 1024 + 1 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => GraphDatabaseEngine.Create("graph-engine", new() { CheckpointJournalSize = -1 }))
            .ParamName.ShouldBe(nameof(GraphDatabaseEngineOptions.CheckpointJournalSize));
        await using var sized = GraphDatabaseEngine.Create("graph-engine", new() { BufferPoolCapacity = 2 * 1024 * 1024 });
        (await sized.CreateDatabaseAsync("sized")).DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("graph-engine");
        long defaultPool = builder.Options.BufferPoolCapacity;
        long defaultSize = builder.Options.CheckpointJournalSize;
        builder.Options.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.Options.CheckpointJournalSize = 8 * 1024 * 1024;

        // Act
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    /// <summary>
    /// Under a sustained write load the journal-size trigger keeps the journal near its
    /// configured size (#1254). The bounds are ratios to the configured size and to what was
    /// written, never an absolute time: the test runs until forty sizes of journal were written,
    /// under a hang guard of minutes. The nodes carry a 3,000-character payload: since storage
    /// format 3 (#1253) a write journals the bytes it changed rather than two 8 KiB images of each
    /// page it touched, so with small nodes forty sizes of journal took several times as many
    /// writes as before.
    /// </summary>
    /// <remarks>
    /// The test counts the checkpoints that truncated the journal and bounds the journal written
    /// per truncation: on average a checkpoint must truncate it before it holds four sizes. Without
    /// the trigger nothing truncates it, and the one length holds all forty. The largest length
    /// alone measured the scheduler as much as the trigger: on a loaded three-core machine one
    /// checkpoint could wait long enough for the four writers to append several sizes, and that
    /// one cycle took the peak past four sizes while the journal written per truncation stayed
    /// under two. The peak is reported, not bounded.
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new()
        {
            StorageStrategy = strategy,
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("bounded");
        using var stop = new CancellationTokenSource();
        string payload = new('x', 3000);
        await using (var setup = await database.CreateSessionAsync())
        {
            // The label and property key exist before the writers race to use them.
            await database.CreateNodeAsync(setup, ["Item"], new Dictionary<string, object?> { ["payload"] = payload });
        }

        var writers = Enumerable.Range(0, 4).Select(writer => DedicatedTestTask.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            while (!stop.IsCancellationRequested && strategy.JournalCounters(database.Name.ToString()).Written < 40 * size)
            {
                await database.CreateNodeAsync(session, ["Item"], new Dictionary<string, object?> { ["payload"] = payload });
            }
        })).ToArray();

        // Act: read exact stream counters while writers stop at forty journal sizes,
        // counting the checkpoints that truncated it.
        var journal = await JournalSamples.CollectAsync(() => strategy.JournalCounters(database.Name.ToString()), 40 * size, Task.WhenAll(writers));
        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(_timeout);

        // Assert: tens of journal sizes were written, and checkpoints truncated the journal before
        // it held four sizes on average.
        string measured = journal.Describe(size);
        journal.Written.ShouldBeGreaterThanOrEqualTo(40 * size, measured);
        journal.WrittenPerTruncation(size).ShouldBeLessThan(4.0, measured);
        engine.State.ShouldBe(EngineState.Running, measured);
    }

    /// <summary>
    /// A refused undo retries on the coordinator's own schedule, independently of full maintenance.
    /// A recorded undo-only worker completion and the next writer prove release; elapsed time is diagnostic.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Deferred undo: a transient undo failure releases the writer through an undo-only worker pass")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterAfterRecordedUndoRetry()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        var options = new GraphDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            MaintenanceInterval = maintenance,
        };
        await using var engine = GraphDatabaseEngine.Create("graph-engine", options);
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Item {name: 'rolled'})");

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred with
        // the database writer lock held; the pages are released at once. (Until #1252 a failed journal
        // write was the transient fault; a journal write failure now takes the database offline.)
        using var undo = CheckpointPace.ObserveDeferredUndo(
            engine.Workers.OfType<DatabaseVersionPurgeWorker<GraphDatabase>>().Single(), database.Name, database.Coordinator.DeferredUndoRetryDelay);
        var watch = Stopwatch.StartNew();
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            database.Coordinator.VersionStore.PendingAbortedPurges.ShouldNotBeEmpty();
            database.Coordinator.NextDeferredUndoRetry.ShouldNotBeNull("the refused undo must be scheduled while its pages stay held");
        }

        await other.ExecuteAsync("INSERT (:Item {name: 'other'})").AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        watch.Stop();
        await undo.VerifyAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await Names(other)).ShouldBe(["other"]);
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        database.Coordinator.DeferredUndoRetryDelay.ShouldBe(options.DeferredUndoRetryDelay);
        engine.State.ShouldBe(EngineState.Running, $"writer released after {watch.Elapsed}");
    }

    private static async Task<List<string>> Names(GraphDatabaseSession session)
    {
        var names = new List<string>();
        var result = await session.ExecuteAsync("MATCH (n:Item) RETURN n.name");
        if (result is DatabaseResultSet set)
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
