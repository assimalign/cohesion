using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Storage operations of the blob engine: a failed journal fsync takes the database offline
/// until it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254),
/// and a deferred undo is retried on its own backoff (#1226).
/// </summary>
public sealed class BlobStorageOperationsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An upload's commit fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, an upload, a download, a read of a download stream opened
    /// before the failure, BEGIN, COMMIT and ROLLBACK of an open transaction, over the wire too —
    /// is refused with COHDBB002, and nothing reaches the file set, through the workers' passes
    /// and the sessions' close included. The root bases' checks come first (concrete-types plan
    /// §6.4): BEGIN on the session holding the open transaction is refused as already active, a
    /// canceled token is refused before the offline refusal of a new session, both execute seams
    /// and BEGIN, the execute seams' argument checks come before it too, and the transaction whose
    /// session closed reports Faulted. Reopening the database runs recovery, which keeps the
    /// upload when its commit record's bytes survived and drops it when they were lost with the
    /// failed fsync.
    /// </summary>
    /// <param name="recordSurvives">False to reopen with only the journal bytes a durable flush confirmed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Offline: a failed journal fsync refuses every operation until the reopen, whose recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_JournalFsyncFails_ShouldRefuseEveryOperationUntilReopened(bool recordSurvives)
    {
        // Arrange: quiet workers, so none of them makes the commit record durable before the
        // committer's own fsync; the test runs their passes itself after the failure.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = !recordSurvives };
        await using var engine = BlobDatabaseEngine.Create(new()
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("blobs", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var wire = await ConnectAsync(listener, token);
        await HandshakeAsync(wire, token);
        await database.CreateContainerAsync("files", token);
        var session = await database.CreateSessionAsync(token);
        var other = await database.CreateSessionAsync(token);
        var files = await session.GetContainerAsync("files", token);
        var otherFiles = await other.GetContainerAsync("files", token);
        await WriteAsync(files, "kept", "kept", token);

        // A reader, and a download stream: the database has one writer at a time, so an open
        // writer would block the commit below.
        var open = await other.BeginTransactionAsync(token);
        (await NamesAsync(otherFiles)).ShouldBe(["kept"]);
        await using var reader = await database.CreateSessionAsync(token);
        var download = await (await reader.GetContainerAsync("files", token)).OpenReadAsync("kept", token);

        // Act: the upload's commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await WriteAsync(files, "unconfirmed", "unconfirmed", token));
            failures.Remaining.ShouldBe(0);
        }

        var atTheFailure = strategy.Capture("blobs");
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateContainerAsync("late", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.CreateContainerAsync("late", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("LIST files", null, token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.OpenReadAsync("kept", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.OpenWriteAsync("late", cancellationToken: token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.GetPropertiesAsync("kept", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await download.ReadExactlyAsync(new byte[4], token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await download.DisposeAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync(token)),
        };

        // The root bases' order (concrete-types plan §6.4): BEGIN on the session that holds the
        // open transaction is refused as already active before the offline refusal, and a canceled
        // token is refused before the offline refusal of a new session, both execute seams and
        // BEGIN. Before the bases, each of these was refused with COHDBB002. The container and blob
        // operations do not pass the session's execute seams, so their order holds.
        var activeBegin = await Should.ThrowAsync<DatabaseException>(async () => await other.BeginTransactionAsync(token));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledCalls = new List<OperationCanceledException>
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await database.CreateSessionAsync(canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(new BlobRequest(), canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("LIST files", null, canceled.Token)),
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.BeginTransactionAsync(canceled.Token)),
        };
        var containerRefusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.GetPropertiesAsync("kept", canceled.Token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.GetContainerAsync("files", canceled.Token)),
        };

        // The execute seams' argument checks are the base's too, ahead of the offline refusal the
        // model made first.
        var nullRequest = await Should.ThrowAsync<ArgumentNullException>(async () => await session.ExecuteAsync((QueryRequest)null!));
        var blankStatement = await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" "));

        // Over the wire: an operation on a session opened before the failure, and a new session.
        await WriteAsync(wire, (ProtocolMessageType)BlobProtocolMessageType.Read, new BlobReadMessage("files", "kept").Encode(), token);
        var operationError = await ReadErrorAsync(wire, token);
        await using var late = await ConnectAsync(listener, token);
        await WriteAsync(late, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "blobs", "late").Encode(), token);
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
        // transaction reports Faulted, as the model's own teardown left it.
        await other.DisposeAsync();
        var closedState = open.State;
        await session.DisposeAsync();
        await reader.DisposeAsync();
        var beforeTheReopen = strategy.Capture("blobs");

        var reopened = await engine.OpenDatabaseAsync("blobs", token);
        var names = await NamesAsync(await reopened.GetContainerAsync("files", token));

        // Assert
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBB002" && refusal.Message.StartsWith("COHDBB002", StringComparison.Ordinal));
        activeBegin.Message.ShouldBe("A transaction or operation is already active on this session.");
        canceledCalls.ShouldAllBe(refusal => refusal.CancellationToken == canceled.Token);
        containerRefusals.ShouldAllBe(refusal => refusal.Code == "COHDBB002");
        nullRequest.ParamName.ShouldBe("request");
        blankStatement.ParamName.ShouldBe("statement");
        closedState.ShouldBe(TransactionState.Faulted);
        operationError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        operationError.Message.ShouldStartWith("COHDBB002", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("COHDBB002", Case.Sensitive);
        engine.State.ShouldBe(EngineState.Running);
        beforeTheReopen.Data.ShouldBe(atTheFailure.Data);
        beforeTheReopen.Journal.ShouldBe(atTheFailure.Journal);
        reopened.ShouldNotBeSameAs(database);
        reopened.IsOffline.ShouldBeFalse();
        names.ShouldBe(recordSurvives ? ["kept", "unconfirmed"] : ["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Buffer pool: a database gets the 32 MiB default, and the options are validated")]
    public async Task BufferPoolCapacity_DefaultAndInvalidOptions_ShouldSizeThePoolAndRefuseBadValues()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("pool");

        // Act & Assert
        database.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        database.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new BlobDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
        Should.Throw<ArgumentOutOfRangeException>(() => BlobDatabaseEngine.Create(new() { BufferPoolCapacity = 512 * 1024 }))
            .ParamName.ShouldBe(nameof(BlobDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => BlobDatabaseEngine.Create(new() { BufferPoolCapacity = 1024 * 1024 + 1 }))
            .ParamName.ShouldBe(nameof(BlobDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => BlobDatabaseEngine.Create(new() { CheckpointJournalSize = -1 }))
            .ParamName.ShouldBe(nameof(BlobDatabaseEngineOptions.CheckpointJournalSize));
        await using var sized = BlobDatabaseEngine.Create(new() { BufferPoolCapacity = 2 * 1024 * 1024 });
        (await sized.CreateDatabaseAsync("sized")).DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder();
        long defaultPool = builder.BufferPoolCapacity;
        long defaultSize = builder.CheckpointJournalSize;
        builder.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.CheckpointJournalSize = 8 * 1024 * 1024;

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
    /// Under a sustained upload load the journal-size trigger keeps the journal near its
    /// configured size (#1254). The bounds are ratios to the configured size and to what was
    /// written, never an absolute time: the test runs until forty sizes of journal were written,
    /// under a hang guard of minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each upload is 128 KiB. Since storage format 3 (#1253) the journal carries an upload's
    /// chunks about once, as page deltas, instead of as a before- and an after-image of every
    /// touched page, so a 16 KiB upload journals about 18 KiB where it journaled about 130 KiB.
    /// The larger upload keeps the number of uploads needed to write forty journal sizes near
    /// what it was: under this load every overwrite leaves a blob version that the catalog's
    /// lookup walks, so each upload costs more than the one before it.
    /// </para>
    /// <para>
    /// The test counts the checkpoints that truncated the journal and bounds the journal written
    /// per truncation: on average a checkpoint must truncate it before it holds four sizes. Without
    /// the trigger nothing truncates it, and the one length holds all forty. The largest length
    /// alone measured the scheduler as much as the trigger: on a loaded three-core machine one
    /// checkpoint could wait long enough for the four writers to append several sizes, and that
    /// one cycle took the peak past four sizes while the journal written per truncation stayed
    /// under two. The peak is reported, not bounded.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        await using var engine = BlobDatabaseEngine.Create(new()
        {
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("bounded");
        await database.CreateContainerAsync("files");
        using var stop = new CancellationTokenSource();
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        // Each writer overwrites its own blob, so the writers never conflict.
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            var files = await session.GetContainerAsync("files");
            while (!stop.IsCancellationRequested)
            {
                await using var stream = await files.OpenWriteAsync($"writer-{writer}");
                await stream.WriteAsync(payload);
            }
        })).ToArray();

        // Act: sample the journal while the writers push well past the size many times over,
        // counting the checkpoints that truncated it.
        var journal = await JournalSamples.CollectAsync(() => database.DataStorage.JournalLength, 40 * size, Task.WhenAll(writers));
        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);

        // Assert: tens of journal sizes were written, and checkpoints truncated the journal before
        // it held four sizes on average.
        string measured = journal.Describe(size);
        journal.Written.ShouldBeGreaterThanOrEqualTo(40 * size, measured);
        journal.WrittenPerTruncation(size).ShouldBeLessThan(4.0, measured);
        engine.State.ShouldBe(EngineState.Running, measured);
    }

    /// <summary>
    /// A rollback's undo fails once. The undo is retried on its own backoff, about 100 ms later,
    /// so the writer waiting for the rolled-back transaction's writer lock proceeds within about a
    /// second although the maintenance interval is an hour (#1226 owner decision of 2026-10-04).
    /// The engine's wiring is checked exactly (the first retry is due within 100 ms of the
    /// deferral), and the release end to end as a ratio to that first delay.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        var options = new BlobDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            MaintenanceInterval = maintenance,
        };
        await using var engine = BlobDatabaseEngine.Create(options);
        var database = await engine.CreateDatabaseAsync("blobs");
        var container = await database.CreateContainerAsync("files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var otherFiles = await other.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "rolled", "rolled");
        await WriteAsync(files, "keep", "overwritten");

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred with the database
        // writer lock held; the pages are released at once. (Until #1252 a failed journal write was
        // the transient fault; a journal write failure now takes the database offline.)
        var watch = Stopwatch.StartNew();
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = database.Coordinator.NextDeferredUndoRetry;

        await WriteAsync(otherFiles, "other", "other").WaitAsync(Timeout);
        watch.Stop();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await NamesAsync(container)).ShouldBe(["keep", "other"]);
        (await ReadAsync(container, "keep")).ShouldBe("original");
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(options.DeferredUndoRetryDelay);
        // The regression this guards against waits a full MaintenanceInterval (an hour here) per retry.
        // 100 retry delays (10 s) still catches it by a factor of 360 and leaves room for a loaded CI
        // runner; the exact wiring is the firstRetry check above.
        (watch.Elapsed / options.DeferredUndoRetryDelay).ShouldBeLessThan(100);
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// Automatic uploads of small blobs share data pages. Every transaction used to own the pages of
    /// its content chunks, so each 2 KiB upload took a fresh 8 KiB page, 1.012 pages an upload, and
    /// the worker pace test's in-memory data file grew toward its 2 GiB capacity. With one content
    /// owner, three chunks fill a page and the catalog records pack beside them.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Space: automatic uploads of small blobs share data pages")]
    public async Task OpenWriteAsync_SmallBlobsAutoCommitted_ShouldShareDataPages()
    {
        // Arrange
        const int uploads = 600;
        await using var engine = BlobDatabaseEngine.Create(new()
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("space");
        var container = await database.CreateContainerAsync("files");
        long pagesBefore = database.DataStorage.PageManager.PageCount;

        // Act: each upload is its own transaction.
        for (int id = 0; id < uploads; id++)
        {
            await WriteAsync(container, $"k{id}", new string('x', 2048));
        }

        // Assert: three chunks to a page is 200 pages, and the catalog adds a few; a page per upload
        // was 607.
        long pages = database.DataStorage.PageManager.PageCount - pagesBefore;
        pages.ShouldBeLessThanOrEqualTo(uploads / 2, $"{pages} data pages for {uploads} uploads of 2 KiB");
        (await NamesAsync(container)).Count.ShouldBe(uploads);
        (await ReadAsync(container, $"k{uploads - 1}")).ShouldBe(new string('x', 2048));
    }

    private static async Task WriteAsync(BlobContainer container, string name, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static async Task<string> ReadAsync(BlobContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<List<string>> NamesAsync(BlobContainer container)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync())
        {
            names.Add(blob.Name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<ProtocolChannel> ConnectAsync(InMemoryConnectionListener listener, CancellationToken token)
    {
        var connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        return new ProtocolChannel(connection.AsStream(), BlobProtocol.Family, leaveOpen: false);
    }

    private static async Task HandshakeAsync(ProtocolChannel channel, CancellationToken token)
    {
        await WriteAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "blobs", "test").Encode(), token);
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
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of blob exchange.");

    private static async Task<ProtocolErrorMessage> ReadErrorAsync(ProtocolChannel channel, CancellationToken token)
    {
        var frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe(ProtocolMessageType.Error);
        return ProtocolErrorMessage.Decode(frame.Payload.Span);
    }
}
