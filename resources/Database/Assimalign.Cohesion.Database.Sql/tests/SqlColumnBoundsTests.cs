using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies declared column modifiers at every assignment boundary, including the production client.</summary>
public sealed class SqlColumnBoundsTests
{
    private const string stringTooLong = "CS02012";
    private const string numericOverflow = "CS02002";

    /// <summary>Values accepted through every assignment path, with their stored representations.</summary>
    public static IEnumerable<object?[]> AcceptedAssignments()
    {
        object?[][] cases =
        [
            ["VARCHAR(3)", "'abc'", "abc"],
            ["VARCHAR(3)", "'abc   '", "abc"],
            ["VARCHAR(3)", "'😀ab'", "😀ab"],
            ["VARCHAR(3)", "'😀ab   '", "😀ab"],
            ["CHAR(3)", "'a'", "a  "],
            ["CHAR(3)", "'abc   '", "abc"],
            ["CHAR(3)", "'😀ab'", "😀ab"],
            ["NUMERIC(4, 2)", "12.345", 12.35m],
            ["NUMERIC(4, 2)", "-12.345", -12.35m],
            ["NUMERIC(4, 2)", "99.994", 99.99m],
            ["NUMERIC(3, 3)", "0.0005", 0.001m],
            ["NUMERIC(1, 0)", "9.49", 9m],
            ["NUMERIC(2)", "1.5", 2m],
            ["VARCHAR", "'a value longer than three characters  '", "a value longer than three characters  "],
            ["NUMERIC", "12.34567", 12.34567m],
            ["VARCHAR(3)", "NULL", null],
            ["CHAR(3)", "NULL", null],
            ["NUMERIC(4, 2)", "NULL", null],
        ];

        foreach (string path in Paths())
        {
            foreach (object?[] item in cases)
            {
                yield return [path, item[0], item[1], item[2]];
            }
        }
    }

    /// <summary>Rejected values include precision overflow caused by rounding and non-space excess characters.</summary>
    public static IEnumerable<object[]> RejectedAssignments()
    {
        string[][] cases =
        [
            ["VARCHAR(3)", "'12.345'", stringTooLong],
            ["VARCHAR(3)", "'😀abc'", stringTooLong],
            ["VARCHAR(3)", "'abc\t'", stringTooLong],
            ["VARCHAR(3)", "'abc\u00a0'", stringTooLong],
            ["CHAR(3)", "'abcd'", stringTooLong],
            ["NUMERIC(4, 2)", "123.45", numericOverflow],
            ["NUMERIC(4, 2)", "99.995", numericOverflow],
            ["NUMERIC(4, 2)", "-99.995", numericOverflow],
            ["NUMERIC(2, 2)", "0.995", numericOverflow],
            ["NUMERIC(2)", "99.5", numericOverflow],
        ];

        foreach (string path in Paths())
        {
            foreach (string[] item in cases)
            {
                yield return [path, item[0], item[1], item[2]];
            }
        }
    }

    /// <summary>Successful writes store the bounded value, and ADD COLUMN uses it for both old and new rows.</summary>
    /// <param name="path">The write path.</param>
    /// <param name="type">The declared column type.</param>
    /// <param name="literal">The assigned SQL literal.</param>
    /// <param name="expected">The stored value.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: every write path stores rounded numbers and bounded Unicode text")]
    [MemberData(nameof(AcceptedAssignments))]
    public async Task Write_AcceptedValue_ShouldStoreBoundedValue(string path, string type, string literal, object? expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string statement = await PrepareAsync(sql => ExecuteAsync(session, sql), path, type, literal, false);

        // Act
        await ExecuteAsync(session, statement);

        // Assert
        (await RowsAsync(session, "SELECT v FROM bounded_values")).ShouldHaveSingleItem()[0].ShouldBe(expected);
        if (path == "add-column")
        {
            await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (2)");
            (await RowsAsync(session, "SELECT v FROM bounded_values ORDER BY id")).Select(row => row[0])
                .ShouldBe(new[] { expected, expected });
        }
    }

    /// <summary>Every rejected assignment has a stable code and leaves all rows and the published schema unchanged.</summary>
    /// <param name="path">The write path.</param>
    /// <param name="type">The declared column type.</param>
    /// <param name="literal">The rejected SQL literal.</param>
    /// <param name="code">The expected evaluation code.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: every write path rejects overflow atomically with its code")]
    [MemberData(nameof(RejectedAssignments))]
    public async Task Write_OutOfBoundsValue_ShouldRejectAtomicallyWithCode(string path, string type, string literal, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("column-bounds-error", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string statement = await PrepareAsync(sql => ExecuteAsync(session, sql), path, type, literal, true);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));

        // Assert
        error.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(code);
        error.ShouldStartWithMessage(code + ":", Case.Sensitive);
        (await RowsAsync(session, "SELECT * FROM bounded_values")).ShouldHaveSingleItem()
            .ShouldBe(path == "add-column" ? new object?[] { 1 } : new object?[] { 1, null });
    }

    /// <summary>The production client reads the same bounded values as an in-process session on every write path.</summary>
    /// <param name="path">The write path.</param>
    /// <param name="type">The declared column type.</param>
    /// <param name="literal">The assigned SQL literal.</param>
    /// <param name="expected">The stored value.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: the production client reads bounded values for every write path")]
    [MemberData(nameof(AcceptedAssignments))]
    public async Task Wire_AcceptedValue_ShouldReturnBoundedValue(string path, string type, string literal, object? expected)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = CreateClient(harness);
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        string statement = await PrepareAsync(sql => ExecuteAsync(connection, sql), path, type, literal, false);

        // Act
        await ExecuteAsync(connection, statement);

        // Assert
        (await connection.QueryAsync("SELECT v FROM bounded_values", cancellationToken: TestTimeout.Token()))
            .ShouldHaveSingleItem()["v"].ShouldBe(expected);
    }

    /// <summary>The production client receives the evaluation code as an execution failure and keeps its session usable.</summary>
    /// <param name="path">The write path.</param>
    /// <param name="type">The declared column type.</param>
    /// <param name="literal">The rejected SQL literal.</param>
    /// <param name="code">The expected evaluation code.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: the production client receives coded overflow errors for every write path")]
    [MemberData(nameof(RejectedAssignments))]
    public async Task Wire_OutOfBoundsValue_ShouldReceiveCodeAndKeepConnection(string path, string type, string literal, string code)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = CreateClient(harness);
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        string statement = await PrepareAsync(sql => ExecuteAsync(connection, sql), path, type, literal, true);

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection, statement));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ShouldStartWithMessage(code + ":", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id FROM bounded_values", cancellationToken: TestTimeout.Token()))
            .ShouldHaveSingleItem()["id"].ShouldBe(1);
    }

    /// <summary>Explicit casts truncate by Unicode scalar count while assignments reject non-space excess characters.</summary>
    /// <param name="type">The character target type.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: explicit character CAST truncates and assignment rejects")]
    [InlineData("VARCHAR(3)")]
    [InlineData("CHAR(3)")]
    public async Task Cast_OverlongCharacterValue_ShouldTruncateWhileAssignmentRejects(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("column-cast-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE bounded_values (id INT, v {type})");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "INSERT INTO bounded_values VALUES (1, '😀abc')"));
        await ExecuteAsync(session, $"INSERT INTO bounded_values VALUES (1, CAST('😀abc' AS {type}))");

        // Assert
        error.ShouldStartWithMessage(stringTooLong + ":", Case.Sensitive);
        (await RowsAsync(session, "SELECT v FROM bounded_values")).ShouldHaveSingleItem()[0].ShouldBe("😀ab");
    }

    /// <summary>Numeric CAST and assignment round at the same midpoint and reject the same precision overflow.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: NUMERIC CAST shares assignment rounding and coded precision overflow")]
    public async Task Cast_NumericModifier_ShouldRoundAndRejectWithAssignmentCode()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("numeric-cast-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT)");
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (1)");

        // Act
        var rounded = await RowsAsync(session, "SELECT CAST(12.345 AS NUMERIC(4, 2)), CAST(-12.345 AS NUMERIC(4, 2)) FROM bounded_values");
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            "SELECT CAST(99.995 AS NUMERIC(4, 2)) FROM bounded_values"));

        // Assert
        rounded.ShouldHaveSingleItem().ShouldBe(new object?[] { 12.35m, -12.35m });
        error.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
        error.ShouldStartWithMessage(numericOverflow + ":", Case.Sensitive);
    }

    /// <summary>Rounding and trimming a predicate's comparand must never narrow an index seek past qualifying rows.</summary>
    /// <param name="type">The indexed column type.</param>
    /// <param name="values">The stored test values.</param>
    /// <param name="predicate">The predicate whose literal must remain a comparison value.</param>
    /// <param name="expected">The independently expected qualifying ids.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: planner literals preserve indexed predicate results")]
    [InlineData("NUMERIC(4, 2)", "(1, 1.23), (2, 1.24)", "v < 1.234", new[] { 1 })]
    [InlineData("NUMERIC(4, 2)", "(1, 1.23), (2, 1.24)", "v > 1.235", new[] { 2 })]
    [InlineData("NUMERIC(4, 2)", "(1, 1.23), (2, 1.24)", "v = 1.234", new int[0])]
    [InlineData("NUMERIC(4, 2)", "(1, 1.23), (2, 1.24)", "v < 123.45", new[] { 1, 2 })]
    [InlineData("VARCHAR(3)", "(1, 'abc'), (2, 'abd')", "v < 'abc '", new[] { 1 })]
    [InlineData("VARCHAR(3)", "(1, 'abc'), (2, 'abd')", "v < 'abcd'", new[] { 1 })]
    [InlineData("CHAR(3)", "(1, 'abc'), (2, 'abd')", "v < 'abc '", new int[0])]
    public async Task Select_BoundedColumnComparand_ShouldMatchScanWithoutApplyingAssignmentModifiers(
        string type, string values, string predicate, int[] expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("planner-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE bounded_values (id INT, v {type})");
        await ExecuteAsync(session, $"INSERT INTO bounded_values VALUES {values}");
        string sql = $"SELECT id FROM bounded_values WHERE {predicate} ORDER BY id";
        var scanned = await RowsAsync(session, sql);
        await ExecuteAsync(session, "CREATE INDEX bounds_index ON bounded_values (v)");

        // Act
        var indexed = await RowsAsync(session, sql);

        // Assert
        scanned.Select(row => row[0]).ShouldBe(expected.Cast<object?>());
        indexed.Select(row => row[0]).ShouldBe(expected.Cast<object?>());
    }

    /// <summary>Parameter values and multi-row writes apply modifiers before publishing any row.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: parameter overflow rolls back the whole statement and preserves an open transaction")]
    public async Task Insert_ParameterizedLaterRowOverflows_ShouldPreserveTransactionAndEarlierStatements()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameter-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v NUMERIC(4, 2))");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (1, 1.235)");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO bounded_values VALUES (2, @valid), (3, @invalid)",
            new Dictionary<string, object?> { ["valid"] = 12.345m, ["invalid"] = 99.995m }, CancellationToken.None));
        await ExecuteAsync(session, "COMMIT");

        // Assert
        error.ShouldStartWithMessage(numericOverflow + ":", Case.Sensitive);
        (await RowsAsync(session, "SELECT id, v FROM bounded_values")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 1, 1.24m });
    }

    /// <summary>CREATE TABLE defaults share the assignment rule before they are cached for omitted-column inserts.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: CREATE TABLE defaults retain assignment rounding and character semantics")]
    public async Task CreateTable_BoundedDefaults_ShouldSupplyBoundedValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v VARCHAR(3) DEFAULT 'abc   ', " +
            "c CHAR(3) DEFAULT 'a', n NUMERIC(4, 2) DEFAULT 12.345, m NUMERIC(4, 2) DEFAULT '-12.345')");
        await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (1)");

        // Assert: CHAR retains its storage padding.
        (await RowsAsync(session, "SELECT v, c, n, m FROM bounded_values")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { "abc", "a  ", 12.35m, -12.35m });
    }

    /// <summary>Domain checks observe the bounded value for assignments, defaults, backfill and explicit CAST.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: domain checks run after shared rounding and character coercion")]
    public async Task DomainCheck_ModifiedInput_ShouldObserveBoundedValue()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("domain-column-bounds");
        builder.Types.Add(SqlType.CreateDomain("rounded_amount", SqlType.Decimal(4, 2),
            SqlScalarFunction.Create("is_rounded_amount", static (decimal value) => value == 1.24m, SqlFunctionVolatility.Immutable)));
        builder.Types.Add(SqlType.CreateDomain("trimmed_code", SqlType.VarChar(3),
            SqlScalarFunction.Create("is_trimmed_code", static (string value) => value == "abc", SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, n rounded_amount DEFAULT 1.235, t trimmed_code DEFAULT 'abc   ')");

        // Act
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (1, 1.235, 'abc   ')");
        await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (2)");
        await ExecuteAsync(session, "ALTER TABLE bounded_values ADD COLUMN extra rounded_amount DEFAULT 1.235");
        var rows = await RowsAsync(session, "SELECT n, t, extra, CAST(1.235 AS rounded_amount), CAST('abcd' AS trimmed_code) " +
            "FROM bounded_values ORDER BY id");

        // Assert
        rows.Count.ShouldBe(2);
        foreach (object?[] row in rows)
        {
            row.ShouldBe(new object?[] { 1.24m, "abc", 1.24m, 1.24m, "abc" });
        }
    }

    /// <summary>A collated index probe keeps the original text even when its scalar count exceeds the inner column's bound.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: indexed joins retain collated matches for longer outer text")]
    public async Task Join_OverlongCollationEquivalentProbe_ShouldMatchScan()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE probes (id INT, v TEXT COLLATE case_accent_insensitive)");
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v VARCHAR(1) COLLATE case_accent_insensitive)");
        await ExecuteAsync(session, "INSERT INTO probes VALUES (1, 'e\u0301'), (2, 'EE'), (3, 'E')");
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (10, 'e'), (20, 'x')");
        const string sql = "SELECT p.id, b.id FROM probes p JOIN bounded_values b ON p.v = b.v ORDER BY p.id";
        var scanned = await RowsAsync(session, sql);
        session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe(["join-hash"]);
        await ExecuteAsync(session, "CREATE INDEX bounds_index ON bounded_values (v)");

        // Act
        var indexed = await RowsAsync(session, sql);

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe(["join-seek:bounds_index"]);
        scanned.Count.ShouldBe(2);
        scanned[0].ShouldBe(new object?[] { 1, 10 });
        scanned[1].ShouldBe(new object?[] { 3, 10 });
        indexed.Count.ShouldBe(2);
        indexed[0].ShouldBe(scanned[0]);
        indexed[1].ShouldBe(scanned[1]);
    }

    /// <summary>Sharing the bounding routine does not let invalid numeric modifiers publish a default.</summary>
    /// <param name="type">The invalid numeric declaration.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: invalid numeric precision and scale refuse default publication")]
    [InlineData("NUMERIC(0, 0)")]
    [InlineData("NUMERIC(2, 3)")]
    public async Task Default_InvalidNumericModifiers_ShouldRejectBeforePublication(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE existing_values (id INT)");
        await ExecuteAsync(session, "INSERT INTO existing_values VALUES (1)");

        // Act
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"CREATE TABLE bounded_values (v {type} DEFAULT 0)"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"ALTER TABLE existing_values ADD COLUMN v {type} DEFAULT 0"));

        // Assert
        database.Catalog.TryGetTable("dbo", "bounded_values", out _).ShouldBeFalse();
        (await RowsAsync(session, "SELECT * FROM existing_values")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1 });
    }

    /// <summary>A scale at Decimal's limit retains fractions while still limiting integer digits.</summary>
    /// <param name="type">The valid declaration near Decimal's scale limit.</param>
    /// <param name="overflow">The first integer outside the declaration's precision.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: wide numeric scale defaults retain fractions and enforce integer bounds")]
    [InlineData("NUMERIC(28, 27)", "10")]
    [InlineData("NUMERIC(28, 28)", "1")]
    public async Task Default_WideNumericScale_ShouldRetainFractionAndEnforcePrecision(string type, string overflow)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("wide-default-column-bounds", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE bounded_values (id INT, v {type} DEFAULT 0.1)");
        await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (1)");

        // Act
        await ExecuteAsync(session, $"ALTER TABLE bounded_values ADD COLUMN extra {type} DEFAULT 0.1");
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            $"ALTER TABLE bounded_values ADD COLUMN refused {type} DEFAULT {overflow}"));

        // Assert
        error.ShouldStartWithMessage(numericOverflow + ":", Case.Sensitive);
        (await RowsAsync(session, "SELECT * FROM bounded_values")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 1, 0.1m, 0.1m });
    }

    /// <summary>Full-precision text rounds directly to the declared scale before conversion to Decimal.</summary>
    /// <returns>The declared precision, scale, input text and exact rounded value.</returns>
    public static IEnumerable<object[]> NumericTextAssignments()
    {
        yield return [4, 2, "1.23499999999999999999999999995", 1.23m];
        yield return [2, 1, "9.94999999999999999999999999999", 9.9m];
        yield return [4, 2, "-1.23499999999999999999999999995", -1.23m];
        yield return [2, 1, "-9.94999999999999999999999999999", -9.9m];
        yield return [4, 2, "1.23500000000000000000000000000", 1.24m];
        yield return [4, 2, "-1.23500000000000000000000000000", -1.24m];
        yield return [4, 2, " \t+1.23499999999999999999999999995 \r\n", 1.23m];
        yield return [4, 2, " \t-1.23500000000000000000000000000 \r\n", -1.24m];
        yield return [4, 2, "0001.23000000000000000000000000000", 1.23m];
        yield return [28, 28, "0.00000000000000000000000000005", 0.0000000000000000000000000001m];
        yield return [4, 2, "-0.00000000000000000000000000000000001", 0m];
    }

    /// <summary>Every text conversion path and domain check observes the same single rounding of the original digits.</summary>
    /// <param name="precision">The target precision.</param>
    /// <param name="scale">The target scale.</param>
    /// <param name="text">The full-precision numeric text.</param>
    /// <param name="expected">The value after rounding once to the target scale.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: full-precision text rounds once on every plain and domain path")]
    [MemberData(nameof(NumericTextAssignments))]
    public async Task Write_FullPrecisionNumericText_ShouldRoundOnceAcrossEveryPath(int precision, int scale, string text, decimal expected)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("numeric-text-column-bounds");
        builder.Types.Add(SqlType.CreateDomain("rounded_amount", SqlType.Decimal(precision, scale),
            SqlScalarFunction.Create("is_rounded_amount", (decimal value) => value == expected, SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string type = $"NUMERIC({precision}, {scale})";
        string literal = $"'{text}'";
        await ExecuteAsync(session, $"CREATE TABLE bounded_values (id INT, v {type} DEFAULT {literal}, n rounded_amount DEFAULT {literal})");
        await ExecuteAsync(session, "CREATE TABLE source_values (v TEXT)");
        await ExecuteAsync(session, $"INSERT INTO source_values VALUES ({literal})");

        // Act
        await ExecuteAsync(session, $"INSERT INTO bounded_values VALUES (1, {literal}, {literal})");
        (await session.ExecuteAsync("INSERT INTO bounded_values VALUES (2, @value, @value)",
            new Dictionary<string, object?> { ["value"] = text }, CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
        await ExecuteAsync(session, "INSERT INTO bounded_values SELECT 3, v, v FROM source_values");
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (4, NULL, NULL)");
        await ExecuteAsync(session, $"UPDATE bounded_values SET v = {literal}, n = {literal} WHERE id = 4");
        await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (5)");
        await ExecuteAsync(session, $"ALTER TABLE bounded_values ADD COLUMN extra {type} DEFAULT {literal}");
        await ExecuteAsync(session, $"ALTER TABLE bounded_values ADD COLUMN domain_extra rounded_amount DEFAULT {literal}");
        var cast = await RowsAsync(session, $"SELECT CAST({literal} AS {type}), CAST({literal} AS rounded_amount) FROM bounded_values WHERE id = 1");

        // Assert: each row identifies a separate write path; domain checks already proved they saw the rounded value.
        var rows = await RowsAsync(session, "SELECT id, v, n, extra, domain_extra FROM bounded_values ORDER BY id");
        rows.Count.ShouldBe(5);
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].ShouldBe(new object?[] { index + 1, expected, expected, expected, expected });
        }
        cast.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, expected });
    }

    /// <summary>Numeric errors cover rounding overflow, inexact unscaled text and results beyond Decimal's scale.</summary>
    /// <returns>The path, target type and rejected full-precision text.</returns>
    public static IEnumerable<object[]> RejectedNumericTextAssignments()
    {
        foreach (string path in NumericTextPaths())
        {
            foreach (string type in new[] { "NUMERIC(3, 2)", "rounded_amount" })
            {
                yield return [path, type, "9.995"];
                yield return [path, type, "-9.995"];
            }
            yield return [path, "NUMERIC", "1.23499999999999999999999999995"];
            yield return [path, "unscaled_amount", "1.23499999999999999999999999995"];
            yield return [path, "exact_amount", "0.00000000000000000000000000001"];
        }
    }

    /// <summary>Text is rejected consistently only when the rounded result overflows or unscaled text is inexact.</summary>
    /// <param name="path">The text conversion path.</param>
    /// <param name="type">The numeric type or domain.</param>
    /// <param name="text">The rejected text.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: numeric text overflow has one code on every conversion path")]
    [MemberData(nameof(RejectedNumericTextAssignments))]
    public async Task Write_UnrepresentableNumericText_ShouldRejectWithNumericCode(string path, string type, string text)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("numeric-text-overflow");
        builder.Types.Add(SqlType.CreateDomain("rounded_amount", SqlType.Decimal(3, 2)));
        builder.Types.Add(SqlType.CreateDomain("unscaled_amount", SqlType.Numeric));
        builder.Types.Add(SqlType.CreateDomain("exact_amount", SqlType.Numeric));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string statement = await PrepareNumericTextAsync(session, path, type, text);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (path == "cast") { await RowsAsync(session, statement); }
            else if (path == "string-parameter")
            {
                await session.ExecuteAsync(statement, new Dictionary<string, object?> { ["value"] = text }, CancellationToken.None);
            }
            else { await ExecuteAsync(session, statement); }
        });

        // Assert
        error.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
        error.ShouldStartWithMessage(numericOverflow + ":", Case.Sensitive);
        if (path == "default") { database.Catalog.TryGetTable("dbo", "bounded_values", out _).ShouldBeFalse(); }
        else
        {
            (await RowsAsync(session, "SELECT * FROM bounded_values")).ShouldHaveSingleItem()
                .ShouldBe(path == "add-column" ? new object?[] { 1 } : new object?[] { 1, null });
        }
    }

    /// <summary>Quoted exponent notation keeps CAST's fixed-point text grammar at every assignment boundary.</summary>
    /// <param name="path">The text conversion path.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: numeric text exponent syntax agrees with CAST")]
    [InlineData("insert-values")]
    [InlineData("string-parameter")]
    [InlineData("insert-select")]
    [InlineData("update")]
    [InlineData("cast")]
    [InlineData("default")]
    [InlineData("add-column")]
    public async Task Write_ExponentNumericText_ShouldRejectAsCastDoes(string path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("numeric-text-syntax", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string statement = await PrepareNumericTextAsync(session, path, "NUMERIC(4, 0)", "1.5e2");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (path == "cast") { await RowsAsync(session, statement); }
            else if (path == "string-parameter")
            {
                await session.ExecuteAsync(statement, new Dictionary<string, object?> { ["value"] = "1.5e2" }, CancellationToken.None);
            }
            else { await ExecuteAsync(session, statement); }
        });

        // Assert
        error.GetBaseException().ShouldBeOfType<FormatException>();
    }

    /// <summary>A numeric DEFAULT literal still accepts SQL exponent notation while quoted text keeps CAST's grammar.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: unquoted numeric DEFAULT retains exponent notation")]
    public async Task Default_ExponentNumericLiteral_ShouldRoundAndBackfill()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("numeric-literal-default", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v NUMERIC(4, 0) DEFAULT 1.5e2)");
        await ExecuteAsync(session, "INSERT INTO bounded_values (id) VALUES (1)");
        await ExecuteAsync(session, "ALTER TABLE bounded_values ADD COLUMN extra NUMERIC(4, 0) DEFAULT 1.5e2");

        // Assert
        (await RowsAsync(session, "SELECT v, extra FROM bounded_values")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 150m, 150m });
    }

    /// <summary>The maximum declared precision applies after scale-zero rounding, including symmetric negative values.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: numeric text rounds at the maximum declared precision")]
    public async Task Cast_MaximumPrecisionText_ShouldRoundBeforeCheckingBounds()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("decimal-boundary-text");
        builder.Types.Add(SqlType.CreateDomain("whole_amount", SqlType.Decimal(28, 0)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v whole_amount)");
        const decimal maximum = 9999999999999999999999999999m;
        const string fits = "9999999999999999999999999999.49999999999999999999999999999";
        const string overflows = "9999999999999999999999999999.5";

        // Act
        await ExecuteAsync(session, $"INSERT INTO bounded_values VALUES (1, '{fits}'), (2, '-{fits}')");
        var cast = await RowsAsync(session, $"SELECT CAST('{fits}' AS whole_amount), CAST('-{fits}' AS whole_amount) FROM bounded_values WHERE id = 1");
        var castError = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            $"SELECT CAST('{overflows}' AS whole_amount) FROM bounded_values WHERE id = 1"));
        var assignmentError = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            $"UPDATE bounded_values SET v = '-{overflows}' WHERE id = 2"));

        // Assert
        cast.ShouldHaveSingleItem().ShouldBe(new object?[] { maximum, -maximum });
        (await RowsAsync(session, "SELECT v FROM bounded_values ORDER BY id")).Select(row => row[0])
            .ShouldBe(new object?[] { maximum, -maximum });
        castError.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
        assignmentError.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
    }

    /// <summary>
    /// Preserves converter regression coverage at Decimal's coefficient and scale limits. These
    /// primitive rounding cases no longer require admitting invalid precision-29 declarations.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: decimal text conversion retains exact coefficient and scale limits")]
    public void ParseExactDecimal_BoundaryRounding_ShouldRequireExactRepresentation()
    {
        // Arrange
        const string fits = "79228162514264337593543950335.49999999999999999999999999999";
        const string overflows = "79228162514264337593543950335.5";
        const string carry = "0.000000000000000000000000000095";
        const string inexact = "0.000000000000000000000000000094";
        const decimal tiny = 0.0000000000000000000000000001m;

        // Act / Assert
        SqlCastConverter.ParseExactDecimal(fits, targetScale: 0).ShouldBe(decimal.MaxValue);
        SqlCastConverter.ParseExactDecimal("-" + fits, targetScale: 0).ShouldBe(decimal.MinValue);
        Should.Throw<OverflowException>(() => SqlCastConverter.ParseExactDecimal(overflows, targetScale: 0));
        Should.Throw<OverflowException>(() => SqlCastConverter.ParseExactDecimal("-" + overflows, targetScale: 0));
        SqlCastConverter.ParseExactDecimal(carry, targetScale: 29).ShouldBe(tiny);
        SqlCastConverter.ParseExactDecimal("-" + carry, targetScale: 29).ShouldBe(-tiny);
        Should.Throw<OverflowException>(() => SqlCastConverter.ParseExactDecimal(inexact, targetScale: 29));
        Should.Throw<OverflowException>(() => SqlCastConverter.ParseExactDecimal("-" + inexact, targetScale: 29));
    }

    /// <summary>The maximum declared scale rounds fractional carry and rejects carry beyond precision.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: scale 28 text rounds carry and rejects precision overflow")]
    public async Task Cast_MaximumScaleNumericText_ShouldRoundCarryAndRejectOverflow()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("wide-scale-numeric-text");
        builder.Types.Add(SqlType.CreateDomain("tiny_amount", SqlType.Decimal(28, 28)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT, v tiny_amount)");
        const string carry = "0.000000000000000000000000000095";
        const string overflow = "0.99999999999999999999999999995";
        const decimal expected = 0.0000000000000000000000000001m;

        // Act
        await ExecuteAsync(session, $"INSERT INTO bounded_values VALUES (1, '{carry}'), (2, '-{carry}')");
        var cast = await RowsAsync(session, $"SELECT CAST('{carry}' AS tiny_amount), CAST('-{carry}' AS tiny_amount) FROM bounded_values WHERE id = 1");
        var castError = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            $"SELECT CAST('{overflow}' AS tiny_amount) FROM bounded_values WHERE id = 1"));
        var assignmentError = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            $"UPDATE bounded_values SET v = '-{overflow}' WHERE id = 2"));

        // Assert
        cast.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, -expected });
        (await RowsAsync(session, "SELECT v FROM bounded_values ORDER BY id")).Select(row => row[0])
            .ShouldBe(new object?[] { expected, -expected });
        castError.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
        assignmentError.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
    }

    /// <summary>An application's text-to-domain cast remains selected and its Decimal result receives normal bounds.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Column bounds: application numeric domain casts retain delegate selection and modifiers")]
    public async Task Cast_ApplicationNumericDomainConversion_ShouldInvokeDelegateThenApplyModifiers()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("application-numeric-domain-cast");
        var amount = SqlType.CreateDomain("invoice_amount", SqlType.Decimal(4, 2),
            SqlScalarFunction.Create("is_rounded_invoice", static (decimal value) => value == 1.24m, SqlFunctionVolatility.Immutable));
        builder.Types.Add(amount).AddCast(SqlCast.Create<string, decimal>(SqlType.Text, amount,
            static text => text == "invoice" ? 1.235m : 99.995m, SqlCastContext.Explicit));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("bounds");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE bounded_values (id INT)");
        await ExecuteAsync(session, "INSERT INTO bounded_values VALUES (1)");

        // Act
        var converted = await RowsAsync(session, "SELECT CAST('invoice' AS invoice_amount) FROM bounded_values");
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            "SELECT CAST('overflow' AS invoice_amount) FROM bounded_values"));

        // Assert
        converted.ShouldHaveSingleItem().ShouldBe(new object?[] { 1.24m });
        error.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe(numericOverflow);
    }

    private static string[] NumericTextPaths() => ["insert-values", "string-parameter", "insert-select", "update", "cast", "default", "add-column"];

    private static async Task<string> PrepareNumericTextAsync(SqlDatabaseSession session, string path, string type, string text)
    {
        string literal = $"'{text}'";
        if (path == "default") { return $"CREATE TABLE bounded_values (id INT, v {type} DEFAULT {literal})"; }
        await ExecuteAsync(session, path == "add-column" ? "CREATE TABLE bounded_values (id INT)" : $"CREATE TABLE bounded_values (id INT, v {type})");
        await ExecuteAsync(session, path == "add-column" ? "INSERT INTO bounded_values VALUES (1)" : "INSERT INTO bounded_values VALUES (1, NULL)");
        if (path == "add-column") { return $"ALTER TABLE bounded_values ADD COLUMN v {type} DEFAULT {literal}"; }
        if (path == "cast") { return $"SELECT CAST({literal} AS {type}) FROM bounded_values"; }
        if (path == "string-parameter") { return "INSERT INTO bounded_values VALUES (2, @value)"; }
        if (path == "insert-select")
        {
            await ExecuteAsync(session, "CREATE TABLE source_values (v TEXT)");
            await ExecuteAsync(session, $"INSERT INTO source_values VALUES ({literal})");
            return "INSERT INTO bounded_values SELECT 2, v FROM source_values";
        }
        return path == "update" ? $"UPDATE bounded_values SET v = {literal} WHERE id = 1"
            : $"INSERT INTO bounded_values VALUES (2, {literal})";
    }

    private static string[] Paths() => ["insert-values", "insert-select", "update", "add-column"];

    private static async Task<string> PrepareAsync(Func<string, Task> execute, string path, string type, string literal, bool seedExisting)
    {
        await execute(path == "add-column" ? "CREATE TABLE bounded_values (id INT)" : $"CREATE TABLE bounded_values (id INT, v {type})");
        if (path == "add-column")
        {
            await execute("INSERT INTO bounded_values VALUES (1)");
            // The current DEFAULT grammar accepts negative numerics as quoted literals.
            string defaultLiteral = literal.StartsWith("-", StringComparison.Ordinal) ? $"'{literal}'" : literal;
            return $"ALTER TABLE bounded_values ADD COLUMN v {type} DEFAULT {defaultLiteral}";
        }

        if (seedExisting || path == "update")
        {
            await execute("INSERT INTO bounded_values VALUES (1, NULL)");
        }

        if (path == "insert-select")
        {
            string sourceType = type.StartsWith("VARCHAR", StringComparison.Ordinal) || type.StartsWith("CHAR", StringComparison.Ordinal)
                ? "TEXT" : "NUMERIC";
            await execute($"CREATE TABLE source_values (v {sourceType})");
            await execute($"INSERT INTO source_values VALUES ({literal})");
            return "INSERT INTO bounded_values SELECT 2, v FROM source_values";
        }

        return path == "update" ? $"UPDATE bounded_values SET v = {literal} WHERE id = 1"
            : $"INSERT INTO bounded_values VALUES (2, {literal})";
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
        => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token());

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static SqlClient CreateClient(ServerTestHarness harness)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = ServerTestHarness.DatabaseName,
                Principal = "column-bounds-tests",
                EndPoint = harness.Listener.EndPoint,
            },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
}
