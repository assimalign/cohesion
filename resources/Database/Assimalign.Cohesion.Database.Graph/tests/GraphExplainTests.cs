using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphExplainTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - EXPLAIN: anchor and every expansion follow the chosen graph plan")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_Match_ShouldDescribeAnchorAndExpansionsAsync(bool index)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-explain", new());
        var database = await engine.CreateDatabaseAsync("explain", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Person {name:'Ada'})-[:KNOWS]->(b:Person {name:'Bob'})-[:KNOWS]->(c:Person {name:'Cy'})", cancellationToken: CancellationToken.None);
        if (index) { await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None); }
        // Act
        var rows = await RowsAsync(session, "EXPLAIN MATCH (a:Person {name:'Ada'})-[:KNOWS]->(b)-[:KNOWS]->(c) RETURN a");
        // Assert
        rows.Count(row => Equals(row[2], "Expand")).ShouldBe(2);
        var anchor = rows[2];
        anchor[2].ShouldBe(index ? "IndexSeek" : "LabelScan");
        anchor[3].ShouldBe("Person");
        anchor[4].ShouldBe(index ? "by_name" : null);
        if (index) { anchor[5].ShouldBe("node=0; property=name; value='Ada'"); }
        rows[3][1].ShouldBe(2);
        rows[4][1].ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXPLAIN: writes change neither nodes nor relationships after commit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ExplainedMutations_ShouldLeaveGraphUnchangedAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-explain-writes", new());
        var database = await engine.CreateDatabaseAsync("writes", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Person {name:'Ada'})-[:KNOWS]->(b:Person {name:'Bob'})", cancellationToken: CancellationToken.None);
        var beforeNodes = await RowsAsync(session, "MATCH (n) RETURN n.name");
        var beforeEdges = await RowsAsync(session, "MATCH (a)-[r]->(b) RETURN a.name, b.name");
        var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        // Act
        foreach (string source in new[] { "INSERT (:Person {name:'Cy'})", "CREATE (:Person {name:'Dee'})-[:KNOWS]->(:Person {name:'Eve'})",
            "MATCH (a:Person) DELETE a", "MATCH (a:Person) DETACH DELETE a" })
        {
            var description = await RowsAsync(session, "EXPLAIN " + source);
            description.ShouldNotBeEmpty();
        }
        if (transaction is not null) { await transaction.CommitAsync(CancellationToken.None); await transaction.DisposeAsync(); }
        // Assert
        (await RowsAsync(session, "MATCH (n) RETURN n.name")).Select(row => row[0]).ShouldBe(beforeNodes.Select(row => row[0]));
        (await RowsAsync(session, "MATCH (a)-[r]->(b) RETURN a.name, b.name")).Select(row => string.Join(",", row))
            .ShouldBe(beforeEdges.Select(row => string.Join(",", row)));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXPLAIN: every graph plan record has a closed description mapping")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only enumeration; shipped description dispatch is pattern matching.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test-only uninitialized records exercise only type patterns.")]
    public void Describe_EveryGraphPlanRecord_ShouldHaveMapping()
    {
        // Arrange / Act
        var types = typeof(GraphPlan).Assembly.GetTypes().Where(type => type.Namespace == typeof(GraphPlan).Namespace &&
            !type.IsAbstract && type.Name.EndsWith("Plan", StringComparison.Ordinal)).ToArray();
        // Assert
        types.ShouldNotBeEmpty();
        foreach (var type in types) { GraphPlanner.DescribeOperator(RuntimeHelpers.GetUninitializedObject(type)).ShouldNotBeNullOrEmpty(type.Name); }
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source)
    {
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
