using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: aggregate levels precede grouped outer validation")]
    [InlineData("SELECT (SELECT SUM(c.id) FROM choices x) FROM candidates c")]
    [InlineData("SELECT (SELECT SUM(c.id) FROM choices x) FROM candidates c GROUP BY c.id + 1")]
    [InlineData("SELECT COUNT(*), (SELECT SUM(c.id) FROM choices x) FROM candidates c")]
    [InlineData("SELECT (SELECT SUM((SELECT c.id FROM choices v)) FROM choices x) FROM candidates c")]
    [InlineData("SELECT COUNT(*), (SELECT SUM((SELECT c.id FROM choices v)) FROM choices x) FROM candidates c")]
    [InlineData("SELECT COUNT(*), (SELECT SUM((SELECT d.n FROM (SELECT c.id AS n FROM choices v) d)) FROM choices x) FROM candidates c")]
    [InlineData("SELECT (SELECT SUM((SELECT d.n FROM (SELECT c.id AS n FROM choices v) d)) FROM choices x) FROM candidates c GROUP BY c.id + 1")]
    [InlineData("SELECT (SELECT (SELECT SUM(c.id) FROM choices y) FROM choices x GROUP BY x.id) FROM candidates c GROUP BY c.id + 1")]
    [InlineData("SELECT COUNT(*) FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x WHERE SUM(c.id) > 0)")]
    [InlineData("SELECT COUNT(*), (SELECT x.id FROM choices x WHERE SUM(c.id) > 0) FROM candidates c")]
    [InlineData("SELECT (SELECT x.id FROM choices x WHERE SUM(c.id) > 0) FROM candidates c GROUP BY c.id + 1")]
    [InlineData("SELECT COUNT(*), (SELECT COUNT(*) FROM choices x HAVING SUM(c.id) > 0) FROM candidates c")]
    [InlineData("SELECT (SELECT COUNT(*) FROM choices x HAVING SUM(c.id) > 0) FROM candidates c GROUP BY c.id + 1")]
    public async Task Execute_OuterLevelAggregate_ShouldReportCapabilityBeforeGrouping(string sql)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate-level");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var result = await ExecuteAsync(session, sql);

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Error);
        result.Diagnostics.ShouldNotBeNull().ShouldContain(d => d.Code == "COHDBL001" && d.Message!.Contains("outer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: nested local dependencies keep mixed aggregates at their own level")]
    public async Task Execute_MixedNestedAggregate_ShouldSumLocalAndOuterValues()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate-local");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "CREATE TABLE u (x INT)");
        await ExecuteAsync(session, "CREATE TABLE v (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (10), (20)");
        await ExecuteAsync(session, "INSERT INTO v VALUES (1)");

        // Act
        var rows = await RowsAsync(session, "SELECT t.id, (SELECT SUM(t.id + (SELECT u.x FROM v)) FROM u) FROM t ORDER BY t.id");

        // Assert
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, 32m });
        rows[1].ShouldBe(new object?[] { 2, 34m });
        (await RowsAsync(session, "SELECT t.id, (SELECT SUM(t.id + (SELECT d.n FROM (SELECT u.x AS n FROM v) d)) FROM u) FROM t ORDER BY t.id"))
            .Select(row => row[1]).ShouldBe(new object?[] { 32m, 34m });
        (await RowsAsync(session, "SELECT COUNT(*), SUM((SELECT t.id FROM v)) FROM t"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2L, 3m });
        (await RowsAsync(session, "SELECT t.id + 1, SUM((SELECT t.id FROM v)) FROM t GROUP BY t.id + 1 ORDER BY 1"))
            .Select(row => row[1]).ShouldBe(new object?[] { 1m, 2m });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: grouped USING references match bound values and types")]
    [InlineData("INT", "BIGINT", "JOIN", "b.id", true)]
    [InlineData("INT", "BIGINT", "JOIN", "a.id", false)]
    [InlineData("BIGINT", "INT", "JOIN", "a.id", true)]
    [InlineData("BIGINT", "INT", "JOIN", "b.id", false)]
    [InlineData("INT", "BIGINT", "LEFT JOIN", "id", true)]
    [InlineData("INT", "BIGINT", "LEFT JOIN", "a.id", false)]
    [InlineData("INT", "BIGINT", "LEFT JOIN", "b.id", false)]
    [InlineData("BIGINT", "INT", "LEFT JOIN", "a.id", true)]
    public async Task Execute_GroupedUsingOuterReference_ShouldCompareBoundGroupingValue(string left, string right, string join, string reference, bool valid)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("grouped-using");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE a (id {left})");
        await ExecuteAsync(session, $"CREATE TABLE b (id {right})");
        await ExecuteAsync(session, "CREATE TABLE v (n INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1), (2)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1), (2)");
        await ExecuteAsync(session, "INSERT INTO v VALUES (1)");
        string sql = $"SELECT (SELECT {reference} FROM v) FROM a {join} b USING (id) GROUP BY id ORDER BY id";

        // Act / Assert
        if (valid)
        {
            (await RowsAsync(session, sql)).Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        }
        else
        {
            var error = await Should.ThrowAsync<SqlPlanningException>(() => RowsAsync(session, sql));
            error.Diagnostics.ShouldContain(d => d.Code == "COHSQLE017" && d.Message!.Contains("Outer column", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: collated derived outer columns retain VARCHAR metadata despite same-named local INT")]
    public async Task Execute_CollatedDerivedOuterColumn_ShouldKeepOuterMetadata()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("derived-metadata");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, label VARCHAR(10))");
        await ExecuteAsync(session, "CREATE TABLE u (label INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a')");
        await ExecuteAsync(session, "INSERT INTO u VALUES (5)");
        const string body = "(SELECT t.label COLLATE case_insensitive AS l FROM u) d";
        string scalarSql = $"SELECT (SELECT d.l FROM {body}) FROM t";

        // Act / Assert
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(scalarSql).Statement.SqlExpression)
            .ShouldBeOfType<SqlCorrelatedSubqueryPlan>();
        var metadata = plan.Queries.ShouldHaveSingleItem().Query.ShouldBeOfType<SqlJoinPlan>()
            .Bindings.ShouldHaveSingleItem().Source.Derived.ShouldNotBeNull().Columns.ShouldHaveSingleItem();
        metadata.Type.Type.ShouldBe(DatabaseType.String);
        metadata.Type.MaxLength.ShouldBe(10);
        metadata.Collation.ShouldBe(Collation.FromName("case_insensitive"));
        (await RowsAsync(session, $"SELECT t.id FROM t WHERE EXISTS (SELECT d.l FROM {body} WHERE d.l = 'A')"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        await using var result = (await ExecuteAsync(session, scalarSql)).ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.String);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBe("a");
        (await RowsAsync(session, $"SELECT t.id FROM t WHERE (SELECT d.l FROM {body}) = 'A'"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: sibling derived references inside children still require LATERAL")]
    [InlineData("SELECT t.id FROM candidates t WHERE EXISTS (SELECT 1 FROM choices u, (SELECT u.id FROM candidates v) d)")]
    [InlineData("SELECT (SELECT 1 FROM choices u, (SELECT u.id FROM candidates v) d) FROM candidates t GROUP BY t.id")]
    public async Task Execute_DerivedSiblingInChild_ShouldRequireLateral(string sql)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("lateral-child");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var result = await ExecuteAsync(session, sql);

        // Assert
        result.Diagnostics.ShouldNotBeNull().ShouldContain(d => d.Code == "COHDBL001" && d.Message!.Contains("LATERAL", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: derived sibling diagnostics distinguish grouped ancestors and outward bindings")]
    public async Task Execute_GroupedDerivedSibling_ShouldDistinguishLateralFromOuterAndUnknown()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("lateral-grouped");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (g INT)");
        await ExecuteAsync(session, "CREATE TABLE u (k INT)");
        await ExecuteAsync(session, "CREATE TABLE v (n INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (2)");
        await ExecuteAsync(session, "INSERT INTO v VALUES (3)");

        // Act / Assert
        var lateral = await ExecuteAsync(session, "SELECT (SELECT 1 FROM u, (SELECT k FROM v) d) FROM t GROUP BY t.g");
        lateral.Diagnostics.ShouldNotBeNull().ShouldContain(d => d.Code == "COHDBL001" && d.Message!.Contains("LATERAL", StringComparison.Ordinal));
        var unknown = await Should.ThrowAsync<SqlPlanningException>(() => RowsAsync(session,
            "SELECT (SELECT 1 FROM u, (SELECT missing FROM v) d) FROM t GROUP BY t.g"));
        unknown.Diagnostics.ShouldContain(d => d.Code == "COHSQLE015");
        (await RowsAsync(session, "SELECT (SELECT d.g FROM u, (SELECT u.g FROM v) d) FROM t u GROUP BY u.g"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: local qualifier shadowing forbids fallback for a missing local column")]
    public async Task Execute_ShadowedQualifierMissingColumn_ShouldReportLocalUnknownColumn()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("shadowed-column");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE sales (id INT, amount INT)");
        await ExecuteAsync(session, "CREATE TABLE labels (k INT)");

        // Act
        var error = await Should.ThrowAsync<SqlPlanningException>(() => RowsAsync(session,
            "SELECT s.id FROM sales s WHERE EXISTS (SELECT 1 FROM labels s WHERE s.amount = 4)"));

        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "COHSQLE015" && d.Message!.Contains("s.amount", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: self-referencing DML reads the complete pre-statement snapshot")]
    [InlineData("DELETE FROM t WHERE NOT EXISTS (SELECT 1 FROM t p WHERE p.id = t.id - 1)", 1, new[] { 2, 3 })]
    [InlineData("UPDATE t SET id = id + 10 WHERE EXISTS (SELECT 1 FROM t n WHERE n.id = t.id + 1)", 2, new[] { 3, 11, 12 })]
    public async Task Execute_SelfReferencingCorrelatedDml_ShouldAvoidHalloween(string sql, int affected, int[] expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("snapshot-dml");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3)");

        // Act
        var result = await ExecuteAsync(session, sql);

        // Assert
        result.AffectedCount.ShouldBe(affected);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(expected.Cast<object?>());
    }
}
