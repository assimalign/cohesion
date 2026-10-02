using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// A failed statement inside an explicit transaction aborts it over the wire exactly as in process
/// (#1188). The graph wire protocol has no transaction control yet (Graph DESIGN, "Transactions,
/// atomicity and recovery"), so each test opens the explicit transaction on the server session's
/// engine session, the scope every wire statement of that connection executes in.
/// </summary>
public sealed class GraphTransactionFailureWireTests
{
    /// <summary>A failure, then a DETACH DELETE, then ROLLBACK leaves the graph unchanged and the session reusable.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - A failed statement aborts the explicit transaction and later statements are refused")]
    public async Task QueryAsync_FailureInsideExplicitTransaction_ShouldRefuseLaterStatementsUntilRollback()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        (await connection.ExecuteAsync("CREATE (:Keep {name: 'k'})-[:LINK]->(:Keep {name: 'j'})", cancellationToken: harness.Token)).ShouldBe(3);
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(harness.Token);
        (await connection.ExecuteAsync("CREATE (:Pending {name: 'p'})", cancellationToken: harness.Token)).ShouldBe(1);

        // Act
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (n:Missing) RETURN n.name", cancellationToken: harness.Token));
        var refused = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("MATCH (n) DETACH DELETE n", cancellationToken: harness.Token));
        var refusedRead = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token));
        var faultedState = transaction.State;
        await transaction.RollbackAsync(harness.Token);

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldContain("COHDBG002", Case.Sensitive);
        refused.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        refused.Message.ShouldContain("COHDBG007", Case.Sensitive);
        refusedRead.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        refusedRead.Message.ShouldContain("COHDBG007", Case.Sensitive);
        faultedState.ShouldBe(TransactionState.Faulted);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(serverSession.Id);
        (await connection.QueryAsync("MATCH (n:Keep) RETURN n.name", cancellationToken: harness.Token))
            .Select(row => (string?)row[0]).Order().ShouldBe(["j", "k"]);
        (await connection.QueryAsync("MATCH (a:Keep)-[r:LINK]->(b:Keep) RETURN a.name", cancellationToken: harness.Token)).ShouldHaveSingleItem();
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).Select(row => (string?)row[2]).ShouldBe(["Keep"]);
    }

    /// <summary>A wire statement rejected while parsing or validating its request aborts the transaction as well.</summary>
    /// <param name="statement">The statement the server rejects before execution.</param>
    /// <param name="code">The error code the rejection reports.</param>
    /// <param name="fragment">A fragment of the rejection's message, which the later refusal repeats as its cause.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Parse and request-validation failures abort the explicit transaction")]
    [InlineData("MATCH (n RETURN n.name", ProtocolErrorCode.ParseFailure, "GQL parse error")]
    [InlineData("MATCH (n:Keep) RETURN n", ProtocolErrorCode.ExecutionFailure, "ExecutePaths")]
    public async Task QueryAsync_RejectedStatementInsideExplicitTransaction_ShouldAbortTransaction(string statement, ProtocolErrorCode code, string fragment)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Pending {name: 'p'})", cancellationToken: harness.Token);

        // Act
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: harness.Token));
        var refused = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("CREATE (:Late)", cancellationToken: harness.Token));
        var faultedState = transaction.State;
        await transaction.RollbackAsync(harness.Token);

        // Assert
        failure.Code.ShouldBe(code);
        failure.Message.ShouldContain(fragment, Case.Sensitive);
        refused.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        refused.Message.ShouldContain("COHDBG007", Case.Sensitive);
        refused.Message.ShouldContain(fragment, Case.Sensitive);
        faultedState.ShouldBe(TransactionState.Faulted);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    /// <summary>A statement whose result the server cannot encode fails for the client, so it aborts the transaction too.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - A result the server cannot encode aborts the explicit transaction")]
    public async Task QueryAsync_ResultEncodingFailsInsideExplicitTransaction_ShouldAbortTransaction()
    {
        // Arrange: the typed API stores a UInt32 property, which the wire value codec cannot encode.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using (var typed = await harness.Database.CreateSessionAsync(harness.Token))
        {
            await harness.Database.CreateNodeAsync(typed, ["Unsigned"], new Dictionary<string, object?> { ["p"] = 7u }, harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Pending)", cancellationToken: harness.Token);

        // Act
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (n:Unsigned) RETURN n.p", cancellationToken: harness.Token));
        var faultedState = transaction.State;
        var commitError = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldContain("UInt32", Case.Sensitive);
        faultedState.ShouldBe(TransactionState.Faulted);
        commitError.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        commitError.Message.ShouldContain("UInt32", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        await using var observer = await harness.Database.CreateSessionAsync(harness.Token);
        var labels = new List<string?>();
        var result = (QueryResultSet)await observer.ExecuteAsync("SHOW LABELS", cancellationToken: harness.Token);
        await using (result)
        {
            await foreach (var row in result.GetRowsAsync(harness.Token)) { labels.Add(row.GetString(2)); }
        }
        labels.ShouldBe(["Unsigned"]);
    }

    /// <summary>COMMIT of a transaction a wire statement aborted commits nothing; the session returns to autocommit.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Commit after a failed wire statement commits nothing")]
    public async Task CommitAsync_AfterFailedWireStatement_ShouldCommitNothing()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Pending {name: 'p'})", cancellationToken: harness.Token);
        await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (n:Missing) RETURN n.name", cancellationToken: harness.Token));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(harness.Token));
        (await connection.ExecuteAsync("CREATE (:After)", cancellationToken: harness.Token)).ShouldBe(1);

        // Assert
        error.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).Select(row => (string?)row[2]).ShouldBe(["After"]);
    }
}
