using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.KeyValuePair.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueSnapshotPinDiagnosticsTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - ReadCommitted: Reads should not move kernel transaction rates or lifecycle events (#1351)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ReadCommittedHitOrMiss_ShouldOnlyCountTheExplicitTransaction(bool missing)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = KeyValueDatabaseEngine.Create("pin-diagnostics", new KeyValueDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("pins", token);
        await using var session = await database.CreateSessionAsync(token);
        await database.PutAsync(session, "key"u8.ToArray(), "value"u8.ToArray(), cancellationToken: token);
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new TransactionRateRecorder(source);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        string[] rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
        long sequence = await recorder.WaitForBoundaryAsync(token);

        // Act: both successful reads and an absent item must release their pins.
        for (int read = 0; read < 4; read++)
        {
            var entry = await database.GetAsync(session, missing ? "missing"u8.ToArray() : "key"u8.ToArray(), token);
            entry.HasValue.ShouldBe(!missing);
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
