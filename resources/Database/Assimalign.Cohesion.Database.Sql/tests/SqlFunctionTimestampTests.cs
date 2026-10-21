using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlFunctionTimestampTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Function context: scalar and aggregate timestamps follow transaction lifetime (#1373)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ApplicationFunctions_ShouldReceiveTransactionTimestamp(bool explicitTransaction)
    {
        // Arrange
        var clock = new ScalarClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var builder = SqlDatabaseEngine.CreateBuilder("function-timestamp");
        builder.Options.TimeProvider = clock;
        builder.Functions.Add(new TimestampFunction()).Add(new TimestampAggregate());
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var start = clock.Instant;
        if (explicitTransaction) { await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None); }

        // Act / Assert
        for (int i = 0; i < 3; i++)
        {
            clock.Instant += TimeSpan.FromDays(1);
            var expected = (explicitTransaction ? start : clock.Instant).UtcTicks;
            var rows = await SqlConstantSelectTests.RowsAsync(session, "SELECT statement_tick(), group_tick(1), group_tick(DISTINCT 1), group_tick(1 ORDER BY 1)");
            rows.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, expected, expected, expected });
        }
        if (explicitTransaction) { await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None); }
        (await SqlConstantSelectTests.RowsAsync(session, "SELECT statement_tick(), group_tick(1)")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { clock.Instant.UtcTicks, clock.Instant.UtcTicks });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Function context: direct construction supplies a timestamp and default remains deterministic (#1373)")]
    public void Construct_DirectContext_ShouldExposeExplicitOrCapturedTimestamp()
    {
        // Arrange
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var before = DateTimeOffset.UtcNow;

        // Act
        var supplied = new SqlFunctionContext(default, null, CancellationToken.None, instant);
        var captured = new SqlFunctionContext(default, null, CancellationToken.None);
        SqlFunctionContext empty = default;

        // Assert
        supplied.TransactionTimestamp.ShouldBe(instant);
        captured.TransactionTimestamp.ShouldBeInRange(before, DateTimeOffset.UtcNow);
        empty.TransactionTimestamp.ShouldBe(DateTimeOffset.MinValue);
    }

    private sealed class TimestampFunction : SqlScalarFunction
    {
        internal TimestampFunction() : base("statement_tick", [], SqlType.BigInt, SqlFunctionVolatility.Stable) { }
        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromInt64(arguments.Context.TransactionTimestamp.UtcTicks);
    }

    private sealed class TimestampAggregate : SqlAggregateFunction
    {
        internal TimestampAggregate() : base("group_tick", [SqlType.Integer], SqlType.BigInt) { }
        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
            => new Accumulator(context.TransactionTimestamp.UtcTicks);

        private sealed class Accumulator : SqlAggregateAccumulator
        {
            private readonly long _ticks;
            internal Accumulator(long ticks) { _ticks = ticks; }
            protected override void AddCore(scoped in SqlArguments arguments)
                => arguments.Context.TransactionTimestamp.UtcTicks.ShouldBe(_ticks);
            protected override SqlValue FinishCore() => SqlValue.FromInt64(_ticks);
        }
    }
}
