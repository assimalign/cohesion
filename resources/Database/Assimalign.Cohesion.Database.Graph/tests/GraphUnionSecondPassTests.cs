using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUnionSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: ordinary NEXT imports named paths into every operand")]
    [InlineData("RETURN p NEXT RETURN p AS x UNION ALL RETURN p AS x")]
    [InlineData("RETURN p NEXT (RETURN p AS x UNION ALL RETURN p AS x)")]
    [InlineData("RETURN p NEXT (RETURN p NEXT RETURN p AS x UNION ALL RETURN p AS x)")]
    [InlineData("WITH p RETURN p NEXT RETURN p AS x UNION ALL RETURN p AS x")]
    public async Task ExecuteAsync_PathInput_ShouldRetainKindAsync(string tail)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-path-input", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N)-[:T]->(:N)", cancellationToken: CancellationToken.None);
        var control = (GraphPathsQueryResult)await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql("MATCH p=()-[:T]->() RETURN p NEXT RETURN p AS x"), CancellationToken.None);
        // Act
        var result = (GraphPathsQueryResult)await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql("MATCH p=()-[:T]->() " + tail), CancellationToken.None);
        // Assert
        control.Paths.ShouldHaveSingleItem();
        result.Paths.Count.ShouldBe(2);
        result.Paths.ShouldAllBe(path => path.Relationships.Count == 1 && path.Nodes.Count == 2);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: path member access refuses during planning even without rows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PathMemberInput_ShouldRefuseBeforeExpansionAsync(bool seeded)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-path-member", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (seeded) { await session.ExecuteAsync("INSERT (:N)-[:T]->(:N)", cancellationToken: CancellationToken.None); }
        // Act / Assert
        foreach (string tail in new[] { "RETURN p.k AS v", "RETURN p.k AS v UNION ALL RETURN 1 AS v" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "MATCH p=()-[:T]->() RETURN p NEXT " + tail, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
            session.LastStatementMetrics!.Physical.ShouldBe(0);
            session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
            session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: quoted display names retain decoded binding names")]
    [InlineData("RETURN \"a\" * 10 AS y", "10;20")]
    [InlineData("RETURN *", "1;2")]
    [InlineData("RETURN \"a\" AS x UNION ALL RETURN \"a\" AS x", "1;2;1;2")]
    public async Task ExecuteAsync_QuotedUnionOutput_ShouldBindLaterStagesAsync(string tail, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-quoted-output", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session,
            "LET \"a\" = 1 RETURN \"a\" UNION ALL LET \"a\" = 2 RETURN \"a\" NEXT " + tail);
        // Assert
        string.Join(';', rows.Select(row => GraphExistsTests.Text(row[0]))).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: generated names needing quotes remain usable after NEXT")]
    public async Task ExecuteAsync_GeneratedQuotedName_ShouldBindLaterStagesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-generated-output", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string name = "a b";
        GqlQueryExpression Operand(long value) => new(new GqlStage[]
        {
            new GqlLetStage(new[] { new GqlProjection(new GqlLiteralExpression(value), name) }),
            new GqlReturnStage(new[] { new GqlProjection(new GqlVariableExpression(name)) }),
        });
        var query = new GqlQueryExpression(new GqlStage[]
        {
            new GqlCompositeStage(new GqlCompositeQuery(Operand(1), GqlCompositeOperator.UnionAll, Operand(2))),
            new GqlNextStage(), new GqlReturnStage([]) { ReturnStar = true },
        });
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        // Assert
        values.ShouldBe(new object?[] { 1L, 2L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: a final NEXT composite reconciles nullable metadata")]
    [InlineData("UNION ALL", 2, false)]
    [InlineData("UNION", 1, false)]
    [InlineData("UNION ALL", 2, true)]
    [InlineData("UNION", 1, true)]
    public async Task ExecuteAsync_NullFinalUnion_ShouldReconcileMetadataAsync(string operation, int count, bool computed)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-final-null", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string projection = computed ? "k + null" : "k AS x";
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "RETURN null AS k NEXT RETURN " + projection + " " + operation + " RETURN " + projection, cancellationToken: CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        // Assert
        values.Count.ShouldBe(count);
        values.ShouldAllBe(value => value == null);
        result.Columns[0].Type.ShouldBe(DatabaseType.Null);
        result.Columns[0].IsNullable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: a final NEXT composite widens metadata from runtime values")]
    public async Task ExecuteAsync_DynamicFinalUnion_ShouldReconcileMetadataAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-final-types", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1}),(:N {k:2.5}),(:N)", cancellationToken: CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "MATCH (n:N) RETURN n.k AS k NEXT RETURN k AS x UNION ALL RETURN k AS x", cancellationToken: CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        // Assert
        values.Count.ShouldBe(6);
        values.Count(value => value == null).ShouldBe(2);
        result.Columns[0].Type.ShouldBe(DatabaseType.Float64);
        result.Columns[0].IsNullable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - NEXT: an empty final star refuses before writer acquisition or mutation")]
    public async Task ExecuteAsync_WriteThenEmptyStar_ShouldRefuseWithoutWriteAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("next-empty-star", new());
        var database = await engine.CreateDatabaseAsync("g", cancellation.Token);
        await using var writer = await database.CreateSessionAsync(cancellation.Token);
        await using var session = await database.CreateSessionAsync(cancellation.Token);
        await using var transaction = await writer.BeginTransactionAsync(cancellation.Token);
        await writer.ExecuteAsync("INSERT (:Held)", cancellationToken: cancellation.Token);
        var request = GraphQueryRequest.FromGql("INSERT (:A) NEXT RETURN *");
        request.Statement.Diagnostics.ShouldBeEmpty();
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            request, cancellation.Token));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        session.LastStatementMetrics!.Physical.ShouldBe(0);
        await transaction.RollbackAsync(cancellation.Token);
        (await GraphExistsTests.RowsAsync(session, "MATCH (n) RETURN count(*)"))[0][0].ShouldBe(0L);
        (await GraphExistsTests.RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Statement: an unexpected missing key becomes a coded internal failure")]
    public async Task ExecuteAsync_UnexpectedMissingKey_ShouldCodeFailureAndReleaseOperationAsync()
    {
        // Arrange: inject a statement-boundary fault independently of the repaired union cursor.
        await using var engine = GraphDatabaseEngine.Create("statement-missing-key", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var cause = new KeyNotFoundException("missing internal binding");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await database.RunAsync<DatabaseResult>(
            session, _ => throw cause, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05031");
        error.InnerException.ShouldBeSameAs(cause);
        error.Message.ShouldNotContain(cause.Message, Case.Sensitive);
        (await GraphExistsTests.RowsAsync(session, "RETURN 1"))[0][0].ShouldBe(1L);
    }
}
