using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Blob.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection(nameof(BlobDatabaseEventSourceCollection))]
public sealed class BlobSnapshotPinDiagnosticsTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - ReadCommitted: Reads should not move kernel transaction rates or lifecycle events (#1351)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ReadCommittedHitOrMiss_ShouldOnlyCountTheExplicitTransaction(bool missing)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = BlobDatabaseEngine.Create("pin-diagnostics", new BlobDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("pins", token);
        await using var session = await database.CreateSessionAsync(token);
        var container = await session.CreateContainerAsync("files", token);
        await using (var write = await container.OpenWriteAsync("item", cancellationToken: token))
        {
            await write.WriteAsync("content"u8.ToArray(), token);
        }
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new TransactionRateRecorder(source);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        string[] rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
        long sequence = await recorder.WaitForBoundaryAsync(token);

        // Act: both successful reads and an absent item must release their pins.
        for (int read = 0; read < 4; read++)
        {
            var properties = await container.GetPropertiesAsync(missing ? "missing" : "item", token);
            properties.HasValue.ShouldBe(!missing);
            if (!missing)
            {
                // Early disposal must release a stream's pin even without reading all chunks.
                await using var stream = await container.OpenReadAsync("item", cancellationToken: token);
                stream.ReadByte().ShouldBe((int)'c');
            }
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
