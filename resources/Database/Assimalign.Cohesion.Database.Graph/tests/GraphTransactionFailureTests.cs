using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
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
    [InlineData("MATCH (n:Missing) RETURN n.name", "COHDBG002", IsolationLevel.Snapshot)]
    [InlineData("MATCH (n:Keep {name: 'k'}) DELETE n", "COHDBG003", IsolationLevel.Snapshot)]
    [InlineData("MATCH (n:Missing) RETURN n.name", "COHDBG002", IsolationLevel.ReadCommitted)]
    [InlineData("MATCH (n:Keep {name: 'k'}) DELETE n", "COHDBG003", IsolationLevel.ReadCommitted)]
    public async Task ExecuteAsync_FailureThenWriteThenRollback_ShouldLeaveGraphUnchanged(string failing, string code, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var seed = await database.CreateNodeAsync(session, ["Keep"]);
        var schema = GraphSchema.Open(database, session);
        var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));

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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending {name: 'p'})");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        error.InnerException.ShouldNotBeNull().Message.ShouldStartWith("COHDBG002", Case.Sensitive);
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));

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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var failed = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await using var transaction = await failed.BeginTransactionAsync();
        await failed.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await failed.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Keep)");

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));

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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
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
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT (:Pending)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:Missing) RETURN n.name"));

        // Act
        await session.DisposeAsync();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
        await using var observer = await database.CreateSessionAsync();
        (await Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    private static async Task<List<QueryRow>> Rows(IDatabaseSession session, string gql)
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
