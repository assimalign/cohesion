using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed partial class GraphRelationshipTypeAnchorTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Type posting priming refuses work before reading another head (#1633)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TypeUnionWorkLimit_ShouldChargeEveryPrimedHeadAsync(bool undirected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-prime-budget", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT " + string.Join(',', Enumerable.Range(0, 12).Select(i => $"()-[:T{i}]->()")), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 32)) + "] INSERT ()", cancellationToken: CancellationToken.None);
        string types = string.Join('|', Enumerable.Range(0, 12).Select(i => $"T{i}"));
        string source = $"MATCH ()-[:{types}]-{(undirected ? "" : ">")}() RETURN 1";
        (await AnchorAsync(session, source))[2].ShouldBe("RelationshipTypeScan");
        long before = db.Store.RelationshipTypeEntriesVisited;
        int reads = 0;
        db.Store.OnRelationshipTypeEntryRead = () => reads++;

        // Act
        DatabaseException error;
        try
        {
            error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                GraphQueryRequest.FromGql(source, null, new GraphQueryLimits { WorkUnits = 1 }), CancellationToken.None));
        }
        finally { db.Store.OnRelationshipTypeEntryRead = null; }
        long postings = db.Store.RelationshipTypeEntriesVisited - before;
        long physical = session.LastStatementMetrics!.Physical;
        _output.WriteLine($"Union ({undirected}): postings {postings}, hooks {reads}, physical {physical}, limit 1.");

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        postings.ShouldBe(1);
        reads.ShouldBe(1);
        physical.ShouldBe(postings);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        db.Coordinator.GetOpenContexts().ShouldBeEmpty();

        // An exact budget must also allow cursor exhaustion without another unit, and
        // the reverse orientation reuses the posting's charge.
        before = db.Store.RelationshipTypeEntriesVisited;
        await using var complete = (DatabaseResultSet)await session.ExecuteAsync(
            GraphQueryRequest.FromGql(source, null, new GraphQueryLimits { WorkUnits = 12 }), CancellationToken.None);
        int rows = 0;
        await foreach (var row in complete.GetRowsAsync(CancellationToken.None)) { rows++; }
        rows.ShouldBe(undirected ? 24 : 12);
        (db.Store.RelationshipTypeEntriesVisited - before).ShouldBe(12);
        session.LastStatementMetrics!.Physical.ShouldBe(12);
        _output.WriteLine($"Exact union budget ({undirected}): rows {rows}, postings 12, physical 12, limit 12.");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Type posting LIMIT charges heads without reading ahead (#1633)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TypeAnchorLimit_ShouldReadOnlyChargedHeadsAsync(bool undirected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-limit-budget", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4] INSERT ()-[:Keep]->()", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4,5,6,7,8,9] INSERT ()", cancellationToken: CancellationToken.None);
        string source = $"MATCH ()-[:Keep]-{(undirected ? "" : ">")}() RETURN 1 LIMIT 1";
        (await AnchorAsync(session, source))[2].ShouldBe("RelationshipTypeScan");
        long before = db.Store.RelationshipTypeEntriesVisited;
        int reads = 0;
        db.Store.OnRelationshipTypeEntryRead = () => reads++;

        // Act
        int rows;
        try { rows = (await RowsAsync(session, source)).Count; }
        finally { db.Store.OnRelationshipTypeEntryRead = null; }
        long postings = db.Store.RelationshipTypeEntriesVisited - before;
        long physical = session.LastStatementMetrics!.Physical;
        _output.WriteLine($"LIMIT ({undirected}): rows {rows}, postings {postings}, hooks {reads}, physical {physical}.");

        // Assert
        rows.ShouldBe(1);
        postings.ShouldBe(1);
        reads.ShouldBe(1);
        physical.ShouldBe(postings);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        db.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Type posting cancellation on the second read charges exactly two heads (#1633)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TypeAnchorCancellation_ShouldStopAtChargedHeadAsync(bool undirected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-cancel-budget", new());
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4] INSERT ()-[:Keep]->()", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4,5,6,7,8,9] INSERT ()", cancellationToken: CancellationToken.None);
        string source = $"MATCH ()-[:Keep]-{(undirected ? "" : ">")}() RETURN 1";
        (await AnchorAsync(session, source))[2].ShouldBe("RelationshipTypeScan");
        using var cancel = new CancellationTokenSource();
        long before = db.Store.RelationshipTypeEntriesVisited;
        int reads = 0;
        db.Store.OnRelationshipTypeEntryRead = () => { if (++reads == 2) { cancel.Cancel(); } };

        // Act
        try
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(source, cancellationToken: cancel.Token));
        }
        finally { db.Store.OnRelationshipTypeEntryRead = null; }
        long postings = db.Store.RelationshipTypeEntriesVisited - before;
        long physical = session.LastStatementMetrics!.Physical;
        _output.WriteLine($"Cancellation ({undirected}): postings {postings}, hooks {reads}, physical {physical}.");

        // Assert
        reads.ShouldBe(2);
        postings.ShouldBe(2);
        physical.ShouldBe(postings);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        db.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }
}
