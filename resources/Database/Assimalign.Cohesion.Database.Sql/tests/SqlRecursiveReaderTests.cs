using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlRecursiveReaderTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive MERGE readers: ON and USING retain the same CTE rows (#1719)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_MergeOnAndSourceReaders_ShouldUpdateEveryTarget(bool indirect)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-merge-readers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string source = indirect ? "d" : "c";
        string definitions = "WITH c AS (SELECT n FROM nums)" + (indirect ? ", d AS (SELECT n FROM c)" : "");

        // Act
        var result = await session.ExecuteAsync(definitions + $" MERGE INTO t USING {source} ON t.id={source}.n " +
            "AND EXISTS(SELECT 1 FROM c WHERE n=3) WHEN MATCHED THEN UPDATE SET v=1", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(3);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT id,v FROM t ORDER BY id")).ShouldBe(["1,1", "2,1", "3,1"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive MERGE readers: action queries retain history for repeated candidates (#1719)")]
    [InlineData("WHEN MATCHED AND EXISTS(SELECT 1 FROM c WHERE n=t.id) THEN UPDATE SET v=1")]
    [InlineData("WHEN MATCHED THEN UPDATE SET v=(SELECT n FROM c WHERE n=t.id)")]
    [InlineData("WHEN MATCHED AND EXISTS(SELECT 1 FROM c WHERE n=3) THEN UPDATE SET v=(SELECT n FROM c WHERE n=t.id)")]
    public async Task Execute_MergeActionReaders_ShouldPreserveEveryCandidate(string actions)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-merge-actions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await session.ExecuteAsync("WITH c AS (SELECT n FROM nums) MERGE INTO t USING c ON t.id=c.n " + actions,
            cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(3);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT v FROM t ORDER BY id")).ShouldBe(actions.Contains("SET v=1", StringComparison.Ordinal)
            ? ["1", "1", "1"] : ["1", "2", "3"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive CTE readers: DML and set shapes preserve both scans (#1719)")]
    [InlineData("INSERT INTO t SELECT n,1 FROM c WHERE EXISTS(SELECT 1 FROM c WHERE n=3)", "SELECT COUNT(*) FROM t", "6")]
    [InlineData("UPDATE t SET v=c.n FROM c WHERE t.id=c.n AND EXISTS(SELECT 1 FROM c WHERE n=3)", "SELECT v FROM t ORDER BY id", "1|2|3")]
    [InlineData("DELETE FROM t USING c WHERE t.id=c.n AND EXISTS(SELECT 1 FROM c WHERE n=3)", "SELECT COUNT(*) FROM t", "0")]
    [InlineData("SELECT n FROM c UNION ALL SELECT n FROM c", "", "1|2|3|1|2|3")]
    [InlineData("SELECT n FROM c UNION SELECT n FROM c", "", "1|2|3")]
    [InlineData("SELECT n FROM c INTERSECT SELECT n FROM c", "", "1|2|3")]
    [InlineData("SELECT n FROM c INTERSECT ALL SELECT n FROM c", "", "1|2|3")]
    [InlineData("SELECT n FROM c EXCEPT SELECT n FROM c WHERE n=2", "", "1|3")]
    [InlineData("SELECT n FROM c EXCEPT ALL SELECT n FROM c WHERE n=2", "", "1|3")]
    public async Task Execute_TwoCteReads_ShouldPreserveStatementResults(string statement, string verification, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-reader-shapes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string sql = "WITH c AS (SELECT n FROM nums) " + statement;

        // Act
        string[] rows;
        if (verification.Length == 0) { rows = await RowsAsync(session, sql); }
        else
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            rows = await RowsAsync(session, verification);
        }

        // Assert
        string.Join('|', rows).ShouldBe(expected);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive view readers: CTAS and views over views keep separate scopes (#1719)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_NestedViewsAndCtas_ShouldPreserveBothReaders(bool ctas)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-view-shapes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE VIEW outer_nums AS SELECT n FROM nums", cancellationToken: CancellationToken.None);
        const string query = "WITH c AS (SELECT n FROM outer_nums) SELECT n FROM c WHERE EXISTS(SELECT 1 FROM c WHERE n=3)";

        // Act
        if (ctas)
        {
            await session.ExecuteAsync("CREATE TABLE copied AS " + query, cancellationToken: CancellationToken.None);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        var rows = await RowsAsync(session, ctas ? "SELECT n FROM copied ORDER BY n" : query);

        // Assert
        rows.ShouldBe(["1", "2", "3"]);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive MERGE walk: source and action queries are rescans (#1719)")]
    [InlineData("WHEN MATCHED AND EXISTS(SELECT 1 FROM c WHERE n=3) THEN UPDATE SET v=1")]
    [InlineData("WHEN MATCHED THEN UPDATE SET v=(SELECT n FROM c WHERE n=t.id)")]
    [InlineData("WHEN NOT MATCHED AND EXISTS(SELECT 1 FROM c WHERE n=3) THEN INSERT(id,v) VALUES(c.n,1)")]
    public async Task RecursiveReaderCounts_MergeQueries_ShouldMarkSourceAndActionsRescanned(string actions)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-merge-counts", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "WITH c AS (SELECT n FROM nums), d AS (SELECT n FROM nums) MERGE INTO t USING c ON t.id=c.n " + actions.Replace("FROM c WHERE", "FROM d WHERE", StringComparison.Ordinal))
            .Statement.SqlExpression).ShouldBeOfType<SqlWithPlan>();

        // Act
        var counts = ReaderCounts(null!, plan);

        // Assert
        foreach (var relation in plan.Relations)
        {
            counts.ShouldContainKey(relation);
            counts[relation].ShouldBe(-1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive reader walk: unknown nodes retain every CTE in scope (#1719)")]
    public async Task RecursiveReaderCounts_UnknownPlan_ShouldMarkCteDependenciesRescanned()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-unknown-reader", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM c WHERE n<3), d AS (SELECT n FROM c) SELECT n FROM d")
            .Statement.SqlExpression).ShouldBeOfType<SqlWithPlan>();
        var unknown = plan with { Input = new SqlUnknownReaderPlan(plan.Input) };

        // Act
        var counts = ReaderCounts(null!, unknown);

        // Assert
        foreach (var relation in plan.Relations)
        {
            counts.ShouldContainKey(relation);
            counts[relation].ShouldBe(-1);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive reader allowance: an uncounted reader fails loudly before allocation (#1719)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenReader_ExhaustedAllowance_ShouldReportInternalDiagnostic(bool closeFirst)
    {
        // Arrange
        var metrics = new SqlStatementMetrics();
        using var memory = new SqlWindowMemory(metrics, 32_768, statementBudgetOnly: true);
        var relation = new SqlDerivedRelation("c", new SqlConstantPlan([], null, [], null, null, false, null), []) { IsCommonTable = true };
        object table = CreateTable(null!, relation, null, default, CancellationToken.None, memory, 1);
        object? reader = OpenReader(table);
        if (closeFirst) { await CloseReaderAsync(table, reader); }
        long retained = metrics.HashMemoryBytes;
        try
        {
            // Act
            var failure = Should.Throw<SqlExecutionException>(() => OpenReader(table));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05090");
            failure.Message.ShouldNotContain("CS05090", Case.Sensitive);
            metrics.HashMemoryBytes.ShouldBe(retained);
        }
        finally { if (!closeFirst) { await CloseReaderAsync(table, reader); } }
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,0),(2,0),(3,0)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW nums AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3) SELECT n FROM r",
            cancellationToken: CancellationToken.None);
    }

    private static async Task<string[]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(string.Join(',', Enumerable.Range(0, row.FieldCount).Select(row.GetValue))); }
        return [.. rows];
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "RecursiveReaderCounts")]
    private static extern Dictionary<SqlDerivedRelation, int> ReaderCounts(SqlPlanExecutor executor, SqlPlan plan);

    private const string tableType = "Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+RecursiveTable, Assimalign.Cohesion.Database.Sql";
    private const string readerType = "Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+RecursiveReader, Assimalign.Cohesion.Database.Sql";

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(tableType)]
    private static extern object CreateTable(SqlPlanExecutor executor, SqlDerivedRelation relation, SqlSubqueryValues? frame,
        SqlStatementContext statement, CancellationToken cancellationToken, SqlWindowMemory memory, int readers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OpenReader")]
    [return: UnsafeAccessorType(readerType)]
    private static extern object? OpenReader([UnsafeAccessorType(tableType)] object table);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CloseReaderAsync")]
    private static extern ValueTask CloseReaderAsync([UnsafeAccessorType(tableType)] object table, [UnsafeAccessorType(readerType)] object? reader);
}
