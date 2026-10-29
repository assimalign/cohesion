using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUnionExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: malformed public AST composites refuse without bypassing planning")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_MalformedCompositeAst_ShouldRefuseAsync(int form)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-malformed", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var leaf = GraphQueryRequest.FromGql("RETURN 1 AS x").Statement.GqlExpression;
        var composite = new GqlCompositeQuery(form == 0 ? null! : leaf,
            form == 1 ? (GqlCompositeOperator)99 : GqlCompositeOperator.Union, leaf);
        var query = form switch
        {
            2 => new GqlQueryExpression(composite) { CatalogSurface = GqlCatalogSurface.Labels },
            3 => new GqlQueryExpression(composite) { Limit = new GqlLiteralExpression(1L) },
            _ => new GqlQueryExpression(composite),
        };
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: cancellation reaches the first and subsequent operands and releases grants")]
    [InlineData(1)]
    [InlineData(7)]
    public async Task ExecuteAsync_CancelAtRecordedWork_ShouldStopOperandAsync(int cancelAt)
    {
        // Arrange: the first operand consumes six units; seven enters the second operand.
        await using var engine = GraphDatabaseEngine.Create("union-cancel", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var budget = new GraphPlanExecutor.ExpansionBudget
        { OnConsume = work => { if (work == cancelAt) { cancellation.Cancel(); } } };
        var query = GraphQueryRequest.FromGql("FOR x IN [1,2,3] RETURN x UNION ALL FOR x IN [4,5,6] RETURN x");
        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await database.RunAsync(session, operation =>
        {
            operation.QueryBudget = budget;
            return GraphPlanExecutor.ExecuteAsync(database, operation, query.Statement, null, cancellation.Token);
        }, cancellation.Token));
        // Assert
        budget.Physical.ShouldBe(cancelAt);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        (await GraphExistsTests.RowsAsync(session, "RETURN 1"))[0][0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: all operands use the statement snapshot across a later commit")]
    public async Task ExecuteAsync_CommitAfterStatementStart_ShouldShareSnapshotAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-snapshot", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N)", cancellationToken: CancellationToken.None);
        var query = GraphQueryRequest.FromGql("MATCH (n:N) RETURN count(*) AS c UNION ALL MATCH (n:N) RETURN count(*) AS c");
        // Act: RunAsync has already captured the reader's snapshot when the writer commits.
        await using var result = (DatabaseResultSet)await database.RunAsync(reader, async operation =>
        {
            await writer.ExecuteAsync("INSERT (:N)", cancellationToken: CancellationToken.None);
            return await GraphPlanExecutor.ExecuteAsync(database, operation, query.Statement, null, CancellationToken.None);
        }, CancellationToken.None);
        var counts = new List<long>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { counts.Add(row.GetInt64(0)); }
        // Assert
        counts.ShouldBe(new long[] { 1, 1 });
        (await GraphExistsTests.RowsAsync(reader, "MATCH (n:N) RETURN count(*)"))[0][0].ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: first operand metadata and typed path delivery are preserved")]
    public async Task ExecuteAsync_CompositeResult_ShouldUseFirstMetadataAndDeliverPathsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-results", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N)-[:T]->(:N)", cancellationToken: CancellationToken.None);
        // Act
        await using var ordinary = (DatabaseResultSet)await session.ExecuteAsync("RETURN count(*) AS c", cancellationToken: CancellationToken.None);
        await using var composite = (DatabaseResultSet)await session.ExecuteAsync("RETURN count(*) AS c UNION ALL RETURN CAST(2 AS FLOAT64) AS c", cancellationToken: CancellationToken.None);
        var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p=()-[:T]->() RETURN p UNION MATCH p=()-[:T]->() RETURN p"), CancellationToken.None);
        // Assert
        composite.Columns.Select(column => (column.Name, column.Type, column.IsNullable)).ShouldBe(ordinary.Columns.Select(column => (column.Name, column.Type, column.IsNullable)));
        paths.Paths.ShouldHaveSingleItem().Relationships.ShouldHaveSingleItem();
    }
}
