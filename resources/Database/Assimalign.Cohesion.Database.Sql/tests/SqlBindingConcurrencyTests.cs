using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

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

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binding: canceled or faulted computations are retried by a concurrent caller")]
    [InlineData("body", "cancel")]
    [InlineData("body", "return-after-cancel")]
    [InlineData("body", "throw")]
    [InlineData("refresh", "cancel")]
    [InlineData("refresh", "return-after-cancel")]
    [InlineData("refresh", "throw")]
    [InlineData("table", "cancel")]
    [InlineData("table", "return-after-cancel")]
    [InlineData("table", "throw")]
    public Task ExecuteAsync_UnsuccessfulBinding_ShouldRetryWithoutPoisoningEntry(string binding, string failureMode)
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            using var cancellation = new CancellationTokenSource();
            using var waiterCancellation = new CancellationTokenSource();
            var builder = SqlDatabaseEngine.CreateBuilder("binding-retry");
            var function = SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable);
            builder.Functions.Add(function);
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var first = await database.CreateSessionAsync(CancellationToken.None);
            await using var second = await database.CreateSessionAsync(CancellationToken.None);
            await using var third = await database.CreateSessionAsync(CancellationToken.None);
            await QueryAsync(first, "CREATE FUNCTION review_sql(p TEXT) RETURNS INTEGER IMMUTABLE RETURN review_gate(1)");
            await CreateUnboundTableAsync(database, "cold", function);
            string sql = binding switch
            {
                "refresh" => "SELECT review_sql('a') FROM INFORMATION_SCHEMA.TABLES",
                "table" => "INSERT INTO cold (id) VALUES (1)",
                _ => "SELECT review_sql('a' COLLATE case_insensitive) FROM INFORMATION_SCHEMA.TABLES",
            };
            gate.AfterRelease = () =>
            {
                if (failureMode == "cancel") { cancellation.Token.ThrowIfCancellationRequested(); }
                // Ordinary function faults are deliberately deferred by folding; exhausted stack aborts binding.
                if (failureMode == "throw") { throw new InsufficientExecutionStackException("binding failed"); }
            };
            gate.Arm();
            var computing = new SqlBindingCaller(() => QueryAsync(first, binding == "refresh"
                ? "CREATE FUNCTION another_sql(p INT) RETURNS INT RETURN p" : sql, cancellation.Token));
            SqlBindingCaller? waiter = null;
            SqlBindingCaller? canceledWaiter = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();
                waiter = new SqlBindingCaller(() => QueryAsync(second, sql));
                waiter.WaitUntilBlocked().ShouldBeTrue();
                waiter.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(1);

                if (failureMode is "cancel" or "throw")
                {
                    canceledWaiter = new SqlBindingCaller(() => QueryAsync(third, sql, waiterCancellation.Token));
                    canceledWaiter.WaitUntilBlocked().ShouldBeTrue();
                    canceledWaiter.Completion.IsCompleted.ShouldBeFalse();

                    // Act
                    waiterCancellation.Cancel();
                    var waiterFailure = await canceledWaiter.Completion.WaitAsync(TimeSpan.FromSeconds(3));

                    // Assert: cancellation must finish while the producer is still gated.
                    waiterFailure.ShouldBeAssignableTo<OperationCanceledException>()!
                        .CancellationToken.ShouldBe(waiterCancellation.Token);
                    cancellation.IsCancellationRequested.ShouldBeFalse();
                    computing.Completion.IsCompleted.ShouldBeFalse();
                    waiter.Completion.IsCompleted.ShouldBeFalse();
                    gate.Calls.ShouldBe(1);
                }

                // Act
                if (failureMode != "throw") { cancellation.Cancel(); }
                gate.Release();
                var failure = await computing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                var retryFailure = await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5));

                // Assert
                if (failureMode == "throw")
                {
                    failure.ShouldBeAssignableTo<DatabaseException>();
                    failure.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull().Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE004");
                }
                else { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
                retryFailure.ShouldBeNull();
                gate.Calls.ShouldBe(2);
            }
            finally
            {
                gate.Release();
                await computing.JoinAsync();
                if (waiter is not null) { await waiter.JoinAsync(); }
                if (canceledWaiter is not null) { await canceledWaiter.JoinAsync(); }
            }
            await QueryAsync(second, sql);
            gate.Calls.ShouldBe(2);
        });

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Table binding: cache hits and other misses proceed while a waiter cancels")]
    public Task ExecuteAsync_TableBinding_ShouldCoordinateOnlyTheRequestedVersion()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            using var cancellation = new CancellationTokenSource();
            var builder = SqlDatabaseEngine.CreateBuilder("table-binding-concurrency");
            var function = SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable);
            builder.Functions.Add(function);
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var first = await database.CreateSessionAsync(CancellationToken.None);
            await using var second = await database.CreateSessionAsync(CancellationToken.None);
            await using var third = await database.CreateSessionAsync(CancellationToken.None);
            await QueryAsync(third, "CREATE TABLE warm (id INT)");
            await CreateUnboundTableAsync(database, "cold", function);
            await CreateUnboundTableAsync(database, "other", function);
            gate.Arm();
            var computing = new SqlBindingCaller(() => QueryAsync(first, "INSERT INTO cold (id) VALUES (1)"));
            SqlBindingCaller? waiter = null;
            SqlBindingCaller? independent = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();
                waiter = new SqlBindingCaller(() => QueryAsync(second, "INSERT INTO cold (id) VALUES (2)", cancellation.Token));
                waiter.WaitUntilBlocked().ShouldBeTrue();
                waiter.Completion.IsCompleted.ShouldBeFalse();

                // Act
                cancellation.Cancel();
                (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeAssignableTo<OperationCanceledException>();
                independent = new SqlBindingCaller(async () =>
                {
                    await QueryAsync(third, "INSERT INTO warm VALUES (1)");
                    await QueryAsync(third, "INSERT INTO other (id) VALUES (1)");
                });

                // Assert
                (await independent.Completion.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeNull();
                computing.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(2);
            }
            finally
            {
                gate.Release();
                (await computing.JoinAsync()).ShouldBeNull();
                if (waiter is not null) { await waiter.JoinAsync(); }
                if (independent is not null) { await independent.JoinAsync(); }
            }
            await QueryAsync(second, "INSERT INTO cold (id) VALUES (3)");
            gate.Calls.ShouldBe(2);
        });

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Validation: a waiter cancels and table hits proceed during a binding callback")]
    public Task FindUnresolved_ConcurrentValidation_ShouldHonorWaiterCancellation()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            using var cancellation = new CancellationTokenSource();
            var builder = SqlDatabaseEngine.CreateBuilder("validation-concurrency");
            var function = SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable);
            builder.Functions.Add(function);
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await QueryAsync(session, "CREATE TABLE warm (id INT)");
            await CreateUnboundTableAsync(database, "cold", function);
            gate.Arm();
            var computing = new SqlBindingCaller(() =>
            {
                database.Definitions.FindUnresolved(CancellationToken.None).ShouldBeNull();
                return Task.CompletedTask;
            });
            SqlBindingCaller? waiter = null;
            SqlBindingCaller? independent = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();
                waiter = new SqlBindingCaller(() =>
                {
                    database.Definitions.FindUnresolved(cancellation.Token);
                    return Task.CompletedTask;
                });
                waiter.WaitUntilBlocked().ShouldBeTrue();
                waiter.Completion.IsCompleted.ShouldBeFalse();

                // Act
                cancellation.Cancel();
                independent = new SqlBindingCaller(() => QueryAsync(session, "INSERT INTO warm VALUES (1)"));

                // Assert
                (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeAssignableTo<OperationCanceledException>();
                (await independent.Completion.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeNull();
                computing.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(1);
            }
            finally
            {
                gate.Release();
                (await computing.JoinAsync()).ShouldBeNull();
                if (waiter is not null) { await waiter.JoinAsync(); }
                if (independent is not null) { await independent.JoinAsync(); }
            }
            database.Definitions.FindUnresolved(CancellationToken.None).ShouldBeNull();
            database.Definitions.ValidationWalkCount.ShouldBe(1);
            gate.Calls.ShouldBe(1);
        });

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Refresh: an old table computation cannot overwrite the new environment's binding")]
    public Task RefreshDefinitions_InFlightTableBinding_ShouldPublishOnlyIntoItsOwnEnvironment()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var gate = new SqlBindingGate();
            var builder = SqlDatabaseEngine.CreateBuilder("refresh-concurrency");
            var function = SqlScalarFunction.Create<int, int>("review_gate", gate.Invoke, SqlFunctionVolatility.Immutable);
            builder.Functions.Add(function);
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            var table = await CreateUnboundTableAsync(database, "cold", function);
            gate.Arm();
            SqlBoundTable? old = null;
            SqlBoundTable? current = null;
            var computing = new SqlBindingCaller(() =>
            {
                old = database.Definitions.Get(table, CancellationToken.None);
                return Task.CompletedTask;
            });
            SqlBindingCaller? refreshing = null;
            try
            {
                gate.WaitUntilEntered().ShouldBeTrue();

                // Act
                refreshing = new SqlBindingCaller(async () =>
                {
                    await QueryAsync(session, "CREATE FUNCTION another_sql(p INT) RETURNS INT RETURN p");
                    current = database.Definitions.Get(table, CancellationToken.None);
                });

                // Assert
                (await refreshing.Completion.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBeNull();
                computing.Completion.IsCompleted.ShouldBeFalse();
                gate.Calls.ShouldBe(2);
            }
            finally
            {
                gate.Release();
                (await computing.JoinAsync()).ShouldBeNull();
                if (refreshing is not null) { await refreshing.JoinAsync(); }
            }
            current.ShouldNotBeNull();
            old.ShouldNotBeSameAs(current);
            database.Definitions.Get(table, CancellationToken.None).ShouldBeSameAs(current);
            gate.Calls.ShouldBe(2);
        });

    private static async ValueTask<SqlCatalogTable> CreateUnboundTableAsync(SqlDatabase database, string name, SqlScalarFunction function)
    {
        // CHECK binding still folds immutable calls. DEFAULT loading must never invoke one.
        // Publish directly so the first table-cache miss, rather than DDL, reaches the gate.
        var table = await database.Catalog.ReserveTableAsync("dbo", name,
        [
            new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)),
            new SqlCatalogColumn("value", new DatabaseTypeInfo(DatabaseType.Int32), defaultLiteral: "1"),
        ], [],
        [
            new SqlCatalogConstraint("ck_binding", SqlCatalogConstraintKind.Check, [], checkExpression: "review_gate(1) > 0",
                checkFunctionCalls: [SqlDefinitionBinder.Key(function)]),
        ], DatabaseObjectOwner.Adhoc, null, cancellationToken: CancellationToken.None);
        await database.Catalog.PublishTableAsync(table, [], [], cancellationToken: CancellationToken.None);
        return table;
    }

    private static async Task QueryAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken = default)
    {
        var result = await session.ExecuteAsync(sql, null, cancellationToken);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        if (result is DatabaseResultSet rows)
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
