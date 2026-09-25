using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Verifies complete ORDER BY results through SqlDatabaseServer and the production typed client.</summary>
public sealed class SqlOrderByWireTests
{
    /// <summary>The motivating single-column alias query sorts ascending, descending and inside arithmetic.</summary>
    /// <param name="ordering">The alias ordering expression.</param>
    /// <param name="expectedAges">The complete expected projected values.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: standalone and nested aliases sort projected values over the wire")]
    [InlineData("years ASC", 18, 24, 29, 35, 42)]
    [InlineData("years DESC", 42, 35, 29, 24, 18)]
    [InlineData("years + 1 ASC", 18, 24, 29, 35, 42)]
    public async Task QueryAsync_ProjectionAlias_ShouldSortProjectedValuesOverTheWire(string ordering, params int[] expectedAges)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE t (id INT PRIMARY KEY, age INT)");
        await ExecuteAsync(connection, "INSERT INTO t VALUES (40, 24), (10, 42), (30, 18), (20, 35), (50, 29)");
        object?[][] inserted = [[24], [42], [18], [35], [29]];
        var expected = expectedAges.Select(age => new object?[] { age }).ToArray();

        // Act / Assert: the old binder reports Unknown column 'years' for all three forms.
        await AssertOrderedAsync(connection, "SELECT age AS years FROM t", ordering, inserted, expected);
    }

    /// <summary>The exact silent-wrong-order reproduction reverses the first output column over the wire.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: ordinal 1 DESC reverses the single id projection over the wire")]
    public async Task QueryAsync_FirstOrdinalDescending_ShouldReverseSingleProjectionOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE t (id INT PRIMARY KEY)");
        await ExecuteAsync(connection, "INSERT INTO t VALUES (40), (10), (30), (20), (50)");
        object?[][] inserted = [[40], [10], [30], [20], [50]];
        object?[][] expected = [[50], [40], [30], [20], [10]];

        // Act / Assert
        await AssertOrderedAsync(connection, "SELECT id FROM t", "1 DESC", inserted, expected);
    }

    /// <summary>Aliases, nested aliases, ordinals and existing source expressions reorder deliberately scrambled rows.</summary>
    /// <param name="ordering">The ordering keys.</param>
    /// <param name="expectedIds">The complete expected row order.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: aliases ordinals and source keys execute over the wire")]
    [InlineData("years ASC", 30, 40, 50, 20, 10)]
    [InlineData("years DESC", 10, 20, 50, 40, 30)]
    [InlineData("years + 1 ASC", 30, 40, 50, 20, 10)]
    [InlineData("ABS(years - 30), id DESC", 50, 20, 40, 30, 10)]
    [InlineData("1 ASC", 30, 40, 50, 20, 10)]
    [InlineData("1 DESC", 10, 20, 50, 40, 30)]
    [InlineData("+1 DESC", 10, 20, 50, 40, 30)]
    [InlineData("2 DESC", 50, 40, 30, 20, 10)]
    [InlineData("ordering_rows.years ASC", 40, 30, 50, 20, 10)]
    [InlineData("age DESC", 10, 20, 50, 40, 30)]
    [InlineData("-age DESC", 30, 40, 50, 20, 10)]
    public async Task QueryAsync_ProjectionOrdering_ShouldReorderCompleteResultsOverTheWire(string ordering, params int[] expectedIds)
    {
        // Arrange: source years deliberately disagrees with the projected years alias.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        var inserted = ProjectedRows();
        var expected = expectedIds.Select(id => inserted.Single(row => Equals(row[1], id))).ToArray();

        // Act / Assert: a scan and insertion-order result are explicitly ruled out before sorting.
        await AssertOrderedAsync(connection, "SELECT age AS years, id FROM ordering_rows", ordering, inserted, expected);
    }

    /// <summary>Arithmetic and string constants retain their value; the effective secondary key must still sort.</summary>
    /// <param name="constant">A constant ordering expression that is not an ordinal.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: constant expressions are not select-list ordinals over the wire")]
    [InlineData("1 + 1")]
    [InlineData("'1'")]
    [InlineData("NULL")]
    public async Task QueryAsync_ConstantExpression_ShouldUseSecondaryKeyOverTheWire(string constant)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        object?[][] inserted = [[40, 24], [10, 42], [30, 18], [20, 35], [50, 29]];
        object?[][] expected = [[10, 42], [20, 35], [50, 29], [40, 24], [30, 18]];

        // Act / Assert: folding 1 + 1 into ordinal 2 would sort age ASC, contradicting every row here.
        await AssertOrderedAsync(connection, "SELECT id, age FROM ordering_rows", $"{constant}, age DESC", inserted, expected);
    }

    /// <summary>Invalid numeric ordinals fail precisely before returning rows, including for an empty source.</summary>
    /// <param name="ordinal">The invalid ordinal token.</param>
    /// <param name="diagnostic">The exact binding diagnostic.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: invalid ordinals fail precisely over the wire")]
    [InlineData("0", "ORDER BY ordinal '0' must be a positive integer.")]
    [InlineData("-1", "ORDER BY ordinal '-1' must be a positive integer.")]
    [InlineData("3", "ORDER BY ordinal '3' is out of range for 2 output columns.")]
    [InlineData("1.5", "ORDER BY ordinal '1.5' must be an integer.")]
    [InlineData("1.0", "ORDER BY ordinal '1.0' must be an integer.")]
    [InlineData("+1.5", "ORDER BY ordinal '1.5' must be an integer.")]
    [InlineData("999999999999999999999999999999999", "ORDER BY ordinal '999999999999999999999999999999999' is out of range for 2 output columns.")]
    public async Task QueryAsync_InvalidOrdinal_ShouldRejectBeforeMaterializationOverTheWire(string ordinal, string diagnostic)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        foreach (string predicate in new[] { "", " WHERE id < 0" })
        {
            var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection,
                $"SELECT age AS years, id FROM ordering_rows{predicate} ORDER BY {ordinal}"));
            error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            error.Message.ShouldContain(diagnostic, Case.Sensitive);
            error.ConnectionUsable.ShouldBeTrue();
        }
        (await QueryAsync(connection, "SELECT age FROM ordering_rows WHERE id = 30")).ShouldHaveSingleItem()[0].ShouldBe(18);
    }

    /// <summary>Valid projected keys bind without attempting source-column lookup on an empty relation.</summary>
    /// <param name="ordering">The alias or ordinal ordering expression.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: empty sources still bind valid projection keys over the wire")]
    [InlineData("years")]
    [InlineData("years + 1")]
    [InlineData("1 DESC")]
    public async Task QueryAsync_EmptySource_ShouldBindProjectionKeysOverTheWire(string ordering)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE empty_order (age INT)");

        // Act
        var result = await QueryAsync(connection, $"SELECT age AS years FROM empty_order ORDER BY {ordering}");

        // Assert: this checks empty-source binding and metadata, rather than claiming an ordering proof.
        result.ShouldBeEmpty();
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("years");
    }

    /// <summary>Aliases and ordinals compose with DISTINCT and pagination after duplicate removal.</summary>
    /// <param name="ordering">The alias or ordinal ordering.</param>
    /// <param name="first">The first expected paginated value.</param>
    /// <param name="last">The last expected paginated value.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: DISTINCT LIMIT and OFFSET compose over the wire")]
    [InlineData("years ASC", 24, 35)]
    [InlineData("1 DESC", 35, 24)]
    public async Task QueryAsync_DistinctPagination_ShouldApplyOrderingBeforeWindowOverTheWire(string ordering, int first, int last)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        await ExecuteAsync(connection, "INSERT INTO ordering_rows VALUES (60, 35, 600, NULL), (70, 24, 700, 1)");
        object?[][] insertionWindow = [[42], [18], [35]];
        object?[][] expected = [[first], [29], [last]];

        // Act / Assert
        await AssertOrderedAsync(connection, "SELECT DISTINCT age AS years FROM ordering_rows", ordering,
            insertionWindow, expected, "LIMIT 3 OFFSET 1");
    }

    /// <summary>Mixed directions retain NULL ordering and use the second projection key to break ties.</summary>
    /// <param name="ordering">The ordering keys and directions.</param>
    /// <param name="expectedIds">The complete expected row order.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: aliases ordinals nulls and mixed directions compose over the wire")]
    [InlineData("priority_alias ASC, 3 DESC", 10, 50, 30, 20, 40)]
    [InlineData("2 DESC, years ASC", 40, 20, 30, 50, 10)]
    public async Task QueryAsync_NullAndMixedKeys_ShouldPreserveNullPlacementOverTheWire(string ordering, params int[] expectedIds)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        object?[][] inserted = [[40, 2, 24], [10, null, 42], [30, 1, 18], [20, 2, 35], [50, null, 29]];
        var expected = expectedIds.Select(id => inserted.Single(row => Equals(row[0], id))).ToArray();

        // Act / Assert
        await AssertOrderedAsync(connection, "SELECT id, priority AS priority_alias, age AS years FROM ordering_rows",
            ordering, inserted, expected);
    }

    /// <summary>Both new forms order rows assembled by a two-table INNER JOIN.</summary>
    /// <param name="ordering">The alias or select-list ordinal key.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: aliases and ordinals order joined rows over the wire")]
    [InlineData("years + 1 DESC")]
    [InlineData("2 DESC")]
    public async Task QueryAsync_InnerJoin_ShouldOrderJoinedProjectionOverTheWire(string ordering)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        await ExecuteAsync(connection, "CREATE TABLE labels (id INT PRIMARY KEY, label TEXT)");
        await ExecuteAsync(connection, "INSERT INTO labels VALUES (20, 'twenty'), (50, 'fifty'), (10, 'ten'), (40, 'forty'), (30, 'thirty')");
        object?[][] inserted = [["forty", 24], ["ten", 42], ["thirty", 18], ["twenty", 35], ["fifty", 29]];
        object?[][] expected = [["ten", 42], ["twenty", 35], ["fifty", 29], ["forty", 24], ["thirty", 18]];

        // Act / Assert
        await AssertOrderedAsync(connection,
            "SELECT l.label, o.age AS years FROM ordering_rows o INNER JOIN labels l ON o.id = l.id",
            ordering, inserted, expected);
    }

    /// <summary>A scalar subquery inside the projected value retains correct alias and ordinal ordering.</summary>
    /// <param name="ordering">The alias or ordinal referring to the completed projection.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: scalar subquery projections compose with aliases and ordinals over the wire")]
    [InlineData("adjusted DESC")]
    [InlineData("1 DESC")]
    public async Task QueryAsync_ScalarSubqueryProjection_ShouldOrderCompletedValuesOverTheWire(string ordering)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        object?[][] inserted = [[26L, 40], [44L, 10], [20L, 30], [37L, 20], [31L, 50]];
        object?[][] expected = [[44L, 10], [37L, 20], [31L, 50], [26L, 40], [20L, 30]];

        // Act / Assert
        await AssertOrderedAsync(connection,
            "SELECT age + (SELECT MAX(id) FROM users) AS adjusted, id FROM ordering_rows",
            ordering, inserted, expected);
    }

    /// <summary>Completed aggregate aliases and ordinals sort groups before DISTINCT and pagination.</summary>
    /// <param name="ordering">The aggregate alias or ordinal key.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: nested aliases and ordinals order completed groups over the wire")]
    [InlineData("total DESC")]
    [InlineData("total + 1 DESC")]
    [InlineData("2 DESC")]
    public async Task QueryAsync_GroupedProjection_ShouldOrderCompletedGroupsOverTheWire(string ordering)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE sales (id INT PRIMARY KEY, category TEXT, amount INT)");
        await ExecuteAsync(connection, "INSERT INTO sales VALUES (40, 'B', 20), (10, 'A', 10), (30, 'C', 5), (20, 'B', 4), (50, 'A', 6), (60, 'C', 40)");
        object?[][] inserted = [["B", 24m], ["A", 16m], ["C", 45m]];
        object?[][] expected = [["C", 45m], ["B", 24m], ["A", 16m]];
        const string projection = "SELECT DISTINCT category, SUM(amount) AS total FROM sales GROUP BY category";

        // Act / Assert
        await AssertOrderedAsync(connection, projection, ordering, inserted, expected);
        await AssertOrderedAsync(connection, projection, ordering, [.. inserted.Skip(1)], [.. expected.Skip(1)], "LIMIT 2 OFFSET 1");
    }

    /// <summary>Virtual relation aliases and ordinals use metadata values, not catalog enumeration order.</summary>
    /// <param name="ordering">The system relation ordering.</param>
    /// <param name="descending">Whether the expected order is descending.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: aliases and ordinals sort system relations over the wire")]
    [InlineData("label ASC", false)]
    [InlineData("1 DESC", true)]
    public async Task QueryAsync_SystemRelation_ShouldOrderProjectedMetadataOverTheWire(string ordering, bool descending)
    {
        // Arrange: creation order differs from both lexical directions.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE catalog_order (middle INT, zebra INT, alpha INT, theta INT)");
        object?[][] inserted = [["middle", 1L], ["zebra", 2L], ["alpha", 3L], ["theta", 4L]];
        object?[][] ascending = [["alpha", 3L], ["middle", 1L], ["theta", 4L], ["zebra", 2L]];
        var expected = descending ? ascending.Reverse().ToArray() : ascending;

        // Act / Assert
        await AssertOrderedAsync(connection,
            "SELECT COLUMN_NAME AS label, ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'catalog_order'",
            ordering, inserted, expected);
    }

    /// <summary>Projection aliases and ordinals retain the source expression's collation.</summary>
    /// <param name="projection">The projected collated expression.</param>
    /// <param name="ordering">The alias or ordinal ordering.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: projection collation survives alias and ordinal binding over the wire")]
    [InlineData("name AS label", "label")]
    [InlineData("name AS label", "1")]
    [InlineData("name COLLATE case_insensitive AS label", "label")]
    [InlineData("name COLLATE case_insensitive AS label", "1")]
    public async Task QueryAsync_CollatedProjection_ShouldRetainOrderingCollationOverTheWire(string projection, string ordering)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE collated_order (id INT PRIMARY KEY, name TEXT COLLATE case_insensitive)");
        await ExecuteAsync(connection, "INSERT INTO collated_order VALUES (40, 'Zed'), (10, 'bob'), (30, 'Alice'), (20, 'dave')");
        object?[][] inserted = [["Zed", 40], ["bob", 10], ["Alice", 30], ["dave", 20]];
        object?[][] expected = [["Alice", 30], ["bob", 10], ["dave", 20], ["Zed", 40]];

        // Act / Assert: binary ordering would incorrectly place Zed before bob and dave.
        await AssertOrderedAsync(connection, $"SELECT {projection}, id FROM collated_order", ordering, inserted, expected);
    }

    /// <summary>Excluded ordering forms return the capability diagnostic and preserve the connection.</summary>
    /// <param name="statement">The excluded form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ORDER BY: excluded forms report COHDBL001 over the wire")]
    [InlineData("SELECT id FROM users ORDER BY id NULLS FIRST")]
    [InlineData("SELECT id FROM users ORDER BY id NULLS LAST")]
    [InlineData("SELECT derived.id FROM (SELECT id FROM users) derived ORDER BY derived.id")]
    [InlineData("SELECT id, COUNT(*) FROM users GROUP BY 1")]
    public async Task QueryAsync_UnsupportedOrdering_ShouldReportCapabilityDiagnosticOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, statement));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await QueryAsync(connection, "SELECT name FROM users WHERE id = 1")).ShouldHaveSingleItem()[0].ShouldBe("ada");
    }

    private static async Task SeedAsync(ISqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE ordering_rows (id INT PRIMARY KEY, age INT, years INT, priority INT)");
        await ExecuteAsync(connection, "INSERT INTO ordering_rows VALUES (40, 24, 100, 2), (10, 42, 500, NULL), (30, 18, 200, 1), (20, 35, 400, 2), (50, 29, 300, NULL)");
    }

    private static object?[][] ProjectedRows() => [[24, 40], [42, 10], [18, 30], [35, 20], [29, 50]];

    private static async Task AssertOrderedAsync(ISqlConnection connection, string projection, string ordering,
        object?[][] insertionOrder, object?[][] expected, string pagination = "")
    {
        var scanned = await QueryAsync(connection, $"{projection} {pagination}");
        var storageOrder = scanned.Select(row => Enumerable.Range(0, row.FieldCount).Select(index => row[index]).ToArray()).ToArray();
        SameRows(expected, insertionOrder).ShouldBeFalse("the expected result must differ from insertion order");
        SameRows(expected, storageOrder).ShouldBeFalse("the expected result must differ from the measured unordered storage scan");

        var result = await QueryAsync(connection, $"{projection} ORDER BY {ordering} {pagination}");

        result.Count.ShouldBe(expected.Length);
        result.Columns.Count.ShouldBe(expected[0].Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Enumerable.Range(0, result[index].FieldCount).Select(column => result[index][column]).ShouldBe(expected[index]);
        }
    }

    private static bool SameRows(IReadOnlyList<object?[]> left, IReadOnlyList<object?[]> right)
        => left.Count == right.Count && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    private static Task<long> ExecuteAsync(ISqlConnection connection, string sql)
        => connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();

    private static Task<SqlResultSet> QueryAsync(ISqlConnection connection, string sql)
        => connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();
}
