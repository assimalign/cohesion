using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
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
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
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
        var stored = (await collection.GetAsync(observer, "keep")).ShouldNotBeNull();
        stored.Version.ShouldBe(keep.Version);
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe("{\"id\":\"keep\",\"v\":1}");
        (await Ids(observer)).ShouldBe(["keep"]);
    }

    /// <summary>Every statement surface refuses work on a faulted transaction, and a rollback restores autocommit and BEGIN.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a faulted transaction refuses every operation until rollback")]
    public async Task Operations_OnFaultedTransaction_ShouldBeRefusedUntilRollback()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await collection.PutAsync(session, "keep", Doc("keep"));
        var scoped = (IDocumentDatabase)session.Database;
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
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.CreateCollectionAsync("late")),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.DropCollectionAsync("items")),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in scoped.GetCollectionsAsync()) { }
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
        (await Ids(session)).ShouldBe(["keep", "next"]);
    }

    /// <summary>COMMIT on a faulted transaction fails with COHDBD001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after a failed statement fails and commits nothing")]
    public async Task CommitAsync_AfterFailedStatement_ShouldThrowAndCommitNothing()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        // The client's rollback in a catch block after the failed commit is a no-op, not a second error.
        await transaction.RollbackAsync();
        (await Ids(session)).ShouldBeEmpty();
        await collection.PutAsync(session, "after", Doc("after"));
        (await Ids(session)).ShouldBe(["after"]);
    }

    /// <summary>Disposing a faulted transaction ends it without throwing, and the session is idle again.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: disposing a faulted transaction ends it")]
    public async Task DisposeAsync_FaultedTransaction_ShouldEndTransactionAndReturnSessionToAutocommit()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        await transaction.DisposeAsync();
        await collection.PutAsync(session, "after", Doc("after"));

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Ids(session)).ShouldBe(["after"]);
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
    }

    /// <summary>A statement that wrote content chunks before failing leaves none of them, nor earlier statements' work.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a partially applied statement leaves nothing after rollback")]
    public async Task PutAsync_PartiallyAppliedStatementFails_ShouldUndoTheWholeTransaction()
    {
        // Arrange: the indexed value outgrows the index key only after the new content chunks are written.
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
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
        (await Ids(session)).ShouldBe(["keep"]);
        var stored = (await collection.GetAsync(session, "keep")).ShouldNotBeNull();
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe("{\"id\":\"keep\",\"a\":\"k\"}");
        (await Ids(session, "WHERE a = 'k'")).ShouldBe(["keep"]);
        (await Ids(session, "WHERE a = 'e'")).ShouldBeEmpty();
    }

    /// <summary>A faulted transaction holds no writer lock: another session writes while it awaits rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a faulted transaction releases the writer lock at once")]
    public async Task Statement_FaultedTransaction_ShouldNotBlockOtherWriters()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var failed = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await using var transaction = await failed.BeginTransactionAsync();
        await collection.PutAsync(failed, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await failed.ExecuteAsync("SELECT * FROM missing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await collection.PutAsync(other, "other", Doc("other"), cancellationToken: timeout.Token);

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Ids(other)).ShouldBe(["other"]);
    }

    /// <summary>An autocommit failure ends only its own statement: the session stays idle and usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: autocommit failures leave the session usable")]
    public async Task Statement_AutocommitFailure_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await collection.PutAsync(session, "keep", Doc("keep"));

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));
        await collection.PutAsync(session, "after", Doc("after"));

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await Ids(session)).ShouldBe(["after", "keep"]);
        await using var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
    }

    /// <summary>A refused statement does not change the faulted transaction or its recorded cause.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: refusals keep the original failure as the cause")]
    public async Task Statement_RepeatedRefusals_ShouldKeepOriginalCause()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
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
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        var blocking = await blocker.BeginTransactionAsync();
        await collection.PutAsync(blocker, "blocker", Doc("blocker"));
        var transaction = await waiting.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = collection.PutAsync(waiting, "waiting", Doc("waiting"), cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act: the refusal is immediate; the timeout only turns a regression into a failure, not a hang.
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refused = await Should.ThrowAsync<DatabaseException>(async () =>
            await collection.PutAsync(waiting, "late", Doc("late"), cancellationToken: timeout.Token));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();
        await blocking.CommitAsync();

        // Assert: a canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        faultedState.ShouldBe(TransactionState.Faulted);
        (await Ids(waiting)).ShouldBe(["blocker"]);
    }

    /// <summary>Closing a session with a faulted transaction ends the transaction without an error.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: closing the session ends a faulted transaction")]
    public async Task DisposeAsync_SessionWithFaultedTransaction_ShouldEndTransaction()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM missing"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        await using var observer = await database.CreateSessionAsync();
        (await Ids(observer)).ShouldBeEmpty();
    }

    /// <summary>
    /// A commit after the session closed under an open transaction fails with COHDBD001 naming the
    /// closure, whichever is asked first, and commits nothing; a rollback afterwards is a no-op.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after the session closed fails with COHDBD001")]
    public async Task CommitAsync_AfterSessionClosed_ShouldFailWithCodeNamingTheClosure()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

        // Act
        await session.DisposeAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.Message.ShouldContain("The document session closed before the transaction ended.", Case.Sensitive);
        repeated.Message.ShouldBe(error.Message);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        await using var observer = await database.CreateSessionAsync();
        (await Ids(observer)).ShouldBeEmpty();
    }

    /// <summary>Failures that come before a statement starts leave the transaction active.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: argument and request validation leave the transaction active")]
    public async Task Validation_BeforeStatementStarts_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var otherDatabase = (IDocumentDatabase)await engine.CreateDatabaseAsync("other");
        await using var session = await database.CreateSessionAsync();
        await using var foreign = await otherDatabase.CreateSessionAsync();
        var scoped = (IDocumentDatabase)session.Database;
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

        // Act
        var rejections = new List<Exception>
        {
            await Should.ThrowAsync<ArgumentException>(async () => await collection.PutAsync(session, " ", Doc("blank"))),
            await Should.ThrowAsync<ArgumentException>(async () => await collection.GetAsync(session, "")),
            await Should.ThrowAsync<ArgumentException>(async () => await scoped.CreateCollectionAsync(" ")),
            await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" ")),
            await Should.ThrowAsync<DatabaseException>(async () => await scoped.CreateCollectionAsync("COHESION_SCHEMA.INDEXES")),
            await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(foreign, "x", Doc("x"))),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new ForeignRequest())),
        };
        var state = transaction.State;
        await transaction.CommitAsync();

        // Assert
        rejections.ShouldAllBe(error => !error.Message.StartsWith("COHDBD001", StringComparison.Ordinal));
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Ids(session)).ShouldBe(["pending"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
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

        // Assert
        currentAfterCommit.ShouldBeNull();
        refusal.Message.ShouldContain("Committed", Case.Sensitive);
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await Ids(session)).ShouldBe(["kept"]);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
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
        (await Ids(session)).ShouldBe(["first", "second"]);
    }

    /// <summary>
    /// A rollback whose abort record cannot be written still ends the transaction (#1226). Since
    /// #1252 the record's append writes only when it drains the journal's append buffer (one small
    /// frame here), and a failed journal write takes the database offline (#1243's rule): the
    /// session is free, the next operation is refused as offline, and the reopen keeps what
    /// committed and nothing of the transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a rollback whose abort record cannot be written still ends the transaction, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy { SmallJournalBuffer = true };
        var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();

        // A delete that matches nothing takes the database writer lock and writes no version, so
        // the abort record is the rollback's only journal append.
        (await collection.DeleteAsync(session, "missing")).ShouldBeFalse();

        // Act
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        await Should.ThrowAsync<DatabaseOfflineException>(async () => await collection.PutAsync(other, "other", Doc("other")));
        await other.DisposeAsync();
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the record's write failed, and the rollback ended the transaction anyway.
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Ids(observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other
    /// and COMMIT commits nothing. The lost record's write took the database offline (#1252), so
    /// the COMMIT and a repeated rollback are refused as offline before they start.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy { SmallJournalBuffer = true };
        var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();
        (await collection.DeleteAsync(session, "missing")).ShouldBeFalse();
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // Act
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Ids(observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// A commit whose record cannot be appended is aborted by the kernel and ends Faulted. Since
    /// #1252 the append fails only when it drains the journal's append buffer (one small frame
    /// here) and that write fails, which takes the database offline: the commit crosses the
    /// boundary as the offline refusal, not as committed or unconfirmed, a later rollback is
    /// refused as offline too, and the reopen holds nothing of the transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a commit whose record cannot be appended aborts, and the database goes offline")]
    public async Task CommitAsync_CommitRecordCannotBeAppended_ShouldAbortAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy { SmallJournalBuffer = true };
        var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

        // Act: the commit record's append drains the record ahead of it, and that write fails.
        DatabaseOfflineException error;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            error = await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        }
        var stateAfterCommit = transaction.State;
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = DocumentDatabaseEngine.Create(new() { StorageStrategy = strategy });
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        error.ShouldNotBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        stateAfterCommit.ShouldBe(TransactionState.Faulted);
        session.CurrentTransaction.ShouldBeNull();
        (await Ids(observer)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a late operation of a rolled-back transaction does not release its deferred writer lock")]
    public async Task LockWriterAsync_LateOperationOfRolledBackTransaction_ShouldNotReleaseItsDeferredWriterLock()
    {
        // Arrange: a rollback whose undo cannot touch the pages another storage bracket holds,
        // and a waiting writer.
        await using var engine = DocumentDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = (DocumentDatabaseTransaction)await session.BeginTransactionAsync();
        await collection.PutAsync(session, "rolled", "1"u8.ToArray());
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            locked = holder.Pages;
        }

        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = collection.PutAsync(other, "other", "2"u8.ToArray()).AsTask();

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
        (await collection.GetAsync(other, "rolled")).ShouldBeNull();
        (await collection.GetAsync(other, "other")).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using (var session = await database.CreateSessionAsync())
        {
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
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        var items = await recovered.GetCollectionAsync("items");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the insert is gone and the delete is undone.
        closeFailure.Flatten().InnerExceptions.ShouldContain(error => error is StorageTransactionException);
        (await items.GetAsync(observer, "rolled")).ShouldBeNull();
        (await items.GetAsync(observer, "kept")).ShouldNotBeNull();
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
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using (var session = await database.CreateSessionAsync())
        {
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
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: only the committed document is there.
        checkpointFailuresLeft.ShouldBe(0);
        offline.ShouldBeTrue();
        (await Ids(observer)).ShouldBe(["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a commit record that could not be made durable crosses the boundary as committed-unconfirmed")]
    public void TranslateKernelFailure_CommitUnconfirmed_ShouldBecomeTheAreaRootsCommitUnconfirmedException()
    {
        // Arrange
        var kernel = new TransactionCommitUnconfirmedException("Transaction 7 committed, but its commit record could not be made durable.", new IOException("flush"));

        // Act
        var translated = DocumentDatabaseInstance.TranslateKernelFailure(kernel);

        // Assert: not an abort, so a caller never retries work that committed.
        var unconfirmed = translated.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        unconfirmed.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        unconfirmed.Message.ShouldBe(kernel.Message);
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
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = (DocumentDatabaseTransaction)await session.BeginTransactionAsync();
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
        await collection.PutAsync(other, "other", Doc("other")).AsTask().WaitAsync(Timeout);
        (await Ids(other)).ShouldBe(["other"]);
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

    private static async ValueTask FailAsync(string failure, IDocumentCollection collection, IDatabaseSession session, Document keep)
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

    private static async Task<List<string>> Ids(IDatabaseSession session, string? where = null)
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
