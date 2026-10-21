using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlSnapshotPinDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Reclaimed DROP should count only the user transaction (#1351)")]
    public async Task DropTable_Reclaimed_ShouldNotCountTheReclamationFence()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = SqlDatabaseEngine.Create("drop-pin-diagnostics", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("pins", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: token);
        database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        ulong objectId = table.ObjectId;
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new TransactionRateRecorder(source);
        long sequence = await recorder.WaitForBoundaryAsync(token);

        // Act: wait for the worker to finish both page reclamation and intent retirement.
        await session.ExecuteAsync("DROP TABLE items", cancellationToken: token);
        while (database.TableReclamation.HasWork) { await Task.Delay(5, token); }
        long reclaimed = await recorder.WaitForBoundaryAsync(token);

        // Assert: reclamation adds nothing to the DROP's one begin and one commit.
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(0);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.DataStorage.GetOwnerPages(objectId).ShouldBeEmpty();
        database.OfflineError.ShouldBeNull();
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        recorder.Sum("transactions-per-second", sequence, reclaimed).ShouldBe(1);
        recorder.Sum("commits-per-second", sequence, reclaimed).ShouldBe(1);
        recorder.Sum("rollbacks-per-second", sequence, reclaimed).ShouldBe(0);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.Where(e => e.EventId is 1 or 2 or 3 && Equals(e.Payload?[0], "pins"))
            .Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionCommitted"]);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - ReadCommitted: Reads should not move kernel transaction rates or lifecycle events (#1351)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ReadCommittedHitOrMiss_ShouldOnlyCountTheExplicitTransaction(bool missing)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = SqlDatabaseEngine.Create("pin-diagnostics", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("pins", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: token);
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new TransactionRateRecorder(source);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        string[] rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
        long sequence = await recorder.WaitForBoundaryAsync(token);

        // Act: both successful reads and an absent item must release their pins.
        for (int read = 0; read < 4; read++)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(missing ? "SELECT id FROM items WHERE id = 2" : "SELECT id FROM items", cancellationToken: token);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(token)) { rows++; }
            rows.ShouldBe(missing ? 0 : 1);
        }
        long readsFinished = await recorder.WaitForBoundaryAsync(token);
        foreach (string rate in rates)
        {
            recorder.Sum(rate, sequence, readsFinished).ShouldBe(0, rate);
        }
        database.Coordinator.GetOpenContexts().Count.ShouldBe(1);
        await transaction.RollbackAsync(token);
        long rollbackFinished = await recorder.WaitForBoundaryAsync(token);
        recorder.Sum("rollbacks-per-second", readsFinished, rollbackFinished).ShouldBe(1);

        // Assert: the real transaction still reports its begin and rollback exactly once.
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.Where(e => e.EventId is 1 or 2 or 3 && Equals(e.Payload?[0], "pins"))
            .Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionRolledBack"]);
    }
}
