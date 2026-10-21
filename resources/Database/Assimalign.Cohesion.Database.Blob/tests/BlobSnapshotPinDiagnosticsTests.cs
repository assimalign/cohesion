using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Recorder = Assimalign.Cohesion.Testing.EventSourceRecorder;

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
        using var recorder = new Recorder(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        string[] rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
        // Drain two publications: a callback already reading the totals when begin runs
        // can publish its pre-begin value afterwards. The next cycle includes that begin.
        long sequence = recorder.CounterSequence;
        for (int drain = 0; drain < 2; drain++)
        {
            foreach (string rate in rates) { await recorder.WaitForCounterAsync(rate, sequence, token); }
            sequence = recorder.CounterSequence;
        }

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
        foreach (string rate in rates)
        {
            (await recorder.WaitForCounterAsync(rate, sequence, token)).Value.ShouldBe(0, rate);
        }
        database.Coordinator.GetOpenContexts().Count.ShouldBe(1);
        sequence = recorder.CounterSequence;
        await transaction.RollbackAsync(token);
        double rollbackIncrement;
        do
        {
            var sample = await recorder.WaitForCounterAsync("rollbacks-per-second", sequence, token);
            sequence = sample.Sequence;
            rollbackIncrement = sample.Value;
        }
        while (rollbackIncrement == 0);
        rollbackIncrement.ShouldBe(1);

        // Assert: the real transaction still reports its begin and rollback exactly once.
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.Where(e => e.EventId is 1 or 2 or 3 && Equals(e.Payload?[0], "pins"))
            .Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionRolledBack"]);
    }
}
