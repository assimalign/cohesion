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
/// the transaction wrote survives. A started rollback always ends the transaction (#1226): a lost
/// abort record changes nothing, an undo the journal rejects is deferred with the writer lock held
/// until the version-purge pass completes it, and one that still fails when the engine closes is
/// scrubbed at the next open. The wire cases are in <c>Blob.Client</c>'s
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
        ShouldBeExpectedFailure(error, failure);
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

    /// <summary>
    /// A read that fails once its stream is open is a failed operation: inside an explicit
    /// transaction it aborts the transaction, so the next operation is refused with COHDBB001, and
    /// disposing the failed stream raises nothing more.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a failed read aborts the explicit transaction")]
    public async Task ReadAsync_FailsInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", new string('k', 100_000));
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");
        var stream = await files.OpenReadAsync("keep");
        var buffer = new byte[1_000];
        (await stream.ReadAsync(buffer)).ShouldBe(buffer.Length);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(buffer, canceled.Token));
        var faultedState = transaction.State;
        var refusedRead = await Should.ThrowAsync<DatabaseException>(async () => await stream.ReadExactlyAsync(buffer));
        await stream.DisposeAsync();
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await Write(files, "late", "late"));
        await transaction.RollbackAsync();

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        refusedRead.Message.ShouldNotStartWith("COHDBB001", Case.Sensitive);
        refused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        // A canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Names(container)).ShouldBe(["keep"]);
    }

    /// <summary>An autocommit read that fails ends only its own read, and the session stays usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a failed autocommit read leaves the session usable")]
    public async Task ReadAsync_FailsInAutocommit_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var stream = await files.OpenReadAsync("keep");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[4], canceled.Token));
        await stream.DisposeAsync();
        await Write(files, "after", "after");

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await Read(files, "keep")).ShouldBe("original");
        (await Names(container)).ShouldBe(["after", "keep"]);
    }

    /// <summary>
    /// A commit after the session closed under an open transaction fails with COHDBB001 naming the
    /// closure, whichever is asked first, and commits nothing; a rollback afterwards is a no-op.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after the session closed fails with COHDBB001")]
    public async Task CommitAsync_AfterSessionClosed_ShouldFailWithCodeNamingTheClosure()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "pending", "pending");

        // Act
        await session.DisposeAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.Message.ShouldContain("The blob session closed before the transaction ended.", Case.Sensitive);
        repeated.Message.ShouldBe(error.Message);
        transaction.State.ShouldBe(TransactionState.RolledBack);
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

    /// <summary>
    /// A rollback whose abort record cannot be written still ends the transaction and releases the
    /// database writer lock, so another session's writer proceeds (#1226). Until #1226 such a
    /// rollback failed and left the transaction Faulted until a later rollback completed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a rollback whose abort record cannot be written still ends the transaction")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndReleaseWriterLock()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var otherFiles = await ((IBlobDatabase)other.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();

        // A delete that matches nothing takes the database writer lock and writes no version, so
        // the abort record is the rollback's only journal write.
        (await files.DeleteAsync("missing")).ShouldBeFalse();

        // Act
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // Assert: the record write failed, and the rollback ended the transaction anyway.
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Write(otherFiles, "other", "other", timeout.Token);
        await using (var next = await session.BeginTransactionAsync())
        {
            await Write(files, "after", "after");
            await next.CommitAsync();
        }
        (await Names(container)).ShouldBe(["after", "keep", "other"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other:
    /// COMMIT is refused and commits nothing, and a repeated rollback raises nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        (await files.DeleteAsync("missing")).ShouldBeFalse();
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
        }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldBe("The transaction is RolledBack.");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Names(container)).ShouldBe(["keep"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = BlobDatabaseEngine.Create(QuietOptions(strategy));
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        await database.CreateContainerAsync("files");
        await using (var session = await database.CreateSessionAsync())
        {
            var scoped = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
            await BlobEngineTests.Write(scoped, "kept", "kept"u8.ToArray());
            var transaction = await session.BeginTransactionAsync();
            await BlobEngineTests.Write(scoped, "rolled", "rolled"u8.ToArray());
            await BlobEngineTests.Write(scoped, "kept", "overwritten"u8.ToArray());

            // The undo's storage bracket begins (the first write) and fails at its first page
            // image (the second), so the bracket rolls itself back and the undo is deferred.
            using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1, skip: 1))
            {
                await transaction.RollbackAsync();
                failures.Remaining.ShouldBe(0);
            }

            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close retries the undo, which fails the same way.
        AggregateException closeFailure;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1, skip: 1))
        {
            closeFailure = Should.Throw<AggregateException>(() => engine.Dispose());
            failures.Remaining.ShouldBe(0);
        }

        await using var reopened = BlobDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = (IBlobDatabase)await reopened.OpenDatabaseAsync("test");
        var files = await recovered.GetContainerAsync("files");

        // Assert: the new blob is gone and the overwrite is undone.
        closeFailure.InnerExceptions.ShouldContain(error => error is IOException);
        (await files.GetPropertiesAsync("rolled")).ShouldBeNull();
        (await BlobEngineTests.Read(files, "kept")).ShouldBe("kept"u8.ToArray());
    }

    /// <summary>
    /// The two end rules meet (#1225 with #1226): a rollback whose undo the journal rejects still
    /// ends the transaction, so it is no longer the session's transaction, but it keeps the
    /// database writer lock; the next writer waits for the purge pass, after which nothing the
    /// transaction wrote survives.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Rollback: a deferred undo holds the writer lock until the purge pass and then leaves nothing")]
    public async Task RollbackAsync_UndoDeferred_ShouldHoldWriterLockUntilThePurgePassAndLeaveNothing()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = (Internal.BlobDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var files = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var otherFiles = await ((IBlobDatabase)other.Database).GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(files, "rolled", "rolled");
        await Write(files, "keep", "overwritten");

        // Act: the undo's first journal write (its storage bracket's begin) fails.
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }
        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = Write(otherFiles, "other", "other");
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        unspent.ShouldBe(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        otherProceededBeforeTheUndo.ShouldBeFalse();
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await Names(container)).ShouldBe(["keep", "other"]);
        (await Read(container, "keep")).ShouldBe("original");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a commit record that could not be made durable crosses the boundary as committed-unconfirmed")]
    public void TranslateKernelFailure_CommitUnconfirmed_ShouldBecomeTheAreaRootsCommitUnconfirmedException()
    {
        // Arrange
        var kernel = new TransactionCommitUnconfirmedException("Transaction 7 committed, but its commit record could not be made durable.", new IOException("flush"));

        // Act
        var translated = Internal.BlobDatabaseInstance.TranslateKernelFailure(kernel);

        // Assert: not an abort, so a caller never retries work that committed.
        var unconfirmed = translated.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        unconfirmed.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        unconfirmed.Message.ShouldBe(kernel.Message);
        unconfirmed.InnerException.ShouldBeSameAs(kernel);
    }

    // The engine's own maintenance workers stay out of the way: these tests drive the purge pass
    // and the close themselves.
    private static BlobDatabaseEngineOptions QuietOptions(FaultInjectingJournalStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
    };

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

    // The failure each case must raise, so a wrong failure cannot pass as the one under test. A
    // canceled task may surface a derived cancellation exception.
    private static void ShouldBeExpectedFailure(Exception error, string failure)
    {
        switch (failure)
        {
            case "container":
            case "exists":
                error.ShouldBeOfType<DatabaseException>();
                break;
            case "upload":
                error.ShouldBeAssignableTo<OperationCanceledException>();
                break;
            case "conflict":
                error.ShouldBeOfType<DatabaseTransactionAbortedException>();
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
