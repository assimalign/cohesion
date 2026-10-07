using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// A statement that fails inside an explicit transaction aborts the whole transaction (#1225, the
/// contract #1188 set for Graph): the session keeps it as <see cref="TransactionState.Faulted"/> and
/// refuses every later statement and BEGIN with COHDBD001 until the caller rolls back, and nothing
/// the transaction wrote survives. A started rollback always ends the transaction (#1226): a lost
/// abort record changes nothing, and an undo the journal rejects is deferred with the writer lock
/// held until the version-purge pass completes it, even against a late operation of the ended
/// transaction, and is scrubbed at the next open when it still fails at close. Documents has no
/// wire server or client, so every case runs in process.
/// </summary>
public sealed class DocumentTransactionFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The issue's reproduction: a failure, then a write, then ROLLBACK leaves the documents unchanged.</summary>
    /// <param name="failure">The kind of statement that fails inside the transaction.</param>
    /// <param name="isolation">The isolation level the explicit transaction runs under.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a failed statement, a later write and ROLLBACK leave the documents unchanged")]
    [InlineData("version", IsolationLevel.Snapshot)]
    [InlineData("query", IsolationLevel.Snapshot)]
    [InlineData("parse", IsolationLevel.Snapshot)]
    [InlineData("system", IsolationLevel.Snapshot)]
    [InlineData("version", IsolationLevel.ReadCommitted)]
    [InlineData("query", IsolationLevel.ReadCommitted)]
    [InlineData("parse", IsolationLevel.ReadCommitted)]
    public async Task Statement_FailureThenWriteThenRollback_ShouldLeaveDocumentsUnchanged(string failure, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var keep = await collection.PutAsync(session, "keep", Doc("keep", "\"v\":1"));
        var transaction = await session.BeginTransactionAsync(isolation);
        await collection.PutAsync(session, "pending", Doc("pending"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await FailAsync(failure, collection, session, keep));
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await collection.DeleteAsync(session, "keep"));
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        await transaction.RollbackAsync();

        // Assert
        refused.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        refused.Message.ShouldContain(error.Message, Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(error);
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        var stored = (await (await observer.GetCollectionAsync("items")).GetAsync(observer, "keep")).ShouldNotBeNull();
        stored.Version.ShouldBe(keep.Version);
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe("{\"id\":\"keep\",\"v\":1}");
        (await IdsAsync(observer)).ShouldBe(["keep"]);
    }

    /// <summary>Every statement surface refuses work on a faulted transaction, and a rollback restores autocommit and BEGIN.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a faulted transaction refuses every operation until rollback")]
    public async Task Operations_OnFaultedTransaction_ShouldBeRefusedUntilRollback()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        var refusals = new List<Exception>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await collection.GetAsync(session, "keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(session, "late", Doc("late"))),
            await Should.ThrowAsync<DatabaseException>(async () => await collection.DeleteAsync(session, "keep")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM items")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT FROM")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT * FROM items"))),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE INDEX ix ON items (v)")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.CreateCollectionAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.DropCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in session.GetCollectionsAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
        };
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();

        // Assert
        refusals.ShouldAllBe(error => error.Message.StartsWith("COHDBD001", StringComparison.Ordinal));
        (await collection.GetAsync(session, "keep")).ShouldNotBeNull();
        await using (var next = await session.BeginTransactionAsync())
        {
            await collection.PutAsync(session, "next", Doc("next"));
            await next.CommitAsync();
        }
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(session)).ShouldBe(["keep", "next"]);
    }

    /// <summary>COMMIT on a faulted transaction fails with COHDBD001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after a failed statement fails and commits nothing")]
    public async Task CommitAsync_AfterFailedStatement_ShouldThrowAndCommitNothing()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert: every commit reports the cause, as the model's own state machine did.
        error.Message.ShouldStartWith("COHDBD001: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        error.InnerException.ShouldBeSameAs(failure);
        repeated.Message.ShouldBe(error.Message);
        repeated.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        // The client's rollback in a catch block after the failed commit is a no-op, not a second error.
        await transaction.RollbackAsync();
        (await IdsAsync(session)).ShouldBeEmpty();
        await collection.PutAsync(session, "after", Doc("after"));
        (await IdsAsync(session)).ShouldBe(["after"]);
    }

    /// <summary>Disposing a faulted transaction ends it without throwing, and the session is idle again.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: disposing a faulted transaction ends it")]
    public async Task DisposeAsync_FaultedTransaction_ShouldEndTransactionAndReturnSessionToAutocommit()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        await transaction.DisposeAsync();
        await collection.PutAsync(session, "after", Doc("after"));

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(session)).ShouldBe(["after"]);
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
    }

    /// <summary>A statement that wrote content chunks before failing leaves none of them, nor earlier statements' work.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a partially applied statement leaves nothing after rollback")]
    public async Task PutAsync_PartiallyAppliedStatementFails_ShouldUndoTheWholeTransaction()
    {
        // Arrange: the indexed value outgrows the index key only after the new content chunks are written.
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE INDEX by_a ON items (a)");
        await collection.PutAsync(session, "keep", Doc("keep", "\"a\":\"k\""));
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "earlier", Doc("earlier", "\"a\":\"e\""));

        // Act
        var error = await Should.ThrowAsync<Exception>(async () =>
            await collection.PutAsync(session, "keep", Doc("keep", "\"a\":\"" + new string('x', 20_000) + "\"")));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldContain("1024-byte key limit", Case.Sensitive);
        faultedState.ShouldBe(TransactionState.Faulted);
        (await IdsAsync(session)).ShouldBe(["keep"]);
        var stored = (await collection.GetAsync(session, "keep")).ShouldNotBeNull();
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe("{\"id\":\"keep\",\"a\":\"k\"}");
        (await IdsAsync(session, "WHERE a = 'k'")).ShouldBe(["keep"]);
        (await IdsAsync(session, "WHERE a = 'e'")).ShouldBeEmpty();
    }

    /// <summary>A faulted transaction holds no writer lock: another session writes while it awaits rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a faulted transaction releases the writer lock at once")]
    public async Task Statement_FaultedTransaction_ShouldNotBlockOtherWriters()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var failed = await database.CreateSessionAsync();
        var collection = await failed.CreateCollectionAsync("items");
        await using var other = await database.CreateSessionAsync();
        var otherItems = await other.GetCollectionAsync("items");
        await using var transaction = await failed.BeginTransactionAsync();
        await collection.PutAsync(failed, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await failed.ExecuteAsync("SELECT * FROM missing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await otherItems.PutAsync(other, "other", Doc("other"), cancellationToken: timeout.Token);

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await IdsAsync(other)).ShouldBe(["other"]);
    }

    /// <summary>An autocommit failure ends only its own statement: the session stays idle and usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: autocommit failures leave the session usable")]
    public async Task Statement_AutocommitFailure_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await collection.PutAsync(session, "keep", Doc("keep"));

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));
        await collection.PutAsync(session, "after", Doc("after"));

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(session)).ShouldBe(["after", "keep"]);
        await using var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
    }

    /// <summary>A refused statement does not change the faulted transaction or its recorded cause.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: refusals keep the original failure as the cause")]
    public async Task Statement_RepeatedRefusals_ShouldKeepOriginalCause()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        var first = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM other"));
        var second = await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(session, "late", Doc("late")));

        // Assert
        first.InnerException.ShouldBeSameAs(failure);
        second.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.Faulted);
    }

    /// <summary>A statement canceled while it waits for the writer lock aborts its transaction and releases the session.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a canceled statement aborts the explicit transaction")]
    public async Task Statement_CanceledInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var blocker = await database.CreateSessionAsync();
        var collection = await blocker.CreateCollectionAsync("items");
        await using var waiting = await database.CreateSessionAsync();
        var waitingItems = await waiting.GetCollectionAsync("items");
        var blocking = await blocker.BeginTransactionAsync();
        await collection.PutAsync(blocker, "blocker", Doc("blocker"));
        var transaction = await waiting.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = waitingItems.PutAsync(waiting, "waiting", Doc("waiting"), cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act: the refusal is immediate; the timeout only turns a regression into a failure, not a hang.
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refused = await Should.ThrowAsync<DatabaseException>(async () =>
            await waitingItems.PutAsync(waiting, "late", Doc("late"), cancellationToken: timeout.Token));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();
        await blocking.CommitAsync();

        // Assert: a canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        faultedState.ShouldBe(TransactionState.Faulted);
        (await IdsAsync(waiting)).ShouldBe(["blocker"]);
    }

    /// <summary>Closing a session with a faulted transaction ends the transaction without an error.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: closing the session ends a faulted transaction")]
    public async Task DisposeAsync_SessionWithFaultedTransaction_ShouldEndTransaction()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        await using var observer = await database.CreateSessionAsync();
        (await IdsAsync(observer)).ShouldBeEmpty();
    }

    /// <summary>
    /// A commit after the session closed under an open transaction fails with COHDBD001 naming why
    /// nothing committed, whichever is asked first, and commits nothing; a rollback afterwards is a
    /// no-op. For an active transaction the cause is the root session base's teardown cause, "The
    /// session closed before the transaction ended." (concrete-types plan §6.4), for the model's
    /// former "The document session closed before the transaction ended."; for one a statement
    /// aborted before the session closed it is the statement's failure, which the base's teardown
    /// keeps (the model's own close did the same before phase 4).
    /// </summary>
    /// <param name="aborted">True when a statement aborted the transaction before the session closed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after the session closed reports COHDBD001 naming why nothing committed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted(bool aborted)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        DatabaseException? failure = aborted
            ? await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"))
            : null;

        // Act
        await session.DisposeAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldStartWith("COHDBD001: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
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
        await using var observer = await database.CreateSessionAsync();
        (await IdsAsync(observer)).ShouldBeEmpty();
    }

    /// <summary>Failures that come before a statement starts leave the transaction active.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: argument and request validation leave the transaction active")]
    public async Task Validation_BeforeStatementStarts_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var otherDatabase = await engine.CreateDatabaseAsync("other");
        await using var session = await database.CreateSessionAsync();
        await using var foreign = await otherDatabase.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

        // Act
        var rejections = new List<Exception>
        {
            await Should.ThrowAsync<ArgumentException>(async () => await collection.PutAsync(session, " ", Doc("blank"))),
            await Should.ThrowAsync<ArgumentException>(async () => await collection.GetAsync(session, "")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.CreateCollectionAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" ")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.CreateCollectionAsync("COHESION_SCHEMA.INDEXES")),
            await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(foreign, "x", Doc("x"))),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new ForeignRequest())),
        };
        var state = transaction.State;
        await transaction.CommitAsync();

        // Assert
        rejections.ShouldAllBe(error => !error.Message.StartsWith("COHDBD001", StringComparison.Ordinal));
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await IdsAsync(session)).ShouldBe(["pending"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var rolledBack = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "discarded", Doc("discarded"));
        await rolledBack.RollbackAsync();
        var committed = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "kept", Doc("kept"));
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
        (await IdsAsync(session)).ShouldBe(["kept"]);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "first", Doc("first"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await collection.PutAsync(session, "second", Doc("second"));
        await transaction.CommitAsync();

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await IdsAsync(session)).ShouldBe(["first", "second"]);
    }

    /// <summary>
    /// A commit of a transaction its caller already rolled back is refused by state, with the root
    /// base's message (concrete-types plan §6.4), the one the model carried.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after ROLLBACK is refused with the transaction's state")]
    public async Task CommitAsync_AfterRollback_ShouldBeRefusedWithTheState()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "discarded", Doc("discarded"));
        await transaction.RollbackAsync();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        refusal.Message.ShouldBe("The transaction is RolledBack.");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await IdsAsync(session)).ShouldBeEmpty();
    }

    /// <summary>
    /// Closing a session aborts the statement running on it, and the statement's abort is the
    /// cause a later commit of its transaction reports with <c>COHDBD001</c>: the teardown's own
    /// cause does not replace it. The model's teardown did the same; since the concrete-types plan's
    /// phase 4 the session's root base runs it (§6.4), so the order is pinned here.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: closing a session aborts its running statement, whose cause a later COMMIT reports")]
    public async Task DisposeAsync_SessionWithRunningStatement_ShouldAbortItAndReportItsCause()
    {
        // Arrange: another transaction holds the writer lock, so the session's statement waits.
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var blocker = await database.CreateSessionAsync();
        var collection = await blocker.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var sessionItems = await session.GetCollectionAsync("items");
        var blocking = await blocker.BeginTransactionAsync();
        await collection.PutAsync(blocker, "blocker", Doc("blocker"));
        var transaction = await session.BeginTransactionAsync();
        var pending = sessionItems.PutAsync(session, "waiting", Doc("waiting")).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await session.DisposeAsync();
        var statement = await Should.ThrowAsync<Exception>(async () => await pending.WaitAsync(Timeout));
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await blocking.CommitAsync();

        // Assert
        statement.ShouldNotBeOfType<TimeoutException>();
        commit.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        commit.Message.ShouldEndWith("Cause: The document session closed while the operation was running.", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        await using var observer = await database.CreateSessionAsync();
        (await IdsAsync(observer)).ShouldBe(["blocker"]);
    }

    /// <summary>
    /// A commit while a statement of the transaction still runs is refused with the root base's
    /// message (concrete-types plan §6.4), for the model's former "Dispose every document operation
    /// before committing its transaction.", and leaves the transaction active: it commits once the
    /// statement completed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT while a statement of the transaction runs is refused and leaves it active")]
    public async Task CommitAsync_WhileStatementRuns_ShouldBeRefusedAndLeaveTheTransactionActive()
    {
        // Arrange: another transaction holds the writer lock, so the statement waits.
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var blocker = await database.CreateSessionAsync();
        var collection = await blocker.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var sessionItems = await session.GetCollectionAsync("items");
        var blocking = await blocker.BeginTransactionAsync();
        await collection.PutAsync(blocker, "blocker", Doc("blocker"));
        var transaction = await session.BeginTransactionAsync();
        var pending = sessionItems.PutAsync(session, "waiting", Doc("waiting")).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var stateAfterRefusal = transaction.State;
        await blocking.CommitAsync();
        (await pending.WaitAsync(Timeout)).Id.Value.ShouldBe("waiting");
        await transaction.CommitAsync();

        // Assert
        refusal.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        stateAfterRefusal.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await IdsAsync(session)).ShouldBe(["blocker", "waiting"]);
    }

    /// <summary>
    /// BEGIN on a session whose transaction is active is refused with the root base's one message
    /// before the model's isolation-level refusal, and a closed session before both (concrete-types
    /// plan §6.4, BEGIN's refusal order): the model refused an unsupported isolation level first.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: BEGIN is refused for a closed session, then an active transaction, before the isolation level")]
    public async Task BeginTransactionAsync_WhileActiveOrClosed_ShouldRefuseBeforeTheIsolationLevel()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
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
        unsupported.Message.ShouldBe("The document engine supports Snapshot and ReadCommitted isolation.");
        closed.Message.ShouldBe("The session is closed.");
    }

    /// <summary>
    /// A closed session refuses BEGIN, both execute seams, its own collection operations and the
    /// document operations of a collection with the root base's message (concrete-types plan §6.4),
    /// for the model's former "The document session is closed.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: a closed session refuses every operation with one message")]
    public async Task ExecuteAsync_OnClosedSession_ShouldRefuseWithOneMessage()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var bound = await session.GetCollectionAsync("items");
        await session.DisposeAsync();

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id FROM items")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT id FROM items"))),
            await Should.ThrowAsync<DatabaseException>(async () => await session.CreateCollectionAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.DropCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in session.GetCollectionsAsync()) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(session, "late", Doc("late"))),
            await Should.ThrowAsync<DatabaseException>(async () => await bound.GetAsync(session, "late")),
        };

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
    }

    /// <summary>
    /// A transaction the kernel ended under its caller (its database was dropped while the session
    /// held it) reports <c>Faulted</c>, and the root bases order its refusal against the database's
    /// disposal and a canceled token (concrete-types plan §6.4): BEGIN refuses the open transaction
    /// with <c>COHDBD001</c> before it checks anything of the model, where the model reported the
    /// disposed database; both execute seams check a canceled token before the model reports the
    /// disposed database, which they still report for a live token, and so does BEGIN once the
    /// caller rolled the ended transaction back.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: a transaction the kernel ended refuses BEGIN with COHDBD001; the execute seams check a canceled token first")]
    public async Task BeginTransactionAsync_TransactionEndedByTheKernel_ShouldOrderTheRefusals()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        await engine.DropDatabaseAsync("test");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        var state = transaction.State;
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(canceled.Token));
        var canceledText = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT id FROM items", null, canceled.Token));
        var text = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync("SELECT id FROM items"));
        var canceledRequest = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT id FROM items"), canceled.Token));
        var request = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT id FROM items")));
        await transaction.RollbackAsync();
        var beginAfterRollback = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.BeginTransactionAsync());

        // Assert
        state.ShouldBe(TransactionState.Faulted);
        begin.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
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
    /// The session's collection operations and a collection's document operations, which check the
    /// database before the session, still report the disposed database.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: a closed session of a dropped database is refused as closed by BEGIN and the execute seams")]
    public async Task ExecuteAsync_ClosedSessionOfDroppedDatabase_ShouldRefuseAsClosedBeforeTheDisposedDatabase()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await session.DisposeAsync();
        await engine.DropDatabaseAsync("test");

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id FROM items")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT id FROM items"))),
        };
        var sessionCollection = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.CreateCollectionAsync("late"));
        var document = await Should.ThrowAsync<ObjectDisposedException>(async () => await collection.GetAsync(session, "late"));

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        sessionCollection.ShouldNotBeNull();
        document.ShouldNotBeNull();
    }

    /// <summary>
    /// A statement holds its session from its start to its end, through the root base's operation
    /// hold (concrete-types plan §6.4), which replaced the model's own reservation and operation
    /// set: while one waits for the writer lock, a second statement on the session through either
    /// seam, a collection operation of the session and a document operation are refused with the
    /// model's message, and BEGIN with the base's "already active" message. None of the refusals
    /// ends the waiting statement, which completes once the lock is free, and the session then runs
    /// statements again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: a running statement holds the session against another statement and BEGIN")]
    public async Task ExecuteAsync_WhileAnotherStatementRuns_ShouldBeRefusedAndLeaveItRunning()
    {
        // Arrange: another transaction holds the writer lock, so the session's statement waits.
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var blocker = await database.CreateSessionAsync();
        var collection = await blocker.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var sessionItems = await session.GetCollectionAsync("items");
        var blocking = await blocker.BeginTransactionAsync();
        await collection.PutAsync(blocker, "blocker", Doc("blocker"));
        var pending = sessionItems.PutAsync(session, "waiting", Doc("waiting")).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        var statement = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id FROM items"));
        var request = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql("SELECT id FROM items")));
        var collectionOperation = await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("items"));
        var documentOperation = await Should.ThrowAsync<DatabaseException>(async () => await sessionItems.GetAsync(session, "blocker"));
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        bool stillWaiting = !pending.IsCompleted;
        await blocking.CommitAsync();
        var completed = await pending.WaitAsync(Timeout);

        // Assert
        new[] { statement, request, collectionOperation, documentOperation }.ShouldAllBe(refusal =>
            refusal.Message == "Dispose the active document operation before starting another operation on this session.");
        begin.Message.ShouldBe("A transaction or operation is already active on this session.");
        stillWaiting.ShouldBeTrue();
        completed.Id.Value.ShouldBe("waiting");
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(session)).ShouldBe(["blocker", "waiting"]);
    }

    /// <summary>
    /// A session's database is the unbound <see cref="DocumentDatabase"/> (option B of the
    /// concrete-types plan, §6.6), and the collection operations are the session's (owner decision
    /// 32 of 2026-10-06): a collection the session created in its transaction is gone after the
    /// rollback, and one it created in autocommit is visible to another session. The database
    /// creates sessions after the session closed, and disposing it closes the database for every
    /// session, never the session itself; once that close ends the engine forgets the database, so
    /// its <c>OpenDatabaseAsync</c> opens it again from its files as a new instance, with its
    /// collections (owner decision 33, #1289). Before phase 4 a session returned a session-bound
    /// view whose disposal closed the session; until decision 32 the database had collection
    /// operations of its own, which ran in autocommit outside the session; until decision 33 the
    /// engine refused the reopen with <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Session: the session's database is the unbound database, and the session runs the collection operations")]
    public async Task Database_OfASession_ShouldBeTheUnboundDatabase()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var kept = await session.CreateCollectionAsync("kept");
        var transaction = await session.BeginTransactionAsync();

        // Act: a collection inside the transaction, which then rolls back; then the session and
        // the database close, and the engine opens the database again.
        var inside = await session.CreateCollectionAsync("inside");
        var visibleToOther = new List<string>();
        await foreach (var visible in other.GetCollectionsAsync()) { visibleToOther.Add(visible.Name); }
        await transaction.RollbackAsync();
        var afterRollback = new List<string>();
        await foreach (var collection in session.GetCollectionsAsync()) { afterRollback.Add(collection.Name); }
        await session.DisposeAsync();
        await using var afterClose = await session.Database.CreateSessionAsync();
        await other.Database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test");
        await using var reader = await reopened.CreateSessionAsync();
        var afterReopen = new List<string>();
        await foreach (var collection in reader.GetCollectionsAsync()) { afterReopen.Add(collection.Name); }

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
        await Should.ThrowAsync<ObjectDisposedException>(async () => await other.ExecuteAsync("SELECT id FROM kept"));
        reopened.ShouldNotBeSameAs(database);
        afterReopen.ShouldBe(["kept"]);
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
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a rollback whose abort record cannot be written still ends the transaction, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var other = await database.CreateSessionAsync();
        var otherItems = await other.GetCollectionAsync("items");
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();

        // A delete that matches nothing takes the database writer lock and writes no version, so
        // the abort record is the rollback's only journal append.
        (await collection.DeleteAsync(session, "missing")).ShouldBeFalse();

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
            lost = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(
                async () => await otherItems.PutAsync(other, "other", Doc("other"), cancellationToken: timeout.Token));
            unspent = failures.Remaining;
        }

        var refused = await Should.ThrowAsync<DatabaseOfflineException>(async () => await otherItems.PutAsync(other, "after", Doc("after")));
        await other.DisposeAsync();
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the rollback wrote nothing and ended the transaction; the drain that carried its
        // record failed.
        unspentAfterTheRollback.ShouldBe(1);
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        StorageOfflineException.Find(lost).ShouldNotBeNull();
        refused.InnerException.ShouldNotBeNull();
        (await IdsAsync(observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other
    /// and COMMIT commits nothing. The record is lost with the drain that carries it (#1252), here
    /// the commit of the session's next write, which takes the database offline: the COMMIT and a
    /// repeated rollback are then refused as offline before they start.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();
        (await collection.DeleteAsync(session, "missing")).ShouldBeFalse();
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await collection.PutAsync(session, "next", Doc("next")));
            unspent = failures.Remaining;
        }

        // Act
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// A commit whose record cannot be written is never acknowledged (#1252): the commit drains the
    /// journal's append buffer through its record before it returns, in every durability mode, and
    /// when that drain fails the database goes offline (#1243's rule) and the commit crosses the
    /// boundary as committed-unconfirmed, its outcome left to the reopen's recovery. The record
    /// never reached the file here, so the reopen holds nothing of the transaction, and a later
    /// rollback is refused as offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a commit whose record cannot be written is unconfirmed, and the database goes offline")]
    public async Task CommitAsync_CommitRecordCannotBeWritten_ShouldBeUnconfirmedAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

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
        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        StorageOfflineException.Find(error).ShouldNotBeNull();
        error.Message.ShouldStartWith("COHDBD002: Database 'test' went offline while a transaction was committing", Case.Sensitive);
        stateAfterCommit.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(observer)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a late operation of a rolled-back transaction does not release its deferred writer lock")]
    public async Task LockWriterAsync_LateOperationOfRolledBackTransaction_ShouldNotReleaseItsDeferredWriterLock()
    {
        // Arrange: a rollback whose undo cannot touch the pages another storage bracket holds,
        // and a waiting writer.
        await using var engine = DocumentDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using var other = await database.CreateSessionAsync();
        var otherItems = await other.GetCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "rolled", "1"u8.ToArray());
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            locked = holder.Pages;
        }

        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = otherItems.PutAsync(other, "other", "2"u8.ToArray()).AsTask();

        // Act: the late operation gets the lock its transaction still holds, finds the
        // transaction ended, and cleans up; then the purge pass completes the undo.
        var late = await Should.ThrowAsync<DatabaseException>(async () => await database.LockWriterAsync(transaction.Context, CancellationToken.None));
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.Checkpoint();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(Timeout);

        // Assert
        locked.ShouldBeGreaterThan(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        late.Message.ShouldContain("ended while waiting for the writer lock");
        otherProceededBeforeTheUndo.ShouldBeFalse();
        (await otherItems.GetAsync(other, "rolled")).ShouldBeNull();
        (await otherItems.GetAsync(other, "other")).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        await using (var session = await database.CreateSessionAsync())
        {
            var collection = await session.CreateCollectionAsync("items");
            await collection.PutAsync(session, "kept", "1"u8.ToArray());
            var transaction = await session.BeginTransactionAsync();
            await collection.PutAsync(session, "rolled", "2"u8.ToArray());
            await collection.DeleteAsync(session, "kept");

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

        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();
        var observerItems = await observer.GetCollectionAsync("items");

        // Assert: the insert is gone and the delete is undone.
        closeFailure.Flatten().InnerExceptions.ShouldContain(error => error is StorageTransactionException);
        (await observerItems.GetAsync(observer, "rolled")).ShouldBeNull();
        (await observerItems.GetAsync(observer, "kept")).ShouldNotBeNull();
    }

    /// <summary>
    /// A checkpoint truncates the journal before it appends the record that lists the transactions
    /// still in flight. When that record's write fails while a rolled-back transaction's undo is
    /// deferred, the journal no longer names the transaction, and its document sits in the flushed
    /// data pages. The storage's checkpoint anchor, written before the truncation, still names it,
    /// so the next open scrubs it (#1226 integration review; before the anchor the rolled-back
    /// document came back as committed). Since #1252 the failed write also takes the database
    /// offline, so the close writes nothing and cannot retry the undo.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a checkpoint record lost while the undo is deferred does not resurrect the document")]
    public async Task Checkpoint_RecordLostWhileTheUndoIsDeferred_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange: a rolled-back transaction whose undo could not touch the pages another storage
        // bracket held, so it is deferred.
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("test");
        await using (var session = await database.CreateSessionAsync())
        {
            var collection = await session.CreateCollectionAsync("items");
            await collection.PutAsync(session, "kept", Doc("kept"));
            var transaction = await session.BeginTransactionAsync();
            await collection.PutAsync(session, "rolled", Doc("rolled"));
            using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
            {
                await transaction.RollbackAsync();
            }

            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the journal holds every record, so the checkpoint's first journal write is its own
        // record after the truncation, which fails and takes the database offline; the close then
        // writes nothing.
        database.DataStorage.WriteAheadJournal.Flush();
        int checkpointFailuresLeft;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            Should.Throw<StorageOfflineException>(() => database.Coordinator.Checkpoint());
            checkpointFailuresLeft = failures.Remaining;
        }

        bool offline = database.IsOffline;
        engine.Dispose();

        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: only the committed document is there.
        checkpointFailuresLeft.ShouldBe(0);
        offline.ShouldBeTrue();
        (await IdsAsync(observer)).ShouldBe(["kept"]);
    }

    /// <summary>
    /// The kernel's unconfirmed commit crosses the model boundary as the area root's, its message led by
    /// the model's offline code like every other unconfirmed commit (owner decision 24, #1272); the
    /// kernel's exception is kept as the inner exception. Before #1272 this path kept the kernel's
    /// message, uncoded.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a commit record that could not be made durable crosses the boundary as committed-unconfirmed, led by COHDBD002")]
    public async Task TranslateKernelFailure_CommitUnconfirmed_ShouldBecomeTheCodedCommitUnconfirmedException()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var kernel = new TransactionCommitUnconfirmedException("Transaction 7 committed, but its commit record could not be made durable.", new IOException("flush"));

        // Act
        var translated = database.TranslateKernelFailure(kernel);

        // Assert: not an abort, so a caller never retries work that committed.
        var unconfirmed = translated.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        unconfirmed.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        unconfirmed.Message.ShouldBe(
            "COHDBD002: Database 'test' went offline while a transaction was committing: the durable flush of its commit record failed (flush) " +
            "after the transaction's commit record was written. The transaction may or may not have committed; do not retry it. Reopen the " +
            "database (OpenDatabaseAsync): its recovery keeps the commit if its record reached stable storage and discards it if not, and " +
            "reading the data back then tells which.");
        unconfirmed.InnerException.ShouldBeSameAs(kernel);
    }

    /// <summary>
    /// The two end rules meet (#1225 with #1226): a statement applying when its transaction's
    /// rollback starts finishes its bracket first, the rollback's undo then fails and is deferred,
    /// and the ended transaction's next statement is refused even though the kernel still tracks
    /// its writer. Nothing the transaction wrote, including the statement the rollback waited
    /// for, survives the purge pass.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a deferred undo still refuses the ended transaction's statements and undoes the one it waited for")]
    public async Task RollbackAsync_DeferredUndoUnderARunningStatement_ShouldRefuseLaterStatementsAndUndoEverything()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using var other = await database.CreateSessionAsync();
        var otherItems = await other.GetCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "first", Doc("first"));

        // A statement of the transaction is inside the apply gate when the rollback starts.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = database.Coordinator.ApplyStatementAsync<bool>(transaction.Context, async _ =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
            return true;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(Timeout);

        // Act: the rollback waits for the apply, then its undo cannot touch the pages another
        // storage bracket holds.
        Task rollback;
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            rollback = transaction.RollbackAsync().AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            bool rollbackWaitedForTheApply = !rollback.IsCompleted;
            release.SetResult();
            (await applying.WaitAsync(Timeout)).ShouldBeTrue();
            await rollback.WaitAsync(Timeout);
            locked = holder.Pages;
            rollbackWaitedForTheApply.ShouldBeTrue();
        }
        bool trackedWhileDeferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count == 1;
        var refused = await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await database.Coordinator.ApplyStatementAsync<bool>(transaction.Context, _ => true, CancellationToken.None));
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        locked.ShouldBeGreaterThan(0);
        trackedWhileDeferred.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        refused.Message.ShouldContain("the statement was not applied", Case.Sensitive);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        await otherItems.PutAsync(other, "other", Doc("other")).AsTask().WaitAsync(Timeout);
        (await IdsAsync(other)).ShouldBe(["other"]);
    }

    // The engine's own maintenance workers stay out of the way, the deferred-undo retry included:
    // these tests drive the purge pass and the checkpoint themselves.
    private static DocumentDatabaseEngineOptions QuietOptions(FaultInjectingJournalStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
        DeferredUndoRetryDelay = TimeSpan.FromHours(1),
    };

    private static async ValueTask FailAsync(string failure, DocumentCollection collection, DocumentDatabaseSession session, Document keep)
    {
        switch (failure)
        {
            case "version":
                // The expected version is stale: the statement fails inside its operation.
                await collection.PutAsync(session, "keep", Doc("keep", "\"v\":3"), new DocumentVersion(keep.Version.Value + 1_000));
                break;
            case "query":
                // The planner finds no such collection.
                await session.ExecuteAsync("SELECT * FROM missing");
                break;
            case "parse":
                // Text the session parses is part of its statement, as in PostgreSQL and Neo4j.
                await session.ExecuteAsync("SELECT FROM");
                break;
            case "system":
                // OQL index DDL on a system collection is refused by the planner, inside the statement.
                await session.ExecuteAsync("CREATE INDEX injected ON cohesion_schema.indexes (x)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    // Every document carries its identity, so a query can list a collection's documents in id order.
    private static ReadOnlyMemory<byte> Doc(string id, string? members = null)
        => Encoding.UTF8.GetBytes(members is null ? $"{{\"id\":\"{id}\"}}" : $"{{\"id\":\"{id}\",{members}}}");

    private static async Task<List<string>> IdsAsync(DocumentDatabaseSession session, string? where = null)
    {
        var ids = new List<string>();
        var result = await session.ExecuteAsync("SELECT id FROM items " + where);
        if (result is not QueryResultSet set) { return ids; }
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync()) { ids.Add(row.GetString(0) ?? "<null>"); }
        }
        return ids;
    }

    // A request that carries no OQL statement: the session refuses it before any statement starts.
    private sealed class ForeignRequest() : QueryRequest(null!);
}
