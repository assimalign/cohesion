using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Executes explicit conversions with assertions that detect the former operand pass-through.
/// </summary>
public sealed class SqlCastTests
{
    /// <summary>Supplies conversions whose runtime representation changes.</summary>
    public static IEnumerable<object[]> SuccessfulConversions()
    {
        object[] numericSources = [(sbyte)42, (short)42, 42, 42L, 42m];
        (string Name, DatabaseType Type, object Value)[] numericTargets =
        [
            ("TINYINT", DatabaseType.Int8, (sbyte)42),
            ("SMALLINT", DatabaseType.Int16, (short)42),
            ("INT", DatabaseType.Int32, 42),
            ("BIGINT", DatabaseType.Int64, 42L),
            ("DECIMAL", DatabaseType.Decimal, 42m),
        ];

        foreach (object source in numericSources)
        {
            foreach (var target in numericTargets)
            {
                if (source.GetType() != target.Value.GetType())
                {
                    yield return [source, target.Name, target.Type, target.Value];
                }
            }

            yield return [source, "TEXT", DatabaseType.String, "42"];
        }

        foreach (var target in numericTargets)
        {
            yield return [" +42.000 ", target.Name, target.Type, target.Value];
        }

        yield return ["-128", "TINYINT", DatabaseType.Int8, sbyte.MinValue];
        yield return ["32767", "SMALLINT", DatabaseType.Int16, short.MaxValue];
        yield return ["-2147483648", "INTEGER", DatabaseType.Int32, int.MinValue];
        yield return ["9223372036854775807", "BIGINT", DatabaseType.Int64, long.MaxValue];
        yield return ["12.3400", "NUMERIC(4, 2)", DatabaseType.Decimal, 12.34m];
        yield return ["1.000", "DECIMAL(2, 0)", DatabaseType.Decimal, 1m];
        yield return ["0.001", "DECIMAL(3, 3)", DatabaseType.Decimal, .001m];
        yield return ["79228162514264337593543950335", "DECIMAL", DatabaseType.Decimal, decimal.MaxValue];
        yield return [" TrUe ", "BOOL", DatabaseType.Boolean, true];
        yield return ["FALSE", "BOOLEAN", DatabaseType.Boolean, false];
        yield return [true, "CHARACTER(4)", DatabaseType.String, "TRUE"];
        yield return [false, "VARCHAR(5)", DatabaseType.String, "FALSE"];
        yield return [12.25m, "TEXT", DatabaseType.String, "12.25"];
    }

    /// <summary>Proves projection values, CLR types, and metadata agree with the target.</summary>
    /// <param name="source">The source parameter.</param>
    /// <param name="target">The SQL target type.</param>
    /// <param name="type">The expected result metadata.</param>
    /// <param name="expected">The converted boxed value.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: supported conversions change projection values and types")]
    [MemberData(nameof(SuccessfulConversions))]
    public async Task Cast_SupportedPair_ShouldConvertProjection(object source, string target, DatabaseType type, object expected)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-projection" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, $"SELECT CAST(@value AS {target}) AS converted FROM t;",
            new Dictionary<string, object?> { ["value"] = source })).ShouldBeAssignableTo<QueryResultSet>();

        object? value = (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0];
        result.Columns[0].Type.ShouldBe(type);
        value.ShouldNotBeNull().GetType().ShouldBe(expected.GetType());
        value.ShouldBe(expected);
    }

    /// <summary>Rejects lossy or invalid conversion instead of accepting the original value.</summary>
    /// <param name="expression">The conversion expression that must fail.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: invalid text overflow scale and length fail explicitly")]
    [InlineData("CAST('abc' AS INT)")]
    [InlineData("CAST('invalid' AS INT)")]
    [InlineData("CAST('' AS INT)")]
    [InlineData("CAST('1,234' AS INT)")]
    [InlineData("CAST('1e2' AS DECIMAL)")]
    [InlineData("CAST('1.5' AS INT)")]
    [InlineData("CAST(1.5 AS INT)")]
    [InlineData("CAST('128' AS TINYINT)")]
    [InlineData("CAST('-129' AS TINYINT)")]
    [InlineData("CAST('32768' AS SMALLINT)")]
    [InlineData("CAST('-32769' AS SMALLINT)")]
    [InlineData("CAST('2147483648' AS INT)")]
    [InlineData("CAST('-2147483649' AS INT)")]
    [InlineData("CAST('9223372036854775808' AS BIGINT)")]
    [InlineData("CAST('-9223372036854775809' AS BIGINT)")]
    [InlineData("CAST('79228162514264337593543950336' AS DECIMAL)")]
    [InlineData("CAST('0.00000000000000000000000000001' AS DECIMAL)")]
    [InlineData("CAST(0.00000000000000000000000000001 AS DECIMAL)")]
    [InlineData("CAST(1e-29 AS DECIMAL)")]
    [InlineData("CAST(1.00000000000000000000000000001 AS DECIMAL)")]
    [InlineData("CAST('1.234' AS DECIMAL(4, 2))")]
    [InlineData("CAST('123.45' AS DECIMAL(4, 2))")]
    [InlineData("CAST('0.001' AS DECIMAL(2, 2))")]
    [InlineData("CAST(123 AS VARCHAR(2))")]
    [InlineData("CAST('abc' AS CHAR(2))")]
    [InlineData("CAST('😀' AS VARCHAR(1))")]
    [InlineData("CAST('yes' AS BOOLEAN)")]
    [InlineData("CAST(1 AS BOOLEAN)")]
    [InlineData("CAST(TRUE AS INT)")]
    public async Task Cast_InvalidValue_ShouldReportConversionError(string expression)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-errors" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {expression} FROM t;"));

        error.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>Supplies runtime types outside the explicitly supported source set.</summary>
    public static IEnumerable<object[]> UnsupportedSources()
    {
        yield return [(byte)1];
        yield return [(ushort)1];
        yield return [1u];
        yield return [1ul];
        yield return ['1'];
        yield return [1.5f];
        yield return [1.5d];
        yield return [new byte[] { 1 }];
        yield return [new DateOnly(2026, 1, 1)];
        yield return [new TimeOnly(1, 2)];
        yield return [new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)];
        yield return [new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)];
        yield return [TimeSpan.FromMinutes(1)];
        yield return [Guid.Empty];
    }

    /// <summary>Rejects unsupported source types even for a textual target.</summary>
    /// <param name="source">The unsupported source value.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: unsupported runtime sources fail explicitly")]
    [MemberData(nameof(UnsupportedSources))]
    public async Task Cast_UnsupportedSource_ShouldReject(object source)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-source" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(@value AS TEXT) FROM t;",
            new Dictionary<string, object?> { ["value"] = source }));

        error.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>Rejects unknown targets before execution, even when no rows can be produced.</summary>
    /// <param name="target">The unknown target name.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: unknown target fails before row evaluation")]
    [InlineData("NOTATYPE")]
    [InlineData("unknown_type")]
    public async Task Cast_UnknownTarget_ShouldRejectAtParseTime(string target)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-target" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session,
            $"SELECT CAST(id AS {target}) FROM t WHERE id < 0;"));

        error.Message.ShouldContain("SQL0004", Case.Sensitive);
        error.Message.ShouldContain("CAST", Case.Sensitive);
        error.Message.ShouldContain(target, Case.Sensitive);
    }

    /// <summary>Rejects CAST in schema expressions that cannot yet execute it honestly.</summary>
    /// <param name="statement">The unsupported schema statement.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: unsupported defaults and CHECK expressions fail explicitly")]
    [InlineData("CREATE TABLE defaults (value INT DEFAULT CAST('42' AS INT));")]
    [InlineData("ALTER TABLE t ADD COLUMN value INT DEFAULT CAST('42' AS INT);")]
    [InlineData("CREATE TABLE checks (value TEXT CHECK(CAST(value AS INT) > 0));")]
    public async Task Cast_UnsupportedSchemaExpression_ShouldReject(string statement)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-schema" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
    }

    /// <summary>Preserves typed nulls and target metadata without sampling result values.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: null and empty projections retain target metadata")]
    public async Task Cast_NullAndEmptyResults_ShouldRetainTargetMetadata()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-null" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var nullResult = (await ExecuteAsync(session, "SELECT CAST(NULL AS INT), CAST(NULL AS DECIMAL(5, 2)), CAST(NULL AS BOOLEAN), CAST('42' AS INT) FROM t;"))
            .ShouldBeAssignableTo<QueryResultSet>();
        object?[] row = (await ReadRowsAsync(nullResult)).ShouldHaveSingleItem();
        (nullResult.Columns[0].Type, row[0]).ShouldBe((DatabaseType.Int32, (object?)null));
        (nullResult.Columns[1].Type, row[1]).ShouldBe((DatabaseType.Decimal, (object?)null));
        (nullResult.Columns[2].Type, row[2]).ShouldBe((DatabaseType.Boolean, (object?)null));
        row[3].ShouldBeOfType<int>().ShouldBe(42);

        await using var emptyResult = (await ExecuteAsync(session, "SELECT CAST('42' AS BIGINT) FROM t WHERE id < 0;"))
            .ShouldBeAssignableTo<QueryResultSet>();
        (emptyResult.Columns[0].Type, (await ReadRowsAsync(emptyResult)).Count).ShouldBe((DatabaseType.Int64, 0));
    }

    /// <summary>Uses converted numeric and boolean values while evaluating predicates.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: WHERE predicates consume target values")]
    public async Task Cast_InPredicate_ShouldUseConvertedValues()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-predicate" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var numeric = (await ExecuteAsync(session, "SELECT id FROM t WHERE CAST('10' AS INT) > 9;"))
            .ShouldBeAssignableTo<QueryResultSet>();
        (await ReadRowsAsync(numeric)).ShouldHaveSingleItem()[0].ShouldBe(1);
        await using var boolean = (await ExecuteAsync(session, "SELECT id FROM t WHERE CAST('TRUE' AS BOOLEAN);"))
            .ShouldBeAssignableTo<QueryResultSet>();
        (await ReadRowsAsync(boolean)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    /// <summary>Preserves decimal predicate bounds when storage indexes have narrower integral keys.</summary>
    /// <param name="predicate">The predicate containing explicitly converted bounds.</param>
    /// <param name="expected">The matching stored identifiers.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: indexed predicates preserve fractional bounds")]
    [InlineData("id > CAST('1.5' AS DECIMAL)", new int[] { 2, 3 })]
    [InlineData("id < CAST('2.5' AS DECIMAL)", new int[] { 1, 2 })]
    [InlineData("id BETWEEN CAST('1.5' AS DECIMAL) AND CAST('2.5' AS DECIMAL)", new int[] { 2 })]
    [InlineData("id = CAST('1.5' AS DECIMAL)", new int[] { })]
    public async Task Cast_IndexedPredicate_ShouldPreserveExactBounds(string predicate, int[] expected)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-index" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES (2), (3);");
        await ExecuteAsync(session, "CREATE INDEX ix_id ON t(id);");

        await using var result = (await ExecuteAsync(session, $"SELECT id FROM t WHERE {predicate} ORDER BY id;"))
            .ShouldBeAssignableTo<QueryResultSet>();

        var rows = await ReadRowsAsync(result);
        rows.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++) { rows[i][0].ShouldBe(expected[i]); }
    }

    /// <summary>Preserves supported SQL numeric literal syntax while converting its representation.</summary>
    /// <param name="literal">The exponent or leading-decimal-point numeric literal.</param>
    /// <param name="expected">The exact invariant textual conversion.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: SQL decimal literal syntax converts exactly")]
    [InlineData("1e2", "100")]
    [InlineData(".5", "0.5")]
    public async Task Cast_NumericLiteral_ShouldConvertExactly(string literal, string expected)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-literal" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, $"SELECT CAST({literal} AS TEXT) FROM t;"))
            .ShouldBeAssignableTo<QueryResultSet>();

        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBeOfType<string>().ShouldBe(expected);
        result.Columns[0].Type.ShouldBe(DatabaseType.String);
    }

    /// <summary>Allows small signed conversion results to participate in downstream numeric operations.</summary>
    /// <param name="statement">The expression or row limit consuming a small signed target.</param>
    /// <param name="expected">The expected widened numeric result.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: small signed targets work in numeric expressions and row limits")]
    [InlineData("SELECT -CAST('12' AS TINYINT) FROM t;", -12L)]
    [InlineData("SELECT ABS(CAST('-12' AS SMALLINT)) FROM t;", 12L)]
    [InlineData("SELECT CAST('12' AS BIGINT) FROM t LIMIT CAST('1' AS SMALLINT);", 12L)]
    public async Task Cast_SmallSignedTarget_ShouldWorkDownstream(string statement, long expected)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-small-integer" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, statement)).ShouldBeAssignableTo<QueryResultSet>();

        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBeOfType<long>().ShouldBe(expected);
    }

    /// <summary>Requires conversion before arithmetic so storage coercion cannot hide the defect.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: INSERT and UPDATE expressions convert before storage")]
    public async Task Cast_InWriteExpression_ShouldConvertBeforeStorage()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cast-write" });
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await ExecuteAsync(session, "INSERT INTO t VALUES (CAST('40' AS INT) + 2);");
        await ExecuteAsync(session, "UPDATE t SET id = CAST('41' AS INT) + 2 WHERE id = 42;");

        await using var result = (await ExecuteAsync(session, "SELECT CAST(id AS TEXT) FROM t WHERE id > 1;"))
            .ShouldBeAssignableTo<QueryResultSet>();
        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBe("43");
    }

    private static async Task SeedAsync(IDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++) { values[i] = row.GetValue(i); }
            rows.Add(values);
        }

        return rows;
    }
}
