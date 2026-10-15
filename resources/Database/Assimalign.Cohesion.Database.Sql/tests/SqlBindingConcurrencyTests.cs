using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition("SQL binding concurrency", DisableParallelization = true)]
public sealed class SqlBindingConcurrencyCollection;

[Collection("SQL binding concurrency")]
public sealed class SqlBindingConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binding: unrelated statements and collations progress while another callback blocks")]
    [InlineData(true)]
    [InlineData(false)]
    public Task ExecuteAsync_IndependentBinding_ShouldCompleteWhileCallbackBlocks(bool refresh)
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            var builder = SqlDatabaseEngine.CreateBuilder("binding-concurrency");
            builder.Functions.Add(SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable));
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var first = await database.CreateSessionAsync(CancellationToken.None);
            await using var second = await database.CreateSessionAsync(CancellationToken.None);
            await QueryAsync(first, "CREATE FUNCTION review_sql(p TEXT) RETURNS INTEGER IMMUTABLE RETURN review_gate(1)");
            gate.Arm();
            var computing = new SqlBindingCaller(() => QueryAsync(first, refresh
                ? "CREATE FUNCTION another_sql(p INT) RETURNS INT RETURN p"
                : "SELECT review_sql('a' COLLATE case_insensitive) FROM INFORMATION_SCHEMA.TABLES"));
            SqlBindingCaller? independent = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();

                // Act
                independent = new SqlBindingCaller(() => QueryAsync(second, refresh
                    ? "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES"
                    : "SELECT review_sql('a' COLLATE case_accent_insensitive) FROM INFORMATION_SCHEMA.TABLES"));
                var failure = await independent.Completion.WaitAsync(TimeSpan.FromSeconds(3));

                // Assert
                failure.ShouldBeNull();
                computing.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(refresh ? 1 : 2);
            }
            finally
            {
                gate.Release();
                (await computing.JoinAsync()).ShouldBeNull();
                if (independent is not null) { await independent.JoinAsync(); }
            }
        });

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Binding: a same-collation waiter cancels before the computing callback returns")]
    public Task ExecuteAsync_CanceledBindingWaiter_ShouldLeaveComputationRunning()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            using var cancellation = new CancellationTokenSource();
            var builder = SqlDatabaseEngine.CreateBuilder("binding-waiter");
            builder.Functions.Add(SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable));
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var first = await database.CreateSessionAsync(CancellationToken.None);
            await using var second = await database.CreateSessionAsync(CancellationToken.None);
            await QueryAsync(first, "CREATE FUNCTION review_sql(p TEXT) RETURNS INTEGER IMMUTABLE RETURN review_gate(1)");
            const string sql = "SELECT review_sql('a' COLLATE case_insensitive) FROM INFORMATION_SCHEMA.TABLES";
            gate.Arm();
            var computing = new SqlBindingCaller(() => QueryAsync(first, sql));
            SqlBindingCaller? waiter = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();
                waiter = new SqlBindingCaller(() => QueryAsync(second, sql, cancellation.Token));
                waiter.WaitUntilBlocked().ShouldBeTrue();
                waiter.Completion.IsCompleted.ShouldBeFalse();

                // Act
                cancellation.Cancel();
                var failure = await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(3));

                // Assert
                failure.ShouldBeAssignableTo<OperationCanceledException>();
                computing.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(1);
            }
            finally
            {
                gate.Release();
                (await computing.JoinAsync()).ShouldBeNull();
                if (waiter is not null) { await waiter.JoinAsync(); }
            }
            await QueryAsync(second, sql);
            gate.Calls.ShouldBe(1);
        });

    private static async Task QueryAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken = default)
    {
        var result = await session.ExecuteAsync(sql, null, cancellationToken);
        result.Status.ShouldBe(QueryResultStatus.Success);
        if (result is QueryResultSet rows)
        {
            await using (rows)
            {
                await foreach (var row in rows.GetRowsAsync(cancellationToken)) { _ = row.FieldCount; }
            }
        }
    }

    private static async Task RepeatAsync(Func<Task> run)
    {
        var elapsed = Stopwatch.StartNew();
        int rounds = 0;
        do
        {
            await run();
            rounds++;
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        }
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 3);
        rounds.ShouldBeGreaterThanOrEqualTo(3);
    }
}
