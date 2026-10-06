using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// A statement that fails inside an explicit transaction aborts the whole transaction (#1188):
/// the session keeps it as <see cref="TransactionState.Faulted"/> and refuses every later
/// statement with COHDBG007 until the caller rolls back, and nothing the transaction wrote survives.
/// </summary>
public sealed class GraphTransactionFailureTests
{
    /// <summary>The issue's reproduction: a failure, then a DETACH DELETE, then ROLLBACK leaves the graph unchanged.</summary>
    /// <param name="failing">The statement that fails inside the transaction.</param>
    /// <param name="code">The code the failing statement reports.</param>
    /// <param name="isolation">The isolation level the explicit transaction runs under.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a failed statement, a later write and ROLLBACK leave the graph unchanged")]
    [InlineData("MATCH (n) RETURN m.name", "COHDBG001", IsolationLevel.Snapshot)]
    [InlineData("MATCH (n:Keep {name: 'k'}) DELETE n", "COHDBG003", IsolationLevel.Snapshot)]
    [InlineData("MATCH (n) RETURN m.name", "COHDBG001", IsolationLevel.ReadCommitted)]
    [InlineData("MATCH (n:Keep {name: 'k'}) DELETE n", "COHDBG003", IsolationLevel.ReadCommitted)]
    public async Task ExecuteAsync_FailureThenWriteThenRollback_ShouldLeaveGraphUnchanged(string failing, string code, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep {name: 'k'})-[:LINK]->(:Keep {name: 'j'})");
        var transaction = await session.BeginTransactionAsync(isolation);
        await session.ExecuteAsync("INSERT (:Pending {name: 'p'})");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(failing));
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) DETACH DELETE n"));
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        await transaction.RollbackAsync();

        // Assert
        failure.Message.ShouldStartWith(code, Case.Sensitive);
        refused.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(failure);
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        (await Rows(observer, "MATCH (n:Keep) RETURN n.name")).Select(row => row.GetString(0)).Order().ShouldBe(["j", "k"]);
        (await Rows(observer, "MATCH (a:Keep)-[r:LINK]->(b:Keep) RETURN a.name, b.name")).ShouldHaveSingleItem();
        (await Rows(observer, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep"]);
    }

    /// <summary>Every statement surface refuses work on a faulted transaction, and a rollback restores autocommit and BEGIN.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a faulted transaction refuses every operation until rollback")]
    public async Task Operations_OnFaultedTransaction_ShouldBeRefusedUntilRollback()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var seed = await database.CreateNodeAsync(session, ["Keep"]);
        var schema = GraphSchema.Open(database, session);
        var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        var refusals = new List<Exception>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Late)")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n RETURN n")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH (n) RETURN n"))),
            await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(session, ["Late"])),
            await Should.ThrowAsync<DatabaseException>(async () => await database.GetNodeAsync(session, seed.Id)),
            await Should.ThrowAsync<DatabaseException>(async () => await database.DeleteNodeAsync(session, seed.Id)),
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await foreach (var _ in database.TraverseAsync(session, new GraphTraversal(seed.Id))) { }
            }),
            await Should.ThrowAsync<DatabaseException>(async () => await schema.GetLabelsAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await schema.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Late"))),
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
        };
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();

        // Assert
        refusals.ShouldAllBe(error => error.Message.StartsWith("COHDBG007", StringComparison.Ordinal));
        (await database.GetNodeAsync(session, seed.Id)).ShouldNotBeNull();
        (await schema.GetLabelsAsync()).Select(label => label.Name).ShouldBe(["Keep"]);
        await using (var next = await session.BeginTransactionAsync())
        {
            await session.ExecuteAsync("INSERT (:Next)");
            await next.CommitAsync();
        }
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep", "Next"]);
    }

    /// <summary>COMMIT on a faulted transaction fails with COHDBG007, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: COMMIT after a failed statement fails and commits nothing")]
    public async Task CommitAsync_AfterFailedStatement_ShouldThrowAndCommitNothing()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending {name: 'p'})");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        error.InnerException.ShouldNotBeNull().Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        // The client's rollback in a catch block after the failed commit is a no-op, not a second error.
        await transaction.RollbackAsync();
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();
        await session.ExecuteAsync("INSERT (:After)");
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["After"]);
    }

    /// <summary>Disposing a faulted transaction ends it without throwing, and the session is idle again.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: disposing a faulted transaction ends it")]
    public async Task DisposeAsync_FaultedTransaction_ShouldEndTransactionAndReturnSessionToAutocommit()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        await transaction.DisposeAsync();
        await session.ExecuteAsync("INSERT (:After)");

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["After"]);
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
    }

    /// <summary>A statement that wrote part of its work before failing leaves none of it, nor earlier statements' work.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a partially applied statement leaves nothing after rollback")]
    public async Task ExecuteAsync_PartiallyAppliedStatementFails_ShouldUndoTheWholeTransaction()
    {
        // Arrange: Required demands an Int64 'age' on every Required node, so the second node of the insert fails.
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        var required = new GraphLabelMetadata(Guid.NewGuid(), "Required");
        await schema.SaveLabelAsync(required);
        await schema.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(required.Id, "age", DatabaseType.Int64, true));
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Earlier)");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:Partial {name: 'first'})-[:TO]->(:Required {name: 'second'})"));
        await transaction.RollbackAsync();

        // Assert
        error.Message.ShouldStartWith("COHDBG003", Case.Sensitive);
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Required"]);
        (await Rows(session, "SHOW RELATIONSHIP TYPES")).ShouldBeEmpty();
        (await Rows(session, "MATCH (n:Required) RETURN n.name")).ShouldBeEmpty();
    }

    /// <summary>A faulted transaction holds no writer lock: another session writes while it awaits rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a faulted transaction releases the writer lock at once")]
    public async Task ExecuteAsync_FaultedTransaction_ShouldNotBlockOtherWriters()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var failed = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await using var transaction = await failed.BeginTransactionAsync();
        await failed.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await failed.ExecuteAsync("MATCH (n) RETURN m.name"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var written = await other.ExecuteAsync("INSERT (:Other)", cancellationToken: timeout.Token);

        // Assert
        written.AffectedCount.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Rows(other, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Other"]);
    }

    /// <summary>An autocommit failure ends only its own statement: the session stays idle and usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: autocommit failures leave the session usable")]
    public async Task ExecuteAsync_AutocommitFailure_ShouldLeaveSessionUsable()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep)");

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));
        await session.ExecuteAsync("INSERT (:After)");

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["After", "Keep"]);
        await using var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
    }

    /// <summary>A refused statement does not change the faulted transaction or its recorded cause.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: refusals keep the original failure as the cause")]
    public async Task ExecuteAsync_RepeatedRefusals_ShouldKeepOriginalCause()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        var first = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Other) RETURN n.name"));
        var second = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Late)"));

        // Assert
        first.InnerException.ShouldBeSameAs(failure);
        second.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.Faulted);
    }

    /// <summary>A statement that fails to parse is a failed statement too, as in Neo4j and PostgreSQL: it aborts the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a parse failure aborts the explicit transaction")]
    public async Task ExecuteAsync_ParseFailureInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync("MATCH (n RETURN n"));
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Late)"));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();

        // Assert
        refused.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        refused.Message.ShouldContain(failure.Message, Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(failure);
        faultedState.ShouldBe(TransactionState.Faulted);
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>A statement canceled while it waits for the writer lock aborts its transaction and releases the session.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a canceled statement aborts the explicit transaction")]
    public async Task ExecuteAsync_CanceledInsideTransaction_ShouldAbortTransaction()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        await using var waiting = await database.CreateSessionAsync();
        var blocking = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("INSERT (:Blocker)");
        var transaction = await waiting.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = waiting.ExecuteAsync("INSERT (:Waiting)", cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await waiting.ExecuteAsync("INSERT (:Late)"));
        var faultedState = transaction.State;
        await transaction.RollbackAsync();
        await blocking.CommitAsync();

        // Assert: a canceled task rethrows a fresh cancellation exception, so the cause matches by kind.
        refused.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        refused.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        faultedState.ShouldBe(TransactionState.Faulted);
        (await Rows(waiting, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Blocker"]);
    }

    /// <summary>Closing a session with a faulted transaction ends the transaction without an error.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: closing the session ends a faulted transaction")]
    public async Task DisposeAsync_SessionWithFaultedTransaction_ShouldEndTransaction()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        await using var observer = await database.CreateSessionAsync();
        (await Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>Typed-API argument validation runs before any statement starts, so it leaves the transaction active.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: typed-API argument validation leaves the transaction active")]
    public async Task TypedOperations_InvalidArgumentsInsideTransaction_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var seed = await database.CreateNodeAsync(session, ["Keep"]);
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");

        // Act
        var rejections = new List<Exception>
        {
            await Should.ThrowAsync<ArgumentNullException>(async () => await database.CreateNodeAsync(session, null!)),
            await Should.ThrowAsync<ArgumentNullException>(async () => await database.CreateNodeAsync(session, [null!])),
            await Should.ThrowAsync<ArgumentException>(async () => await database.CreateNodeAsync(session, [" "])),
            await Should.ThrowAsync<ArgumentNullException>(async () => await database.CreateRelationshipAsync(session, seed.Id, seed.Id, null!)),
            await Should.ThrowAsync<ArgumentException>(async () => await database.CreateRelationshipAsync(session, seed.Id, seed.Id, "")),
        };
        var traversal = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            await foreach (var _ in database.TraverseAsync(session, new GraphTraversal(seed.Id, MaxDepth: -1))) { }
        });
        var state = transaction.State;
        await transaction.CommitAsync();

        // Assert
        rejections.Count.ShouldBe(5);
        traversal.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep", "Pending"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var rolledBack = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Discarded)");
        await rolledBack.RollbackAsync();
        var committed = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Kept)");
        await committed.CommitAsync();

        // Act
        await rolledBack.RollbackAsync();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await committed.RollbackAsync());

        // Assert: the root base's message (concrete-types plan §6.4), the one the model carried.
        refusal.Message.ShouldBe("The transaction is Committed; a committed transaction cannot roll back.");
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Kept"]);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:First)");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await session.ExecuteAsync("INSERT (:Second)");
        await transaction.CommitAsync();

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["First", "Second"]);
    }

    /// <summary>
    /// A commit of a transaction its caller already rolled back is refused by state, with the root
    /// base's message (concrete-types plan §6.4), the one the model carried.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: COMMIT after ROLLBACK is refused with the transaction's state")]
    public async Task CommitAsync_AfterRollback_ShouldBeRefusedWithTheState()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Discarded)");
        await transaction.RollbackAsync();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        refusal.Message.ShouldBe("The transaction is RolledBack.");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>
    /// Every commit of a transaction a statement aborted reports <c>COHDBG007</c> with the
    /// statement's failure as its cause (concrete-types plan §6.4): the second one too, where the
    /// model's own state machine reported "The transaction is RolledBack." once the first commit
    /// had ended the transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: every COMMIT of an aborted transaction reports COHDBG007 with the cause")]
    public async Task CommitAsync_AgainAfterAbortedCommit_ShouldReportTheCodedCause()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"));

        // Act
        var first = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var second = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        first.Message.ShouldStartWith("COHDBG007: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        second.Message.ShouldBe(first.Message);
        second.InnerException.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>
    /// A transaction whose session closed before the caller ended it was rolled back by the
    /// session's teardown, and a later commit reports <c>COHDBG007</c> naming why (concrete-types
    /// plan §6.4): "The session closed before the transaction ended." for an active transaction,
    /// and the statement's failure for one a statement aborted first. The model's own teardown
    /// disposed the transaction without a cause, and the commit reported "The transaction is
    /// RolledBack." either way.
    /// </summary>
    /// <param name="aborted">True when a statement aborted the transaction before the session closed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Transaction: COMMIT after the session closed reports COHDBG007 naming why nothing committed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted(bool aborted)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        DatabaseException? failure = aborted
            ? await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN m.name"))
            : null;
        await session.DisposeAsync();

        // Act
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        commit.Message.ShouldStartWith("COHDBG007: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        if (failure is null)
        {
            commit.Message.ShouldEndWith("Cause: The session closed before the transaction ended.", Case.Sensitive);
        }
        else
        {
            commit.InnerException.ShouldBeSameAs(failure);
        }

        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        (await Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>
    /// Closing a session aborts the statement running on it, and the statement's abort is the
    /// cause a later commit of its transaction reports with <c>COHDBG007</c> (concrete-types plan
    /// §6.4): the teardown's own cause does not replace it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: closing a session aborts its running statement, whose cause a later COMMIT reports")]
    public async Task DisposeAsync_SessionWithRunningStatement_ShouldAbortItAndReportItsCause()
    {
        // Arrange: another transaction holds the writer lock, so the session's statement waits.
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        var session = await database.CreateSessionAsync();
        var blocking = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("INSERT (:Blocker)");
        var transaction = await session.BeginTransactionAsync();
        var pending = session.ExecuteAsync("INSERT (:Waiting)").AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await session.DisposeAsync();
        var statement = await Should.ThrowAsync<Exception>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await blocking.CommitAsync();

        // Assert
        statement.ShouldNotBeOfType<TimeoutException>();
        commit.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        commit.Message.ShouldEndWith("Cause: The graph session closed while the operation was running.", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        await using var observer = await database.CreateSessionAsync();
        (await Rows(observer, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Blocker"]);
    }

    /// <summary>
    /// A commit while a statement of the transaction still runs is refused with the root base's
    /// message (concrete-types plan §6.4), for the model's former "Dispose every graph operation
    /// before committing its transaction.", and leaves the transaction active: it commits once the
    /// statement completed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: COMMIT while a statement of the transaction runs is refused and leaves it active")]
    public async Task CommitAsync_WhileStatementRuns_ShouldBeRefusedAndLeaveTheTransactionActive()
    {
        // Arrange: another transaction holds the writer lock, so the statement waits.
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        await using var session = await database.CreateSessionAsync();
        var blocking = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("INSERT (:Blocker)");
        var transaction = await session.BeginTransactionAsync();
        var pending = session.ExecuteAsync("INSERT (:Waiting)").AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var stateAfterRefusal = transaction.State;
        await blocking.CommitAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).AffectedCount.ShouldBe(1);
        await transaction.CommitAsync();

        // Assert
        refusal.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        stateAfterRefusal.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Blocker", "Waiting"]);
    }

    /// <summary>
    /// BEGIN on a session whose transaction is active is refused with the root base's one message
    /// before the model's isolation-level refusal, and a closed session before both (concrete-types
    /// plan §6.4, BEGIN's refusal order): the model refused an unsupported isolation level first.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Session: BEGIN is refused for a closed session, then an active transaction, before the isolation level")]
    public async Task BeginTransactionAsync_WhileActiveOrClosed_ShouldRefuseBeforeTheIsolationLevel()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
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
        unsupported.Message.ShouldBe("The graph engine supports Snapshot and ReadCommitted isolation.");
        closed.Message.ShouldBe("The session is closed.");
    }

    /// <summary>
    /// A closed session refuses BEGIN, both execute seams, the typed operations and the schema
    /// surface with the root base's message (concrete-types plan §6.4), for the model's former
    /// "The graph session is closed.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Session: a closed session refuses every operation with one message")]
    public async Task ExecuteAsync_OnClosedSession_ShouldRefuseWithOneMessage()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        await session.DisposeAsync();

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name"))),
            await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(session, ["Late"])),
            await Should.ThrowAsync<DatabaseException>(async () => await schema.GetLabelsAsync()),
            Should.Throw<DatabaseException>(() => GraphSchema.Open(database, session)),
        };

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
    }

    /// <summary>
    /// A transaction the kernel ended under its caller (its database was dropped while the session
    /// held it) reports <c>Faulted</c>, and the root bases order its refusal against the database's
    /// disposal and a canceled token (concrete-types plan §6.4): BEGIN refuses the open transaction
    /// with <c>COHDBG007</c> before it checks anything of the model, where the model reported the
    /// disposed database; both execute seams check a canceled token before the model reports the
    /// disposed database, which they still report for a live token, and so does BEGIN once the
    /// caller rolled the ended transaction back.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Session: a transaction the kernel ended refuses BEGIN with COHDBG007; the execute seams check a canceled token first")]
    public async Task BeginTransactionAsync_TransactionEndedByTheKernel_ShouldOrderTheRefusals()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        await engine.DropDatabaseAsync("graph");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        var state = transaction.State;
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(canceled.Token));
        var canceledText = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name", null, canceled.Token));
        var text = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name"));
        var canceledRequest = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name"), canceled.Token));
        var request = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name")));
        await transaction.RollbackAsync();
        var beginAfterRollback = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.BeginTransactionAsync());

        // Assert
        state.ShouldBe(TransactionState.Faulted);
        begin.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
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
    /// The typed operations and the schema surface, which check the database before the session,
    /// still report the disposed database.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Session: a closed session of a dropped database is refused as closed by BEGIN and the execute seams")]
    public async Task ExecuteAsync_ClosedSessionOfDroppedDatabase_ShouldRefuseAsClosedBeforeTheDisposedDatabase()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        await session.DisposeAsync();
        await engine.DropDatabaseAsync("graph");

        // Act
        var refusals = new List<DatabaseException>
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name")),
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name"))),
        };
        var typed = await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateNodeAsync(session, ["Late"]));
        var schemaRead = await Should.ThrowAsync<ObjectDisposedException>(async () => await schema.GetLabelsAsync());
        var schemaOpen = Should.Throw<ObjectDisposedException>(() => GraphSchema.Open(database, session));

        // Assert
        refusals.ShouldAllBe(refusal => refusal.Message == "The session is closed.");
        typed.ShouldNotBeNull();
        schemaRead.ShouldNotBeNull();
        schemaOpen.ShouldNotBeNull();
    }

    /// <summary>
    /// A statement holds its session from its start to its end, through the root base's operation
    /// hold (concrete-types plan §6.4), which replaced the model's own reservation: while one waits
    /// for the writer lock, a second statement on the session, a typed operation and a schema read
    /// are refused with the model's message, and BEGIN with the base's "already active" message.
    /// None of the refusals ends the waiting statement, which completes once the lock is free, and
    /// the session then runs statements again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Session: a running statement holds the session against another statement and BEGIN")]
    public async Task ExecuteAsync_WhileAnotherStatementRuns_ShouldBeRefusedAndLeaveItRunning()
    {
        // Arrange: another transaction holds the writer lock, so the session's statement waits.
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var blocker = await database.CreateSessionAsync();
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        var blocking = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("INSERT (:Blocker)");
        var pending = session.ExecuteAsync("INSERT (:Waiting)").AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        var statement = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n) RETURN n.name"));
        var request = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (n) RETURN n.name")));
        var typed = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(session, ["X"]));
        var schemaRead = await Should.ThrowAsync<DatabaseException>(async () => await schema.GetLabelsAsync());
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        bool stillWaiting = !pending.IsCompleted;
        await blocking.CommitAsync();
        var completed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        new[] { statement, request, typed, schemaRead }.ShouldAllBe(refusal =>
            refusal.Message == "Dispose the active graph operation before starting another operation on this session.");
        begin.Message.ShouldBe("A transaction or operation is already active on this session.");
        stillWaiting.ShouldBeTrue();
        completed.AffectedCount.ShouldBe(1);
        session.CurrentTransaction.ShouldBeNull();
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Blocker", "Waiting"]);
    }

    /// <summary>
    /// A rollback whose abort record cannot be written still ends the transaction and releases the
    /// database writer lock (#1226). Since #1252 the rollback appends the record to the journal's
    /// append buffer, and it reaches the file with the next drain: here the commit of another
    /// session's statement, which proceeds because the writer lock is free. That drain fails,
    /// which takes the database offline (#1243's rule): the statement is reported unconfirmed, the
    /// next one is refused as offline, and the reopen keeps what committed before and nothing of
    /// either transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a rollback whose abort record cannot be written still ends the transaction, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep {name: 'keep'})");
        var transaction = await session.BeginTransactionAsync();

        // A delete that matches nothing takes the database writer lock and writes no version, so
        // the abort record is the rollback's only journal append.
        await session.ExecuteAsync("MATCH (n:Keep) WHERE n.name = 'missing' DELETE n");

        // Act: the rollback buffers the abort record; the other session's statement needs the
        // writer lock, and its commit drains the record, which fails.
        int unspentAfterTheRollback;
        int unspent;
        DatabaseTransactionCommitUnconfirmedException lost;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            unspentAfterTheRollback = failures.Remaining;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            lost = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(
                async () => await other.ExecuteAsync("INSERT (:Other)", cancellationToken: timeout.Token));
            unspent = failures.Remaining;
        }

        var refused = await Should.ThrowAsync<DatabaseOfflineException>(async () => await other.ExecuteAsync("INSERT (:After)"));
        await other.DisposeAsync();
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("graph");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the rollback wrote nothing and ended the transaction; the drain that carried its
        // record failed.
        unspentAfterTheRollback.ShouldBe(1);
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        StorageOfflineException.Find(lost).ShouldNotBeNull();
        refused.InnerException.ShouldNotBeNull();
        (await Rows(observer, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other
    /// and COMMIT commits nothing. The record is lost with the drain that carries it (#1252), here
    /// the commit of the session's next statement, which takes the database offline: the COMMIT
    /// and a repeated rollback are then refused as offline before they start.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep {name: 'keep'})");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("MATCH (n:Keep) WHERE n.name = 'missing' DELETE n");
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await session.ExecuteAsync("INSERT (:Next)"));
            unspent = failures.Remaining;
        }

        // Act
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());
        await session.DisposeAsync();
        engine.Dispose();
        await using var reopened = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("graph");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Rows(observer, "MATCH (n:Keep) RETURN n.name")).ShouldHaveSingleItem();
    }

    /// <summary>
    /// A commit whose record cannot be written is never acknowledged (#1252): the commit drains the
    /// journal's append buffer through its record before it returns, in every durability mode, and
    /// when that drain fails the database goes offline (#1243's rule) and the commit crosses the
    /// boundary as committed-unconfirmed, its outcome left to the reopen's recovery. The record
    /// never reached the file here, so the reopen holds nothing of the transaction, and a later
    /// rollback is refused as offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a commit whose record cannot be written is unconfirmed, and the database goes offline")]
    public async Task CommitAsync_CommitRecordCannotBeWritten_ShouldBeUnconfirmedAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");

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
        await using var reopened = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("graph");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        StorageOfflineException.Find(error).ShouldNotBeNull();
        stateAfterCommit.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    /// <summary>
    /// A rollback whose undo cannot touch its pages still ends the transaction; the writer keeps
    /// the database writer lock until the version-purge pass completes the undo, and the failed
    /// bracket leaves nothing behind that would refuse a checkpoint (#1226). Another storage bracket
    /// holds every page while the rollback runs; until #1252 a failed journal write was the fault,
    /// and a journal write failure now takes the database offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a rollback whose undo is deferred holds the writer lock until the purge pass")]
    public async Task RollbackAsync_UndoDeferred_ShouldHoldWriterLockUntilThePurgePassAndKeepCheckpointsRunning()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep {name: 'keep'})");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Rolled {name: 'rolled'})");

        // Act: the undo's storage bracket cannot touch the first page it undoes.
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            locked = holder.Pages;
        }
        var deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = other.ExecuteAsync("INSERT (:Other)").AsTask();
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.Checkpoint();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        locked.ShouldBeGreaterThan(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        otherProceededBeforeTheUndo.ShouldBeFalse();
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        database.Coordinator.Checkpoint();
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep", "Other"]);
        (await Rows(session, "MATCH (n:Keep) RETURN n.name")).Select(row => row.GetString(0)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// An operation of a rolled-back transaction that reaches the writer lock after the end
    /// releases nothing while the transaction's undo is deferred, so a waiting writer keeps
    /// waiting for the undo (#1226).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a late operation of a rolled-back transaction does not release its deferred writer lock")]
    public async Task LockWriterAsync_LateOperationOfRolledBackTransaction_ShouldNotReleaseItsDeferredWriterLock()
    {
        // Arrange: a rollback whose undo could not touch the pages another storage bracket held,
        // and a writer waiting for the lock.
        await using var engine = GraphDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Rolled {name: 'rolled'})");
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
        }
        var waiting = other.ExecuteAsync("INSERT (:Other)").AsTask();

        // Act: the late operation gets the lock its transaction still holds, finds the
        // transaction ended, and cleans up.
        var late = await Should.ThrowAsync<DatabaseException>(async () => await database.LockWriterAsync(transaction.Context, CancellationToken.None));
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        late.Message.ShouldContain("ended while waiting for the writer lock");
        otherProceededBeforeTheUndo.ShouldBeFalse();
        (await Rows(session, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Other"]);
    }

    /// <summary>
    /// A rolled-back transaction whose undo still fails when the engine closes is reported by the
    /// close, and its writes do not come back as committed data at the next open: the close keeps
    /// the journal's classification of the writer, and recovery scrubs it (#1226).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var engine = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var database = await engine.CreateDatabaseAsync("graph");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("INSERT (:Keep {name: 'keep'})");
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT (:Rolled {name: 'rolled'})-[:LINK]->(:Rolled {name: 'also'})");

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
        await using var reopened = GraphDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = await reopened.OpenDatabaseAsync("graph");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        closeFailure.Flatten().InnerExceptions.ShouldContain(error => error is StorageTransactionException);
        (await Rows(observer, "SHOW LABELS")).Select(row => row.GetString(2)).ShouldBe(["Keep"]);
        (await Rows(observer, "MATCH (n) RETURN n.name")).Select(row => row.GetString(0)).ShouldBe(["keep"]);
        (await Rows(observer, "MATCH ()-[r]->() RETURN r")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Transaction: a commit record that could not be made durable crosses the boundary as committed-unconfirmed")]
    public void TranslateKernelFailure_CommitUnconfirmed_ShouldBecomeTheAreaRootsCommitUnconfirmedException()
    {
        // Arrange
        var kernel = new TransactionCommitUnconfirmedException("Transaction 7 committed, but its commit record could not be made durable.", new IOException("flush"));

        // Act
        var translated = GraphDatabase.TranslateKernelFailure(kernel);

        // Assert: not an abort, so a caller never retries work that committed.
        var unconfirmed = translated.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        unconfirmed.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        unconfirmed.Message.ShouldBe(kernel.Message);
        unconfirmed.InnerException.ShouldBeSameAs(kernel);
    }

    // The engine's own maintenance workers stay out of the way: these tests drive the purge
    // pass and the checkpoint themselves.
    private static GraphDatabaseEngineOptions QuietOptions(FaultInjectingJournalStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),

        // The deferred-undo retry stays out of the way too: these tests drive the purge pass.
        DeferredUndoRetryDelay = TimeSpan.FromHours(1),
    };

    private static async Task<List<QueryRow>> Rows(GraphDatabaseSession session, string gql)
    {
        var result = await session.ExecuteAsync(gql);
        var rows = new List<QueryRow>();
        if (result is not QueryResultSet set) { return rows; }
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync()) { rows.Add(row); }
        }
        return rows;
    }
}
