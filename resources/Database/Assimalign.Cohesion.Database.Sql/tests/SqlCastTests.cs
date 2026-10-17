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
        yield return ["1.234", "DECIMAL(4, 2)", DatabaseType.Decimal, 1.23m];
        yield return ["0.001", "DECIMAL(2, 2)", DatabaseType.Decimal, 0m];
        yield return [123, "VARCHAR(2)", DatabaseType.String, "12"];
        yield return ["abc", "CHAR(2)", DatabaseType.Character, "ab"];
        yield return ["😀", "VARCHAR(1)", DatabaseType.String, "😀"];
        yield return ["79228162514264337593543950335", "DECIMAL", DatabaseType.Decimal, decimal.MaxValue];
        yield return [" TrUe ", "BOOL", DatabaseType.Boolean, true];
        yield return ["FALSE", "BOOLEAN", DatabaseType.Boolean, false];
        yield return [true, "CHARACTER(4)", DatabaseType.Character, "TRUE"];
        yield return [false, "VARCHAR(5)", DatabaseType.String, "FALSE"];
        yield return [12.25m, "TEXT", DatabaseType.String, "12.25"];

        // Phase E3: the cast registry's new targets, from text as INSERT parses it for a column of
        // the type, and REAL and DOUBLE from the numbers.
        yield return ["2026-02-01", "DATE", DatabaseType.Date, new DateOnly(2026, 2, 1)];
        yield return [new DateTime(2026, 2, 1, 13, 45, 30), "DATE", DatabaseType.Date, new DateOnly(2026, 2, 1)];
        yield return ["13:45:30", "TIME", DatabaseType.Time, new TimeOnly(13, 45, 30)];
        yield return ["2026-02-01T13:45:30", "TIMESTAMP", DatabaseType.DateTime, new DateTime(2026, 2, 1, 13, 45, 30)];
        yield return ["2026-02-01T13:45:30+02:00", "TIMESTAMPTZ", DatabaseType.DateTimeOffset,
            new DateTimeOffset(2026, 2, 1, 13, 45, 30, TimeSpan.FromHours(2))];
        yield return ["01:30:00", "INTERVAL", DatabaseType.TimeSpan, TimeSpan.FromMinutes(90)];
        yield return ["6f9619ff-8b86-d011-b42d-00c04fc964ff", "UUID", DatabaseType.Guid, Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")];
        yield return [" 1.5 ", "REAL", DatabaseType.Float32, 1.5f];
        yield return ["-2.5e3", "DOUBLE", DatabaseType.Float64, -2500d];
        yield return [1.5f, "DOUBLE", DatabaseType.Float64, 1.5d];
        yield return [1.5d, "REAL", DatabaseType.Float32, 1.5f];
        foreach (object source in numericSources)
        {
            yield return [source, "REAL", DatabaseType.Float32, 42f];
            yield return [source, "DOUBLE", DatabaseType.Float64, 42d];
        }
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
        await using var engine = SqlDatabaseEngine.Create("cast-projection", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, $"SELECT CAST(@value AS {target}) AS converted FROM t;",
            new Dictionary<string, object?> { ["value"] = source })).ShouldBeAssignableTo<DatabaseResultSet>();

        object? value = (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0];
        result.Columns[0].Type.ShouldBe(type);
        value.ShouldNotBeNull().GetType().ShouldBe(expected.GetType());
        value.ShouldBe(expected);
    }

    /// <summary>Rejects lossy or invalid conversion instead of accepting the original value.</summary>
    /// <param name="expression">The conversion expression that must fail.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: invalid text and overflow fail explicitly")]
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
    [InlineData("CAST('123.45' AS DECIMAL(4, 2))")]
    [InlineData("CAST('yes' AS BOOLEAN)")]
    [InlineData("CAST(1 AS BOOLEAN)")]
    [InlineData("CAST(TRUE AS INT)")]
    [InlineData("CAST('not a date' AS DATE)")]
    [InlineData("CAST(1 AS DATE)")]
    [InlineData("CAST('25:00' AS TIME)")]
    [InlineData("CAST('x' AS UUID)")]
    [InlineData("CAST(1.5 AS UUID)")]
    [InlineData("CAST('1,5' AS DOUBLE)")]
    [InlineData("CAST(TRUE AS DOUBLE)")]
    [InlineData("CAST('1' AS JSON)")]
    [InlineData("CAST(1 AS BINARY)")]
    public async Task Cast_InvalidValue_ShouldReportConversionError(string expression)
    {
        await using var engine = SqlDatabaseEngine.Create("cast-errors", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {expression} FROM t;"));

        error.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>
    /// A cast between types of the cast registry fails with the conversion's reason, naming the
    /// registry's pair when none converts, and REAL refuses what it overflows or underflows (E3).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: the registry names a missing pair, and REAL refuses overflow and underflow")]
    public async Task Cast_RegistryFailures_ShouldNameThePairOrTheRange()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-registry", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var missing = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(id AS DATE) FROM t;"));
        var overflow = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(@value AS REAL) FROM t;",
            new Dictionary<string, object?> { ["value"] = 1e300 }));
        var underflow = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(@value AS REAL) FROM t;",
            new Dictionary<string, object?> { ["value"] = 1e-300 }));
        await using var infinite = (await ExecuteAsync(session, "SELECT CAST(@value AS REAL) FROM t;",
            new Dictionary<string, object?> { ["value"] = double.PositiveInfinity })).ShouldBeAssignableTo<DatabaseResultSet>();

        missing.DiagnosticDetail("COHSQLE011").ShouldBe("Cannot CAST type INTEGER to DATE: no cast from INTEGER to DATE is registered. " +
            "Register one on the engine's builder (SqlDatabaseEngineBuilder.Types.AddCast) before Build.");
        overflow.Message.ShouldContain("is out of range for REAL", Case.Sensitive);
        underflow.Message.ShouldContain("underflows REAL", Case.Sensitive);
        (await ReadRowsAsync(infinite)).ShouldHaveSingleItem()[0].ShouldBe(float.PositiveInfinity);
    }

    /// <summary>
    /// A CAST no registered cast performs fails while planning whenever the plan knows the operand's
    /// type, so it fails over an empty table as over a populated one (<c>COHSQLE011</c>, PostgreSQL's
    /// 42846 from parse analysis); before E3 the parser refused these targets outright.
    /// </summary>
    /// <param name="expression">A CAST of a statically typed operand that no cast converts.</param>
    /// <param name="pair">The pair the message names.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: a pair no cast converts fails while planning with COHSQLE011, rows or not")]
    [InlineData("CAST(1 AS DATE)", "BIGINT to DATE")]
    [InlineData("CAST(TRUE AS DOUBLE)", "BOOLEAN to DOUBLE")]
    [InlineData("CAST(TRUE AS INT)", "BOOLEAN to INTEGER")]
    [InlineData("CAST(1 AS BOOLEAN)", "BIGINT to BOOLEAN")]
    [InlineData("CAST(1.5 AS UUID)", "NUMERIC to UUID")]
    [InlineData("CAST('1' AS JSON)", "TEXT to JSON")]
    [InlineData("CAST(1 AS BINARY)", "BIGINT to BINARY")]
    [InlineData("CAST(id AS DATE)", "INTEGER to DATE")]
    [InlineData("CAST(id = 1 AS DATE)", "BOOLEAN to DATE")]
    [InlineData("CAST(CAST(id AS DOUBLE) AS NUMERIC)", "DOUBLE to NUMERIC")]
    [InlineData("CAST(UPPER('a') AS BINARY)", "TEXT to BINARY")]
    public async Task Cast_NoRegisteredCast_ShouldFailWhilePlanning(string expression, string pair)
    {
        await using var engine = SqlDatabaseEngine.Create("cast-planning", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var empty = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {expression} FROM t WHERE id < 0;"));
        var populated = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {expression} FROM t;"));
        var written = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"UPDATE t SET id = 2 WHERE {expression} IS NULL;"));

        empty.DiagnosticDetail("COHSQLE011").ShouldStartWith($"Cannot CAST type {pair}: ", Case.Sensitive);
        populated.Message.ShouldBe(empty.Message);
        written.Message.ShouldBe(empty.Message);
        (await RowsAsync(session, "SELECT id FROM t;")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    /// <summary>
    /// The planner's check sees the value a row holds, as the evaluator converts it: a JSON column's
    /// value is text, so it casts to text, as it did before the cast registry.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: a JSON column casts to text, the planner typing it as the row holds it")]
    public async Task Cast_JsonColumnToText_ShouldPlanAsTheRowHoldsIt()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-json", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE j (id INT, doc JSON);");
        await ExecuteAsync(session, "INSERT INTO j VALUES (1, '{\"a\":1}');");

        var rows = await RowsAsync(session, "SELECT CAST(doc AS TEXT), CAST(doc AS VARCHAR(10)) FROM j;");
        var empty = await RowsAsync(session, "SELECT CAST(doc AS TEXT) FROM j WHERE id < 0;");

        rows.ShouldHaveSingleItem().ShouldBe(["{\"a\":1}", "{\"a\":1}"]);
        empty.ShouldBeEmpty();
    }

    /// <summary>
    /// A parameter's type only its value reveals, so a CAST of a parameter no cast converts plans, and
    /// fails with the same code when a row evaluates it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: a parameter no cast converts fails with COHSQLE011 when a row evaluates it")]
    public async Task Cast_ParameterWithoutACast_ShouldFailWhenEvaluated()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-parameter", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        var parameters = new Dictionary<string, object?> { ["value"] = 5 };

        await using var empty = (await ExecuteAsync(session, "SELECT CAST(@value AS DATE) FROM t WHERE id < 0;", parameters))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadRowsAsync(empty)).ShouldBeEmpty();
        var populated = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(@value AS DATE) FROM t;", parameters));

        populated.DiagnosticDetail("COHSQLE011").ShouldStartWith("Cannot CAST type INTEGER to DATE: ", Case.Sensitive);
    }

    /// <summary>
    /// DATE, TIMESTAMP and TIMESTAMPTZ text reads the same under CAST and INSERT, whatever the host's
    /// clock and time zone: an ISO date first, TIMESTAMP keeps the written clock time and ignores an
    /// offset (PostgreSQL's <c>timestamp_in</c>), TIMESTAMPTZ without an offset is UTC.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: temporal text reads the same under CAST and INSERT, independent of the host's clock and time zone")]
    public async Task Cast_TemporalText_ShouldNotDependOnTheHost()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-temporal", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE times (id INT, d DATE, ts TIMESTAMP, tz TIMESTAMPTZ);");

        var cast = (await RowsAsync(session,
            "SELECT CAST('2026-01-01T00:00:00+05:00' AS TIMESTAMP), CAST('2026-01-01 08:30:00Z' AS TIMESTAMP), " +
            "CAST('2026-01-01 00:00:00' AS TIMESTAMPTZ), CAST('2026-02-01' AS TIMESTAMPTZ), " +
            "CAST('2026-02-01T13:45:30+02:00' AS TIMESTAMPTZ) FROM t;")).ShouldHaveSingleItem();
        await ExecuteAsync(session, "INSERT INTO times VALUES (1, '2026-02-01', '2026-01-01T00:00:00+05:00', '2026-01-01 00:00:00');");
        var inserted = (await RowsAsync(session, "SELECT d, ts, tz FROM times;")).ShouldHaveSingleItem();
        var missingYear = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST('1.5' AS DATE) FROM t;"));
        var shortYear = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST('12.345' AS DATE) FROM t;"));
        var timestamp = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST('1.5 10:00' AS TIMESTAMP) FROM t;"));
        var insertRefused = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO times (id, d) VALUES (2, '1.5');"));

        var clock = cast[0].ShouldBeOfType<DateTime>();
        clock.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0));
        clock.Kind.ShouldBe(DateTimeKind.Unspecified);
        cast[1].ShouldBe(new DateTime(2026, 1, 1, 8, 30, 0));
        cast[2].ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        cast[2].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        cast[3].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        cast[4].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.FromHours(2));
        inserted.ShouldBe([new DateOnly(2026, 2, 1), new DateTime(2026, 1, 1, 0, 0, 0), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)]);
        inserted[2].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        missingYear.Message.ShouldBe("CAST from String to DATE failed: DATE text must start with an ISO 8601 date, yyyy-mm-dd.");
        shortYear.Message.ShouldContain("must start with an ISO 8601 date", Case.Sensitive);
        timestamp.Message.ShouldContain("TIMESTAMP text must start with an ISO 8601 date", Case.Sensitive);
        insertRefused.Message.ShouldStartWith("Cannot convert value '1.5' to column 'd'", Case.Sensitive);
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
        await using var engine = SqlDatabaseEngine.Create("cast-source", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT CAST(@value AS TEXT) FROM t;",
            new Dictionary<string, object?> { ["value"] = source }));

        error.Message.ShouldContain("CAST", Case.Sensitive);
    }

    /// <summary>
    /// Rejects unknown targets while planning, even when no rows can be produced: the parser leaves a
    /// name that is not a built-in type to the engine's type catalog (phase E3), which does not know it.
    /// </summary>
    /// <param name="target">The unknown target name.</param>
    /// <param name="invalidModifier">Whether parsing rejects modifiers before catalog lookup.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: unknown target fails before row evaluation")]
    [InlineData("NOTATYPE", false)]
    [InlineData("unknown_type", false)]
    [InlineData("unknown_type(4)", true)]
    public async Task Cast_UnknownTarget_ShouldRejectBeforeExecution(string target, bool invalidModifier)
    {
        await using var engine = SqlDatabaseEngine.Create("cast-target", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            $"SELECT CAST(id AS {target}) FROM t WHERE id < 0;"));

        if (invalidModifier)
        {
            error.ShouldBeOfType<DatabaseParseException>();
            error.Message.ShouldContain("SQL0005", Case.Sensitive);
            error.Message.ShouldContain("CAST target", Case.Sensitive);
        }
        else
        {
            error.ShouldNotBeOfType<DatabaseParseException>();
            error.DiagnosticDetail("COHSQLE010").ShouldStartWith("CAST target: type '" + target + "' does not exist", Case.Sensitive);
            error.Message.ShouldContain("SqlDatabaseEngineBuilder.Types", Case.Sensitive);
        }
    }

    /// <summary>Rejects CAST in schema expressions that cannot yet execute it honestly.</summary>
    /// <param name="statement">The unsupported schema statement.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: unsupported defaults and CHECK expressions fail explicitly")]
    [InlineData("CREATE TABLE defaults (value INT DEFAULT CAST('42' AS INT));")]
    [InlineData("ALTER TABLE t ADD COLUMN value INT DEFAULT CAST('42' AS INT);")]
    [InlineData("CREATE TABLE checks (value TEXT CHECK(CAST(value AS INT) > 0));")]
    public async Task Cast_UnsupportedSchemaExpression_ShouldReject(string statement)
    {
        await using var engine = SqlDatabaseEngine.Create("cast-schema", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
    }

    /// <summary>Preserves typed nulls and target metadata without sampling result values.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: null and empty projections retain target metadata")]
    public async Task Cast_NullAndEmptyResults_ShouldRetainTargetMetadata()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-null", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var nullResult = (await ExecuteAsync(session, "SELECT CAST(NULL AS INT), CAST(NULL AS DECIMAL(5, 2)), CAST(NULL AS BOOLEAN), CAST('42' AS INT) FROM t;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        object?[] row = (await ReadRowsAsync(nullResult)).ShouldHaveSingleItem();
        (nullResult.Columns[0].Type, row[0]).ShouldBe((DatabaseType.Int32, (object?)null));
        (nullResult.Columns[1].Type, row[1]).ShouldBe((DatabaseType.Decimal, (object?)null));
        (nullResult.Columns[2].Type, row[2]).ShouldBe((DatabaseType.Boolean, (object?)null));
        row[3].ShouldBeOfType<int>().ShouldBe(42);

        await using var emptyResult = (await ExecuteAsync(session, "SELECT CAST('42' AS BIGINT) FROM t WHERE id < 0;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (emptyResult.Columns[0].Type, (await ReadRowsAsync(emptyResult)).Count).ShouldBe((DatabaseType.Int64, 0));
    }

    /// <summary>Uses converted numeric and boolean values while evaluating predicates.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: WHERE predicates consume target values")]
    public async Task Cast_InPredicate_ShouldUseConvertedValues()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-predicate", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var numeric = (await ExecuteAsync(session, "SELECT id FROM t WHERE CAST('10' AS INT) > 9;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadRowsAsync(numeric)).ShouldHaveSingleItem()[0].ShouldBe(1);
        await using var boolean = (await ExecuteAsync(session, "SELECT id FROM t WHERE CAST('TRUE' AS BOOLEAN);"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
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
        await using var engine = SqlDatabaseEngine.Create("cast-index", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES (2), (3);");
        await ExecuteAsync(session, "CREATE INDEX ix_id ON t(id);");

        await using var result = (await ExecuteAsync(session, $"SELECT id FROM t WHERE {predicate} ORDER BY id;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

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
        await using var engine = SqlDatabaseEngine.Create("cast-literal", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, $"SELECT CAST({literal} AS TEXT) FROM t;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

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
        await using var engine = SqlDatabaseEngine.Create("cast-small-integer", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await using var result = (await ExecuteAsync(session, statement)).ShouldBeAssignableTo<DatabaseResultSet>();

        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBeOfType<long>().ShouldBe(expected);
    }

    /// <summary>Requires conversion before arithmetic so storage coercion cannot hide the defect.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: INSERT and UPDATE expressions convert before storage")]
    public async Task Cast_InWriteExpression_ShouldConvertBeforeStorage()
    {
        await using var engine = SqlDatabaseEngine.Create("cast-write", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("cast");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        await ExecuteAsync(session, "INSERT INTO t VALUES (CAST('40' AS INT) + 2);");
        await ExecuteAsync(session, "UPDATE t SET id = CAST('41' AS INT) + 2 WHERE id = 42;");

        await using var result = (await ExecuteAsync(session, "SELECT CAST(id AS TEXT) FROM t WHERE id > 1;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBe("43");
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(DatabaseResultSet result)
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
