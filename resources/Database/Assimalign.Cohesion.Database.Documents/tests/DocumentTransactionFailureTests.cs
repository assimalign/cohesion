using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// A statement that fails inside an explicit transaction aborts the whole transaction (#1225, the
/// contract #1188 set for Graph): the session keeps it as <see cref="TransactionState.Faulted"/> and
/// refuses every later statement and BEGIN with COHDBD001 until the caller rolls back, and nothing
/// the transaction wrote survives. Documents has no wire server or client, so every case runs in process.
/// </summary>
public sealed class DocumentTransactionFailureTests
{
    /// <summary>The issue's reproduction: a failure, then a write, then ROLLBACK leaves the documents unchanged.</summary>
    /// <param name="failure">The kind of statement that fails inside the transaction.</param>
    /// <param name="isolation">The isolation level the explicit transaction runs under.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a failed statement, a later write and ROLLBACK leave the documents unchanged")]
    [InlineData("version", IsolationLevel.Snapshot)]
    [InlineData("query", IsolationLevel.Snapshot)]
    [InlineData("parse", IsolationLevel.Snapshot)]
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

    /// <summary>A rollback that does not complete leaves the transaction faulted and refusing work until a rollback completes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a rollback that does not complete leaves the transaction faulted")]
    public async Task RollbackAsync_ThatDoesNotComplete_ShouldLeaveTransactionFaultedUntilRetried()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await collection.PutAsync(session, "keep", Doc("keep"));
        var transaction = await session.BeginTransactionAsync();
        (await collection.GetAsync(session, "keep")).ShouldNotBeNull();

        // Act: the abort record is the rollback's only journal write, so failing it fails the rollback.
        IOException rollbackFailure;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            rollbackFailure = await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync());
        }
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(session, "late", Doc("late")));
        var beginRefused = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        await transaction.RollbackAsync();

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        refused.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        refused.Message.ShouldContain("did not complete", Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(rollbackFailure);
        beginRefused.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await collection.PutAsync(session, "after", Doc("after"));
        (await Ids(session)).ShouldBe(["after", "keep"]);
    }

    /// <summary>COMMIT after a rollback that did not complete fails with COHDBD001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: COMMIT after an incomplete rollback fails and ends the transaction")]
    public async Task CommitAsync_AfterRollbackThatDidNotComplete_ShouldFailAndEndTransaction()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        (await collection.GetAsync(session, "missing")).ShouldBeNull();
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync());
        }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBD001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.InnerException.ShouldBeOfType<IOException>();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await transaction.RollbackAsync();
    }

    /// <summary>A commit the kernel aborts crosses the boundary translated, and a catch-block rollback afterwards raises nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Transaction: a kernel-aborted commit is translated and a later rollback is a no-op")]
    public async Task CommitAsync_KernelAbortsCommit_ShouldTranslateAndAcceptRollback()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new() { StorageStrategy = new FaultInjectingJournalStorageStrategy() });
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "pending", Doc("pending"));

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
        await collection.PutAsync(session, "after", Doc("after"));
        (await Ids(session)).ShouldBe(["after"]);
    }

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
