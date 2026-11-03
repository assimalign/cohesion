using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

using static Assimalign.Cohesion.Database.Sql.Tests.SqlPartialIndexTests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlIndexDefaultOrderTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Index defaults: nullable orderings preserve sorted results and stop at LIMIT (#1787)")]
    [InlineData("", "", " DESC")]
    [InlineData(" DESC", " DESC", "")]
    [InlineData(" NULLS LAST", " NULLS LAST", " DESC NULLS FIRST")]
    [InlineData(" DESC NULLS FIRST", " DESC NULLS FIRST", " NULLS LAST")]
    public async Task ExecuteAsync_NullableIndex_ShouldMatchOnlyCompatibleOrders(string indexOrder, string forward, string backward)
    {
        // Arrange: capture every result before an index can remove its sort. A single
        // NULL and distinct non-NULL values make exact row order deterministic.
        await using var engine = SqlDatabaseEngine.Create("index-defaults", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(a INT)");
        await Execute(session, "INSERT INTO t VALUES(NULL)," + string.Join(',', Enumerable.Range(0, 1023).Select(i => $"({1022 - i})")));
        string[] orders = ["", " ASC", " NULLS FIRST", " NULLS LAST", " DESC", " DESC NULLS LAST", " DESC NULLS FIRST"];
        string[] windows = ["", " LIMIT 10", " LIMIT 7 OFFSET 3", " LIMIT 0", " OFFSET 1020"];
        var queries = (from order in orders from window in windows select "SELECT a FROM t ORDER BY a" + order + window).ToArray();
        var before = new object?[queries.Length][][];
        for (int i = 0; i < queries.Length; i++) { before[i] = await Rows(session, queries[i]); }

        // Act / Assert: all 35 ORDER BY results stay identical after CREATE INDEX.
        await Execute(session, "CREATE INDEX ix ON t(a" + indexOrder + ")");
        for (int i = 0; i < queries.Length; i++)
        { (await Rows(session, queries[i])).ShouldBe(before[i], queries[i]); }
        foreach (var (order, reverse) in new[] { (forward, false), (backward, true) })
        {
            var explanation = await Rows(session, "EXPLAIN SELECT a FROM t ORDER BY a" + order + " LIMIT 10");
            explanation.ShouldContain(row => Equals(row[2], "OrderedIndexScan") &&
                (row[5] as string ?? "").Contains("sort_removed=true", StringComparison.Ordinal));
            await Rows(session, "SELECT a FROM t ORDER BY a" + order + " LIMIT 10");
            session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:" + (reverse ? "backward" : "forward"));
            session.LastStatementMetrics.RecordsExamined.ShouldBe(10);
            session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        }
        string incompatible = indexOrder is "" or " DESC" ? " NULLS LAST" : "";
        SqlOrderedIndexTests.Plan(database, "SELECT a FROM t ORDER BY a" + incompatible + " LIMIT 10")
            .Access.ShouldNotBeOfType<SqlOrderedIndexPath>();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Index defaults: primary and UNIQUE keys use zero; both system views expose only overrides (#1787)")]
    public async Task ExecuteAsync_DefaultAndExplicitKeys_ShouldReportOnlyNonDefaultPlacements()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("index-default-metadata", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,a INT,b INT,c INT,CONSTRAINT uq UNIQUE(a))");
        await Execute(session, "CREATE INDEX ix ON t(id,a DESC,b NULLS LAST,c DESC NULLS FIRST)");

        // Act / Assert
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var indexes = database.Catalog.GetIndexes(table.ObjectId);
        foreach (var index in indexes.Where(index => index.Name != "ix"))
        { index.KeyOrders.ShouldBe([SqlCatalogIndexKeyOrder.AscendingNullsFirst]); }
        foreach (string view in new[] { "INFORMATION_SCHEMA.INDEXES", "COHESION_SCHEMA.INDEXES" })
        {
            (await Rows(session, "SELECT DIRECTION,NULL_PLACEMENT FROM " + view + " WHERE INDEX_NAME='ix' ORDER BY ORDINAL_POSITION"))
                .ShouldBe(new object?[][] { [null, null], ["DESC", null], [null, "LAST"], ["DESC", "FIRST"] });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema v12: live catalog reconstruction plans zero operations for every key default (#1787)")]
    public async Task ApplySchemaAsync_DefaultOrders_ShouldReconstructWithoutRebuildingIndexes()
    {
        // Arrange: null and zero-filled vectors declare the same dialect default.
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new("t", "Tests:Row", [new("id", DatabaseType.Int32, false), new("a", DatabaseType.Int32, true)],
                new("pk_t", ["id"]),
                [new("uq", ["a"], true), new("plain", ["a"]), new("zero", ["a"], KeyOrders: [default]),
                 new("ix_desc", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.DescendingNullsLast]),
                 new("ix_last", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.AscendingNullsLast]),
                 new("ix_first", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.DescendingNullsFirst])], [])], []);
        await using var engine = SqlDatabaseEngine.Create("index-default-schema", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await database.ApplySchemaAsync(desired, CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Advance the catalog generation so Apply must snapshot live metadata and
        // invoke SqlSchemaMigrationPlanner instead of returning the recorded hash.
        await Execute(session, "CREATE TABLE unrelated(x INT)");
        int walks = database.SchemaProvisioner.CatalogWalkCount;

        // Act
        var result = await database.ApplySchemaAsync(desired, CancellationToken.None);
        await database.VerifySchemaAsync(desired, CancellationToken.None);

        // Assert
        database.SchemaProvisioner.CatalogWalkCount.ShouldBeGreaterThan(walks);
        database.SchemaProvisioner.LastPlan!.Operations.ShouldBeEmpty();
        result.OperationCount.ShouldBe(0);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.GetIndexes(table.ObjectId).Single(index => index.IsPrimaryKey)
            .KeyOrders.ShouldBe([SqlCatalogIndexKeyOrder.AscendingNullsFirst]);
    }
}
