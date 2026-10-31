using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJoinSearchSafetyTests
{
    private readonly ITestOutputHelper _output;

    public SqlJoinSearchSafetyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: concurrent small hash builds preserve the old memory grant (#1629)")]
    public async Task ExecuteAsync_AsymmetricHashInputs_ShouldBuildSmallSides()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-small-build", new() { StatementMemoryBudget = 20_000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await InsertAsync(session, table, table == "b" ? 500 : 32);
        }

        // Act
        int rows = 0;
        try
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT a.id,b.id,c.id FROM a JOIN b ON a.k=b.k JOIN c ON a.k=c.k", cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        }
        finally { Evidence(session.LastStatementMetrics!, rows); }

        // Assert
        rows.ShouldBe(32);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(8240);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: expression hash keys retain linear candidate work (#1629)")]
    public async Task ExecuteAsync_ExpressionKey_ShouldRetainHashJoin()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-expression-key", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE a(id INT PRIMARY KEY,k TEXT)", "CREATE TABLE b(id INT PRIMARY KEY,n INT)",
            "CREATE TABLE c(id INT PRIMARY KEY)", "INSERT INTO c VALUES(1)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("INSERT INTO a VALUES" + string.Join(',', Enumerable.Range(1, 80).Select(i => $"({i},'{new string('x', i)}')")), cancellationToken: CancellationToken.None);
        await InsertAsync(session, "b", 80);

        // Act
        int rows = 0;
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "SELECT a.id,b.id,c.id FROM a JOIN (b CROSS JOIN c) ON CHAR_LENGTH(a.k)=b.n", cancellationToken: CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        var metrics = session.LastStatementMetrics!;
        Evidence(metrics, rows);

        // Assert
        rows.ShouldBe(80);
        metrics.JoinCandidatePairs.ShouldBeLessThanOrEqualTo(160);
        metrics.JoinAccessPaths.ShouldContain("join-hash");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: independent DML sources stream before deduplication and multiple-match refusal (#1629)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_IndependentDmlSources_ShouldPreserveStreamingMemory(bool update, bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-dml-stream", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "a", "b" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,{(table == "t" ? "v" : "k")} INT)", cancellationToken: CancellationToken.None);
            await InsertAsync(session, table, table == "t" ? 20_000 : indexed && table == "a" ? 1 : 20);
        }
        if (indexed) { await session.ExecuteAsync("CREATE UNIQUE INDEX key_b ON b(k)", cancellationToken: CancellationToken.None); }
        string sql = update ? "UPDATE t SET v=a.k FROM a,b WHERE a.k=b.k" : indexed
            ? "DELETE FROM t USING a JOIN b ON a.id<=b.id WHERE a.k=b.k" : "DELETE FROM t USING a,b WHERE a.k=b.k";
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var source = update ? ((SqlUpdatePlan)written).Source! : ((SqlDeletePlan)written).Source!;
        var associations = source.Associations;
        // Reproduce pre-search hash eligibility; the non-equality ON retains its buffered loop.
        var steps = associations.Steps.Select(step => step with
        { Hash = SqlPlanner.HashKeys(step.Condition, associations.Bindings, associations.Columns, step.Binding) }).ToArray();
        var oldSource = source with { Associations = associations with { Steps = steps } };
        SqlPlan old = update ? ((SqlUpdatePlan)written) with { Source = oldSource } : ((SqlDeletePlan)written) with { Source = oldSource };
        long oldPeak = 0;
        for (int round = 0; round < 2; round++)
        {
            var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            var context = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            try
            {
                // Act
                var selected = round == 0 ? old : executor.Cost(written, context, CancellationToken.None);
                if (update)
                {
                    var failure = await Should.ThrowAsync<DatabaseException>(async () => await executor.ExecuteAsync(selected, context, CancellationToken.None));
                    _output.WriteLine($"round={round}; code={failure.Diagnostics.Single().Code}");
                    failure.Diagnostics.Single().Code.ShouldBe("CS05068");
                }
                else
                {
                    var result = await executor.ExecuteAsync(selected, context, CancellationToken.None);
                    result.AffectedCount.ShouldBe(20_000);
                }
            }
            finally
            {
                Evidence(context.Metrics, update ? 0 : 20_000);
                await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
            }
            // Assert
            if (round == 0) { oldPeak = context.Metrics.PeakHashMemoryBytes; }
            else { context.Metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(oldPeak); }
            context.Metrics.HashMemoryBytes.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: MERGE rejects association WHERE and target-only source predicates (#1629)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cost_InvalidMergeAssociation_ShouldAssertInvariants(int variant)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-merge-invariant", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "a", "b" })
        { await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None); }
        var merge = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "MERGE INTO t USING a JOIN b ON a.k=b.k ON t.id=a.id WHEN MATCHED THEN DELETE").Statement.SqlExpression).ShouldBeOfType<SqlMergePlan>();
        var join = merge.Source.Associations;
        var predicate = new SqlBoundIsNull(new SqlBoundColumn(variant == 1 ? join.Bindings[1].Offset : 0), false);
        if (variant < 2) { join = join with { Where = predicate }; }
        else
        {
            var root = join.Tree!;
            var child = root.Right!;
            var step = child.Step! with { Condition = predicate };
            join = join with { Tree = root with { Right = child with { Step = step } }, Steps = [step, root.Step!] };
        }
        var invalid = merge with { Source = merge.Source with { Associations = join } };
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act / Assert
            Should.Throw<UnreachableException>(() => new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(invalid, new(transaction, database.Coordinator), CancellationToken.None));
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    private void Evidence(SqlStatementMetrics metrics, int rows)
        => _output.WriteLine($"rows={rows}; pairs={metrics.JoinCandidatePairs}; examined={metrics.RecordsExamined}; peak={metrics.PeakHashMemoryBytes}; paths={string.Join(',', metrics.JoinAccessPaths ?? [])}");

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: volatile calls and nextval retain ON/WHERE counts (#1629)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_VolatilePredicate_ShouldKeepEvaluationCounts(bool where, bool sequence)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("search-volatile-count");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => { calls++; return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE a(id INT PRIMARY KEY,k INT)", "CREATE TABLE b(id INT PRIMARY KEY,k INT)",
            "CREATE TABLE c(id INT PRIMARY KEY,k INT)", "CREATE SEQUENCE s", "INSERT INTO a VALUES(1,1),(2,2)",
            "INSERT INTO b VALUES(1,1),(2,1),(3,2),(4,2),(5,2)", "INSERT INTO c VALUES(1,1),(2,1),(3,1),(4,2)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        string predicate = sequence ? "nextval('s')>0" : "observe(a.id)>0";
        string query = "SELECT a.id,b.id,c.id FROM a JOIN b ON a.k=b.k" + (where ? "" : " AND " + predicate) +
            " JOIN c ON b.k=c.k" + (where ? " WHERE " + predicate : "");

        // Act
        int rows = 0;
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None))
        { await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; } }
        if (sequence)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT currval('s')", cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { calls = Convert.ToInt32(row.GetValue(0)); }
        }

        // Assert
        rows.ShouldBe(9);
        calls.ShouldBe(where ? 9 : 5);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: noncontiguous masks prove expression hash membership (#1629)")]
    public async Task HashKeys_ExpressionWithNoncontiguousMask_ShouldRetainExpressionVectors()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-expression-mask", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE a(id INT,k TEXT)", "CREATE TABLE b(id INT,n INT)", "CREATE TABLE c(id INT)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT a.id FROM a JOIN (b CROSS JOIN c) ON CHAR_LENGTH(a.k)=b.n").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var condition = plan.Tree!.Step!.Condition;

        // Act / Assert
        SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, 0, fullFrame: true, innerMask: 5)
            .ShouldNotBeNull().InnerExpressions.ShouldNotBeNull();
        SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, 1, fullFrame: true, innerMask: 2)
            .ShouldNotBeNull().OuterExpressions.ShouldNotBeNull();
        SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, 0, fullFrame: true, innerMask: 3).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: unproven hash builds keep the old budget-free loop plan (#1629)")]
    public async Task ExecuteAsync_UnprovenHashBuilds_ShouldKeepOldLoopPlan()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-bushy-width", new() { StatementMemoryBudget = 5000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c", "d" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT,v INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(1, 16).Select(i => $"({i},{i},{i})")), cancellationToken: CancellationToken.None);
        }

        // Act
        int rows = 0;
        try
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT a.id,b.id,c.id,d.id FROM a,b,c,d WHERE a.k=c.k AND b.k=d.k AND a.v=b.v", cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        }
        finally { Evidence(session.LastStatementMetrics!, rows); }

        // Assert
        rows.ShouldBe(16);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: an empty old prefix avoids activating later hash builds (#1629)")]
    public async Task ExecuteAsync_EmptyJoinPrefix_ShouldPreserveBuildActivation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-build-prefix", new() { StatementMemoryBudget = 11_032 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(1, table == "c" ? 32 : 80)
                .Select(i => $"({i},{(table == "b" ? i + 1000 : i)})")), cancellationToken: CancellationToken.None);
        }

        // Act
        int rows = 0;
        try
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT a.id,b.id,c.id FROM a JOIN b ON a.k=b.k JOIN c ON a.k=c.k", cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        }
        finally { Evidence(session.LastStatementMetrics!, rows); }

        // Assert
        rows.ShouldBe(0);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(11_032);
    }

    private static async Task InsertAsync(SqlDatabaseSession session, string table, int count)
    {
        for (int start = 1; start <= count; start += 1000)
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(1000, count - start + 1)).Select(i => $"({i},{i})")), cancellationToken: CancellationToken.None);
        }
    }
}
