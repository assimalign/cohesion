using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlQualifierTests
{
    public static IEnumerable<object[]> InvalidQualifiers()
    {
        foreach (bool empty in new[] { false, true })
        {
            foreach (bool explicitTransaction in new[] { false, true })
            {
                foreach (string alias in new[] { "", " AS x" })
                {
                    foreach (string reference in alias.Length == 0
                        ? new[] { "bogus.a", "missing.t.a" }
                        : new[] { "t.a", "dbo.t.a", "bogus.a", "dbo.x.a" })
                    {
                        yield return [$"UPDATE t{alias} SET a = {reference} + 1", alias, reference, empty, explicitTransaction];
                        yield return [$"UPDATE t{alias} SET a = 3 WHERE {reference} = 2", alias, reference, empty, explicitTransaction];
                        yield return [$"DELETE FROM t{alias} WHERE {reference} = 2", alias, reference, empty, explicitTransaction];
                    }
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML qualifiers: hidden and unknown names fail like SELECT without changing rows or transaction state")]
    [MemberData(nameof(InvalidQualifiers))]
    public async Task Execute_InvalidQualifier_ShouldRejectBeforeWritingAsync(
        string statement, string alias, string reference, bool empty, bool explicitTransaction)
    {
        // Arrange: an invalid SET must be rejected even on empty input, and an invalid
        // WHERE must not reach either row. PostgreSQL parse_relation.c:3759 hides aliases.
        await using var engine = SqlDatabaseEngine.Create("dml-qualifiers", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        if (!empty)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES (1), (2)", cancellationToken: CancellationToken.None);
        }
        await using var transaction = explicitTransaction
            ? await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None) : null;
        string select = $"SELECT {reference} FROM t{alias}";

        // Act
        var selectFailure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(select, cancellationToken: CancellationToken.None));
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert: preserve SELECT's location and span for a qualified reference.
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        var selected = selectFailure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE015");
        diagnostic.Code.ShouldBe(selected.Code);
        diagnostic.Start.ShouldBe(statement.IndexOf(reference, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + selected.End - selected.Start);
        (diagnostic.End - diagnostic.Start).ShouldBe(selected.End - selected.Start);
        failure.Message.ShouldBe(selectFailure.Message);
        (await ReadAsync(session)).ShouldBe(empty ? Array.Empty<int>() : new[] { 1, 2 });
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Active);
            session.CurrentTransaction.ShouldBeSameAs(transaction);
            // A later valid write and commit prove that rejection left the transaction usable.
            await session.ExecuteAsync("INSERT INTO t VALUES (4)", cancellationToken: CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            transaction.State.ShouldBe(TransactionState.Committed);
            (await ReadAsync(session)).ShouldBe(empty ? new[] { 4 } : new[] { 1, 2, 4 });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML qualifiers: target names and aliases work in SET and WHERE")]
    [InlineData("", "t.a")]
    [InlineData("", "dbo.t.a")]
    [InlineData(" AS x", "x.a")]
    [InlineData(" AS x", "X.a")]
    public async Task Execute_VisibleQualifier_ShouldUpdateAndDeleteAsync(string alias, string reference)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-visible", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        // Act
        var update = await session.ExecuteAsync($"UPDATE t{alias} SET a = {reference} + 2 WHERE {reference} = 1", cancellationToken: CancellationToken.None);
        var afterUpdate = await ReadAsync(session);
        var delete = await session.ExecuteAsync($"DELETE FROM t{alias} WHERE {reference} = 2", cancellationToken: CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        update.Status.ShouldBe(DatabaseResultStatus.Success);
        update.AffectedCount.ShouldBe(1);
        afterUpdate.ShouldBe(new[] { 2, 3 });
        delete.Status.ShouldBe(DatabaseResultStatus.Success);
        delete.AffectedCount.ShouldBe(1);
        (await ReadAsync(session)).ShouldBe(new[] { 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML qualifiers: SET retains its refusal and predicates validate names during binding")]
    [InlineData("UPDATE t AS x SET a = (SELECT t.a FROM t AS x)", true)]
    [InlineData("UPDATE t AS x SET a = 2 WHERE a IN (SELECT bogus.a FROM t)", false)]
    [InlineData("DELETE FROM t AS x WHERE EXISTS (SELECT t.a FROM t AS x)", false)]
    public async Task Execute_DmlSubquery_ShouldValidatePositionAndQualifierAsync(string statement, bool assignment)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-subquery", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        DatabaseException failure = assignment
            ? await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask())
            : await Should.ThrowAsync<SqlPlanningException>(() => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == (assignment ? "COHDBL001" : "COHSQLE015") && diagnostic.Start.HasValue);
        transaction.State.ShouldBe(TransactionState.Active);
        (await ReadAsync(session)).ShouldBe(new[] { 1 });
    }

    private static async Task<int[]> ReadAsync(SqlDatabaseSession session)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT a FROM t ORDER BY a", cancellationToken: CancellationToken.None);
        var values = new List<int>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            values.Add((int)row.GetValue(0)!);
        }
        return values.ToArray();
    }
}
