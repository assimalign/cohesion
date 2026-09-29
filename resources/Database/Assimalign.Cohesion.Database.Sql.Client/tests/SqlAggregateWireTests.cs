using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Exercises grouping and aggregates through encoded SQL exchanges between
/// SqlDatabaseServer and the production typed SQL client.
/// </summary>
public sealed class SqlAggregateWireTests
{
    /// <summary>Every aggregate returns its distinct answer and declared type for nullable input.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: ungrouped values and types survive the wire")]
    public async Task QueryAsync_UngroupedAggregates_ShouldReturnTypedValuesOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT COUNT(*) AS rows, COUNT(amount) AS populated, SUM(amount) AS total, " +
            "AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM sales",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["rows", "populated", "total", "average", "minimum", "maximum"]);
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
        AssertAggregateValues(result.ShouldHaveSingleItem(), 7L, 4L, 40m, 10m, 4, 20);
    }

    /// <summary>Groups preserve COUNT's two meanings and each aggregate's null behavior.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: grouped nullable values survive the wire")]
    public async Task QueryAsync_GroupedAggregates_ShouldIgnoreNullsAndRetainAllNullGroupOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT region, COUNT(*) AS rows, COUNT(amount) AS populated, SUM(amount) AS total, " +
            "AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum " +
            "FROM sales GROUP BY region ORDER BY region",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Select(row => row["region"]).ShouldBe(new object?[] { "A", "B", "C" });
        AssertAggregateValues(result[0], 3L, 2L, 30m, 15m, 10, 20);
        AssertAggregateValues(result[1], 3L, 2L, 10m, 5m, 4, 6);
        AssertAggregateValues(result[2], 1L, 0L, null, null, null, null);
    }

    /// <summary>More than one grouping expression produces one result per complete key.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: multiple grouping expressions survive the wire")]
    public async Task QueryAsync_MultipleGroupingExpressions_ShouldReturnDistinctCompositeGroupsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT region, UPPER(category) AS category_name, COUNT(*) AS rows, SUM(amount) AS total " +
            "FROM sales GROUP BY region, UPPER(category) ORDER BY region, UPPER(category)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Select(row => row["region"]).ShouldBe(new object?[] { "A", "A", "B", "B", "C" });
        result.Select(row => row["category_name"]).ShouldBe(new object?[] { "X", "Y", "X", "Y", "X" });
        result.Select(row => row["rows"]).ShouldBe(new object?[] { 2L, 1L, 1L, 2L, 1L });
        result.Select(row => row["total"]).ShouldBe(new object?[] { 30m, null, 4m, 6m, null });
    }

    /// <summary>Grouping does not require a projected aggregate, including a HAVING-only aggregate.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: grouping and HAVING work without projected aggregates over the wire")]
    public async Task QueryAsync_UnprojectedAggregate_ShouldGroupAndFilterOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var grouped = await connection.QueryAsync("SELECT region FROM sales GROUP BY region ORDER BY region",
            cancellationToken: SqlClientTestHarness.Timeout());
        var filtered = await connection.QueryAsync(
            "SELECT region FROM sales GROUP BY region HAVING COUNT(amount) > 0 ORDER BY region",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        grouped.Select(row => row["region"]).ShouldBe(new object?[] { "A", "B", "C" });
        filtered.Select(row => row["region"]).ShouldBe(new object?[] { "A", "B" });
    }

    /// <summary>WHERE changes aggregate input before HAVING rejects a remaining group.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: WHERE precedes grouping and HAVING follows it over the wire")]
    public async Task QueryAsync_WhereAndHaving_ShouldFilterRowsThenGroupsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act: B has three source rows, but WHERE leaves only its amount of 6.
        var beforeHaving = await connection.QueryAsync(
            "SELECT region, COUNT(*) AS rows, SUM(amount) AS total FROM sales " +
            "WHERE amount >= 6 GROUP BY region ORDER BY region",
            cancellationToken: SqlClientTestHarness.Timeout());
        var afterHaving = await connection.QueryAsync(
            "SELECT region, COUNT(*) AS rows, SUM(amount) AS total FROM sales " +
            "WHERE amount >= 6 GROUP BY region HAVING COUNT(*) >= 2 ORDER BY region",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: the row predicate retains B; the group predicate rejects it.
        beforeHaving.Select(row => row["region"]).ShouldBe(new object?[] { "A", "B" });
        beforeHaving[1]["rows"].ShouldBe(1L);
        beforeHaving[1]["total"].ShouldBe(6m);
        var accepted = afterHaving.ShouldHaveSingleItem();
        accepted["region"].ShouldBe("A");
        accepted["rows"].ShouldBe(2L);
        accepted["total"].ShouldBe(30m);
    }

    /// <summary>Aggregate ordering and pagination apply to finished groups.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: ordering limit and offset apply to groups over the wire")]
    public async Task QueryAsync_GroupOrderingAndPagination_ShouldSelectFinishedGroupOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT region, SUM(amount) AS total FROM sales GROUP BY region " +
            "ORDER BY SUM(amount) DESC, region ASC LIMIT 1 OFFSET 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        var row = result.ShouldHaveSingleItem();
        row["region"].ShouldBe("B");
        row["total"].ShouldBe(10m);
    }

    /// <summary>The no-input identity differs between an implicit group and explicit grouping.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: empty grouped and ungrouped inputs differ over the wire")]
    public async Task QueryAsync_EmptyInput_ShouldReturnUngroupedIdentityAndNoExplicitGroupsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        const string aggregates = "COUNT(*) AS rows, COUNT(amount) AS populated, SUM(amount) AS total, " +
            "AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum";

        // Act
        var ungrouped = await connection.QueryAsync($"SELECT {aggregates} FROM sales WHERE id < 0",
            cancellationToken: SqlClientTestHarness.Timeout());
        var grouped = await connection.QueryAsync($"SELECT region, {aggregates} FROM sales WHERE id < 0 GROUP BY region",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        AssertAggregateValues(ungrouped.ShouldHaveSingleItem(), 0L, 0L, null, null, null, null);
        grouped.ShouldBeEmpty();
        ungrouped.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
        grouped.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.String, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
    }

    /// <summary>An all-null column remains different from an empty relation for COUNT(*).</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: all-null column returns null aggregates over the wire")]
    public async Task QueryAsync_AllNullColumn_ShouldCountRowsAndReturnNullValuesOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT COUNT(*) AS rows, COUNT(absent) AS populated, SUM(absent) AS total, " +
            "AVG(absent) AS average, MIN(absent) AS minimum, MAX(absent) AS maximum FROM sales",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        AssertAggregateValues(result.ShouldHaveSingleItem(), 7L, 0L, null, null, null, null);
    }

    /// <summary>Integer averages preserve fractions and decimal division's stated rounding.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: integer averages preserve decimal fractions over the wire")]
    public async Task QueryAsync_IntegerAverage_ShouldReturnDecimalFractionAndRoundedThirdOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE fractions (id INT, amount INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO fractions VALUES (1, 0), (2, 1), (3, 0)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var half = await connection.QueryAsync("SELECT AVG(amount) AS average FROM fractions WHERE id <= 2",
            cancellationToken: SqlClientTestHarness.Timeout());
        var third = await connection.QueryAsync("SELECT AVG(amount) AS average FROM fractions",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        half.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Decimal);
        half.ShouldHaveSingleItem()["average"].ShouldBe(0.5m);
        third.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Decimal);
        third.ShouldHaveSingleItem()["average"].ShouldBe(0.3333333333333333333333333333m);
    }

    /// <summary>Scalar argument promotion remains visible in MIN and MAX result metadata.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: unary and ABS extrema preserve promoted wire types")]
    public async Task QueryAsync_PromotedAggregateArguments_ShouldReturnMatchingValuesAndTypesOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT MIN(-amount) AS minimum, MAX(ABS(amount)) AS maximum FROM sales",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int64, DatabaseType.Int64]);
        var row = result.ShouldHaveSingleItem();
        row["minimum"].ShouldBe(-20L);
        row["maximum"].ShouldBe(20L);
    }

    /// <summary>Mixed integer widths in CASE use one common result type for aggregate extrema.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: CASE extrema share their promoted wire type")]
    public async Task QueryAsync_MixedCaseArgument_ShouldReturnCommonIntegerTypeOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await connection.QueryAsync(
            "SELECT MIN(CASE WHEN id = 1 THEN amount ELSE CAST(amount AS BIGINT) END) AS minimum, " +
            "MAX(CASE WHEN id = 1 THEN amount ELSE CAST(amount AS BIGINT) END) AS maximum FROM sales WHERE id <= 2",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int64, DatabaseType.Int64]);
        var row = result.ShouldHaveSingleItem();
        row["minimum"].ShouldBe(10L);
        row["maximum"].ShouldBe(20L);
    }

    /// <summary>ORDER BY can bind a projected aggregate alias for grouped and ungrouped queries.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: ORDER BY aliases bind completed aggregates over the wire")]
    public async Task QueryAsync_AggregateOrderByAlias_ShouldOrderCompletedValuesOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var grouped = await connection.QueryAsync(
            "SELECT region, SUM(amount) AS total FROM sales GROUP BY region ORDER BY total DESC LIMIT 1 OFFSET 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        var ungrouped = await connection.QueryAsync("SELECT SUM(amount) AS total FROM sales ORDER BY total DESC",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        var row = grouped.ShouldHaveSingleItem();
        row["region"].ShouldBe("B");
        row["total"].ShouldBe(10m);
        ungrouped.ShouldHaveSingleItem()["total"].ShouldBe(40m);
    }

    /// <summary>Null grouping keys share one group without making NULL equal in predicates.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: null keys share one group over the wire")]
    public async Task QueryAsync_NullGroupingKeys_ShouldProduceOneNullGroupOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE nullable_groups (label TEXT, amount INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO nullable_groups VALUES (NULL, 2), (NULL, 5), ('A', 9)",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync(
            "SELECT label, COUNT(*) AS rows, SUM(amount) AS total FROM nullable_groups GROUP BY label ORDER BY label",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Count.ShouldBe(2);
        result[0]["label"].ShouldBeNull();
        result[0]["rows"].ShouldBe(2L);
        result[0]["total"].ShouldBe(7m);
        result[1]["label"].ShouldBe("A");
        result[1]["rows"].ShouldBe(1L);
        result[1]["total"].ShouldBe(9m);
    }

    /// <summary>A two-table join feeds grouping before HAVING, aggregate order, and pagination.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: join grouping composes over the wire")]
    public async Task QueryAsync_JoinGrouping_ShouldComposeEveryAggregateAndGroupClauseOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        await connection.ExecuteAsync("CREATE TABLE regions (code TEXT PRIMARY KEY, label TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO regions VALUES ('A', 'North'), ('B', 'South'), ('C', 'West')",
            cancellationToken: SqlClientTestHarness.Timeout());
        var command = new SqlCommand(
            "SELECT r.label, COUNT(*) AS rows, COUNT(s.amount) AS populated, SUM(s.amount) AS total, " +
            "AVG(s.amount) AS average, MIN(s.amount) AS minimum, MAX(s.amount) AS maximum " +
            "FROM sales s INNER JOIN regions r ON s.region = r.code WHERE s.id >= @first " +
            "GROUP BY r.label HAVING COUNT(*) >= 2 ORDER BY SUM(s.amount) DESC LIMIT 1 OFFSET 1")
            .WithParameter("first", 1);

        // Act
        var result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert
        var row = result.ShouldHaveSingleItem();
        row["label"].ShouldBe("South");
        AssertAggregateValues(row, 3L, 2L, 10m, 5m, 4, 6);
        result.Columns.Select(column => column.Type).ShouldBe(
            [DatabaseType.String, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Int32]);
    }

    /// <summary>An arbitrary source value cannot escape grouping, including over a join.</summary>
    /// <param name="statement">The projection with an ungrouped column.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: ungrouped projected columns fail clearly over the wire")]
    [InlineData("SELECT region, category, SUM(amount) FROM sales GROUP BY region")]
    [InlineData("SELECT region, SUM(amount) FROM sales")]
    [InlineData("SELECT a.region, b.category, SUM(a.amount) FROM sales a JOIN sales b ON a.id = b.id GROUP BY a.region")]
    public async Task QueryAsync_UngroupedProjectedColumn_ShouldReportBindingErrorOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            statement, cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("GROUP BY", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) AS rows FROM sales", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["rows"].ShouldBe(7L);
    }

    /// <summary>Excluded aggregate forms fail through the capability diagnostic before planning.</summary>
    /// <param name="statement">The excluded aggregate form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Aggregates: excluded forms report COHDBL001 over the wire")]
    [InlineData("SELECT COUNT(DISTINCT score) FROM users")]
    [InlineData("SELECT SUM(DISTINCT score) FROM users")]
    [InlineData("SELECT name, COUNT(*) FROM users GROUP BY ROLLUP(name)")]
    [InlineData("SELECT name, COUNT(*) FROM users GROUP BY CUBE(name)")]
    [InlineData("SELECT name, COUNT(*) FROM users GROUP BY GROUPING SETS ((name), ())")]
    [InlineData("SELECT SUM(score) OVER (PARTITION BY name) FROM users")]
    [InlineData("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY score) FROM users")]
    public async Task QueryAsync_ExcludedAggregate_ShouldReportCapabilityDiagnosticOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            statement, cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) AS rows FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["rows"].ShouldBe(2L);
    }

    private static void AssertAggregateValues(SqlRow row, long rows, long populated, decimal? total,
        decimal? average, int? minimum, int? maximum)
    {
        row["rows"].ShouldBe(rows);
        row["populated"].ShouldBe(populated);
        row["total"].ShouldBe(total);
        row["average"].ShouldBe(average);
        row["minimum"].ShouldBe(minimum);
        row["maximum"].ShouldBe(maximum);
    }

    private static async Task SeedAsync(ISqlConnection connection)
    {
        await connection.ExecuteAsync("CREATE TABLE sales (id INT PRIMARY KEY, region TEXT, category TEXT, amount INT, absent INT)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync(
            "INSERT INTO sales VALUES (1, 'A', 'x', 10, NULL), (2, 'A', 'x', 20, NULL), (3, 'A', 'y', NULL, NULL), " +
            "(4, 'B', 'x', 4, NULL), (5, 'B', 'y', 6, NULL), (6, 'B', 'y', NULL, NULL), (7, 'C', 'x', NULL, NULL)",
            cancellationToken: SqlClientTestHarness.Timeout());
    }
}
