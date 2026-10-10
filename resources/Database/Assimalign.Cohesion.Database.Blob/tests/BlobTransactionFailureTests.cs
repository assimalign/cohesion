using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync(isolation);
        if (failure == "conflict")
        {
            // Another session replaces the blob after this snapshot began, before the transaction
            // takes the database writer lock with its first write.
            await WriteAsync(container, "keep", "changed");
        }
        await WriteAsync(files, "pending", "pending");

        // Act
        var error = await Should.ThrowAsync<Exception>(async () => await FailAsync(failure, session, files));
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
        (await ReadAsync(container, "keep")).ShouldBe(failure == "conflict" ? "changed" : "original");
        (await container.GetPropertiesAsync("pending")).ShouldBeNull();
        (await container.GetPropertiesAsync("upload")).ShouldBeNull();
    }

    /// <summary>Every operation surface refuses work on a faulted transaction, and a rollback restores autocommit and BEGIN.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a faulted transaction refuses every operation until rollback")]
    public async Task Operations_OnFaultedTransaction_ShouldBeRefusedUntilRollback()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"));

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
            await Should.ThrowAsync<DatabaseException>(async () => await session.CreateContainerAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.DropContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in session.GetContainersAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
        };
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();

        // Assert
        refusals.ShouldAllBe(error => error.Message.StartsWith("COHDBB001", StringComparison.Ordinal));
        (await ReadAsync(files, "keep")).ShouldBe("original");
        await using (var next = await session.BeginTransactionAsync())
        {
            await WriteAsync(files, "next", "next");
            await next.CommitAsync();
        }
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(container)).ShouldBe(["keep", "next"]);
    }

    /// <summary>COMMIT on a faulted transaction fails with COHDBB001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after a failed operation fails and commits nothing")]
    public async Task CommitAsync_AfterFailedOperation_ShouldThrowAndCommitNothing()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        await using var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBB001: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        error.Message.ShouldEndWith("Cause: " + failure.Message, Case.Sensitive);
        error.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        // The client's rollback in a catch block after the failed commit is a no-op, not a second error.
        await transaction.RollbackAsync();
        // A second commit keeps reporting why nothing committed, with the cause, as the model's own
        // state machine did; the root base carries it since phase 4 of the concrete-types plan.
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        repeated.Message.ShouldBe(error.Message);
        repeated.InnerException.ShouldBeSameAs(failure);
        (await NamesAsync(container)).ShouldBeEmpty();
        await WriteAsync(files, "after", "after");
        (await NamesAsync(container)).ShouldBe(["after"]);
    }

    /// <summary>Disposing a faulted transaction ends it without throwing, and the session is idle again.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: disposing a faulted transaction ends it")]
    public async Task DisposeAsync_FaultedTransaction_ShouldEndTransactionAndReturnSessionToAutocommit()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"));

        // Act
        await transaction.DisposeAsync();
        await WriteAsync(files, "after", "after");

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(container)).ShouldBe(["after"]);
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
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "earlier", "earlier");
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
        (await ReadAsync(container, "keep")).ShouldBe("original");
        (await NamesAsync(container)).ShouldBe(["keep"]);
    }

    /// <summary>A faulted transaction holds no writer lock: another session writes while it awaits rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a faulted transaction releases the writer lock at once")]
    public async Task Operation_FaultedTransaction_ShouldNotBlockOtherWriters()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var failed = await database.CreateSessionAsync();
        var files = await failed.GetContainerAsync("files");
        await using var transaction = await failed.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        await Should.ThrowAsync<DatabaseException>(async () => await failed.GetContainerAsync("missing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await WriteAsync(container, "other", "other", timeout.Token);

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await NamesAsync(container)).ShouldBe(["other"]);
    }

    /// <summary>An autocommit failure ends only its own operation: the session stays idle and usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: autocommit failures leave the session usable")]
    public async Task Operation_AutocommitFailure_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"));
        await WriteAsync(files, "after", "after");

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(container)).ShouldBe(["after"]);
        await using var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
    }

    /// <summary>A canceled operation waiting for the writer lock aborts its transaction and releases the session.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a canceled operation aborts the explicit transaction")]
    public async Task Operation_CanceledWhileWaitingInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        var blockerFiles = await blocker.GetContainerAsync("files");
        var waitingFiles = await waiting.GetContainerAsync("files");
        var blocking = await blocker.BeginTransactionAsync();
        await WriteAsync(blockerFiles, "blocker", "blocker");
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
        (await NamesAsync(container)).ShouldBe(["blocker"]);
    }

    /// <summary>Closing a session with a faulted transaction ends the transaction without an error.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: closing the session ends a faulted transaction")]
    public async Task DisposeAsync_SessionWithFaultedTransaction_ShouldEndTransaction()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        (await NamesAsync(container)).ShouldBeEmpty();
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
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", new string('k', 100_000));
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
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
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await WriteAsync(files, "late", "late"));
        await transaction.RollbackAsync();

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        refusedRead.Message.ShouldNotStartWith("COHDBB001", Case.Sensitive);
        refused.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        // A canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await NamesAsync(container)).ShouldBe(["keep"]);
    }

    /// <summary>An autocommit read that fails ends only its own read, and the session stays usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a failed autocommit read leaves the session usable")]
    public async Task ReadAsync_FailsInAutocommit_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var stream = await files.OpenReadAsync("keep");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[4], canceled.Token));
        await stream.DisposeAsync();
        await WriteAsync(files, "after", "after");

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await ReadAsync(files, "keep")).ShouldBe("original");
        (await NamesAsync(container)).ShouldBe(["after", "keep"]);
    }

    /// <summary>
    /// A commit after the session closed under an open transaction fails with COHDBB001 naming why
    /// nothing committed, whichever is asked first, and commits nothing; a rollback afterwards is a
    /// no-op. For an active transaction the cause is the root session base's teardown cause, "The
    /// session closed before the transaction ended." (concrete-types plan §6.4), for the model's
    /// former "The blob session closed before the transaction ended."; for one an operation
    /// aborted before the session closed it is the operation's failure, which the base's teardown
    /// keeps (the model's own close did the same before phase 4).
    /// </summary>
    /// <param name="aborted">True when an operation aborted the transaction before the session closed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after the session closed reports COHDBB001 naming why nothing committed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted(bool aborted)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        DatabaseException? failure = aborted
            ? await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("missing"))
            : null;

        // Act
        await session.DisposeAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldStartWith("COHDBB001: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        if (failure is null)
        {
            error.Message.ShouldEndWith("Cause: The session closed before the transaction ended.", Case.Sensitive);
        }
        else
        {
            error.InnerException.ShouldBeSameAs(failure);
            error.Message.ShouldEndWith("Cause: " + failure.Message, Case.Sensitive);
        }

        repeated.Message.ShouldBe(error.Message);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await NamesAsync(container)).ShouldBeEmpty();
    }

    /// <summary>Failures that come before an operation starts leave the transaction active.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: argument and request validation leave the transaction active")]
    public async Task Validation_BeforeOperationStarts_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");

        // Act
        var rejections = new List<Exception>
        {
            await Should.ThrowAsync<ArgumentException>(async () => await files.OpenWriteAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await files.OpenReadAsync("")),
            await Should.ThrowAsync<ArgumentException>(async () => await files.DeleteAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.GetContainerAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" ")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("LIST files")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new BlobRequest())),
        };
        var state = transaction.State;
        await transaction.CommitAsync();

        // Assert: a blob session has no query or statement language. Option B (concrete-types plan
        // §6.6) names the session's own container operations, where the request seam named the
        // former IBlobDatabase of session.Database.
        rejections[5].Message.ShouldBe("Blob sessions have no statement language or server-scoped commands.");
        rejections[6].Message.ShouldBe("Blob sessions have no query language. Use the session's container operations.");
        rejections.ShouldAllBe(error => !error.Message.StartsWith("COHDBB001", StringComparison.Ordinal));
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await NamesAsync(container)).ShouldBe(["pending"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var rolledBack = await session.BeginTransactionAsync();
        await WriteAsync(files, "discarded", "discarded");
        await rolledBack.RollbackAsync();
        var committed = await session.BeginTransactionAsync();
        await WriteAsync(files, "kept", "kept");
        await committed.CommitAsync();
        var currentAfterCommit = session.CurrentTransaction;

        // Act
        await rolledBack.RollbackAsync();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await committed.RollbackAsync());

        // Assert: the root base's message (concrete-types plan §6.4), the one the model carried.
        currentAfterCommit.ShouldBeNull();
        refusal.Message.ShouldBe("The transaction is Committed; a committed transaction cannot roll back.");
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await NamesAsync(container)).ShouldBe(["kept"]);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "first", "first");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await WriteAsync(files, "second", "second");
        await transaction.CommitAsync();

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await NamesAsync(container)).ShouldBe(["first", "second"]);
    }

    /// <summary>
    /// A commit of a transaction its caller already rolled back is refused by state, with the root
    /// base's message (concrete-types plan §6.4).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after ROLLBACK is refused with the transaction's state")]
    public async Task CommitAsync_AfterRollback_ShouldBeRefusedWithTheState()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "discarded", "discarded");
        await transaction.RollbackAsync();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        refusal.Message.ShouldBe("The transaction is RolledBack.");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await NamesAsync(container)).ShouldBeEmpty();
    }

    /// <summary>
    /// Closing a session aborts the operation running on it, here an upload stream still open, and
    /// the operation's abort is the cause a later commit of its transaction reports with
    /// <c>COHDBB001</c>: the teardown's own cause does not replace it. The model's teardown did the
    /// same; since the concrete-types plan's phase 4 the session's root base runs it (§6.4), so the
    /// order is pinned here. The open stream refuses a write once the session closed, with the
    /// base's closed-session message.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: closing a session aborts its open stream, whose cause a later COMMIT reports")]
    public async Task DisposeAsync_SessionWithOpenStream_ShouldAbortItAndReportItsCause()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        var upload = await files.OpenWriteAsync("keep");
        await upload.WriteAsync(new byte[20_000]);

        // Act
        await session.DisposeAsync();
        var write = await Should.ThrowAsync<DatabaseException>(async () => await upload.WriteAsync(new byte[100]));
        await Record.ExceptionAsync(async () => await upload.DisposeAsync());
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        write.Message.ShouldBe("The session is closed.");
        commit.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        commit.Message.ShouldEndWith("Cause: The blob session closed while the operation was running.", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await ReadAsync(container, "keep")).ShouldBe("original");
        (await NamesAsync(container)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// A commit while an operation of the transaction still runs (here a delete waiting for the
    /// writer lock) is refused with the root base's message (concrete-types plan §6.4), for the
    /// model's former "Dispose every blob stream before committing its transaction.", and leaves
    /// the transaction active: it commits once the operation completed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT while an operation of the transaction runs is refused and leaves it active")]
    public async Task CommitAsync_WhileOperationRuns_ShouldBeRefusedAndLeaveTheTransactionActive()
    {
        // Arrange: another transaction holds the writer lock, so the operation waits.
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "doomed", "doomed");
        await using var blocker = await database.CreateSessionAsync();
        await using var session = await database.CreateSessionAsync();
        var blockerFiles = await blocker.GetContainerAsync("files");
        var files = await session.GetContainerAsync("files");
        var blocking = await blocker.BeginTransactionAsync();
        await WriteAsync(blockerFiles, "blocker", "blocker");
        var transaction = await session.BeginTransactionAsync();
        var pending = files.DeleteAsync("doomed").AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var stateAfterRefusal = transaction.State;
        await blocking.CommitAsync();
        (await pending.WaitAsync(Timeout)).ShouldBeTrue();
        await transaction.CommitAsync();

        // Assert
        refusal.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        stateAfterRefusal.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await NamesAsync(container)).ShouldBe(["blocker"]);
    }

    /// <summary>
    /// BEGIN on a session whose transaction is active is refused with the root base's one message
    /// before the model's isolation-level refusal, and a closed session before both (concrete-types
    /// plan §6.4, BEGIN's refusal order): the model refused an unsupported isolation level first,
    /// and its "already active" message was "A transaction or stream is already active on this
    /// session.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: BEGIN is refused for a closed session, then an active transaction, before the isolation level")]
    public async Task BeginTransactionAsync_WhileActiveOrClosed_ShouldRefuseBeforeTheIsolationLevel()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();

        // Act
        var again = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        var serializable = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable));
        await transaction.RollbackAsync();
        var unsupported = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable));
        await session.DisposeAsync();
        var closed = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable));

        // Assert
        again.Message.ShouldBe("A transaction or operation is already active on this session.");
        serializable.Message.ShouldBe("A transaction or operation is already active on this session.");
        unsupported.Message.ShouldBe("The blob engine supports Snapshot and ReadCommitted isolation.");
        closed.Message.ShouldBe("The session is closed.");
    }

    /// <summary>
    /// A closed session refuses BEGIN, both execute seams, its own container operations and the
    /// operations of a container bound to it with the root base's message (concrete-types plan
    /// §6.4), for the model's former "The blob session is closed.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: a closed session refuses every operation with one message")]
    public async Task Operations_OnClosedSession_ShouldRefuseWithOneMessage()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        var session = await database.CreateSessionAsync();
        var bound = await session.GetContainerAsync("files");
        await session.DisposeAsync();

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("LIST files")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new BlobRequest())),
            await Should.ThrowAsync<DatabaseException>(async () => await session.CreateContainerAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.DropContainerAsync("files")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in session.GetContainersAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.OpenWriteAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.OpenReadAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.GetPropertiesAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.DeleteAsync("keep")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in bound.GetBlobsAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.GetOwnershipAsync()),
        };

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
        (await ReadAsync(container, "keep")).ShouldBe("original");
    }

    /// <summary>
    /// A transaction the kernel ended under its caller (its database was dropped while the session
    /// held it) reports <c>Faulted</c>, and the root bases order its refusal against the database's
    /// disposal and a canceled token (concrete-types plan §6.4): BEGIN refuses the open transaction
    /// with <c>COHDBB001</c> before it checks anything of the model, where the model reported the
    /// disposed database; both execute seams check a canceled token before the model reports the
    /// disposed database, which they still report for a live token, and so does BEGIN once the
    /// caller rolled the ended transaction back.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: a transaction the kernel ended refuses BEGIN with COHDBB001; the execute seams check a canceled token first")]
    public async Task BeginTransactionAsync_TransactionEndedByTheKernel_ShouldOrderTheRefusals()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        await AutocommitContainer.CreateAsync(database, "files");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");
        await engine.DropDatabaseAsync("test");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        var state = transaction.State;
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(canceled.Token));
        var canceledText = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("LIST files", null, canceled.Token));
        var text = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync("LIST files"));
        var canceledRequest = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(new BlobRequest(), canceled.Token));
        var request = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync(new BlobRequest()));
        await transaction.RollbackAsync();
        var beginAfterRollback = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.BeginTransactionAsync());

        // Assert
        state.ShouldBe(TransactionState.Faulted);
        begin.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        canceledText.CancellationToken.ShouldBe(canceled.Token);
        text.ShouldNotBeNull();
        canceledRequest.CancellationToken.ShouldBe(canceled.Token);
        request.ShouldNotBeNull();
        beginAfterRollback.ShouldNotBeNull();
        session.CurrentTransaction.ShouldBeNull();
    }

    /// <summary>
    /// A closed session is refused as closed before its database's disposal is checked, by BEGIN
    /// and both execute seams, which run the root base's checks first (concrete-types plan §6.4):
    /// the model checked the disposed database first and reported <see cref="ObjectDisposedException"/>.
    /// The session's container operations and the operations of a container bound to it, which
    /// check the database before the session, still report the disposed database.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: a closed session of a dropped database is refused as closed by BEGIN and the execute seams")]
    public async Task ExecuteAsync_ClosedSessionOfDroppedDatabase_ShouldRefuseAsClosedBeforeTheDisposedDatabase()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        await AutocommitContainer.CreateAsync(database, "files");
        var session = await database.CreateSessionAsync();
        var bound = await session.GetContainerAsync("files");
        await session.DisposeAsync();
        await engine.DropDatabaseAsync("test");

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("LIST files")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new BlobRequest())),
        };
        var sessionContainer = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.CreateContainerAsync("late"));
        var blob = await Should.ThrowAsync<ObjectDisposedException>(async () => await bound.GetPropertiesAsync("late"));

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        sessionContainer.ShouldNotBeNull();
        blob.ShouldNotBeNull();
    }

    /// <summary>
    /// An operation holds its session from its start to its end, through the root base's operation
    /// hold (concrete-types plan §6.4), which replaced the model's own reservation and operation
    /// set; a blob stream's operation ends when the stream is disposed. While an upload stream is
    /// open, another operation of a container bound to the session and a container operation of the
    /// session are refused with the model's message, and BEGIN with the base's "already active"
    /// message, for the model's former "A transaction or stream is already active on this
    /// session.". None of the refusals ends the upload, which publishes once it is disposed, and
    /// the session then runs operations and BEGIN again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: an open stream holds the session against another operation and BEGIN")]
    public async Task Operations_WhileAStreamIsOpen_ShouldBeRefusedAndLeaveItRunning()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var upload = await files.OpenWriteAsync("upload");
        await upload.WriteAsync(Encoding.UTF8.GetBytes("up"));

        // Act
        var blobOperation = await Should.ThrowAsync<DatabaseException>(async () => await files.OpenReadAsync("keep"));
        var containerOperation = await Should.ThrowAsync<DatabaseException>(async () => await session.GetContainerAsync("files"));
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        await upload.WriteAsync(Encoding.UTF8.GetBytes("load"));
        await upload.DisposeAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var uploaded = await ReadAsync(files, "upload");
        await transaction.CommitAsync();

        // Assert
        new[] { blobOperation, containerOperation }.ShouldAllBe(refusal =>
            refusal.Message == "Dispose the active blob stream before starting another operation on this session.");
        begin.Message.ShouldBe("A transaction or operation is already active on this session.");
        uploaded.ShouldBe("upload");
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(container)).ShouldBe(["keep", "upload"]);
    }

    /// <summary>
    /// A session's database is the unbound <see cref="BlobDatabase"/> (option B of the
    /// concrete-types plan, §6.6), and the container operations are the session's (owner decision
    /// 32 of 2026-10-06): a container the session created in its transaction is gone after the
    /// rollback, and one it created in autocommit is visible to another session. The database
    /// creates sessions after the session closed, and disposing it closes the database for every
    /// session, never the session itself; once that close ends the engine forgets the database, so
    /// its <c>OpenDatabaseAsync</c> opens it again from its files as a new instance, with its
    /// containers and blobs (owner decision 33, #1289). Before phase 4 a session returned a
    /// session-bound view whose disposal closed the session; until decision 32 the database had
    /// container operations of its own, which ran in autocommit outside the session; until decision
    /// 33 the engine refused the reopen with <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: the session's database is the unbound database, and the session runs the container operations")]
    public async Task Database_OfASession_ShouldBeTheUnboundDatabase()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var kept = await session.CreateContainerAsync("kept");
        await WriteAsync(kept, "item", "kept");
        var transaction = await session.BeginTransactionAsync();

        // Act: a container inside the transaction, which then rolls back; then the session and
        // the database close, and the engine opens the database again.
        var inside = await session.CreateContainerAsync("inside");
        var visibleToOther = new List<string>();
        await foreach (var visible in other.GetContainersAsync()) { visibleToOther.Add(visible.Name); }
        await transaction.RollbackAsync();
        var afterRollback = new List<string>();
        await foreach (var container in session.GetContainersAsync()) { afterRollback.Add(container.Name); }
        await session.DisposeAsync();
        await using var afterClose = await session.Database.CreateSessionAsync();
        await other.Database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test");
        await using var reader = await reopened.CreateSessionAsync();
        var afterReopen = new List<string>();
        await foreach (var container in reader.GetContainersAsync()) { afterReopen.Add(container.Name); }
        string item = await ReadAsync(await reader.GetContainerAsync("kept"), "item");

        // Assert
        session.Database.ShouldBeSameAs(database);
        database.Engine.ShouldBeSameAs(engine);
        kept.Name.ShouldBe("kept");
        inside.Name.ShouldBe("inside");
        visibleToOther.ShouldBe(["kept"]);
        afterRollback.ShouldBe(["kept"]);
        afterClose.State.ShouldBe(SessionState.Open);
        other.State.ShouldBe(SessionState.Open);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateSessionAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await other.GetContainerAsync("kept"));
        reopened.ShouldNotBeSameAs(database);
        afterReopen.ShouldBe(["kept"]);
        item.ShouldBe("kept");
    }

    /// <summary>
    /// A rollback whose abort record cannot be written still ends the transaction and releases the
    /// database writer lock (#1226). Since #1252 the rollback appends the record to the journal's
    /// append buffer, and it reaches the file with the next drain: here the commit of another
    /// session's write, which proceeds because the writer lock is free. That drain fails, which
    /// takes the database offline (#1243's rule): the write is reported unconfirmed, the next
    /// operation is refused as offline, and the reopen keeps what committed before and nothing of
    /// either transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a rollback whose abort record cannot be written still ends the transaction, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var otherFiles = await other.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();

        // A delete that matches nothing takes the database writer lock and writes no version, so
        // the abort record is the rollback's only journal append.
        (await files.DeleteAsync("missing")).ShouldBeFalse();

        // Act: the rollback buffers the abort record; the other session's write needs the writer
        // lock, and its commit drains the record, which fails.
        int unspentAfterTheRollback;
        int unspent;
        DatabaseTransactionCommitUnconfirmedException lost;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            unspentAfterTheRollback = failures.Remaining;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            lost = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await WriteAsync(otherFiles, "other", "other", timeout.Token));
            unspent = failures.Remaining;
        }

        var refused = await Should.ThrowAsync<DatabaseOfflineException>(async () => await WriteAsync(otherFiles, "after", "after"));
        await other.DisposeAsync();
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");

        // Assert: the rollback wrote nothing and ended the transaction; the drain that carried its
        // record failed.
        unspentAfterTheRollback.ShouldBe(1);
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        StorageOfflineException.Find(lost).ShouldNotBeNull();
        refused.InnerException.ShouldNotBeNull();
        (await NamesAsync(await AutocommitContainer.GetAsync(recovered, "files"))).ShouldBe(["keep"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other
    /// and COMMIT commits nothing. The record is lost with the drain that carries it (#1252), here
    /// the commit of the session's next write, which takes the database offline: the COMMIT and a
    /// repeated rollback are then refused as offline before they start.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        (await files.DeleteAsync("missing")).ShouldBeFalse();
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await WriteAsync(files, "next", "next"));
            unspent = failures.Remaining;
        }

        // Act
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(await AutocommitContainer.GetAsync(recovered, "files"))).ShouldBe(["keep"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        await AutocommitContainer.CreateAsync(database, "files");
        await using (var session = await database.CreateSessionAsync())
        {
            var scoped = await session.GetContainerAsync("files");
            await BlobEngineTests.WriteAsync(scoped, "kept", "kept"u8.ToArray());
            var transaction = await session.BeginTransactionAsync();
            await BlobEngineTests.WriteAsync(scoped, "rolled", "rolled"u8.ToArray());
            await BlobEngineTests.WriteAsync(scoped, "kept", "overwritten"u8.ToArray());

            // Another storage bracket holds every page, through the close: the undo's bracket
            // cannot touch the first page it undoes, so it rolls itself back and the undo is
            // deferred. (Until #1252 a failed journal write was the fault; a journal write
            // failure now takes the database offline.)
            _ = PageWriteLockHolder.LockEveryPage(database.DataStorage); // abandoned with the storage
            await transaction.RollbackAsync();
            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close retries the undo, which fails the same way.
        var closeFailure = Should.Throw<AggregateException>(() => engine.Dispose());

        await using var reopened = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        var files = await AutocommitContainer.GetAsync(recovered, "files");

        // Assert: the new blob is gone and the overwrite is undone.
        closeFailure.Flatten().InnerExceptions.ShouldContain(error => error is StorageTransactionException);
        (await files.GetPropertiesAsync("rolled")).ShouldBeNull();
        (await BlobEngineTests.ReadAsync(files, "kept")).ShouldBe("kept"u8.ToArray());
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
        await using var engine = BlobDatabaseEngine.Create("blob-engine", QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("test");
        var container = await AutocommitContainer.CreateAsync(database, "files");
        await WriteAsync(container, "keep", "original");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var otherFiles = await other.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "rolled", "rolled");
        await WriteAsync(files, "keep", "overwritten");

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred.
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            locked = holder.Pages;
        }
        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = WriteAsync(otherFiles, "other", "other");
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        locked.ShouldBeGreaterThan(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        otherProceededBeforeTheUndo.ShouldBeFalse();
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await NamesAsync(container)).ShouldBe(["keep", "other"]);
        (await ReadAsync(container, "keep")).ShouldBe("original");
    }

    /// <summary>
    /// The kernel's unconfirmed commit crosses the model boundary as the area root's, its message led by
    /// the model's offline code like every other unconfirmed commit (owner decision 24, #1272); the
    /// kernel's exception is kept as the inner exception. Before #1272 this path kept the kernel's
    /// message, uncoded.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a commit record that could not be made durable crosses the boundary as committed-unconfirmed, led by COHDBB002")]
    public async Task TranslateKernelFailure_CommitUnconfirmed_ShouldBecomeTheCodedCommitUnconfirmedException()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("test");
        var kernel = new TransactionCommitUnconfirmedException("Transaction 7 committed, but its commit record could not be made durable.", new IOException("flush"));

        // Act
        var translated = database.TranslateKernelFailure(kernel);

        // Assert: not an abort, so a caller never retries work that committed.
        var unconfirmed = translated.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        unconfirmed.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        unconfirmed.Message.ShouldBe(
            "COHDBB002: Database 'test' went offline while a transaction was committing: the durable flush of its commit record failed (flush) " +
            "after the transaction's commit record was written. The transaction may or may not have committed; do not retry it. Reopen the " +
            "database (OpenDatabaseAsync): its recovery keeps the commit if its record reached stable storage and discards it if not, and " +
            "reading the data back then tells which.");
        unconfirmed.InnerException.ShouldBeSameAs(kernel);
    }

    // The engine's own maintenance workers stay out of the way: these tests drive the purge pass
    // and the close themselves.
    private static BlobDatabaseEngineOptions QuietOptions(FaultInjectingJournalStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),

        // The deferred-undo retry stays out of the way too: these tests drive the purge pass.
        DeferredUndoRetryDelay = TimeSpan.FromHours(1),
    };

    /// <summary>
    /// A commit whose record cannot be written is never acknowledged (#1252): the commit drains the
    /// journal's append buffer through its record before it returns, in every durability mode, and
    /// when that drain fails the database goes offline (#1243's rule) and the commit crosses the
    /// boundary as committed-unconfirmed, its outcome left to the reopen's recovery. The record
    /// never reached the file here, so the reopen holds nothing of the transaction, and a later
    /// rollback is refused as offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Transaction: a commit whose record cannot be written is unconfirmed, and the database goes offline")]
    public async Task CommitAsync_CommitRecordCannotBeWritten_ShouldBeUnconfirmedAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        await AutocommitContainer.CreateAsync(database, "files");
        var session = await database.CreateSessionAsync();
        var files = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await WriteAsync(files, "pending", "pending");

        // Act: the commit's drain carries its commit record, and that write fails.
        DatabaseTransactionCommitUnconfirmedException error;
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.CommitTransaction))
        {
            error = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await transaction.CommitAsync());
            unspent = failures.Remaining;
        }
        var stateAfterCommit = transaction.State;
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = BlobDatabaseEngine.Create("blob-engine", QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");

        // Assert
        unspent.ShouldBe(0);
        StorageOfflineException.Find(error).ShouldNotBeNull();
        error.Message.ShouldStartWith("COHDBB002: Database 'test' went offline while a transaction was committing", Case.Sensitive);
        stateAfterCommit.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await NamesAsync(await AutocommitContainer.GetAsync(recovered, "files"))).ShouldBeEmpty();
    }

    private static async Task FailAsync(string failure, BlobDatabaseSession session, BlobContainer files)
    {
        switch (failure)
        {
            case "container":
                // The catalog has no such container: the operation fails inside its transaction.
                await session.GetContainerAsync("missing");
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

    private static async Task WriteAsync(BlobContainer container, string name, string content, CancellationToken cancellationToken = default)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static async Task WriteAsync(AutocommitContainer container, string name, string content, CancellationToken cancellationToken = default)
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

    private static async Task<string> ReadAsync(AutocommitContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<List<string>> NamesAsync(BlobContainer container)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync()) { names.Add(blob.Name); }
        return names;
    }

    private static async Task<List<string>> NamesAsync(AutocommitContainer container)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync()) { names.Add(blob.Name); }
        return names;
    }
}
