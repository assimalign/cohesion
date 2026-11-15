using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeTextRecoveryTests
{
    private const string initial = "ALTER CURRENT GRAPH TYPE SET {(:Person =>),(:Place =>),(:Person)-[:VISITS => {count :: INT64}]->(:Place)}";
    private const string alter = "ALTER CURRENT GRAPH TYPE ALTER {(:Person)-[:VISITS => {count :: INT64 NOT NULL, note :: STRING}]->(:Place)}";
    private const string replacement = "ALTER CURRENT GRAPH TYPE SET {(:\"Person\" =>), (:\"Place\" =>), (:\"Person\")-[:\"VISITS\" => {\"count\" :: INT64 NOT NULL, \"note\" :: STRING}]->(:\"Place\")}";

    [Theory(DisplayName = "Cohesion Test [Graph] - Text edge ALTER: same-name crash replacement recovers exact state twice (#1806)")]
    [InlineData("tombstones")]
    [InlineData("edge:VISITS")]
    [InlineData("commit-record")]
    [InlineData("committed")]
    public async Task Execute_SameNameEdgeAlterCrash_ShouldRecoverExactStateAsync(string boundary)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("text-type-crash", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync(initial, cancellationToken: token);
        await session.ExecuteAsync("INSERT (:Person)-[:VISITS {count:1}]->(:Place)", cancellationToken: token);
        string previous = (string)(await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE"))[0][2]!;
        int captured = 0;
        database.Catalog.AfterGraphTypeMutation = stage =>
        {
            if (stage != boundary) { return; }
            Capture();
            throw new InvalidOperationException("Abandon text edge ALTER at " + stage);
        };
        // Act
        try
        {
            if (boundary == "commit-record")
            {
                FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => { strategy.CopyDatabase("source", "image"); captured++; };
                await session.ExecuteAsync(alter, cancellationToken: token);
            }
            else if (boundary == "committed") { await session.ExecuteAsync(alter, cancellationToken: token); Capture(); }
            else
            {
                var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(alter, cancellationToken: token));
                error.InnerException.ShouldBeOfType<InvalidOperationException>();
            }
        }
        finally { database.Catalog.AfterGraphTypeMutation = null; FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }
        // Assert
        captured.ShouldBe(1);
        bool committed = boundary is "commit-record" or "committed";
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var image = await engine.OpenDatabaseAsync("image", token);
            await using (var reader = await image.CreateSessionAsync(token))
            {
                (await GraphCatalogIntrospectionTests.Rows(reader, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2].ShouldBe(committed ? replacement : previous);
                (await GraphCatalogIntrospectionTests.Rows(reader, "MATCH (n) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
                (await GraphCatalogIntrospectionTests.Rows(reader, "MATCH (:Person)-[r:VISITS]->(:Place) RETURN r.count")).ShouldHaveSingleItem()[0].ShouldBe(1L);
                var schema = GraphSchema.Open(image, reader);
                (await schema.GetGraphTypeAsync(token))!.EdgeTypes.ShouldHaveSingleItem().Properties.Count.ShouldBe(committed ? 2 : 1);
                var context = await image.Coordinator.BeginAsync(Transactions.IsolationLevel.Snapshot, token);
                try { image.Catalog.HasPropertyKey("note", context.Snapshot).ShouldBe(committed); }
                finally { await image.Coordinator.RollbackAsync(context, token); }
            }
            await image.DisposeAsync();
        }
        void Capture()
        {
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            strategy.CopyDatabase("source", "image"); captured++;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text edge ALTER: cancellation before edge validation preserves type and data (#1806)")]
    public async Task Execute_CanceledEdgeValidation_ShouldPreservePreviousTypeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("text-type-cancel", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(initial, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person)-[:VISITS {count:1}]->(:Place)", cancellationToken: CancellationToken.None);
        string previous = (string)(await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE"))[0][2]!;
        using var cancellation = new CancellationTokenSource();
        int validations = 0;
        database.Catalog.BeforeGraphTypeValidation = name => { name.ShouldBe("VISITS"); validations++; cancellation.Cancel(); };
        // Act
        try { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(alter, cancellationToken: cancellation.Token)); }
        finally { database.Catalog.BeforeGraphTypeValidation = null; }
        var metrics = session.LastStatementMetrics!;
        // Assert
        validations.ShouldBe(1);
        metrics.Memory.ActiveLeases.ShouldBe(0);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW CURRENT GRAPH TYPE")).ShouldHaveSingleItem()[2].ShouldBe(previous);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (:Person)-[r:VISITS]->(:Place) RETURN r.count")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await session.ExecuteAsync("INSERT (:Person)-[:VISITS]->(:Place)", cancellationToken: CancellationToken.None);
    }
}
