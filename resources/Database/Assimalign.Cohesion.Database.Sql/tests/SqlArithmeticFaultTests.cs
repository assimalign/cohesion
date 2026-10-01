using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Arithmetic faults (#1069): division and modulo by zero and numeric overflow fail
/// only their statement, with a stable code, in process and over the SQL server.
/// The session, and an open transaction's state, survive for the next statement.
/// </summary>
public sealed class SqlArithmeticFaultTests
{
    private const string DivisionByZero = "COHSQLE001";
    private const string OutOfRange = "COHSQLE002";

    private const string CreateNumbers =
        "CREATE TABLE numbers (id INT PRIMARY KEY, tiny_value TINYINT, small_value SMALLINT, int_value INT, " +
        "big_value BIGINT, real_value REAL, double_value DOUBLE, decimal_value DECIMAL(10, 2), zero_value INT)";

    private const string InsertNumbers =
        "INSERT INTO numbers VALUES " +
        "(1, -8, -300, -70000, -5000000000, -1.5, -2.25, -3.75, 0), " +
        "(2, 8, 300, 70000, 9223372036854775807, 2.5, 4.5, 6.25, 0)";

    /// <summary>Every operand family divides or takes a remainder by zero, literal, column, computed, or bound.</summary>
    /// <param name="expression">The faulting projection.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: division and modulo by zero fail the statement with COHSQLE001")]
    [InlineData("int_value / 0")]
    [InlineData("int_value % 0")]
    [InlineData("tiny_value / (id - id)")]
    [InlineData("small_value % zero_value")]
    [InlineData("big_value / zero_value")]
    [InlineData("int_value / @zero")]
    [InlineData("decimal_value / 0")]
    [InlineData("decimal_value % 0.0")]
    [InlineData("real_value / 0")]
    [InlineData("double_value % zero_value")]
    [InlineData("int_value / @decimalZero")]
    [InlineData("int_value % @doubleZero")]
    public async Task Projection_DivisionByZero_ShouldFailWithCodeAndKeepSession(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var parameters = new Dictionary<string, object?> { ["zero"] = 0, ["decimalZero"] = 0m, ["doubleZero"] = 0d };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, $"SELECT {expression} FROM numbers WHERE id = 1", parameters));

        // Assert: a coded statement failure, not DivideByZeroException, and the session runs on.
        AssertCode(failure, DivisionByZero);
        failure.Message.ShouldStartWith("COHSQLE001: Division by zero", Case.Sensitive);
        session.State.ShouldBe(SessionState.Open);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM numbers")).ShouldBe(2L);
    }

    /// <summary>Every executable expression position reports the same code and applies nothing.</summary>
    /// <param name="statement">The faulting statement.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: division by zero fails in every statement position and writes nothing")]
    [InlineData("SELECT id FROM numbers WHERE int_value / zero_value > 0")]
    [InlineData("SELECT id FROM numbers ORDER BY int_value % zero_value")]
    [InlineData("SELECT zero_value, SUM(int_value) / 0 FROM numbers GROUP BY zero_value")]
    [InlineData("SELECT zero_value FROM numbers GROUP BY zero_value HAVING COUNT(*) / 0 > 0")]
    [InlineData("SELECT CASE WHEN id = 2 THEN id / 0 ELSE id END FROM numbers")]
    [InlineData("SELECT a.id FROM numbers a INNER JOIN numbers b ON a.id = b.id / b.zero_value")]
    [InlineData("SELECT id FROM numbers WHERE id = 1 / 0")]
    [InlineData("SELECT id FROM numbers LIMIT 1 / 0")]
    [InlineData("SELECT CAST(int_value / 0 AS INT) FROM numbers")]
    [InlineData("SELECT id FROM numbers WHERE id IN (SELECT id / zero_value FROM numbers)")]
    [InlineData("UPDATE numbers SET int_value = 10 / (id - 2)")]
    [InlineData("DELETE FROM numbers WHERE int_value % zero_value = 0")]
    [InlineData("INSERT INTO numbers (id, int_value) VALUES (3, 1 / 0)")]
    [InlineData("INSERT INTO numbers (id, int_value) SELECT id + 10, int_value / zero_value FROM numbers")]
    public async Task Statement_DivisionByZero_ShouldFailWithCodeAndApplyNothing(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));

        // Assert: the UPDATE faults on its second target, so a partial apply would show here.
        AssertCode(failure, DivisionByZero);
        var rows = await RowsAsync(session, "SELECT id, int_value FROM numbers ORDER BY id");
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, -70000 });
        rows[1].ShouldBe(new object?[] { 2, 70000 });
    }

    /// <summary>A CHECK that divides by zero fails the write, and a failed ADD CONSTRAINT leaves no constraint.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: CHECK evaluation faults fail INSERT and ADD CONSTRAINT atomically")]
    public async Task CheckConstraint_DivisionByZero_ShouldFailWriteAndDdl()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("arithmetic");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE ratios (numerator INT, denominator INT, CONSTRAINT non_negative CHECK (numerator / denominator >= 0))");
        await ExecuteAsync(session, "CREATE TABLE pending (numerator INT, denominator INT)");
        await ExecuteAsync(session, "INSERT INTO pending VALUES (1, 0)");

        // Act / Assert: the row-level fault rejects the insert.
        AssertCode(await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "INSERT INTO ratios VALUES (1, 0)")), DivisionByZero);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM ratios")).ShouldBe(0L);
        (await ExecuteAsync(session, "INSERT INTO ratios VALUES (4, 2)")).AffectedCount.ShouldBe(1);

        // Act / Assert: validating existing rows faults before the constraint is published.
        AssertCode(await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "ALTER TABLE pending ADD CONSTRAINT pending_ratio CHECK (numerator / denominator >= 0)")), DivisionByZero);
        (await ExecuteAsync(session, "INSERT INTO pending VALUES (-1, 1)")).AffectedCount.ShouldBe(1);
    }

    /// <summary>A fault inside BEGIN fails only its statement; the transaction keeps its work and both outcomes still apply.</summary>
    /// <param name="commit">True to COMMIT after the fault, false to ROLLBACK.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: a fault inside a transaction keeps the transaction open and decidable")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitTransaction_Fault_ShouldKeepTransactionStateRules(bool commit)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        engine.TryGetDatabase("arithmetic", out IDatabase database).ShouldBeTrue();
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "BEGIN");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        await ExecuteAsync(session, "INSERT INTO numbers (id, int_value) VALUES (3, 30)");

        // Act: both kinds of fault, including a write whose second target faults.
        AssertCode(await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "UPDATE numbers SET int_value = 10 / (id - 2)")), DivisionByZero);
        AssertCode(await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "SELECT big_value + 1 FROM numbers WHERE id = 2")), OutOfRange);

        // Assert: still the same active transaction, with its own insert and none of the failed update.
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.Active);
        var rows = await RowsAsync(session, "SELECT id, int_value FROM numbers ORDER BY id");
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(new object?[] { 1, -70000 });
        rows[1].ShouldBe(new object?[] { 2, 70000 });
        rows[2].ShouldBe(new object?[] { 3, 30 });
        (await ScalarAsync(observer, "SELECT COUNT(*) FROM numbers")).ShouldBe(2L);

        (await ExecuteAsync(session, commit ? "COMMIT" : "ROLLBACK")).Status.ShouldBe(QueryResultStatus.Success);
        transaction.State.ShouldBe(commit ? TransactionState.Committed : TransactionState.RolledBack);
        (await ScalarAsync(observer, "SELECT COUNT(*) FROM numbers")).ShouldBe(commit ? 3L : 2L);
        (await ScalarAsync(observer, "SELECT int_value FROM numbers WHERE id = 1")).ShouldBe(-70000);
    }

    /// <summary>BIGINT results beyond the signed 64-bit range fail instead of wrapping.</summary>
    /// <param name="expression">The overflowing projection, evaluated on the row holding BIGINT's maximum.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: BIGINT overflow fails with COHSQLE002 instead of wrapping")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("big_value + 1")]
    [InlineData("big_value + id")]
    [InlineData("-9223372036854775807 - 2")]
    [InlineData("-big_value - 2")]
    [InlineData("4611686018427387904 * 2")]
    [InlineData("big_value * -2")]
    [InlineData("(-9223372036854775807 - 1) / -1")]
    [InlineData("-(-9223372036854775807 - 1)")]
    [InlineData("ABS(-9223372036854775807 - 1)")]
    [InlineData("@maximum + int_value")]
    [InlineData("9223372036854775808")]
    public async Task Projection_BigIntOverflow_ShouldFailWithCode(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var parameters = new Dictionary<string, object?> { ["maximum"] = long.MaxValue };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, $"SELECT {expression} FROM numbers WHERE id = 2", parameters));

        // Assert
        AssertCode(failure, OutOfRange);
        failure.Message.ShouldStartWith("COHSQLE002: Numeric value out of range", Case.Sensitive);
        (await ScalarAsync(session, "SELECT big_value FROM numbers WHERE id = 2")).ShouldBe(long.MaxValue);
    }

    /// <summary>Results at the edge of the range are exact, and narrow integers compute in BIGINT.</summary>
    /// <param name="expression">The projection.</param>
    /// <param name="expected">The exact BIGINT result.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: in-range BIGINT boundaries and narrow integers compute exactly")]
    [InlineData("9223372036854775806 + 1", long.MaxValue)]
    [InlineData("big_value - 0", long.MaxValue)]
    [InlineData("(-9223372036854775807 - 1) / 1", long.MinValue)]
    [InlineData("(-9223372036854775807 - 1) % -1", 0L)]
    [InlineData("-7 % 3", -1L)]
    [InlineData("-7 / 2", -3L)]
    [InlineData("int_value * 100000", 7000000000L)]
    [InlineData("2147483647 + 1", 2147483648L)]
    [InlineData("ABS(CAST(-2147483648 AS INT))", 2147483648L)]
    [InlineData("-CAST(-2147483648 AS INT)", 2147483648L)]
    public async Task Projection_InRangeInteger_ShouldReturnExactBigInt(string expression, long expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        object? value = await ScalarAsync(session, $"SELECT {expression} FROM numbers WHERE id = 2");

        // Assert
        value.ShouldBeOfType<long>().ShouldBe(expected);
    }

    /// <summary>Decimal results and approximate operands outside Decimal's range fail with the same code.</summary>
    /// <param name="expression">The overflowing projection.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: DECIMAL overflow and unrepresentable approximate operands fail with COHSQLE002")]
    [InlineData("@maximum * 2")]
    [InlineData("@maximum + @maximum")]
    [InlineData("-@maximum - 1")]
    [InlineData("@maximum / 0.5")]
    [InlineData("@huge + 1")]
    [InlineData("@notANumber * 1")]
    [InlineData("@infinity - 1")]
    [InlineData("real_value * @huge")]
    public async Task Projection_DecimalOrApproximateOverflow_ShouldFailWithCode(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);
        var parameters = new Dictionary<string, object?>
        {
            ["maximum"] = decimal.MaxValue,
            ["huge"] = 1e300,
            ["notANumber"] = double.NaN,
            ["infinity"] = float.PositiveInfinity,
        };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, $"SELECT {expression} FROM numbers WHERE id = 1", parameters));

        // Assert
        AssertCode(failure, OutOfRange);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM numbers")).ShouldBe(2L);
    }

    /// <summary>ABS accepts every numeric storage type and keeps approximate and decimal types.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: ABS accepts every numeric storage type including REAL")]
    public async Task Abs_EveryNumericStorageType_ShouldReturnMagnitudeAndType()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT ABS(tiny_value), ABS(small_value), ABS(int_value), ABS(big_value), ABS(real_value), " +
            "ABS(double_value), ABS(decimal_value) FROM numbers WHERE id = 1")).ShouldBeAssignableTo<QueryResultSet>();
        var rows = await ReadRowsAsync(result);

        // Assert: REAL stays Float32 rather than throwing, and the metadata matches the values.
        result.Columns.ShouldSatisfyAllConditions(
            () => result.Columns[0].Type.ShouldBe(DatabaseType.Int64),
            () => result.Columns[3].Type.ShouldBe(DatabaseType.Int64),
            () => result.Columns[4].Type.ShouldBe(DatabaseType.Float32),
            () => result.Columns[5].Type.ShouldBe(DatabaseType.Float64),
            () => result.Columns[6].Type.ShouldBe(DatabaseType.Decimal));
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 8L, 300L, 70000L, 5000000000L, 1.5f, 2.25d, 3.75m });

        // REAL also flows through ABS in predicates, aggregates, and NULL propagation.
        (await ScalarAsync(session, "SELECT MAX(ABS(real_value)) FROM numbers")).ShouldBeOfType<float>().ShouldBe(2.5f);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM numbers WHERE ABS(real_value) > 2")).ShouldBe(1L);
        (await ScalarAsync(session, "SELECT ABS(@nothing) FROM numbers WHERE id = 1",
            new Dictionary<string, object?> { ["nothing"] = null })).ShouldBeNull();
    }

    /// <summary>A REAL branch beyond Decimal's range cannot take a Decimal result type.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: result-type normalization reports out-of-range values with COHSQLE002")]
    public async Task ResultTypeNormalization_OutOfRange_ShouldFailWithCode()
    {
        // Arrange
        await using var engine = CreateEngine();
        await using var session = await SeedAsync(engine);

        // Act / Assert: COALESCE(REAL, DECIMAL) is typed DECIMAL; 3e38 does not fit it.
        AssertCode(await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "SELECT COALESCE(@large, decimal_value) FROM numbers WHERE id = 1",
            new Dictionary<string, object?> { ["large"] = 3e38f })), OutOfRange);
    }

    /// <summary>SUM and AVG accumulation overflow carries the overflow code.</summary>
    /// <param name="aggregate">The accumulating aggregate.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Arithmetic: SUM and AVG overflow fail with COHSQLE002")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    public async Task Aggregate_Overflow_ShouldFailWithCode(string aggregate)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("arithmetic");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE totals (amount DECIMAL, approximate DOUBLE)");
        await ExecuteAsync(session, "INSERT INTO totals VALUES (@maximum, @nan), (@maximum, @nan)",
            new Dictionary<string, object?> { ["maximum"] = decimal.MaxValue, ["nan"] = double.NaN });

        // Act / Assert
        foreach (string column in new[] { "amount", "approximate" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() =>
                ExecuteAsync(session, $"SELECT {aggregate}({column}) FROM totals"));
            AssertCode(failure, OutOfRange);
            failure.Message.ShouldContain(aggregate, Case.Sensitive);
        }
    }

    /// <summary>Over the wire every fault is an ExecutionFailure carrying its code, and the session stays ready.</summary>
    /// <param name="statement">The faulting statement.</param>
    /// <param name="code">The expected diagnostic code.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Arithmetic: wire faults are coded execution failures that keep the session")]
    [InlineData("SELECT id / 0 FROM users", DivisionByZero)]
    [InlineData("SELECT id % 0 FROM users", DivisionByZero)]
    [InlineData("SELECT CAST(id AS DECIMAL) / 0 FROM users", DivisionByZero)]
    [InlineData("UPDATE users SET id = id / (id - 2)", DivisionByZero)]
    [InlineData("SELECT 9223372036854775807 + id FROM users", OutOfRange)]
    [InlineData("SELECT -(-9223372036854775807 - 1) FROM users", OutOfRange)]
    [InlineData("SELECT ABS(-9223372036854775807 - 1) FROM users", OutOfRange)]
    [InlineData("SELECT 9223372036854775808 FROM users", OutOfRange)]
    public async Task Wire_ArithmeticFault_ShouldReturnExecutionFailureAndKeepSession(string statement, string code)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(statement).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);

        // Assert: the statement failed; the session did not terminate and runs the next one.
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(code + ":", Case.Sensitive);
        harness.Server.Context.Sessions.Count.ShouldBe(1);
        (await CountAsync(client)).ShouldBe(2L);
        (await CountAsync(client, "id = 2")).ShouldBe(1L);
    }

    /// <summary>A wire fault inside BEGIN leaves the transaction open; COMMIT publishes the work done before it.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Arithmetic: a wire fault inside a transaction keeps it open for COMMIT")]
    public async Task Wire_FaultInsideTransaction_ShouldKeepTransactionOpen()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var writer = await harness.DialAsync();
        await using var observer = await harness.DialAsync();
        await writer.HandshakeAsync();
        await observer.HandshakeAsync();
        await ExecuteAsync(writer, "BEGIN");
        await ExecuteAsync(writer, "INSERT INTO users VALUES (3, 'lin')");

        // Act
        await writer.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("UPDATE users SET id = id / (id - 2)").Encode());
        var error = ProtocolErrorMessage.Decode((await writer.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(DivisionByZero + ":", Case.Sensitive);
        (await CountAsync(writer)).ShouldBe(3L);
        (await CountAsync(observer)).ShouldBe(2L);
        await ExecuteAsync(writer, "COMMIT");
        (await CountAsync(observer)).ShouldBe(3L);
        (await CountAsync(observer, "id = 1")).ShouldBe(1L);
        (await CountAsync(observer, "id = 2")).ShouldBe(1L);
        harness.Server.Context.Sessions.Count.ShouldBe(2);
    }

    private static void AssertCode(DatabaseException failure, string code)
    {
        failure.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(code);
        failure.Message.ShouldStartWith(code + ": ", Case.Sensitive);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "arithmetic-faults" });

    private static async Task<IDatabaseSession> SeedAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("arithmetic");
        var session = await database.CreateSessionAsync(CancellationToken.None);
        (await ExecuteAsync(session, CreateNumbers)).Status.ShouldBe(QueryResultStatus.Success);
        (await ExecuteAsync(session, InsertNumbers)).AffectedCount.ShouldBe(2);
        return session;
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(statement, parameters, CancellationToken.None).AsTask();

    private static async Task<object?> ScalarAsync(IDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var rows = await RowsAsync(session, statement, parameters);
        return rows.ShouldHaveSingleItem().ShouldHaveSingleItem();
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, statement, parameters)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++) { values[index] = row.GetValue(index); }
            rows.Add(values);
        }
        return rows;
    }

    private static async Task ExecuteAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static async Task<long> CountAsync(ProtocolTestClient client, string? predicate = null)
    {
        string sql = "SELECT COUNT(*) FROM users" + (predicate is null ? "" : $" WHERE {predicate}");
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        long count = (long)DatabaseValueCodec.DecodeComponent(row.Payload.Span)!;
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        return count;
    }
}
