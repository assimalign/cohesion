using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDiscardedProposalTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - ON CONFLICT: discarded oversized rows and non-arbiter keys succeed (#1266)")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_DiscardedOversizedProposal_ShouldPreserveConflictBehavior(bool oversizedKey, bool update, bool select)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("discarded-proposal", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, v TEXT{(oversizedKey ? " UNIQUE" : "")})", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 'original')", cancellationToken: timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        string source = select ? "SELECT 1, @v" : "VALUES (1, @v)";
        string action = update ? "DO UPDATE SET v = 'x'" : "DO NOTHING";

        // Act
        var result = await session.ExecuteAsync($"INSERT INTO t {source} ON CONFLICT(id) {action}",
            new Dictionary<string, object?> { ["v"] = new string('a', oversizedKey ? 2000 : 9000) }, timeout.Token);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        result.AffectedCount.ShouldBe(update ? 1 : 0);
        transaction.State.ShouldBe(TransactionState.Active);
        await transaction.CommitAsync(timeout.Token);
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT id, v FROM t", cancellationToken: timeout.Token);
        (await SqlT1ScalarTests.ReadAsync(rows)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, update ? "x" : "original" });
    }
}
