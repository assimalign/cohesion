using System;
using System.Collections.Generic;
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
            .ShouldBeAssignableTo<QueryResultSet>();

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
            .ShouldBeAssignableTo<QueryResultSet>();

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
            .ShouldBeAssignableTo<QueryResultSet>();
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
        error.Message.ShouldContain(aggregate, Case.Sensitive);
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
            .ShouldBeAssignableTo<QueryResultSet>();

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
        await ExecuteAsync(session, "CREATE TABLE midpoints (category TEXT, amount DECIMAL(29, 28));");
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
            .ShouldBeAssignableTo<QueryResultSet>();
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Float64, DatabaseType.Float64]);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1e100, 2e100 });
        foreach (string aggregate in new[] { "SUM", "AVG" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT {aggregate}(amount) FROM large_floats;"));
            error.Message.ShouldContain(aggregate, Case.Sensitive);
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
            .ShouldBeAssignableTo<QueryResultSet>();
        populated.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.String]);
        (await ReadRowsAsync(populated)).ShouldHaveSingleItem().ShouldBe(new object?[] { 27, 27, "typed", "typed" });
        await using var empty = (await ExecuteAsync(session, projection + " WHERE id < 0;", parameters))
            .ShouldBeAssignableTo<QueryResultSet>();
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
            .ShouldBeAssignableTo<QueryResultSet>();

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
            .ShouldBeAssignableTo<QueryResultSet>();
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
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("aggregate");
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
            .ShouldBeAssignableTo<QueryResultSet>();

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int64, DatabaseType.Int64]);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { -2L, 12L });
    }

    /// <summary>Existing scalar UPPER/LOWER behavior passes non-string arguments through without changing their aggregate type.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Aggregates: scalar numeric passthrough retains extrema value types")]
    public async Task Aggregates_NumericStringFunctionOperand_ShouldMatchScalarRuntimeType()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session, "SELECT MIN(UPPER(amount)), MAX(LOWER(amount)) FROM metrics;"))
            .ShouldBeAssignableTo<QueryResultSet>();

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32]);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { -3, 11 });
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

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "aggregate-tests" });

    private static async Task SeedAsync(IDatabaseSession session)
    {
        (await ExecuteAsync(session, CreateMetrics)).Status.ShouldBe(QueryResultStatus.Success);
        (await ExecuteAsync(session, InsertMetrics)).AffectedCount.ShouldBe(9);
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(statement, parameters, CancellationToken.None).AsTask();

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

    private static void CheckRows(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int index = 0; index < expected.Length; index++) { actual[index].ShouldBe(expected[index]); }
    }
}
