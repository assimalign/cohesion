using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAggregateExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: correlated masks belong to their arguments' query level")]
    [InlineData("(SELECT GROUPING(t.a) GROUP BY t.a)")]
    [InlineData("(SELECT GROUPING(t.a))")]
    [InlineData("(SELECT GROUPING(t.a) FROM t q GROUP BY q.b)")]
    [InlineData("(SELECT (SELECT GROUPING_ID(t.a)))")]
    public async Task GroupingSets_OuterMask_ShouldReadOwningLevel(string mask)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("outer_masks");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,3),(2,3);");

        // Act
        var rows = await RowsAsync(session, $"SELECT a,{mask} FROM t GROUP BY ROLLUP(a) ORDER BY GROUPING(a),a;");
        var plain = await RowsAsync(session, $"SELECT a,{mask} FROM t GROUP BY a ORDER BY a;");

        // Assert
        CheckRows(rows, [[1,0],[2,0],[null,1]]);
        CheckRows(plain, [[1,0],[2,0]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: a mask cannot mix local and outer grouping arguments")]
    public async Task GroupingSets_MixedMaskLevels_ShouldRefuse()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("mixed_masks");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "SELECT (SELECT GROUPING(t.a,q.b) FROM t q GROUP BY t.a,q.b) FROM t GROUP BY a;"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01062");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: ROLLUP and CUBE refuse nested GROUPING SETS")]
    [InlineData("ROLLUP(GROUPING SETS ((a),(b)),c)")]
    [InlineData("CUBE(GROUPING SETS ((a),(b)),c)")]
    [InlineData("ROLLUP((a,GROUPING SETS ((b),(c))))")]
    [InlineData("GROUPING SETS (CUBE((GROUPING SETS ((a),(b)),c)))")]
    public async Task GroupingSets_ForbiddenNesting_ShouldRefuseSyntax(string grouping)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("forbidden_nesting");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT,c INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,2,3);");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            $"SELECT COUNT(*) FROM t GROUP BY {grouping};"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS00010");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: EXPLAIN retains expanded key membership and duplicates")]
    public async Task GroupingSets_ExplainMembership_ShouldDistinguishEqualSetCounts()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("explain_membership");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");

        // Act
        var cube = await RowsAsync(session, "EXPLAIN SELECT COUNT(*) FROM t GROUP BY CUBE(a,b);");
        var repeated = await RowsAsync(session, "EXPLAIN SELECT COUNT(*) FROM t GROUP BY GROUPING SETS ((a,b),(a,b),(),());");
        var plain = await RowsAsync(session, "EXPLAIN SELECT COUNT(*) FROM t GROUP BY a,b;");

        // Assert
        string cubeDetail = (string)cube.Single(row => Equals(row[2], "Group"))[5]!;
        string repeatedDetail = (string)repeated.Single(row => Equals(row[2], "Group"))[5]!;
        cubeDetail.ShouldContain("grouping_sets=4; set_keys=[(),(0),(1),(0,1)]", Case.Sensitive);
        repeatedDetail.ShouldContain("grouping_sets=4; set_keys=[(),(),(0,1),(0,1)]", Case.Sensitive);
        cubeDetail.ShouldNotBe(repeatedDetail);
        ((string)plain.Single(row => Equals(row[2], "Group"))[5]!).ShouldBe(
            "keys=2; aggregates=1; having=False; filter=False; order_keys=0; order_by=[]; distinct=False; limit=; offset=");
    }
}
