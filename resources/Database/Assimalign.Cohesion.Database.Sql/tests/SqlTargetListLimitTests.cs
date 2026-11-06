using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTargetListLimitTests
{
    private readonly ITestOutputHelper _output;

    public SqlTargetListLimitTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Target list backing arrays never grow past 1664 slots (#1792)")]
    public async Task Bind_StarBoundary_ShouldCapBackingCapacity()
    {
        // Arrange: 16 stars over 104 columns previously grew a 2,048-slot backing array.
        await using var engine = SqlDatabaseEngine.Create("target-capacity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(" + string.Join(",", Enumerable.Range(0, 104).Select(i => "c" + i + " INT")) + ")", cancellationToken: CancellationToken.None);
        var expression = SqlQueryRequest.FromSql("SELECT " + string.Join(",", Enumerable.Repeat("*", 16)) + " FROM t").Statement.SqlExpression;
        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(expression).ShouldBeOfType<SqlSelectPlan>();
        // Assert
        var projections = plan.Projections.ShouldBeOfType<List<SqlProjection>>();
        projections.Count.ShouldBe(1664);
        projections.Capacity.ShouldBeLessThanOrEqualTo(1664);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Target lists: recursive, flat, walker, view and RETURNING refuse within an allocation ceiling (#1792)")]
    [InlineData("cte")]
    [InlineData("flat")]
    [InlineData("plain")]
    [InlineData("walker")]
    [InlineData("view")]
    [InlineData("returning")]
    public async Task Bind_TooWideTarget_ShouldRefuseWithinAllocationCeiling(string shape)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("target-bound", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string definitions = string.Join(",", Enumerable.Range(0, 100).Select(i => "c" + i + " INT"));
        await session.ExecuteAsync("CREATE TABLE t(" + definitions + ")", cancellationToken: CancellationToken.None);
        string flat = string.Join(",", Enumerable.Repeat(shape == "plain" ? "*" : "t.*", 1000));
        string chain = "WITH c0 AS (SELECT 1 AS a)";
        for (int i = 1; i <= 30; i++) { chain += $",c{i} AS (SELECT c{i - 1}.*,c{i - 1}.* FROM c{i - 1})"; }
        string nested = "SELECT c0 AS a FROM t";
        for (int i = 0; i < 30; i++) { nested = "SELECT d.*,d.* FROM (" + nested + ") d"; }
        string sql = shape switch
        {
            "cte" => chain + " SELECT 1 FROM c30",
            "walker" => "SELECT (SELECT SUM(a) FROM (" + nested + ") d) FROM t",
            "view" => "CREATE VIEW v AS SELECT " + flat + " FROM t",
            "returning" => "DELETE FROM t RETURNING " + string.Join(",", Enumerable.Repeat("*", 1000)),
            _ => "SELECT " + flat + " FROM t",
        };
        var expression = SqlQueryRequest.FromSql(sql).Statement.SqlExpression;
        var planner = new SqlPlanner(database.Catalog, null);
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Should.Throw<DatabaseException>(() => planner.Plan(expression));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS05097");
        failure.Message.ShouldContain("1664");
        failure.Diagnostics.Single(d => d.Code == "CS05097").Start.ShouldNotBeNull();
        allocated.ShouldBeLessThan(32L * 1024 * 1024);
        _output.WriteLine($"{shape}: allocated {allocated} bytes; {failure.Message}");
        await using var usable = (await session.ExecuteAsync("SELECT 1", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        usable.Columns.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Target lists: 1664 succeeds and 1665 refuses for stars and explicit entries (#1792)")]
    [InlineData("*", 1664)] [InlineData("*", 1665)]
    [InlineData("t.*", 1664)] [InlineData("t.*", 1665)]
    [InlineData("a", 1664)] [InlineData("a", 1665)]
    [InlineData("1", 1664)] [InlineData("1", 1665)]
    public async Task Execute_TargetListBoundary_ShouldCountExpandedEntries(string target, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("target-boundary", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(a INT)", cancellationToken: CancellationToken.None);
        string sql = "SELECT " + string.Join(",", Enumerable.Repeat(target, count)) + (target == "1" ? "" : " FROM t");
        // Act / Assert
        if (count == 1664)
        {
            await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns.Count.ShouldBe(count);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await using var unexpected = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)) as DatabaseResultSet;
            });
            failure.Diagnostics.ShouldContain(d => d.Code == "CS05097");
            failure.Message.ShouldContain("1665");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Catalog width: 193 minimal printable columns fit and 194 exceed one record (#1792)")]
    public async Task CreateTable_MaximumCatalogWidth_ShouldStayBelowTargetLimit()
    {
        // Arrange: shortest distinct printable ASCII names, ignoring identifier case.
        await using var engine = SqlDatabaseEngine.Create("catalog-width", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE SCHEMA s", cancellationToken: CancellationToken.None);
        var characters = Enumerable.Range(33, 94).Select(i => ((char)i).ToString()).ToArray();
        var names = characters.Concat(characters.SelectMany(a => characters.Select(b => a + b)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(194).Select(n => "\"" + n.Replace("\"", "\"\"") + "\" INT").ToArray();
        // Act / Assert
        await session.ExecuteAsync("CREATE TABLE s.t(" + string.Join(",", names.Take(193)) + ")", cancellationToken: CancellationToken.None);
        database.Catalog.Tables.Single(t => t.Name == "t").Columns.Count.ShouldBe(193);
        await session.ExecuteAsync("DROP TABLE s.t", cancellationToken: CancellationToken.None);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "CREATE TABLE s.t(" + string.Join(",", names) + ")", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS05042");
        failure.Message.ShouldContain("8132");
        failure.Message.ShouldContain("8092");
    }
}
