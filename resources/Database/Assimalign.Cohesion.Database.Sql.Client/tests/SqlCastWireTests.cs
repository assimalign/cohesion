using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Proves CAST conversions, target metadata, and failures through the real SQL
/// server and typed client. Raw boxed values prevent client getter coercion from
/// concealing an executor that returns the operand unchanged.
/// </summary>
public sealed class SqlCastWireTests
{
    /// <summary>Enumerates every supported numeric/string source and target pair.</summary>
    /// <returns>The source and target type identities.</returns>
    public static IEnumerable<object[]> NumericAndStringPairs()
    {
        DatabaseType[] types =
        [
            DatabaseType.Int8, DatabaseType.Int16, DatabaseType.Int32,
            DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.String,
        ];

        foreach (DatabaseType source in types)
        {
            foreach (DatabaseType target in types)
            {
                yield return [source, target];
            }
        }
    }

    /// <summary>All signed integer, decimal, and string pairs carry their target runtime and wire types.</summary>
    /// <param name="source">The source database type.</param>
    /// <param name="target">The target database type.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: numeric/string conversion matrix survives the wire")]
    [MemberData(nameof(NumericAndStringPairs))]
    public async Task QueryAsync_NumericOrStringPair_ShouldConvertRawValueAndMetadata(DatabaseType source, DatabaseType target)
    {
        // Arrange: same-type pairs start with a different parameter type and an
        // inner CAST, so even identity coverage fails under operand pass-through.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        object input = NumberValue(source);
        string operand = "@value";
        if (source == target)
        {
            input = source == DatabaseType.String ? 42 : "42";
            operand = $"CAST(@value AS {TypeName(source)})";
        }
        var command = new SqlCommand($"SELECT CAST({operand} AS {TypeName(target)}) AS converted FROM users WHERE id = 1")
            .WithParameter("value", input);

        // Act
        SqlResultSet result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert: use raw values, never getters that can convert strings/numbers.
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(target);
        AssertNumberValue(result.ShouldHaveSingleItem()["converted"], target);
    }

    /// <summary>Boolean/text pairs use explicit boolean parsing and canonical uppercase text.</summary>
    /// <param name="expression">The CAST expression.</param>
    /// <param name="target">The target database type.</param>
    /// <param name="expected">The expected raw value.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: boolean/text pairs survive the wire")]
    [InlineData("CAST(' tRuE ' AS BOOLEAN)", DatabaseType.Boolean, true)]
    [InlineData("CAST('FALSE' AS BOOLEAN)", DatabaseType.Boolean, false)]
    [InlineData("CAST(TRUE AS VARCHAR(4))", DatabaseType.String, "TRUE")]
    [InlineData("CAST(FALSE AS VARCHAR(5))", DatabaseType.String, "FALSE")]
    [InlineData("CAST(CAST('true' AS BOOLEAN) AS BOOLEAN)", DatabaseType.Boolean, true)]
    public async Task QueryAsync_BooleanOrTextPair_ShouldConvertRawValueAndMetadata(string expression, DatabaseType target, object expected)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync($"SELECT {expression} AS converted FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(target);
        result.ShouldHaveSingleItem()["converted"].ShouldBe(expected);
    }

    /// <summary>Null and empty projections retain CAST's target type rather than a guessed string type.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: null and empty projections retain target metadata")]
    public async Task QueryAsync_NullOrEmptyProjection_ShouldRetainTargetMetadata()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet nullResult = await connection.QueryAsync("SELECT CAST(NULL AS INT) AS converted, CAST('42' AS INT) AS companion FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        SqlResultSet emptyResult = await connection.QueryAsync("SELECT CAST(name AS SMALLINT) AS converted FROM users WHERE id = 0",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: each assertion includes the changed type, so unchanged NULL or
        // absence of rows cannot accidentally satisfy the regression check.
        SqlRow nullRow = nullResult.ShouldHaveSingleItem();
        (nullResult.Columns[0].Type, nullRow["converted"], nullRow["companion"])
            .ShouldBe((DatabaseType.Int32, (object?)null, (object?)42));
        (emptyResult.Columns.ShouldHaveSingleItem().Type, emptyResult.Count)
            .ShouldBe((DatabaseType.Int16, 0));
    }

    /// <summary>Fractional decimals retain exact values, including insignificant trailing zeroes.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: exact decimal precision and scale survive the wire")]
    public async Task QueryAsync_ExactDecimal_ShouldPreserveFractionalValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync(
            "SELECT CAST('  -12.3400  ' AS NUMERIC(4, 2)) AS negative, CAST('0.12' AS DECIMAL(2, 2)) AS fraction FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Decimal, DatabaseType.Decimal });
        SqlRow row = result.ShouldHaveSingleItem();
        row["negative"].ShouldBeOfType<decimal>().ShouldBe(-12.34m);
        row["fraction"].ShouldBeOfType<decimal>().ShouldBe(0.12m);
    }

    /// <summary>Converted booleans control filtering and converted integers participate in numeric comparisons.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: converted values control WHERE predicates")]
    public async Task QueryAsync_WherePredicate_ShouldUseConvertedValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet booleanResult = await connection.QueryAsync("SELECT id FROM users WHERE CAST('true' AS BOOLEAN) ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        SqlResultSet numericResult = await connection.QueryAsync("SELECT id FROM users WHERE id = CAST('2' AS INT)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: pass-through filters every boolean row and makes the numeric
        // comparison fail instead of returning the selected row.
        booleanResult.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2 });
        numericResult.Select(row => row["id"]).ShouldBe(new object?[] { 2 });
    }

    /// <summary>Fractional CAST bounds preserve rows when an integer index cannot represent the bound exactly.</summary>
    /// <param name="comparison">The range comparison operator.</param>
    /// <param name="bound">The fractional decimal text.</param>
    /// <param name="expected">The expected converted row identifier.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: indexed integer ranges preserve fractional boundaries")]
    [InlineData(">", "1.5", "2")]
    [InlineData("<", "1.4", "1")]
    public async Task QueryAsync_IndexedFractionalRange_ShouldPreserveBoundaryRows(string comparison, string bound, string expected)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE INDEX ix_users_id ON users (id)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act: rounding 1.5 to 2 or 1.4 to 1 in an index seek would lose the result.
        SqlResultSet result = await connection.QueryAsync(
            $"SELECT CAST(id AS VARCHAR(2)) AS converted FROM users WHERE id {comparison} CAST('{bound}' AS DECIMAL)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: pass-through cannot compare the integer key with the text bound.
        result.ShouldHaveSingleItem()["converted"].ShouldBeOfType<string>().ShouldBe(expected);
    }

    /// <summary>Small signed CAST results work as unary, function, and row-count operands.</summary>
    /// <param name="typeName">The small signed integer target.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: small integers work downstream in arithmetic and pagination")]
    [InlineData("TINYINT")]
    [InlineData("SMALLINT")]
    public async Task QueryAsync_SmallIntegerConsumers_ShouldUseConvertedNumbers(string typeName)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync(
            $"SELECT -CAST('7' AS {typeName}) AS negative, ABS(CAST('-8' AS {typeName})) AS magnitude FROM users ORDER BY id " +
            $"LIMIT CAST('1' AS {typeName}) OFFSET CAST('1' AS {typeName})",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: pass-through leaves text that neither unary arithmetic nor LIMIT accepts.
        SqlRow row = result.ShouldHaveSingleItem();
        row["negative"].ShouldBeOfType<long>().ShouldBe(-7L);
        row["magnitude"].ShouldBeOfType<long>().ShouldBe(8L);
    }

    /// <summary>SQL exponent literals are parsed exactly before conversion to text.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: exact SQL exponent literals preserve their value")]
    public async Task QueryAsync_ExactExponentLiteral_ShouldConvertToCanonicalText()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync("SELECT CAST(1.25e1 AS VARCHAR(4)) AS converted FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: pass-through would expose decimal 12.5 instead of text.
        result.ShouldHaveSingleItem()["converted"].ShouldBeOfType<string>().ShouldBe("12.5");
    }

    /// <summary>Write expressions canonicalize through integer conversion before string storage.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: INSERT and UPDATE use converted write expressions")]
    public async Task ExecuteAsync_WriteExpressions_ShouldStoreConvertedRepresentation()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE cast_writes (id INT PRIMARY KEY, value VARCHAR(8))",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act / Assert: storage itself would preserve the original leading zeroes.
        await connection.ExecuteAsync("INSERT INTO cast_writes (id, value) VALUES (1, CAST(CAST('00042' AS INT) AS VARCHAR(8)))",
            cancellationToken: SqlClientTestHarness.Timeout());
        SqlResultSet inserted = await connection.QueryAsync("SELECT value FROM cast_writes WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        inserted.ShouldHaveSingleItem()["value"].ShouldBeOfType<string>().ShouldBe("42");

        await connection.ExecuteAsync("UPDATE cast_writes SET value = CAST(CAST('0007' AS INT) AS VARCHAR(8)) WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        SqlResultSet updated = await connection.QueryAsync("SELECT value FROM cast_writes WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        updated.ShouldHaveSingleItem()["value"].ShouldBeOfType<string>().ShouldBe("7");
    }

    /// <summary>Invalid values, overflow, discarded fractions, and truncation fail through the client.</summary>
    /// <param name="expression">The invalid CAST expression.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: invalid values return execution failures over the wire")]
    [InlineData("CAST('abc' AS INT)")]
    [InlineData("CAST('yes' AS BOOLEAN)")]
    [InlineData("CAST('1e2' AS INT)")]
    [InlineData("CAST('1,000' AS DECIMAL)")]
    [InlineData("CAST('128' AS TINYINT)")]
    [InlineData("CAST('32768' AS SMALLINT)")]
    [InlineData("CAST('2147483648' AS INT)")]
    [InlineData("CAST('9223372036854775808' AS BIGINT)")]
    [InlineData("CAST('79228162514264337593543950336' AS DECIMAL)")]
    [InlineData("CAST('0.12345678901234567890123456789' AS DECIMAL)")]
    [InlineData("CAST(1e-29 AS DECIMAL)")]
    [InlineData("CAST(0.00000000000000000000000000001 AS DECIMAL)")]
    [InlineData("CAST('1.5' AS INT)")]
    [InlineData("CAST(1.5 AS INT)")]
    [InlineData("CAST('12.34' AS DECIMAL(3, 1))")]
    [InlineData("CAST('123.4' AS DECIMAL(3, 1))")]
    [InlineData("CAST(123 AS VARCHAR(2))")]
    [InlineData("CAST('abc' AS VARCHAR(2))")]
    [InlineData("CAST(TRUE AS INT)")]
    [InlineData("CAST(1 AS BOOLEAN)")]
    public async Task QueryAsync_InvalidValue_ShouldReturnExecutionFailure(string expression)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert: the old one-line evaluator would succeed for every operand.
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync($"SELECT {expression} AS converted FROM users WHERE id = 1",
                cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>Unknown types, unsupported targets, and illegal modifiers fail before any rows are read.</summary>
    /// <param name="typeName">The invalid target specification.</param>
    /// <param name="diagnostic">The precise parser diagnostic.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: invalid target specifications return parse failures")]
    [InlineData("NOTATYPE", "SQL0004")]
    [InlineData("FLOAT", "SQL0005")]
    [InlineData("DATE", "SQL0005")]
    [InlineData("INT(3)", "SQL0005")]
    [InlineData("VARCHAR(0)", "SQL0005")]
    [InlineData("VARCHAR(3, 1)", "SQL0005")]
    [InlineData("DECIMAL(29, 2)", "SQL0005")]
    [InlineData("DECIMAL(3, 4)", "SQL0005")]
    public async Task QueryAsync_InvalidTarget_ShouldRejectBeforeExecution(string typeName, string diagnostic)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert: no row reaches the evaluator, so this proves early validation.
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync($"SELECT CAST(name AS {typeName}) AS converted FROM users WHERE id = 0",
                cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        failure.Message.ShouldContain(diagnostic, Case.Sensitive);
    }

    /// <summary>Unsupported wire source types cannot escape conversion by targeting text.</summary>
    /// <param name="source">The unsupported source identity.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: unsupported source types are rejected over the wire")]
    [InlineData(DatabaseType.Float32)]
    [InlineData(DatabaseType.Float64)]
    [InlineData(DatabaseType.Guid)]
    [InlineData(DatabaseType.Binary)]
    [InlineData(DatabaseType.DateTime)]
    public async Task QueryAsync_UnsupportedSource_ShouldReturnExecutionFailure(DatabaseType source)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        object value = source switch
        {
            DatabaseType.Float32 => 42f,
            DatabaseType.Float64 => 42d,
            DatabaseType.Guid => Guid.Empty,
            DatabaseType.Binary => new byte[] { 42 },
            DatabaseType.DateTime => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        var command = new SqlCommand("SELECT CAST(@value AS VARCHAR(100)) AS converted FROM users WHERE id = 1")
            .WithParameter("value", value);

        // Act / Assert
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(command, SqlClientTestHarness.Timeout()));
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>Write storage cannot conceal failed conversions, and a failure leaves CAST usable on the same connection.</summary>
    /// <param name="statement">The invalid write statement.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - CAST: invalid write expressions fail and preserve the connection")]
    [InlineData("INSERT INTO users (id, name) VALUES (3, CAST('abc' AS INT))")]
    [InlineData("UPDATE users SET name = CAST('abc' AS INT) WHERE id = 1")]
    public async Task ExecuteAsync_InvalidWrite_ShouldRejectBeforeStorageAndKeepConnectionUsable(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert: VARCHAR storage would accept the unconverted 'abc'.
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Message.ShouldContain("CAST", Case.Sensitive);

        SqlResultSet recovered = await connection.QueryAsync("SELECT CAST('42' AS INT) AS converted FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        recovered.ShouldHaveSingleItem()["converted"].ShouldBeOfType<int>().ShouldBe(42);
    }

    private static string TypeName(DatabaseType type) => type switch
    {
        DatabaseType.Int8 => "TINYINT",
        DatabaseType.Int16 => "SMALLINT",
        DatabaseType.Int32 => "INT",
        DatabaseType.Int64 => "BIGINT",
        DatabaseType.Decimal => "DECIMAL(4, 2)",
        DatabaseType.String => "VARCHAR(2)",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static object NumberValue(DatabaseType type) => type switch
    {
        DatabaseType.Int8 => (sbyte)42,
        DatabaseType.Int16 => (short)42,
        DatabaseType.Int32 => 42,
        DatabaseType.Int64 => 42L,
        DatabaseType.Decimal => 42m,
        DatabaseType.String => "42",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static void AssertNumberValue(object? value, DatabaseType type)
    {
        switch (type)
        {
            case DatabaseType.Int8: value.ShouldBeOfType<sbyte>().ShouldBe((sbyte)42); break;
            case DatabaseType.Int16: value.ShouldBeOfType<short>().ShouldBe((short)42); break;
            case DatabaseType.Int32: value.ShouldBeOfType<int>().ShouldBe(42); break;
            case DatabaseType.Int64: value.ShouldBeOfType<long>().ShouldBe(42L); break;
            case DatabaseType.Decimal: value.ShouldBeOfType<decimal>().ShouldBe(42m); break;
            case DatabaseType.String: value.ShouldBeOfType<string>().ShouldBe("42"); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}
