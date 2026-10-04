using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// An operation that fails inside an explicit transaction aborts the whole transaction (#1225, the
/// contract #1188 set for Graph): the session keeps it as <see cref="TransactionState.Faulted"/> and
/// refuses every later operation and BEGIN with COHDBB001 until the caller rolls back, and nothing
/// the transaction wrote survives. The wire cases are in <c>Blob.Client</c>'s
/// <c>BlobTransactionFailureWireTests</c>.
/// </summary>
public sealed class BlobTransactionFailureTests
{
    /// <summary>The issue's reproduction: a failure, then a write, then ROLLBACK leaves the blobs unchanged.</summary>
    /// <param name="failure">The kind of operation that fails inside the transaction.</param>
    /// <param name="isolation">The isolation level the explicit transaction runs under.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a failed operation, a later write and ROLLBACK leave the blobs unchanged")]
    [InlineData("container", IsolationLevel.Snapshot)]
    [InlineData("exists", IsolationLevel.Snapshot)]
    [InlineData("upload", IsolationLevel.Snapshot)]
    [InlineData("conflict", IsolationLevel.Snapshot)]
    [InlineData("container", IsolationLevel.ReadCommitted)]
    [InlineData("exists", IsolationLevel.ReadCommitted)]
    [InlineData("upload", IsolationLevel.ReadCommitted)]
    public async Task Operation_FailureThenWriteThenRollback_ShouldLeaveBlobsUnchanged(string failure, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync(isolation);
        if (failure == "conflict")
        {
            // Another session replaces the blob after this snapshot began, before the transaction
            // takes the database writer lock with its first write.
            await Write(container, "keep", "changed");
        }
        await Write(files, "pending", "pending");

        // Act
        var error = await Should.ThrowAsync<Exception>(async () => await FailAsync(failure, scoped, files));
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await files.DeleteAsync("keep"));
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        await transaction.RollbackAsync();

        // Assert
        refused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        if (error is OperationCanceledException)
        {
            // A canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
            refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        }
        else
        {
            refused.Message.ShouldContain(error.Message, Case.Sensitive);
            refused.InnerException.ShouldBeSameAs(error);
        }
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Read(container, "keep")).ShouldBe(failure == "conflict" ? "changed" : "original");
        (await container.GetPropertiesAsync("pending")).ShouldBeNull();
        (await container.GetPropertiesAsync("upload")).ShouldBeNull();
    }

    /// <summary>Every operation surface refuses work on a faulted transaction, and a rollback restores autocommit and BEGIN.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a faulted transaction refuses every operation until rollback")]
    public async Task Operations_OnFaultedTransaction_ShouldBeRefusedUntilRollback()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));

        // Act
        var refusals = new List<Exception>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await files.OpenWriteAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await files.OpenReadAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await files.GetPropertiesAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await files.DeleteAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in files.GetBlobsAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.CreateContainerAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.DropContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in scoped.GetContainersAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
        };
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();

        // Assert
        refusals.ShouldAllBe(error => error.Message.StartsWith("COHDBB001", StringComparison.Ordinal));
        (await Read(files, "keep")).ShouldBe("original");
        await using (var next = await session.BeginTransactionAsync())
        {
            await Write(files, "next", "next");
            await next.CommitAsync();
        }
        session.CurrentTransaction.ShouldBeNull();
        (await Names(container)).ShouldBe(["keep", "next"]);
    }

    /// <summary>COMMIT on a faulted transaction fails with COHDBB001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after a failed operation fails and commits nothing")]
    public async Task CommitAsync_AfterFailedOperation_ShouldThrowAndCommitNothing()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        await using var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        // The client's rollback in a catch block after the failed commit is a no-op, not a second error.
        await transaction.RollbackAsync();
        // A second commit keeps reporting why nothing committed.
        (await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync()))
            .Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        (await Names(container)).ShouldBeEmpty();
        await Write(files, "after", "after");
        (await Names(container)).ShouldBe(["after"]);
    }

    /// <summary>Disposing a faulted transaction ends it without throwing, and the session is idle again.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: disposing a faulted transaction ends it")]
    public async Task DisposeAsync_FaultedTransaction_ShouldEndTransactionAndReturnSessionToAutocommit()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));

        // Act
        await transaction.DisposeAsync();
        await Write(files, "after", "after");

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Names(container)).ShouldBe(["after"]);
        // COMMIT keeps its coded error after the transaction ended, so its outcome never depends on
        // whether the caller's commit or the session's teardown ran first.
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        commit.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        commit.InnerException.ShouldBeSameAs(failure);
    }

    /// <summary>An upload that persisted chunks before failing leaves none of them, nor earlier operations' work.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a partially written upload leaves nothing after rollback")]
    public async Task OpenWriteAsync_UploadFailsAfterChunks_ShouldUndoTheWholeTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "earlier", "earlier");
        using var cancellation = new CancellationTokenSource();
        var upload = await files.OpenWriteAsync("keep", cancellationToken: cancellation.Token);
        await upload.WriteAsync(new byte[100_000]);

        // Act: the replacement already persisted several chunks when its upload is canceled.
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await upload.WriteAsync(new byte[100]));
        await upload.DisposeAsync();
        var faultedState = transaction.State;
        await transaction.RollbackAsync();

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        (await Read(container, "keep")).ShouldBe("original");
        (await Names(container)).ShouldBe(["keep"]);
    }

    /// <summary>A faulted transaction holds no writer lock: another session writes while it awaits rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a faulted transaction releases the writer lock at once")]
    public async Task Operation_FaultedTransaction_ShouldNotBlockOtherWriters()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var failed = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)failed.Database;
        var files = await scoped.GetContainerAsync("files");
        await using var transaction = await failed.BeginTransactionAsync();
        await Write(files, "pending", "pending");
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await Write(container, "other", "other", timeout.Token);

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Names(container)).ShouldBe(["other"]);
    }

    /// <summary>An autocommit failure ends only its own operation: the session stays idle and usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: autocommit failures leave the session usable")]
    public async Task Operation_AutocommitFailure_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));
        await Write(files, "after", "after");

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await Names(container)).ShouldBe(["after"]);
        await using var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
    }

    /// <summary>A canceled operation waiting for the writer lock aborts its transaction and releases the session.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a canceled operation aborts the explicit transaction")]
    public async Task Operation_CanceledWhileWaitingInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        var blockerFiles = await ((IBlobDatabase)blocker.Database).GetContainerAsync("files");
        var waitingFiles = await ((IBlobDatabase)waiting.Database).GetContainerAsync("files");
        var blocking = await blocker.BeginTransactionAsync();
        await Write(blockerFiles, "blocker", "blocker");
        var transaction = await waiting.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = waitingFiles.DeleteAsync("blocker", cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act: the refusal is immediate; the timeout only turns a regression into a failure, not a hang.
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await waitingFiles.DeleteAsync("late", timeout.Token));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();
        await blocking.CommitAsync();

        // Assert: a canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        faultedState.ShouldBe(TransactionState.Faulted);
        (await Names(container)).ShouldBe(["blocker"]);
    }

    /// <summary>Closing a session with a faulted transaction ends the transaction without an error.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: closing the session ends a faulted transaction")]
    public async Task DisposeAsync_SessionWithFaultedTransaction_ShouldEndTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("missing"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        (await Names(container)).ShouldBeEmpty();
    }

    /// <summary>Failures that come before an operation starts leave the transaction active.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: argument and request validation leave the transaction active")]
    public async Task Validation_BeforeOperationStarts_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var files = await scoped.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");

        // Act
        var rejections = new List<Exception>
        {
            await Should.ThrowAsync<ArgumentException>(async () => await files.OpenWriteAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await files.OpenReadAsync("")),
            await Should.ThrowAsync<ArgumentException>(async () => await files.DeleteAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await scoped.GetContainerAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" ")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("LIST files")),
        };
        var state = transaction.State;
        await transaction.CommitAsync();

        // Assert
        rejections.ShouldAllBe(error => !error.Message.StartsWith("COHDBB001", StringComparison.Ordinal));
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Names(container)).ShouldBe(["pending"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var rolledBack = await session.BeginTransactionAsync();
        await Write(files, "discarded", "discarded");
        await rolledBack.RollbackAsync();
        var committed = await session.BeginTransactionAsync();
        await Write(files, "kept", "kept");
        await committed.CommitAsync();
        var currentAfterCommit = session.CurrentTransaction;

        // Act
        await rolledBack.RollbackAsync();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await committed.RollbackAsync());

        // Assert
        currentAfterCommit.ShouldBeNull();
        refusal.Message.ShouldContain("Committed", Case.Sensitive);
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await Names(container)).ShouldBe(["kept"]);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "first", "first");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await Write(files, "second", "second");
        await transaction.CommitAsync();

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Names(container)).ShouldBe(["first", "second"]);
    }

    /// <summary>A rollback that does not complete leaves the transaction faulted and refusing work until a rollback completes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a rollback that does not complete leaves the transaction faulted")]
    public async Task RollbackAsync_ThatDoesNotComplete_ShouldLeaveTransactionFaultedUntilRetried()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        (await files.GetPropertiesAsync("keep")).ShouldNotBeNull();

        // Act: the abort record is the rollback's only journal write, so failing it fails the rollback.
        IOException rollbackFailure;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            rollbackFailure = await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync());
        }
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await files.DeleteAsync("keep"));
        var beginRefused = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        await transaction.RollbackAsync();

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        refused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        refused.Message.ShouldContain("did not complete", Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(rollbackFailure);
        beginRefused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await Write(files, "after", "after");
        (await Names(container)).ShouldBe(["after", "keep"]);
    }

    /// <summary>COMMIT after a rollback that did not complete fails with COHDBB001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after an incomplete rollback fails and ends the transaction")]
    public async Task CommitAsync_AfterRollbackThatDidNotComplete_ShouldFailAndEndTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        (await files.GetPropertiesAsync("missing")).ShouldBeNull();
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync());
        }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.InnerException.ShouldBeOfType<IOException>();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await transaction.RollbackAsync();
    }

    /// <summary>A commit the kernel aborts crosses the boundary translated, and a catch-block rollback afterwards raises nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a kernel-aborted commit is translated and a later rollback is a no-op")]
    public async Task CommitAsync_KernelAbortsCommit_ShouldTranslateAndAcceptRollback()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");

        // Act: the commit record is the commit's first journal write; the kernel then aborts the transaction.
        DatabaseTransactionAbortedException error;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await transaction.CommitAsync());
        }
        var stateAfterCommit = transaction.State;
        await transaction.RollbackAsync();

        // Assert
        error.InnerException.ShouldBeOfType<TransactionAbortedException>();
        stateAfterCommit.ShouldBe(TransactionState.Faulted);
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.CurrentTransaction.ShouldBeNull();
        await Write(files, "after", "after");
        (await Names(container)).ShouldBe(["after"]);
    }

    private static async Task FailAsync(string failure, IBlobDatabase scoped, IBlobContainer files)
    {
        switch (failure)
        {
            case "container":
                // The catalog has no such container: the operation fails inside its transaction.
                await scoped.GetContainerAsync("missing");
                break;
            case "exists":
                // An upload that may not replace an existing blob is refused once its operation started.
                await files.OpenWriteAsync("keep", new BlobWriteOptions { Overwrite = false });
                break;
            case "upload":
            {
                // The upload persists a chunk, then its token is canceled mid-stream.
                using var cancellation = new CancellationTokenSource();
                var upload = await files.OpenWriteAsync("upload", cancellationToken: cancellation.Token);
                await upload.WriteAsync(new byte[20_000]);
                cancellation.Cancel();
                try
                {
                    await upload.WriteAsync(new byte[100]);
                }
                finally
                {
                    await upload.DisposeAsync();
                }
                break;
            }
            case "conflict":
                // The blob changed after this snapshot began: first-updater-wins refuses the delete.
                await files.DeleteAsync("keep");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
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
        await foreach (var blob in container.GetBlobsAsync()) { names.Add(blob.Name); }
        return names;
    }
}
