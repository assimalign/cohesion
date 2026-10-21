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
        builder.Functions.Add(new TimestampFunction()).Add(new TimestampAggregate()).Add(new TimestampValueFunction());
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
            (await SqlConstantSelectTests.RowsAsync(session, "SELECT timestamp_value() = now(), timestamp_value() = CURRENT_TIMESTAMP"))
                .ShouldHaveSingleItem().ShouldBe(new object?[] { true, true });
        }
        if (explicitTransaction) { await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None); }
        (await SqlConstantSelectTests.RowsAsync(session, "SELECT statement_tick(), group_tick(1)")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { clock.Instant.UtcTicks, clock.Instant.UtcTicks });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Function context: absent statement contexts have no timestamp (#1373)")]
    public void Construct_DirectContext_ShouldExposeExplicitOrNullTimestamp()
    {
        // Arrange
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        // Act
        var supplied = new SqlFunctionContext(default, null, CancellationToken.None, instant);
        var captured = new SqlFunctionContext(default, null, CancellationToken.None);
        SqlFunctionContext empty = default;

        // Assert
        supplied.TransactionTimestamp.ShouldBe(instant);
        captured.TransactionTimestamp.ShouldBeNull();
        empty.TransactionTimestamp.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Function context: table and domain CHECK see the transaction timestamp (#1373)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CheckInTransaction_ShouldReceiveTransactionTimestamp(bool domain)
    {
        // Arrange
        var clock = new ScalarClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var start = clock.Instant;
        var builder = SqlDatabaseEngine.CreateBuilder("check-timestamp");
        builder.Options.TimeProvider = clock;
        builder.Functions.Add(new CheckTimestampFunction());
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (domain) { await session.ExecuteAsync("CREATE DOMAIN tick_domain AS BIGINT CHECK (check_tick(VALUE))", cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync(domain ? "CREATE TABLE t (tick tick_domain)" : "CREATE TABLE t (tick BIGINT CHECK (check_tick(tick)))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        clock.Instant += TimeSpan.FromDays(1);

        // Act
        await session.ExecuteAsync($"INSERT INTO t VALUES ({start.UtcTicks})", cancellationToken: CancellationToken.None);
        clock.Instant += TimeSpan.FromDays(1);
        await session.ExecuteAsync($"INSERT INTO t VALUES ({start.UtcTicks})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);

        // Assert
        (await SqlConstantSelectTests.RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    private sealed class TimestampFunction : SqlScalarFunction
    {
        internal TimestampFunction() : base("statement_tick", [], SqlType.BigInt, SqlFunctionVolatility.Stable) { }
        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromInt64(arguments.Context.TransactionTimestamp!.Value.UtcTicks);
    }

    private sealed class TimestampValueFunction : SqlScalarFunction
    {
        internal TimestampValueFunction() : base("timestamp_value", [], SqlType.TimestampTz, SqlFunctionVolatility.Stable) { }
        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromDateTimeOffset(arguments.Context.TransactionTimestamp!.Value);
    }

    private sealed class CheckTimestampFunction : SqlScalarFunction
    {
        internal CheckTimestampFunction() : base("check_tick", [SqlType.BigInt], SqlType.Boolean, SqlFunctionVolatility.Immutable) { }
        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromBoolean(arguments.Context.TransactionTimestamp?.UtcTicks == arguments[0].AsInt64());
    }

    private sealed class TimestampAggregate : SqlAggregateFunction
    {
        internal TimestampAggregate() : base("group_tick", [SqlType.Integer], SqlType.BigInt) { }
        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
            => new Accumulator(context.TransactionTimestamp!.Value.UtcTicks);

        private sealed class Accumulator : SqlAggregateAccumulator
        {
            private readonly long _ticks;
            internal Accumulator(long ticks) { _ticks = ticks; }
            protected override void AddCore(scoped in SqlArguments arguments)
                => arguments.Context.TransactionTimestamp!.Value.UtcTicks.ShouldBe(_ticks);
            protected override SqlValue FinishCore() => SqlValue.FromInt64(_ticks);
        }
    }
}
