using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies grouping, aggregate values and metadata through the live SQL session pipeline.</summary>
public sealed class SqlAggregateExecutionTests
{
    private const string CreateMetrics =
        "CREATE TABLE metrics (id INT, category TEXT, region TEXT, amount INT, empty_amount INT);";

    private const string InsertMetrics =
        "INSERT INTO metrics VALUES " +
        "(1, 'red', 'west', 4, NULL), (2, 'red', 'west', 10, NULL), (3, 'red', 'east', NULL, NULL), " +
        "(4, 'blue', 'west', 7, NULL), (5, 'blue', 'east', -3, NULL), (6, 'blue', 'east', NULL, NULL), " +
        "(7, NULL, 'north', 11, NULL), (8, NULL, 'north', NULL, NULL), (9, 'empty', 'west', NULL, NULL);";

    /// <summary>Each aggregate has a distinct known answer, and integer AVG retains its fractional part.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: ungrouped values skip nulls and expose exact result types")]
    public async Task Aggregates_MixedNullInput_ShouldReturnKnownValuesAndTypes()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT COUNT(*) AS rows_seen, COUNT(amount) AS values_seen, SUM(amount) AS total, " +
            "AVG(amount) AS mean, MIN(amount) AS smallest, MAX(amount) AS largest FROM metrics;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["rows_seen", "values_seen", "total", "mean", "smallest", "largest"]);
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
        result.Columns.Take(2).ShouldAllBe(column => !column.IsNullable);
        result.Columns.Skip(2).ShouldAllBe(column => column.IsNullable);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 9L, 5L, 29m, 5.8m, -3, 11 });
    }

    /// <summary>Null keys share a group, and an all-null group has zero COUNT(expr) and null numeric aggregates.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - GROUP BY: every aggregate executes independently per group")]
    public async Task GroupBy_SingleColumn_ShouldReturnAllAggregatesPerGroup()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT category, COUNT(*), COUNT(amount), SUM(amount), AVG(amount), MIN(amount), MAX(amount) " +
            "FROM metrics GROUP BY category ORDER BY category;");

        // Assert
        CheckRows(rows,
        [
            [null, 2L, 1L, 11m, 11m, 11, 11],
            ["blue", 3L, 2L, 4m, 2m, -3, 7],
            ["empty", 1L, 0L, null, null, null, null],
            ["red", 3L, 2L, 14m, 7m, 4, 10],
        ]);
    }

    /// <summary>COUNT(*) includes all-null rows while every aggregate over the null column follows its stated rule.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: all-null column returns zero count and null extrema sum and average")]
    public async Task Aggregates_AllNullColumn_ShouldKeepRowCountAndReturnNullValues()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT COUNT(*), COUNT(empty_amount), SUM(empty_amount), AVG(empty_amount), MIN(empty_amount), MAX(empty_amount) FROM metrics;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 9L, 0L, null, null, null, null });
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
        result.Columns.Skip(2).ShouldAllBe(column => column.IsNullable);
    }

    /// <summary>Empty input has one implicit aggregate group and no explicit groups, whether empty by storage or WHERE.</summary>
    /// <param name="filtered">Whether WHERE empties a populated table.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: empty input distinguishes grouped and ungrouped results")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_EmptyInput_ShouldReturnOneUngroupedRowAndNoGroupedRows(bool filtered)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);
        if (filtered) { await ExecuteAsync(session, InsertMetrics); }
        string where = filtered ? " WHERE id < 0" : string.Empty;

        // Act / Assert
        await using var result = (await ExecuteAsync(session,
            $"SELECT COUNT(*), COUNT(amount), SUM(amount), AVG(amount), MIN(amount), MAX(amount) FROM metrics{where};"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 0L, 0L, null, null, null, null });
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
        (await RowsAsync(session,
            $"SELECT category, COUNT(*), COUNT(amount), SUM(amount), AVG(amount), MIN(amount), MAX(amount) FROM metrics{where} GROUP BY category;"))
            .ShouldBeEmpty();
        (await RowsAsync(session, $"SELECT COUNT(*) FROM metrics{where} HAVING COUNT(*) = 0;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 0L });
        (await RowsAsync(session, $"SELECT COUNT(*) FROM metrics{where} HAVING COUNT(*) > 0;"))
            .ShouldBeEmpty();
    }

    /// <summary>Every component participates in grouping equality rather than only the first expression.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - GROUP BY: composite keys preserve every distinct combination")]
    public async Task GroupBy_MultipleColumns_ShouldUseEveryKeyComponent()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT category, region, COUNT(*), SUM(amount) FROM metrics GROUP BY category, region ORDER BY category, region;");

        // Assert
        CheckRows(rows,
        [
            [null, "north", 2L, 11m], ["blue", "east", 2L, -3m], ["blue", "west", 1L, 7m],
            ["empty", "west", 1L, null], ["red", "east", 1L, null], ["red", "west", 2L, 14m],
        ]);
    }

    /// <summary>Computed grouping keys retain SQL null values and can be reused within projected scalar expressions.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - GROUP BY: computed expressions bind by meaning and retain null keys")]
    public async Task GroupBy_ComputedExpressions_ShouldEvaluateKeysAndDerivedProjection()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act / Assert
        CheckRows(await RowsAsync(session,
            "SELECT amount > 5, COUNT(*), SUM(amount) FROM metrics GROUP BY amount > 5 ORDER BY amount > 5;"),
            [[null, 4L, null], [false, 2L, 1m], [true, 3L, 28m]]);
        CheckRows(await RowsAsync(session,
            "SELECT (m.amount + 1) * 2, COUNT(*) FROM metrics m WHERE amount IS NOT NULL GROUP BY amount + 1 ORDER BY m.amount + 1;"),
            [[-4L, 1L], [10L, 1L], [16L, 1L], [22L, 1L], [24L, 1L]]);
        CheckRows(await RowsAsync(session,
            "SELECT UPPER(m.category), COUNT(*) FROM metrics m GROUP BY CATEGORY ORDER BY category;"),
            [[null, 2L], ["BLUE", 3L], ["EMPTY", 1L], ["RED", 3L]]);
    }

    /// <summary>The negative blue row is removed by WHERE before aggregation; HAVING then rejects whole groups.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - HAVING: WHERE filters rows before HAVING filters completed groups")]
    public async Task Having_WithWhere_ShouldApplyRowFilterBeforeGroupFilter()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act / Assert: without WHERE the blue sum is 4 and fails HAVING; with WHERE it is 7 and survives.
        CheckRows(await RowsAsync(session,
            "SELECT category, SUM(amount) FROM metrics GROUP BY category HAVING SUM(amount) > 5 ORDER BY category;"),
            [[null, 11m], ["red", 14m]]);
        CheckRows(await RowsAsync(session,
            "SELECT category, SUM(amount) FROM metrics WHERE amount > 0 GROUP BY category HAVING SUM(amount) > 5 ORDER BY category;"),
            [[null, 11m], ["blue", 7m], ["red", 14m]]);
        // All three groups have positive rows, but only red has at least two rows after WHERE.
        CheckRows(await RowsAsync(session,
            "SELECT category, COUNT(*), SUM(amount) FROM metrics WHERE amount > 0 GROUP BY category HAVING COUNT(*) > 1;"),
            [["red", 2L, 14m]]);
        // An unknown HAVING predicate rejects the all-null group rather than treating NULL as true.
        (await RowsAsync(session,
            "SELECT category FROM metrics GROUP BY category HAVING AVG(amount) > 100;"))
            .ShouldBeEmpty();
        CheckRows(await RowsAsync(session,
            "SELECT category FROM metrics GROUP BY category HAVING SUM(amount) IS NULL;"), [["empty"]]);
    }

    /// <summary>Grouping consumes joined pairs and preserves WHERE, HAVING, ordering, and windowing for either access path.</summary>
    /// <param name="indexed">Whether the join can use an equality index.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: INNER JOIN composes with grouping filtering and pagination")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupBy_InnerJoin_ShouldAggregateJoinedPairsAndApplyRemainingClauses(bool indexed)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE categories (name TEXT, label TEXT);");
        await ExecuteAsync(session, "INSERT INTO categories VALUES ('red', 'warm'), ('blue', 'cold'), ('empty', 'none'), ('missing', 'unmatched');");
        if (indexed) { await ExecuteAsync(session, "CREATE INDEX ix_metrics_category ON metrics(category);"); }

        // Act / Assert
        CheckRows(await RowsAsync(session,
            "SELECT c.label, COUNT(*), COUNT(m.amount), SUM(m.amount), AVG(m.amount), MIN(m.amount), MAX(m.amount) " +
            "FROM categories c INNER JOIN metrics m ON c.name = m.category GROUP BY c.label ORDER BY c.label;"),
            [["cold", 3L, 2L, 4m, 2m, -3, 7], ["none", 1L, 0L, null, null, null, null], ["warm", 3L, 2L, 14m, 7m, 4, 10]]);
        CheckRows(await RowsAsync(session,
            "SELECT c.label, COUNT(*), SUM(m.amount), AVG(m.amount), MIN(m.amount), MAX(m.amount) " +
            "FROM categories c INNER JOIN metrics m ON c.name = m.category WHERE m.amount IS NOT NULL " +
            "GROUP BY c.label HAVING SUM(m.amount) > 3 ORDER BY SUM(m.amount) DESC LIMIT 1 OFFSET 1;"),
            [["cold", 2L, 4m, 2m, -3, 7]]);
    }

    /// <summary>ORDER BY may consume an aggregate absent from the SELECT list, and pagination applies after sorting groups.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - GROUP BY: hidden aggregate ordering precedes LIMIT and OFFSET")]
    public async Task GroupBy_OrderedPage_ShouldSortCompletedGroupsBeforeWindowing()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT category FROM metrics GROUP BY category ORDER BY SUM(amount) DESC LIMIT 2 OFFSET 1;");

        // Assert
        CheckRows(rows, [[null], ["blue"]]);
    }

    /// <summary>An ungrouped column cannot be read from a representative row, even when there are no rows.</summary>
    /// <param name="statement">A projection or later clause with an ungrouped source column.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: ungrouped columns fail clearly before input is read")]
    [InlineData("SELECT category, amount FROM metrics GROUP BY category;")]
    [InlineData("SELECT COUNT(*), amount FROM metrics;")]
    [InlineData("SELECT category, SUM(amount) FROM metrics GROUP BY category ORDER BY amount;")]
    [InlineData("SELECT category, SUM(amount) FROM metrics GROUP BY category HAVING amount > 1;")]
    [InlineData("SELECT amount FROM metrics GROUP BY amount + 1;")]
    public async Task Aggregates_UngroupedColumn_ShouldRejectBeforeReadingRows(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
        error.Message.ShouldContain("amount", Case.Sensitive);
        error.Message.ShouldContain("GROUP BY", Case.Sensitive);
    }

    /// <summary>Aggregate inputs cannot contain aggregates, and row predicates cannot depend on completed groups.</summary>
    /// <param name="statement">A query with an aggregate in an invalid semantic position.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: invalid aggregate placement fails before reading rows")]
    [InlineData("SELECT COUNT(*) FROM metrics WHERE SUM(amount) > 0;")]
    [InlineData("SELECT COUNT(*) FROM metrics GROUP BY SUM(amount);")]
    [InlineData("SELECT SUM(COUNT(amount)) FROM metrics;")]
    [InlineData("SELECT COUNT(*) FROM metrics m JOIN metrics n ON SUM(m.amount) = n.amount;")]
    public async Task Aggregates_InvalidPlacement_ShouldReject(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
        error.Message.ShouldContain("aggregate");
    }

    /// <summary>
    /// UPDATE and DELETE act on one row at a time, so an aggregate in an assignment or a filter has
    /// no group to summarize. It fails while planning, as PostgreSQL rejects one in UPDATE and WHERE
    /// (SQLSTATE 42803): over an empty table as over a populated one, and without changing a row.
    /// It used to succeed over an empty table and fail per row over a populated one.
    /// </summary>
    /// <param name="statement">An UPDATE or DELETE with an aggregate outside any subquery.</param>
    /// <param name="message">The planner's message.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: UPDATE and DELETE reject aggregates before reading rows")]
    [InlineData("UPDATE metrics SET amount = SUM(amount);", "Aggregate functions are not allowed in UPDATE SET.")]
    [InlineData("UPDATE metrics SET amount = 1, region = UPPER(MAX(region));", "Aggregate functions are not allowed in UPDATE SET.")]
    [InlineData("UPDATE metrics SET amount = 0 WHERE COUNT(*) > 1;", "Aggregate functions are not allowed in WHERE.")]
    [InlineData("DELETE FROM metrics WHERE COUNT(*) > 1;", "Aggregate functions are not allowed in WHERE.")]
    [InlineData("DELETE FROM metrics WHERE amount > 0 AND min(amount) < 0;", "Aggregate functions are not allowed in WHERE.")]
    public async Task Aggregates_InUpdateOrDelete_ShouldRejectBeforeReadingRows(string statement, string message)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = CreateEngine();
            var database = await engine.CreateDatabaseAsync("aggregate");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            if (withRows)
            {
                await SeedAsync(session);
            }
            else
            {
                (await ExecuteAsync(session, CreateMetrics)).Status.ShouldBe(DatabaseResultStatus.Success);
            }
            var before = await SnapshotAsync(session);

            // Act
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));

            // Assert
            error.ShouldHaveMessage(message, $"withRows: {withRows}");
            (await SnapshotAsync(session)).ShouldBe(before, $"withRows: {withRows}");
        }

        static async Task<string[]> SnapshotAsync(SqlDatabaseSession session)
            => (await RowsAsync(session, "SELECT id, category, region, amount FROM metrics ORDER BY id;"))
                .Select(row => string.Join(",", row)).ToArray();
    }

    /// <summary>SUM and AVG reject text explicitly rather than inferring a numeric result from non-null rows.</summary>
    /// <param name="aggregate">The numeric aggregate to validate.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: SUM and AVG require numeric arguments")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    public async Task Aggregates_TextArgument_ShouldRejectNumericAggregates(string aggregate)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {aggregate}(category) FROM metrics;"));
        error.ShouldContainMessage(aggregate, Case.Sensitive);
        error.Message.ShouldContain("numeric");
    }

    /// <summary>Repeating averages use Decimal division, while finite fractions and numeric input types remain exact.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - AVG: fractional and repeating results use Decimal precision")]
    public async Task Avg_NumericInputs_ShouldReturnDecimalWithDocumentedDivisionRounding()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE numbers (integer_value INT, decimal_value DECIMAL(18, 4), float_value FLOAT);");
        await ExecuteAsync(session, "INSERT INTO numbers VALUES (0, 0.125, 2.5), (0, 0.5, 7.25), (1, NULL, NULL), (NULL, NULL, NULL);");

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT AVG(integer_value), SUM(decimal_value), AVG(decimal_value), SUM(float_value), AVG(float_value), MIN(float_value), MAX(float_value) FROM numbers;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(
            new object?[] { 0.3333333333333333333333333333m, 0.625m, 0.3125m, 9.75m, 4.875m, 2.5d, 7.25d });
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Float64, DatabaseType.Float64]);
    }

    /// <summary>At Decimal's fractional precision limit, AVG resolves both directions of a midpoint toward an even digit.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - AVG: decimal precision midpoints round to even")]
    public async Task Avg_DecimalMidpoints_ShouldRoundToEvenAtScaleLimit()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE midpoints (category TEXT, amount DECIMAL(28, 28));");
        await ExecuteAsync(session, "INSERT INTO midpoints VALUES " +
            "('down', 0.0000000000000000000000000005), ('down', 0), " +
            "('up', 0.0000000000000000000000000007), ('up', 0);");

        // Act
        var rows = await RowsAsync(session, "SELECT category, AVG(amount) FROM midpoints GROUP BY category ORDER BY category;");

        // Assert
        CheckRows(rows, [["down", 0.0000000000000000000000000002m], ["up", 0.0000000000000000000000000004m]]);
    }

    /// <summary>Finite floating-point keys and extrema retain their range while decimal aggregates report overflow explicitly.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: large finite floats group and preserve extrema beyond Decimal range")]
    public async Task Aggregates_LargeFiniteFloat_ShouldGroupAndReturnExtremaButRejectDecimalOverflow()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE large_floats (amount FLOAT);");
        await ExecuteAsync(session, "INSERT INTO large_floats VALUES (@first), (@second), (@first);",
            new Dictionary<string, object?> { ["first"] = 1e100, ["second"] = 2e100 });

        // Act / Assert
        CheckRows(await RowsAsync(session, "SELECT amount, COUNT(*) FROM large_floats GROUP BY amount ORDER BY amount;"),
            [[1e100, 2L], [2e100, 1L]]);
        await using var result = (await ExecuteAsync(session, "SELECT MIN(amount), MAX(amount) FROM large_floats;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Float64, DatabaseType.Float64]);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1e100, 2e100 });
        foreach (string aggregate in new[] { "SUM", "AVG" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {aggregate}(amount) FROM large_floats;"));
            error.ShouldContainMessage(aggregate, Case.Sensitive);
            error.Message.ShouldContain("overflow");
        }
    }

    /// <summary>Parameter operands retain their declared runtime types even when the implicit group has no input.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: parameter extrema retain metadata for populated and empty input")]
    public async Task Aggregates_ParameterOperands_ShouldPreserveValueTypesAndEmptyMetadata()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        var parameters = new Dictionary<string, object?> { ["number"] = 27, ["text"] = "typed" };
        const string projection = "SELECT MIN(@number), MAX(@number), MIN(@text), MAX(@text) FROM metrics";

        // Act / Assert
        await using var populated = (await ExecuteAsync(session, projection + ";", parameters))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        populated.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.String]);
        (await ReadRowsAsync(populated)).ShouldHaveSingleItem().ShouldBe(new object?[] { 27, 27, "typed", "typed" });
        await using var empty = (await ExecuteAsync(session, projection + " WHERE id < 0;", parameters))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        empty.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.String]);
        (await ReadRowsAsync(empty)).ShouldHaveSingleItem().ShouldBe(new object?[] { null, null, null, null });
    }

    /// <summary>COALESCE metadata considers non-null alternatives and converts integer defaults to the common result type.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: null-first COALESCE preserves aggregate metadata and fallback values")]
    public async Task Aggregates_NullFirstCoalesce_ShouldInferCommonTypeAcrossAllArguments()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT category, COALESCE(NULL, SUM(amount), 0), COALESCE(NULL, MIN(amount), 0) " +
            "FROM metrics GROUP BY category ORDER BY category;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.Decimal, DatabaseType.Int64]);
        CheckRows(await ReadRowsAsync(result), [[null, 11m, 11L], ["blue", 4m, -3L], ["empty", 0m, 0L], ["red", 14m, 4L]]);
    }

    /// <summary>Current binary text grouping keeps distinct case variants and MIN/MAX preserve string values.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - GROUP BY: binary string equality keeps case variants distinct")]
    public async Task GroupBy_TextKeys_ShouldUseBinaryEqualityAndStringExtrema()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('Alice'), ('alice'), ('Alice'), (NULL), (NULL);");

        // Act / Assert
        CheckRows(await RowsAsync(session, "SELECT name, COUNT(*) FROM names GROUP BY name ORDER BY name;"),
            [[null, 2L], ["Alice", 2L], ["alice", 1L]]);
        await using var result = (await ExecuteAsync(session, "SELECT MIN(name), MAX(name), COUNT(name) FROM names;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", "alice", 3L });
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String, DatabaseType.Int64]);
    }

    /// <summary>Aggregates accept row expressions, parameters, and CASE, and their results compose as scalar expressions.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: expression arguments and aggregate result expressions compose")]
    public async Task Aggregates_ExpressionArguments_ShouldEvaluatePerRowThenComposeResults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT COUNT(amount + 1), SUM(amount * @factor), SUM(CASE WHEN amount > 0 THEN amount ELSE NULL END), " +
            "SUM(amount) + COUNT(amount), AVG(CAST(amount AS DECIMAL(18, 4))) FROM metrics;",
            new Dictionary<string, object?> { ["factor"] = 2 });

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 5L, 58m, 32m, 34m, 5.8m });
    }

    /// <summary>Grouping is a distinct plan node over the join, with group ordering and pagination retained above it.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: grouping wraps a JOIN plan rather than changing scan execution")]
    public async Task Plan_GroupedJoin_ShouldUseDistinctGroupingNode()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);
        await ExecuteAsync(session, "CREATE TABLE categories (name TEXT, label TEXT);");
        var request = SqlQueryRequest.FromSql(
            "SELECT c.label, SUM(m.amount) FROM metrics m JOIN categories c ON m.category = c.name " +
            "WHERE m.amount > 0 GROUP BY c.label HAVING SUM(m.amount) > 5 ORDER BY SUM(m.amount) DESC LIMIT 2 OFFSET 1;");

        // Act
        var plan = new SqlPlanner(database.Catalog, request.Parameters).Plan(request.Statement.SqlExpression);

        // Assert
        var group = plan.ShouldBeOfType<SqlGroupPlan>();
        var join = group.Input.ShouldBeOfType<SqlJoinPlan>();
        join.Where.ShouldNotBeNull();
        join.OrderBy.ShouldBeEmpty();
        join.Limit.ShouldBeNull();
        join.Offset.ShouldBeNull();
        group.Keys.Count.ShouldBe(1);
        group.Having.ShouldNotBeNull();
        group.OrderBy.ShouldHaveSingleItem();
        group.Limit.ShouldBe(2);
        group.Offset.ShouldBe(1);
    }

    /// <summary>Virtual catalog inputs share grouping, empty-input, and aggregate semantics with stored tables.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: system views support grouped catalog queries")]
    public async Task GroupBy_SystemView_ShouldUseOrdinaryAggregateSemantics()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);
        await ExecuteAsync(session, "CREATE TABLE compact (id INT, name TEXT);");

        // Act / Assert
        CheckRows(await RowsAsync(session,
            "SELECT TABLE_NAME, COUNT(*), SUM(ORDINAL_POSITION), AVG(ORDINAL_POSITION), MIN(ORDINAL_POSITION), MAX(ORDINAL_POSITION) " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' GROUP BY TABLE_NAME " +
            "HAVING COUNT(*) > 1 ORDER BY SUM(ORDINAL_POSITION) DESC LIMIT 1 OFFSET 1;"),
            [["compact", 2L, 3m, 1.5m, 1L, 2L]]);
        CheckRows(await RowsAsync(session,
            "SELECT COUNT(*), SUM(ORDINAL_POSITION), AVG(ORDINAL_POSITION), MIN(ORDINAL_POSITION), MAX(ORDINAL_POSITION) " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'absent';"), [[0L, null, null, null, null]]);
    }

    /// <summary>CASE branch numeric promotion must agree with MIN/MAX metadata and returned runtime values.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: CASE input types agree with extrema metadata")]
    public async Task Aggregates_MixedCaseNumericBranches_ShouldPromoteResultMetadata()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT MIN(CASE WHEN id = 1 THEN amount ELSE amount + 1 END), " +
            "MAX(CASE WHEN id = 1 THEN amount ELSE amount + 1 END) FROM metrics;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int64, DatabaseType.Int64]);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { -2L, 12L });
    }

    /// <summary>T1 string signatures reject numeric inputs even inside aggregate arguments.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: scalar string functions reject numeric aggregate inputs")]
    public async Task Aggregates_NumericStringFunctionOperand_ShouldRejectSignature()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT MIN(UPPER(amount)), MAX(LOWER(amount)) FROM metrics;"));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE006");
    }

    /// <summary>Alternative scalar branches with incompatible types fail before an empty input can conceal the mismatch.</summary>
    /// <param name="expression">The incompatible aggregate argument.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: incompatible alternative operand types fail clearly")]
    [InlineData("CASE WHEN id = 1 THEN amount ELSE category END")]
    [InlineData("COALESCE(amount, category)")]
    public async Task Aggregates_IncompatibleAlternativeTypes_ShouldRejectDuringPlanning(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, CreateMetrics);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT MIN({expression}) FROM metrics;"));
        error.Message.ShouldContain("compatible types", Case.Sensitive);
        error.Message.ShouldContain("Int32", Case.Sensitive);
        error.Message.ShouldContain("String", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: DISTINCT tuples and ordinary calls have separate slots")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_DistinctNumericInputs_ShouldUseSqlEquality(bool grouped)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (x INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 1), (1, 1), (2, 1), (NULL, 1);");
        // Act
        var rows = await RowsAsync(session, "SELECT COUNT(x), COUNT(DISTINCT x), COUNT(ALL x), SUM(DISTINCT x), AVG(DISTINCT x), MIN(DISTINCT x), MAX(DISTINCT x), SUM(x ORDER BY g) FROM t" + (grouped ? " GROUP BY g" : "") + ";");
        // Assert
        CheckRows(rows, [[3L, 2L, 3L, 3m, 1.5m, 1, 2, 4m]]);
        CheckRows(await RowsAsync(session, "SELECT COUNT(DISTINCT a.x) FROM t a JOIN t b ON a.g = b.g" + (grouped ? " GROUP BY a.g" : "") + " HAVING SUM(DISTINCT a.x) = 3 ORDER BY COUNT(DISTINCT a.x);"), [[2L]]);
        CheckRows(await RowsAsync(session, "SELECT COUNT(DISTINCT CASE WHEN x = 1 THEN 1 ELSE 1.0 END) FROM t" + (grouped ? " GROUP BY g" : "") + ";"), [[1L]]);
        CheckRows(await RowsAsync(session, "SELECT COUNT(DISTINCT x), SUM(DISTINCT x) FROM t WHERE g < 0" + (grouped ? " GROUP BY g" : "") + ";"), grouped ? [] : [[0L, null]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - String aggregates: grouped and global ordering share nullable text behavior")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_OrderedStrings_ShouldReturnTextAndMetadata(bool grouped)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, name TEXT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, 'c', 1), (1, 'a', 1), (2, 'b', 1), (4, NULL, 1);");
        string tail = " FROM t" + (grouped ? " GROUP BY g" : "") + ";";
        // Act
        await using var result = (await ExecuteAsync(session, "SELECT STRING_AGG(name, ',' ORDER BY id), LISTAGG(name, ',') WITHIN GROUP (ORDER BY id), STRING_AGG(name, '' ORDER BY id DESC), LISTAGG(name) WITHIN GROUP (ORDER BY g, id DESC), STRING_AGG(name, NULL ORDER BY id)" + tail)).ShouldBeAssignableTo<DatabaseResultSet>();
        // Assert
        CheckRows(await ReadRowsAsync(result), [["a,b,c", "a,b,c", "cba", "cba", "abc"]]);
        result.Columns.ShouldAllBe(column => column.Type == DatabaseType.String && column.IsNullable && column.MaxLength == null);
        result.Columns.Take(2).Select(column => column.Name).ShouldBe(["string_agg", "listagg"]);
        string grouping = grouped ? " GROUP BY g" : "";
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ','), LISTAGG(name) WITHIN GROUP (ORDER BY id) FROM t WHERE id < 0" + grouping + ";"), grouped ? [] : [[null, null]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ',') FROM t WHERE name IS NULL" + grouping + ";"), [[null]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ',' ORDER BY 1)" + tail), [["c,a,b"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ',' ORDER BY (SELECT MAX(x.id) FROM t x WHERE x.name = t.name))" + tail), [["a,b,c"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, @d ORDER BY id)" + tail, new Dictionary<string, object?> { ["d"] = null }), [["abc"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(CAST(name AS CHAR(5)), ',' ORDER BY id)" + tail), [["a,b,c"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ','), STRING_AGG(name, ',' ORDER BY id), LISTAGG(name, ',') WITHIN GROUP (ORDER BY id) FROM t WHERE id = 1" + grouping + ";"), [["a", "a", "a"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ',' ORDER BY id) AS names, STRING_AGG(name, ',' ORDER BY id DESC) AS reversed FROM t" + grouping + " ORDER BY reversed DESC;"), [["a,b,c", "c,b,a"]]);
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, 'z', 2), (1, 'x', 2), (2, 'y', 2);");
        CheckRows(await RowsAsync(session, "SELECT g, STRING_AGG(name, ',' ORDER BY id) FROM t GROUP BY g ORDER BY g DESC;"), [[2, "x,y,z"], [1, "a,b,c"]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DISTINCT aggregates: declared and explicit collations and NaN use SQL equality")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_CollatedDistinct_ShouldMatchSelectDistinct(bool grouped)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (name TEXT COLLATE case_insensitive, id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES ('Ada', 1, 1), ('ada', 2, 1), ('Bob', 3, 1);");
        string tail = " FROM t" + (grouped ? " GROUP BY g" : "") + ";";
        // Act / Assert
        CheckRows(await RowsAsync(session, "SELECT COUNT(DISTINCT name), COUNT(DISTINCT name COLLATE binary), COUNT(DISTINCT @nan)" + tail, new Dictionary<string, object?> { ["nan"] = double.NaN }), [[2L, 3L, 1L]]);
        var text = (string)(await RowsAsync(session, "SELECT STRING_AGG(DISTINCT name, ',' ORDER BY name)" + tail))[0][0]!;
        text.Split(',').Length.ShouldBe(2);
        text.Split(',')[0].ShouldBeOneOf("Ada", "ada");
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(name, ',' ORDER BY name COLLATE binary DESC)" + tail), [["ada,Bob,Ada"]]);
        CheckRows(await RowsAsync(session, "SELECT STRING_AGG(DISTINCT name, ',' ORDER BY name DESC)" + tail), [["Bob,Ada"]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregate shape: invalid modifiers and delimiters fail while planning")]
    [InlineData("COUNT(DISTINCT x, id)", "COHSQLE006", "COUNT")]
    [InlineData("STRING_AGG(name)", "COHSQLE006", "delimiter")]
    [InlineData("STRING_AGG(name ORDER BY id, ',')", "COHSQLE006", "delimiter")]
    [InlineData("STRING_AGG(name, name)", "COHDBL001", "delimiter")]
    [InlineData("STRING_AGG(DISTINCT name, ',' ORDER BY id)", "COHSQLE032", "argument list")]
    [InlineData("STRING_AGG(x, ',')", "COHSQLE006", "CAST")]
    [InlineData("STRING_AGG(CAST(name AS JSON), ',')", "COHSQLE006", "CAST")]
    [InlineData("UPPER(DISTINCT name)", "COHSQLE032", "not an aggregate")]
    [InlineData("UPPER(ALL name)", "COHSQLE032", "not an aggregate")]
    [InlineData("UPPER(name ORDER BY id)", "COHSQLE032", "not an aggregate")]
    [InlineData("\"LISTAGG\"(name)", "SQL0003", "WITHIN GROUP")]
    [InlineData("\"LISTAGG\"(name ORDER BY id)", "SQL0003", "WITHIN GROUP")]
    [InlineData("STRING_AGG(name, ',' ORDER BY COUNT(*))", "COHSQLE017", "Nested")]
    public async Task Aggregates_InvalidShape_ShouldFailBeforeInput(string expression, string code, string message)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (x INT, id INT, name TEXT);");
        // Act
        if (code == "COHDBL001")
        {
            var result = await ExecuteAsync(session, $"SELECT {expression} FROM t;");
            // Assert: capability refusals use the statement-result carrier.
            result.Status.ShouldBe(DatabaseResultStatus.Error);
            result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            result.Diagnostics[0].Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {expression} FROM t;"));
            // Assert
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
            error.Message.ShouldContain(message, Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - String aggregates: length cap applies before ordered inputs finish")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Aggregates_StringLengthCap_ShouldFailWithCodedRangeError(bool distinct, bool grouped)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (name TEXT, zero INT, g INT);");
        var parameters = new Dictionary<string, object?> { ["a"] = new string('a', 524288), ["b"] = new string('b', 524288) };
        await ExecuteAsync(session, "INSERT INTO t VALUES ('a', 0, 1), ('b', 0, 1), ('c', 0, 1);");
        string value = distinct
            ? "CASE WHEN name = 'c' THEN CAST(1 / zero AS TEXT) WHEN name = 'a' THEN @a ELSE @b END"
            : "CASE WHEN name = 'a' THEN @a ELSE @b END";
        string key = distinct ? value : "CASE WHEN name = 'c' THEN 1 / zero ELSE 1 END";
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT STRING_AGG(" + (distinct ? "DISTINCT " : "") + value + ", ',' ORDER BY " + key + ") FROM t" + (grouped ? " GROUP BY g" : "") + ";", parameters));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE002");
        error.Message.ShouldContain("1048576", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: application accumulators receive DISTINCT coerced ordered inputs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_ApplicationFunction_ShouldApplyModifiersOutsideAccumulator(bool grouped)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-tests");
        builder.Functions.Add(SqlAggregateFunction.Create<string, string, string>("collect",
            seed: static () => "", step: static (state, value) => state + value, finish: static state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (s CHAR(3), id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES ('c', 1, 1), ('a', 2, 1), ('b', 3, 1), ('b', 4, 1);");
        string tail = " FROM t" + (grouped ? " GROUP BY g" : "") + ";";
        // Act / Assert
        CheckRows(await RowsAsync(session, "SELECT collect(s), collect(DISTINCT s ORDER BY s), collect(s ORDER BY id DESC)" + tail), [["cabb", "abc", "bbac"]]);
        CheckRows(await RowsAsync(session, "SELECT COUNT(DISTINCT CASE WHEN id = 1 THEN @a ELSE @b END), SUM(DISTINCT CASE WHEN id = 1 THEN @a ELSE @b END)" + tail,
            new Dictionary<string, object?> { ["a"] = 1.0000000000000002d, ["b"] = 1.0000000000000004d }), [[2L, 2m]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Scalar modifiers: persisted entry points reject aggregate-only syntax")]
    [InlineData("CREATE TABLE bad (name TEXT, CHECK (UPPER(DISTINCT name) = 'X'));")]
    [InlineData("CREATE TABLE bad (name TEXT DEFAULT UPPER(ALL 'x'));")]
    [InlineData("CREATE FUNCTION bad(value TEXT) RETURNS TEXT RETURN UPPER(value ORDER BY value);")]
    public async Task Aggregates_PersistedScalarModifier_ShouldReject(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE032");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Aggregate sorting: finish cancellation precedes expensive collated keys")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregates_CanceledFinishBeforeOrderedAggregate_ShouldStopPromptly(bool grouped)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var afterCancel = new Stopwatch();
        int finishes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-cancellation");
        builder.Functions.Add(SqlAggregateFunction.Create<int, int, int>("cancel_finish", () => 0,
            (state, value) => state + value, state =>
            {
                finishes++;
                afterCancel.Start();
                cancellation.Cancel();
                return state;
            }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0, 1000).Select(id => $"({id}, 1)")) + ";");
        string sql = "SELECT cancel_finish(id), SUM(id ORDER BY (RPAD('a', 4000, 'a') || CAST(1000-id AS TEXT)) COLLATE case_insensitive) FROM t" +
            (grouped ? " GROUP BY g" : "") + ";";
        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
        // Assert
        finishes.ShouldBe(1);
        error.CancellationToken.ShouldBe(cancellation.Token);
        afterCancel.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Outer sorting: key evaluation and wrapped comparisons preserve cancellation")]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task OrderBy_CancellationDuringKeys_ShouldStopWithinBoundedWork(int cancelAt)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var afterCancel = new Stopwatch();
        int keys = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("ordering-cancellation");
        builder.Functions.Add(SqlScalarFunction.Create<int, string>("cancel_key", value =>
        {
            if (++keys == cancelAt)
            {
                afterCancel.Start();
                cancellation.Cancel();
            }
            return new string('a', 4000) + (1000 - value);
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0, 1000).Select(id => $"({id})")) + ";");
        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            "SELECT id FROM t ORDER BY cancel_key(id) COLLATE case_insensitive;", cancellationToken: cancellation.Token));
        // Assert
        keys.ShouldBeInRange(cancelAt, Math.Min(1000, cancelAt + 32));
        error.CancellationToken.ShouldBe(cancellation.Token);
        afterCancel.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create("aggregate-tests", new SqlDatabaseEngineOptions());

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        (await ExecuteAsync(session, CreateMetrics)).Status.ShouldBe(DatabaseResultStatus.Success);
        (await ExecuteAsync(session, InsertMetrics)).AffectedCount.ShouldBe(9);
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(statement, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, statement, parameters)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(DatabaseResultSet result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++) { values[index] = row.GetValue(index); }
            rows.Add(values);
        }
        return rows;
    }

    private static void CheckRows(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int index = 0; index < expected.Length; index++) { actual[index].ShouldBe(expected[index]); }
    }
}
