using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed partial class GraphRelationshipTypeAnchorTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Maintenance removes purged and rolled-back type publications from planner estimates (#1633)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_TypeHistoryAfterMaintenance_ShouldChooseTypeAnchorAgainAsync(bool rollback)
    {
        // Arrange: the pinned snapshot keeps all 81 publications until the explicit pass.
        await using var engine = GraphDatabaseEngine.Create("type-estimate-maintenance", new() { MaintenanceInterval = TimeSpan.FromDays(1) });
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await db.CreateSessionAsync(CancellationToken.None);
        await using var reader = await db.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 63)) + "] INSERT (:Common)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:Common)-[:Rare {w:0}]->()", cancellationToken: CancellationToken.None);
        await using var pin = await reader.BeginTransactionAsync(Transactions.IsolationLevel.Snapshot, CancellationToken.None);
        await using var transaction = rollback ? await writer.BeginTransactionAsync(CancellationToken.None) : null;
        for (int i = 1; i <= 80; i++)
        {
            await writer.ExecuteAsync($"MATCH ()-[r:Rare]->() SET r.w={i}", cancellationToken: CancellationToken.None);
        }
        db.Store.EstimateRelationshipTypeCount("Rare").ShouldBe(81);
        db.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        db.Store.EstimateRelationshipTypeCount("Rare").ShouldBe(81);
        const string source = "MATCH p = (a:Common)-[:Rare]->(b) RETURN p";
        long beforeDecodes = db.Store.NodeRecordsDecoded;
        var before = await MatchAsync(db, writer, source);
        beforeDecodes = db.Store.NodeRecordsDecoded - beforeDecodes;
        before.Budget.Physical.ShouldBe(65);
        beforeDecodes.ShouldBe(65);
        if (transaction is not null) { await transaction.RollbackAsync(CancellationToken.None); }
        await pin.RollbackAsync(CancellationToken.None);

        // Act: use the already-open store, with no planning-time scan or reopen.
        long purged = db.Coordinator.RunVersionPurgePass(CancellationToken.None);
        long estimate = db.Store.EstimateRelationshipTypeCount("Rare");
        long decodes = db.Store.NodeRecordsDecoded;
        long postings = db.Store.RelationshipTypeEntriesVisited;
        var anchor = await AnchorAsync(writer, source);
        long planningDecodes = db.Store.NodeRecordsDecoded - decodes;
        long planningPostings = db.Store.RelationshipTypeEntriesVisited - postings;
        decodes = db.Store.NodeRecordsDecoded;
        var after = await MatchAsync(db, writer, source);
        long executionDecodes = db.Store.NodeRecordsDecoded - decodes;
        _output.WriteLine($"Maintenance (rollback={rollback}): reclaimed {purged}; estimate 81 -> {estimate}; plan {anchor[2]}; physical {before.Budget.Physical} -> {after.Budget.Physical}; node decodes {beforeDecodes} -> {executionDecodes}; planning decodes {planningDecodes}, postings {planningPostings}.");

        // Assert
        estimate.ShouldBe(1);
        anchor[2].ShouldBe("RelationshipTypeScan");
        anchor[6].ShouldBe(1L);
        planningDecodes.ShouldBe(0);
        planningPostings.ShouldBe(0);
        after.Paths.ShouldBe(before.Paths);
        after.Paths.ShouldHaveSingleItem();
        after.Budget.Physical.ShouldBe(1);
        executionDecodes.ShouldBe(2);
        purged.ShouldBe(rollback ? 0 : 320); // Each old version: record, two adjacency entries, type posting.
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Maintenance removes a completely rolled-back type population from its estimate (#1633)")]
    public async Task Plan_RolledBackTypeCreationAfterMaintenance_ShouldEstimateZeroAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("type-estimate-rollback", new() { MaintenanceInterval = TimeSpan.FromDays(1) });
        var db = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:Keep]->()", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:Keep]->(), ()-[:RolledBack]->()", cancellationToken: CancellationToken.None);
        db.Store.EstimateRelationshipTypeCount("Keep").ShouldBe(2);
        db.Store.EstimateRelationshipTypeCount("RolledBack").ShouldBe(1);

        // Act
        await transaction.RollbackAsync(CancellationToken.None);
        db.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        long keep = db.Store.EstimateRelationshipTypeCount("Keep");
        long removed = db.Store.EstimateRelationshipTypeCount("RolledBack");
        _output.WriteLine($"Rollback maintenance: Keep 2 -> {keep}; RolledBack 1 -> {removed}.");

        // Assert
        keep.ShouldBe(1);
        removed.ShouldBe(0);
        (await RowsAsync(session, "MATCH ()-[:Keep]->() RETURN 1")).ShouldHaveSingleItem();
    }
}
