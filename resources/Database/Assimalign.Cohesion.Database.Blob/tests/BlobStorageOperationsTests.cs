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
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
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
    /// and the sessions' close included. Reopening the database runs recovery, which keeps the
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("blobs", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var wire = await ConnectAsync(listener, token);
        await HandshakeAsync(wire, token);
        await database.CreateContainerAsync("files", token);
        var session = await database.CreateSessionAsync(token);
        var other = await database.CreateSessionAsync(token);
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files", token);
        var otherFiles = await ((IBlobDatabase)other.Database).GetContainerAsync("files", token);
        await Write(files, "kept", "kept", token);

        // A reader, and a download stream: the database has one writer at a time, so an open
        // writer would block the commit below.
        var open = await other.BeginTransactionAsync(token);
        (await Names(otherFiles)).ShouldBe(["kept"]);
        await using var reader = await database.CreateSessionAsync(token);
        var download = await (await ((IBlobDatabase)reader.Database).GetContainerAsync("files", token)).OpenReadAsync("kept", token);

        // Act: the upload's commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await Write(files, "unconfirmed", "unconfirmed", token));
            failures.Remaining.ShouldBe(0);
        }

        var atTheFailure = strategy.Capture("blobs");
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateContainerAsync("late", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.OpenReadAsync("kept", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.OpenWriteAsync("late", cancellationToken: token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await files.GetPropertiesAsync("kept", token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await download.ReadExactlyAsync(new byte[4], token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await download.DisposeAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync(token)),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync(token)),
        };

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
            worker.RunIteration(CancellationToken.None);
        }

        await other.DisposeAsync();
        await session.DisposeAsync();
        await reader.DisposeAsync();
        var beforeTheReopen = strategy.Capture("blobs");

        var reopened = (BlobDatabaseInstance)await engine.OpenDatabaseAsync("blobs", token);
        var names = await Names(await reopened.GetContainerAsync("files", token));

        // Assert
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBB002" && refusal.Message.StartsWith("COHDBB002", StringComparison.Ordinal));
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("pool");

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
        ((BlobDatabaseInstance)await sized.CreateDatabaseAsync("sized")).DataStorage.BufferPoolCapacity.ShouldBe(256);
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
        await using var engine = (BlobDatabaseEngine)builder.Build();
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    /// <summary>
    /// Under a sustained upload load the journal-size trigger keeps the journal near its
    /// configured size (#1254). The bound is a ratio to the configured size.
    /// </summary>
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("bounded");
        await database.CreateContainerAsync("files");
        using var stop = new CancellationTokenSource();
        byte[] payload = new byte[16 * 1024];
        Random.Shared.NextBytes(payload);

        // Each writer overwrites its own blob, so the writers never conflict.
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
            while (!stop.IsCancellationRequested)
            {
                await using var stream = await files.OpenWriteAsync($"writer-{writer}");
                await stream.WriteAsync(payload);
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("blobs");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var otherFiles = await ((IBlobDatabase)other.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "rolled", "rolled");
        await Write(files, "keep", "overwritten");

        // Act: the rollback's first journal write is its undo bracket's begin record, which fails
        // once, so the undo is deferred with the database writer lock held.
        int unspent;
        var watch = Stopwatch.StartNew();
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = database.Coordinator.NextDeferredUndoRetry;

        await Write(otherFiles, "other", "other").WaitAsync(Timeout);
        watch.Stop();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await Names(container)).ShouldBe(["keep", "other"]);
        (await Read(container, "keep")).ShouldBe("original");
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(options.DeferredUndoRetryDelay);
        (watch.Elapsed / options.DeferredUndoRetryDelay).ShouldBeLessThan(20);
        engine.State.ShouldBe(EngineState.Running);
    }

    private static async Task Write(IBlobContainer container, string name, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static async Task<string> Read(IBlobContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<List<string>> Names(IBlobContainer container)
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
