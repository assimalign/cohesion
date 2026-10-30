using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlBitwiseTests
{
    private readonly ITestOutputHelper _output;

    public SqlBitwiseTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: every width and boundary agrees with the .NET oracle (#1721)")]
    public void Evaluate_IntegerMatrix_ShouldMatchOracle()
    {
        // Arrange: independently use the CLR operators for each width.
        var parameters = new Dictionary<string, object?>();
        var evaluator = new SqlExpressionEvaluator([], parameters);
        int comparisons = 0;
        // Act / Assert
        foreach (int width in new[] { 16, 32, 64 })
        {
            long min = width == 16 ? short.MinValue : width == 32 ? int.MinValue : long.MinValue;
            long max = width == 16 ? short.MaxValue : width == 32 ? int.MaxValue : long.MaxValue;
            long[] values = [min, min + 1, -7, -1, 0, 1, 7, max - 1, max];
            foreach (long a in values)
            {
                parameters["a"] = Narrow(a, width);
                evaluator.Evaluate(Expression("~@a"), []).ShouldBe(Narrow(~a, width));
                comparisons++;
                foreach (long b in values.Concat([-129L, -65, -64, -33, -32, -17, -16, 15, 16, 17, 31, 32, 33, 63, 64, 65, 129]))
                {
                    parameters["b"] = Narrow(b, width);
                    foreach (var op in new[] { SqlBinaryOperator.BitwiseAnd, SqlBinaryOperator.BitwiseOr, SqlBinaryOperator.BitwiseXor })
                    {
                        object expected = width switch
                        {
                            16 => Small((short)parameters["a"]!, (short)parameters["b"]!, op),
                            32 => Medium((int)parameters["a"]!, (int)parameters["b"]!, op),
                            _ => Large((long)parameters["a"]!, (long)parameters["b"]!, op),
                        };
                        object? actual = evaluator.Evaluate(Expression("@a " + SqlBitwise.Text(op) + " @b"), []);
                        actual.ShouldBe(expected);
                        actual!.GetType().ShouldBe(expected.GetType());
                        comparisons++;
                    }
                }
            }
        }
        comparisons.ShouldBe(2133);
        _output.WriteLine($"{comparisons} typed oracle comparisons");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: SQL expressions preserve types, NULLs and PostgreSQL precedence (#1721)")]
    [InlineData("1 | 2 & 3", 3)]
    [InlineData("1 | 2 # 3", 0)]
    [InlineData("~1 + 2", -4L)]
    [InlineData("~1 & 3", 2)]
    [InlineData("1 << (2 + 1)::INT", 8)]
    [InlineData("(-8)::INT >> 2", -2)]
    [InlineData("NULL & 1", null)]
    [InlineData("1 | NULL", null)]
    [InlineData("NULL # NULL", null)]
    [InlineData("~NULL", null)]
    [InlineData("NULL << 3", null)]
    [InlineData("1 >> NULL", null)]
    [InlineData("1::SMALLINT & 3::SMALLINT", (short)1)]
    [InlineData("1::SMALLINT # 3::INT", 2)]
    [InlineData("1::INT | 2::BIGINT", 3L)]
    [InlineData("~1::SMALLINT", (short)-2)]
    [InlineData("32767::SMALLINT << 1::SMALLINT", (short)-2)]
    [InlineData("1::SMALLINT << 16::SMALLINT", (short)0)]
    [InlineData("1::INT << 32", 1)]
    [InlineData("1::BIGINT << 64", 1L)]
    [InlineData("1::INT << (-1)::INT", int.MinValue)]
    public async Task Execute_ExpressionCorpus_ShouldReturnTypedValueAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        object? actual = (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression)).ShouldHaveSingleItem()[0];
        actual.ShouldBe(expected);
        if (expected is not null) { actual!.GetType().ShouldBe(expected.GetType()); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: wrong operands bind before empty inputs and skipped branches (#1721)")]
    [InlineData("'1'")]
    [InlineData("TRUE")]
    [InlineData("1.0")]
    [InlineData("1::TINYINT")]
    [InlineData("1::REAL")]
    [InlineData("1::DOUBLE PRECISION")]
    [InlineData("'x'::CHAR")]
    [InlineData("DATE '2026-01-01'")]
    [InlineData("TIME '12:00:00'")]
    [InlineData("TIMESTAMP '2026-01-01 12:00:00'")]
    [InlineData("INTERVAL '1 day'")]
    [InlineData("X'01'")]
    [InlineData("'00000000-0000-0000-0000-000000000001'::UUID")]
    [InlineData("'{}'::JSON")]
    [InlineData("'{}'::JSONB")]
    [InlineData("TIMESTAMP WITH TIME ZONE '2026-01-01 12:00:00+00:00'")]
    [InlineData("NULL::TEXT")]
    public async Task Execute_InvalidTypes_ShouldRefuseAtBindingAsync(string operand)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(n INT)");
        // Act / Assert
        foreach (string expression in new[] { $"~({operand})", $"({operand}) & 1", $"1 | ({operand})", $"1 # ({operand})", $"1 << ({operand})", $"({operand}) >> 1" })
        {
            foreach (string sql in new[] { "SELECT " + expression + " FROM empty_rows", "SELECT CASE WHEN FALSE THEN " + expression + " ELSE 1 END" })
            {
                var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, sql));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: grouped, subquery, function and parameter types compose (#1721)")]
    public async Task Execute_ComposedExpressions_ShouldKeepIntegerTypesAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(n SMALLINT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES(3),(7)");
        // Act / Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT ~MIN(n), MAX(n) & 3, SUM(n)::BIGINT # 1 FROM t"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { (short)-4, 3, 11L });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT (SELECT 3::SMALLINT) # 1, CAST((1 # 3) AS TEXT), ABS(1 & 3)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2, "2", 1L });
        await using var result = (await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT @a & @b, ~@a", new Dictionary<string, object?> { ["a"] = (short)7, ["b"] = 3L }), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(3L); row.GetValue(1).ShouldBe((short)-8); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: every mixed integer pair promotes to the wider width (#1721)")]
    public async Task Execute_MixedWidths_ShouldPromoteAndOrXorAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (int left in new[] { 16, 32, 64 })
        {
            foreach (int right in new[] { 16, 32, 64 })
            {
                var parameters = new Dictionary<string, object?> { ["a"] = Narrow(7, left), ["b"] = Narrow(3, right) };
                var request = SqlQueryRequest.FromSql("SELECT @a & @b, @a | @b, @a # @b", parameters);
                await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                int rows = 0, width = Math.Max(left, right);
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    long[] values = [3, 7, 4];
                    for (int column = 0; column < values.Length; column++)
                    {
                        row.GetValue(column).ShouldBe(Narrow(values[column], width));
                        row.GetValue(column)!.GetType().ShouldBe(Narrow(0, width).GetType());
                    }
                    rows++;
                }
                rows.ShouldBe(1);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: PostgreSQL shift signatures and SMALLINT truncation (#1721)")]
    [InlineData("32767::SMALLINT << 1", (short)-2)]
    [InlineData("1::SMALLINT << 16::SMALLINT", (short)0)]
    [InlineData("(-32768)::SMALLINT >> 16::SMALLINT", (short)-1)]
    [InlineData("1::INT << 32::BIGINT", null)]
    public async Task Execute_PostgresShiftCases_ShouldMatchReferenceAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        if (expected is null)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT " + expression, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
        }
        else
        {
            var actual = (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression)).ShouldHaveSingleItem()[0];
            actual.ShouldBe(expected);
            actual!.GetType().ShouldBe(expected.GetType());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: shift width, value classes and all count residues match PostgreSQL (#1721)")]
    [InlineData(16, 16)]
    [InlineData(16, 32)]
    [InlineData(16, 64)]
    [InlineData(32, 16)]
    [InlineData(32, 32)]
    [InlineData(32, 64)]
    [InlineData(64, 16)]
    [InlineData(64, 32)]
    [InlineData(64, 64)]
    public void Evaluate_ShiftMatrix_ShouldMatchPostgresOracle(int width, int countWidth)
    {
        // Arrange: C promotes int16 to int32 before shifting; C# has the same promotion.
        long min = width == 16 ? short.MinValue : width == 32 ? int.MinValue : long.MinValue;
        long max = width == 16 ? short.MaxValue : width == 32 ? int.MaxValue : long.MaxValue;
        long[] values = [min, min + 1, -7, -1, 0, 1, 7, max - 1, max];
        var counts = Enumerable.Range(-129, 259).Concat([short.MinValue, short.MaxValue, int.MinValue, int.MaxValue]).ToArray();
        var parameters = new Dictionary<string, object?>();
        var evaluator = new SqlExpressionEvaluator([], parameters);
        var leftShift = Expression("@a << @b");
        var rightShift = Expression("@a >> @b");
        int comparisons = 0;
        // Act / Assert
        foreach (long value in values)
        {
            parameters["a"] = Narrow(value, width);
            foreach (int rawCount in counts)
            {
                parameters["b"] = Narrow(rawCount, countWidth);
                foreach (var expression in new[] { leftShift, rightShift })
                {
                    if (countWidth == 64)
                    {
                        var error = Should.Throw<DatabaseException>(() => evaluator.Bind(expression));
                        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
                    }
                    else
                    {
                        int count = countWidth == 16 ? unchecked((short)rawCount) : rawCount;
                        bool left = ReferenceEquals(expression, leftShift);
                        long shifted = width == 64 ? (left ? value << count : value >> count)
                            : left ? unchecked((int)value) << count : unchecked((int)value) >> count;
                        object expected = Narrow(shifted, width);
                        object? actual = evaluator.Evaluate(expression, []);
                        actual.ShouldBe(expected, $"{width}-bit {value}, {countWidth}-bit count {count}, left={left}");
                        actual!.GetType().ShouldBe(expected.GetType());
                    }
                    comparisons++;
                }
            }
        }
        comparisons.ShouldBe(4734);
        _output.WriteLine($"{comparisons} shift oracle comparisons/refusals");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: CASE, COALESCE and domains use bound result widths (#1721)")]
    [InlineData("COALESCE(NULL::BIGINT, 1)")]
    [InlineData("CASE WHEN FALSE THEN 0::BIGINT ELSE 1 END")]
    [InlineData("COALESCE(NULL::wide_integer, 1)")]
    [InlineData("CASE WHEN FALSE THEN 0::wide_integer ELSE 1 END")]
    [InlineData("COALESCE(NULL::SMALLINT, 1)")]
    [InlineData("CASE WHEN FALSE THEN 0::SMALLINT ELSE 1 END")]
    [InlineData("COALESCE(NULL::small_integer, 1)")]
    [InlineData("CASE WHEN FALSE THEN 0::small_integer ELSE 1 END")]
    public async Task Execute_WrappedInteger_ShouldUseBoundWidthAsync(string operand)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("bitwise");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        builder.Types.Add(SqlType.CreateDomain("wide_integer", SqlType.BigInt));
        builder.Types.Add(SqlType.CreateDomain("small_integer", SqlType.SmallInt));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int width = operand.Contains("SMALLINT", StringComparison.Ordinal) || operand.Contains("small_integer", StringComparison.Ordinal) ? 16 : 64;
        string[] expressions = [$"({operand}) << 32", $"({operand}) >> 32", $"({operand}) & 3::SMALLINT",
            $"3::SMALLINT | ({operand})", $"({operand}) # 3::SMALLINT", $"~({operand})"];
        long[] expected = [width == 64 ? 4294967296 : 1, width == 64 ? 0 : 1, 1, 3, 2, -2];
        // Act / Assert: both values and public result metadata must agree with binding.
        for (int index = 0; index < expressions.Length; index++)
        {
            await using var result = (await session.ExecuteAsync("SELECT " + expressions[index], cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns.ShouldHaveSingleItem().Type.ShouldBe(width == 64 ? DatabaseType.Int64 : DatabaseType.Int16);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                row.GetValue(0).ShouldBe(Narrow(expected[index], width));
                row.GetValue(0)!.GetType().ShouldBe(Narrow(0, width).GetType());
                rows++;
            }
            rows.ShouldBe(1);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: every wrapped result and parameter is validated before reading rows (#1721)")]
    [InlineData("COALESCE(NULL::INT, 'x')")]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE 'x' END")]
    [InlineData("COALESCE(NULL::INT, @p)")]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE @p END")]
    public async Task Execute_WrappedInvalidOperand_ShouldRefuseBeforeRowsAsync(string operand)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(n INT)");
        var parameters = new Dictionary<string, object?> { ["p"] = new object() };
        // Act / Assert: none of the alternatives can execute over the empty source.
        foreach (string expression in new[] { $"({operand}) & 1", $"1 | ({operand})", $"({operand}) # 1", $"~({operand})", $"({operand}) << 1", $"1 >> ({operand})",
            $"~COALESCE(NULL::INT, CASE WHEN FALSE THEN {operand} ELSE 1 END)" })
        {
            var request = SqlQueryRequest.FromSql("SELECT " + expression + " FROM empty_rows", parameters);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: BIGINT counts refuse before empty inputs, including typed NULLs and wrapped results (#1721)")]
    [InlineData("32::BIGINT")]
    [InlineData("NULL::BIGINT")]
    [InlineData("COALESCE(NULL::BIGINT, 32)")]
    [InlineData("CASE WHEN TRUE THEN 32 ELSE NULL::BIGINT END")]
    [InlineData("COALESCE(NULL::INT, @p)")]
    public async Task Execute_BigIntCount_ShouldRefuseBeforeRowsAsync(string count)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(n INT)");
        // Act / Assert
        foreach (string left in new[] { "1::SMALLINT", "1::INT", "1::BIGINT", "NULL" })
        {
            foreach (string op in new[] { "<<", ">>" })
            {
                var request = SqlQueryRequest.FromSql($"SELECT {left} {op} ({count}) FROM empty_rows", new Dictionary<string, object?> { ["p"] = 32L });
                var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: correlated alternatives prepare before typed binary and unary evaluation (#1721)")]
    public async Task Execute_CorrelatedAlternative_ShouldUseBoundWidthAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(n INT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES(1),(2)");
        const string operand = "COALESCE(NULL::BIGINT, (SELECT n FROM t WHERE t.n=o.n))";
        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session, $"SELECT ({operand}) << 32, ~({operand}) FROM t o ORDER BY n");
        // Assert
        rows.ShouldBe(new object?[][] { [4294967296L, -2L], [8589934592L, -3L] });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: unsupported CLR parameter values fail during binding (#1721)")]
    public async Task Execute_UnknownParameterType_ShouldRefuseBeforeRowsAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(n INT)");
        // Act / Assert
        foreach (string expression in new[] { "~@p", "@p & 1", "1 << @p" })
        {
            var request = SqlQueryRequest.FromSql("SELECT " + expression + " FROM empty_rows", new Dictionary<string, object?> { ["p"] = new object() });
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bitwise: prefix operations execute on text and typed sessions with empty and populated inputs (#1721)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_PrefixOperations_ShouldWorkOnBothSessionSeamsAsync(bool typed, bool populated)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(id INT, a INT)");
        if (populated) { await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES(1,10),(2,20)"); }
        // Act / Assert
        (await RowsAsync("SELECT ~NULL")).ShouldBe(new object?[][] { [null] });
        (await RowsAsync("SELECT ~a FROM t ORDER BY id")).ShouldBe(populated ? new object?[][] { [-11], [-21] } : []);
        (await RowsAsync("SELECT a FROM t WHERE ~a = 1")).ShouldBeEmpty();
        (await RowsAsync("SELECT a FROM t WHERE a = ~ ~a ORDER BY id")).ShouldBe(populated ? new object?[][] { [10], [20] } : []);
        var update = await ExecuteAsync("UPDATE t SET a = ~a");
        update.AffectedCount.ShouldBe(populated ? 2 : 0);
        (await RowsAsync("SELECT a FROM t ORDER BY id")).ShouldBe(populated ? new object?[][] { [-11], [-21] } : []);

        Task<DatabaseResult> ExecuteAsync(string text) => typed
            ? session.ExecuteAsync(SqlQueryRequest.FromSql(text), CancellationToken.None).AsTask()
            : session.ExecuteAsync(text, cancellationToken: CancellationToken.None).AsTask();

        async Task<List<object?[]>> RowsAsync(string text)
        {
            await using var result = (await ExecuteAsync(text)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
            return rows;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Bitwise: canonical views, defaults and checks survive restart (#1721)")]
    public async Task Open_StoredOperators_ShouldReloadDefinitionsAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("bitwise", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(n INT DEFAULT 1 | 2 & 3, CHECK ((n & 1) = 1))");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE VIEW v AS SELECT ~n AS negated, n # 7 AS x, n << 1 AS l, n >> 1 AS r FROM t");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");
            (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='t' AND COLUMN_NAME='n'"))
                .ShouldHaveSingleItem()[0].ShouldBe("1 | 2 & 3");
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("bitwise", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO t DEFAULT VALUES");
        // Assert
        (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT * FROM v")).ShouldBe(new object?[][] { [-4, 4, 6, 1], [-4, 4, 6, 1] });
        var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO t VALUES(2)"));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code != null && diagnostic.Code.StartsWith("CS03", StringComparison.Ordinal));
    }

    private static object Narrow(long n, int width)
    {
        if (width == 16) { return unchecked((short)n); }
        if (width == 32) { return unchecked((int)n); }
        return n;
    }
    private static object Small(short a, short b, SqlBinaryOperator op) => unchecked((short)(op switch
    { SqlBinaryOperator.BitwiseAnd => a & b, SqlBinaryOperator.BitwiseOr => a | b, _ => a ^ b }));
    private static object Medium(int a, int b, SqlBinaryOperator op) => op switch
    { SqlBinaryOperator.BitwiseAnd => a & b, SqlBinaryOperator.BitwiseOr => a | b, _ => a ^ b };
    private static object Large(long a, long b, SqlBinaryOperator op) => op switch
    { SqlBinaryOperator.BitwiseAnd => a & b, SqlBinaryOperator.BitwiseOr => a | b, _ => a ^ b };

    private static SqlExpression Expression(string text)
        => ((SqlSelectExpression)((SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + text)).SqlExpression!).Columns[0].Expression;
}
