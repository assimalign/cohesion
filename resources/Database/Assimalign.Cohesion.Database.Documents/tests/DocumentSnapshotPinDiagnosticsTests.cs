using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentDatabaseEventSourceCollection))]
public sealed class DocumentSnapshotPinDiagnosticsTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - ReadCommitted: Reads should not move kernel transaction rates or lifecycle events (#1351)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ReadCommittedHitOrMiss_ShouldOnlyCountTheExplicitTransaction(bool missing)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("pin-diagnostics", new DocumentDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("pins", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.CreateCollectionAsync("items", token);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',{n:1})", cancellationToken: token);
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new TransactionRateRecorder(source);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        string[] rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
        long sequence = await recorder.WaitForBoundaryAsync(token);

        // Act: both successful reads and an absent item must release their pins.
        for (int read = 0; read < 4; read++)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(missing ? "SELECT n FROM items WHERE n = 2" : "SELECT n FROM items", cancellationToken: token);
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
