using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Proves the arithmetic-fault contract (#1069) through the real SQL server and the
/// typed client: division by zero and numeric overflow are coded execution failures
/// that leave the connection, and any open transaction, usable for the next command.
/// </summary>
public sealed class SqlArithmeticWireTests
{
    private const string DivisionByZero = "COHSQLE001";
    private const string OutOfRange = "COHSQLE002";

    /// <summary>Every fault reaches the client as a coded execution failure on a still-usable connection.</summary>
    /// <param name="statement">The faulting command.</param>
    /// <param name="code">The expected engine diagnostic code.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: faults are coded execution failures that keep the connection")]
    [InlineData("SELECT score / 0 FROM users", DivisionByZero)]
    [InlineData("SELECT score % (id - id) FROM users", DivisionByZero)]
    [InlineData("SELECT CAST(score AS DECIMAL) / 0 FROM users", DivisionByZero)]
    [InlineData("SELECT id FROM users WHERE score / (id - 1) > 0", DivisionByZero)]
    [InlineData("UPDATE users SET score = score / (id - 2)", DivisionByZero)]
    [InlineData("SELECT score + 9223372036854775708 FROM users", OutOfRange)]
    [InlineData("SELECT score * 92233720368547759 FROM users", OutOfRange)]
    [InlineData("SELECT -(-9223372036854775807 - 1) FROM users", OutOfRange)]
    [InlineData("SELECT ABS(-9223372036854775807 - 1) FROM users", OutOfRange)]
    [InlineData("SELECT (-9223372036854775807 - 1) / -1 FROM users", OutOfRange)]
    [InlineData("SELECT SUM(CAST('79228162514264337593543950335' AS DECIMAL)) FROM users", OutOfRange)]
    [InlineData("UPDATE users SET id = id + 2147483647", OutOfRange)]
    public async Task QueryAsync_ArithmeticFault_ShouldReturnCodedExecutionFailure(string statement, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert: a statement failure, not a terminated session, and nothing was written.
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldStartWith(code + ":", Case.Sensitive);
        SqlResultSet scores = await connection.QueryAsync("SELECT id, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        scores.Select(row => (row["id"], row["score"])).ShouldBe([(1, 100L), (2, 200L)]);
    }

    /// <summary>A bound zero divisor of every numeric runtime type is caught, not only literal zero.</summary>
    /// <param name="divisor">The bound divisor.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: bound zero divisors of every numeric type fail with COHSQLE001")]
    [MemberData(nameof(ZeroDivisors))]
    public async Task QueryAsync_BoundZeroDivisor_ShouldFailWithCode(object divisor)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?> { ["divisor"] = divisor };

        // Act / Assert: division and modulo, then the same connection keeps working.
        foreach (string op in new[] { "/", "%" })
        {
            SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync($"SELECT score {op} @divisor FROM users", parameters, SqlClientTestHarness.Timeout()));
            failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            failure.Message.ShouldStartWith(DivisionByZero + ":", Case.Sensitive);
        }

        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    /// <summary>A bound nonzero divisor too small for Decimal is out of range, never reported as zero.</summary>
    /// <param name="divisor">The bound nonzero divisor.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: a nonzero approximate divisor below Decimal's step fails with COHSQLE002")]
    [MemberData(nameof(UnderflowingDivisors))]
    public async Task QueryAsync_UnderflowingApproximateDivisor_ShouldFailOutOfRange(object divisor)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?> { ["divisor"] = divisor };

        // Act / Assert
        foreach (string op in new[] { "/", "%" })
        {
            SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync($"SELECT score {op} @divisor FROM users", parameters, SqlClientTestHarness.Timeout()));
            failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            failure.ConnectionUsable.ShouldBeTrue();
            failure.Message.ShouldStartWith(OutOfRange + ":", Case.Sensitive);
            failure.Message.ShouldContain("underflows DECIMAL", Case.Sensitive);
        }

        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    /// <summary>Supplies a nonzero REAL and DOUBLE below Decimal's smallest step.</summary>
    /// <returns>The underflowing divisors.</returns>
    public static IEnumerable<object[]> UnderflowingDivisors() => [[1e-30f], [1e-30d]];

    /// <summary>Incomparable ORDER BY keys fail the command, not the connection.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Evaluation: incomparable ORDER BY keys are an execution failure that keeps the connection")]
    public async Task QueryAsync_IncomparableOrderByKeys_ShouldKeepConnection()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT id FROM users ORDER BY CASE WHEN id = 1 THEN 'a' ELSE id END",
                cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldStartWith("Cannot compare values of types", Case.Sensitive);
        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    /// <summary>A guarded divisor does not fault: AND stops once its left operand is FALSE.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: an AND guard prevents a division by zero over the wire")]
    public async Task QueryAsync_GuardedDivision_ShouldNotFault()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync(
            "SELECT COUNT(*) AS total FROM users WHERE score - score <> 0 AND id / (score - score) > 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.ShouldHaveSingleItem()["total"].ShouldBe(0L);
    }

    /// <summary>Supplies a zero of each numeric runtime type the wire codec carries.</summary>
    /// <returns>The zero divisors.</returns>
    public static IEnumerable<object[]> ZeroDivisors()
        => [[(sbyte)0], [(short)0], [0], [0L], [0f], [0d], [0m]];

    /// <summary>A fault inside a transaction leaves it open: earlier work commits, the failed command applies nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: a fault inside a transaction keeps it open for COMMIT")]
    public async Task ExecuteAsync_FaultInsideTransaction_ShouldKeepTransactionState()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await using var observer = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("BEGIN", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (3, 'lin', 300)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act: the update faults on its second row, after the first row's value was computed.
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("UPDATE users SET score = score / (id - 2)", cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Message.ShouldStartWith(DivisionByZero + ":", Case.Sensitive);
        SqlResultSet inside = await connection.QueryAsync("SELECT id, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        SqlResultSet outside = await observer.QueryAsync("SELECT COUNT(*) AS total FROM users",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("COMMIT", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        inside.Select(row => (row["id"], row["score"])).ShouldBe([(1, 100L), (2, 200L), (3, 300L)]);
        outside.ShouldHaveSingleItem()["total"].ShouldBe(2L);
        SqlResultSet committed = await observer.QueryAsync("SELECT id, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        committed.Select(row => (row["id"], row["score"])).ShouldBe([(1, 100L), (2, 200L), (3, 300L)]);
    }

    /// <summary>Results exactly at the BIGINT bounds, and integer division semantics, survive the wire unchanged.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: in-range BIGINT results are exact over the wire")]
    public async Task QueryAsync_InRangeBigInt_ShouldReturnExactValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync(
            "SELECT score + 9223372036854775707 AS maximum, -score - 9223372036854775708 AS minimum, " +
            "(-9223372036854775807 - 1) % -1 AS remainder, -7 / 2 AS quotient, 2147483647 + id AS widened " +
            "FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.ShouldAllBe(column => column.Type == DatabaseType.Int64);
        SqlRow row = result.ShouldHaveSingleItem();
        row["maximum"].ShouldBeOfType<long>().ShouldBe(long.MaxValue);
        row["minimum"].ShouldBeOfType<long>().ShouldBe(long.MinValue);
        row["remainder"].ShouldBeOfType<long>().ShouldBe(0L);
        row["quotient"].ShouldBeOfType<long>().ShouldBe(-3L);
        row["widened"].ShouldBeOfType<long>().ShouldBe(2147483648L);
    }

    /// <summary>ABS accepts every numeric storage type over the wire, REAL included, with matching metadata.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Arithmetic: ABS accepts every numeric storage type over the wire")]
    public async Task QueryAsync_AbsOfEveryNumericType_ShouldReturnMagnitudeAndType()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync(
            "CREATE TABLE magnitudes (tiny_value TINYINT, small_value SMALLINT, int_value INT, big_value BIGINT, " +
            "real_value REAL, double_value DOUBLE, decimal_value DECIMAL(6, 2))",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync(
            "INSERT INTO magnitudes VALUES (-8, -300, -2147483647 - 1, -5000000000, -1.5, -2.25, -3.75)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync(
            "SELECT ABS(tiny_value) AS tiny, ABS(small_value) AS small, ABS(int_value) AS whole, ABS(big_value) AS big, " +
            "ABS(real_value) AS single, ABS(double_value) AS wide, ABS(decimal_value) AS exact FROM magnitudes",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: REAL no longer throws, and INT's minimum widens instead of overflowing.
        result.Columns.Select(column => column.Type).ShouldBe(
        [
            DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64,
            DatabaseType.Float32, DatabaseType.Float64, DatabaseType.Decimal,
        ]);
        SqlRow row = result.ShouldHaveSingleItem();
        row["tiny"].ShouldBeOfType<long>().ShouldBe(8L);
        row["small"].ShouldBeOfType<long>().ShouldBe(300L);
        row["whole"].ShouldBeOfType<long>().ShouldBe(2147483648L);
        row["big"].ShouldBeOfType<long>().ShouldBe(5000000000L);
        row["single"].ShouldBeOfType<float>().ShouldBe(1.5f);
        row["wide"].ShouldBeOfType<double>().ShouldBe(2.25d);
        row["exact"].ShouldBeOfType<decimal>().ShouldBe(3.75m);
    }
}
