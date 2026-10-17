using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlFunctionCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Invoke: the supplied context alone determines whether application cancellation passes")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Invoke_ApplicationCancellation_ShouldUseContextToken(bool canceled, bool foreignToken)
    {
        // Arrange
        var original = foreignToken
            ? new TaskCanceledException("foreign cancellation", null, new CancellationToken(true))
            : new OperationCanceledException("application cancellation");
        var function = SqlScalarFunction.Create<int, int>("cancel_scalar", _ => throw original);

        // Act
        Exception failure = Should.Throw<Exception>(() =>
        {
            var context = new SqlFunctionContext(default, null, new CancellationToken(canceled));
            function.Invoke(new SqlArguments([SqlValue.FromInt32(1)], context));
        });

        // Assert
        if (canceled)
        {
            failure.ShouldBeSameAs(original);
        }
        else
        {
            failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE007");
            failure.Message.ShouldContain("Function 'cancel_scalar'", Case.Sensitive);
            failure.InnerException.ShouldBeSameAs(original);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregate: each phase uses the supplied statement token")]
    [InlineData("seed", false)]
    [InlineData("add", false)]
    [InlineData("finish", false)]
    [InlineData("seed", true)]
    [InlineData("add", true)]
    [InlineData("finish", true)]
    public void CreateAccumulator_ApplicationCancellation_ShouldUseStatementToken(string phase, bool canceled)
    {
        // Arrange
        var original = new OperationCanceledException("application cancellation");
        var function = SqlAggregateFunction.Create<int, int, int>("cancel_aggregate",
            () => phase == "seed" ? throw original : 0,
            (state, value) => phase == "add" ? throw original : state + value,
            state => throw original);

        // Act
        Exception failure = Should.Throw<Exception>(() =>
        {
            var context = new SqlFunctionContext(default, null, new CancellationToken(canceled));
            var accumulator = function.CreateAccumulator(context);
            if (phase != "seed")
            {
                accumulator.Add(new SqlArguments([SqlValue.FromInt32(1)], context));
            }
            accumulator.Finish(context.CancellationToken);
        });

        // Assert
        if (canceled)
        {
            failure.ShouldBeSameAs(original);
        }
        else
        {
            failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE007");
            failure.Message.ShouldContain("Function 'cancel_aggregate'", Case.Sensitive);
            failure.InnerException.ShouldBeSameAs(original);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Finish: a direct call has no statement to cancel")]
    public void Finish_DirectCall_ShouldCodeApplicationCancellation()
    {
        // Arrange
        var original = new OperationCanceledException(new CancellationToken(true));
        var function = SqlAggregateFunction.Create<int, int, int>("direct_finish", () => 0,
            (state, value) => state + value, _ => throw original);
        var context = new SqlFunctionContext(default, null, new CancellationToken(true));
        var accumulator = function.CreateAccumulator(context);
        accumulator.Add(new SqlArguments([SqlValue.FromInt32(1)]));

        // Act
        var failure = Should.Throw<DatabaseException>(() => accumulator.Finish());

        // Assert
        failure.DiagnosticDetail("COHSQLE007").ShouldStartWith("Function 'direct_finish' failed:", Case.Sensitive);
        failure.InnerException.ShouldBeSameAs(original);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Invoke: default arguments have no statement to cancel")]
    public void Invoke_DefaultContext_ShouldCodeApplicationCancellation()
    {
        // Arrange
        var original = new OperationCanceledException(new CancellationToken(true));
        var function = SqlScalarFunction.Create<int, int>("direct_cancel", _ => throw original);

        // Act
        var failure = Should.Throw<DatabaseException>(() => function.Invoke(new SqlArguments([SqlValue.FromInt32(1)])));

        // Assert
        failure.DiagnosticDetail("COHSQLE007").ShouldStartWith("Function 'direct_cancel' failed:", Case.Sensitive);
        failure.InnerException.ShouldBeSameAs(original);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Domain cancellation: a check registered only in Types still receives the statement token")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OnlyDomainCheckRegistered_ShouldUseStatementToken(bool cancel)
    {
        // Arrange
        using var statement = new CancellationTokenSource();
        var original = new OperationCanceledException("domain cancellation");
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("domain-cancellation");
        builder.Types.Add(SqlType.CreateDomain("checked_int", SqlType.Integer,
            SqlScalarFunction.Create<int, bool>("domain_cancel", _ =>
            {
                calls++;
                if (cancel)
                {
                    statement.Cancel();
                }
                throw original;
            }, SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)", null, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", null, CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<Exception>(async () =>
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT CAST(id AS checked_int) FROM t", null, statement.Token);
            await foreach (var row in result.GetRowsAsync(statement.Token))
            {
                _ = row.FieldCount;
            }
        });

        // Assert
        calls.ShouldBe(1);
        if (cancel)
        {
            failure.ShouldBeAssignableTo<OperationCanceledException>();
            statement.IsCancellationRequested.ShouldBeTrue();
        }
        else
        {
            failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("COHSQLE007");
            failure.Message.ShouldContain("Function 'domain_cancel'", Case.Sensitive);
            failure.InnerException.ShouldBeSameAs(original);
        }
    }
}
