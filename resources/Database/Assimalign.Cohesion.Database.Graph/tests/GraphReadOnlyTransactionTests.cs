using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphReadOnlyTransactionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Read-only transactions: autocommit, explicit ends and snapshot pins append and flush nothing (#1280)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task Read_AutocommitAndExplicitReadOnly_ShouldLeaveJournalAndDurabilityUntouched(IsolationLevel isolationLevel)
    {
        // Arrange: synchronous simulated durability, with workers kept out of the measurement.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("read-only", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("reads", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        using var flushes = FaultInjectingJournalStorageStrategy.FailJournalFlushes(0);
        await session.ExecuteAsync("INSERT (:Item {value: 42})", cancellationToken: token);
        flushes.Attempts.ShouldBeGreaterThan(0); // Prove that the flush counter observes writers.
        int beforeFlushes = flushes.Attempts;
        var storages = new[] { database.DataStorage };
        var before = storages.Select(storage => (storage.JournalLength, storage.WriteAheadJournal.LastLsn)).ToArray();

        using var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1);

        async Task ReadAsync()
        {
            var rows = (await session.ExecuteAsync("MATCH (n:Item) RETURN n.value", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>()!;
            int count = 0;
            await foreach (var row in rows.GetRowsAsync(token))
            {
                Convert.ToInt32(row.GetValue(0)).ShouldBe(42);
                count++;
            }
            count.ShouldBe(1);
        }

        // Act: the loop is a deterministic work-count guard, including read-committed pins.
        for (int i = 0; i < 64; i++)
        {
            await ReadAsync();
        }
        await using (var transaction = await session.BeginTransactionAsync(isolationLevel, token))
        {
            for (int i = 0; i < 64; i++)
            {
                await ReadAsync();
            }
            await transaction.CommitAsync(token);
        }
        await using (var transaction = await session.BeginTransactionAsync(isolationLevel, token))
        {
            await ReadAsync();
            await transaction.RollbackAsync(token);
        }
        await using (var transaction = await session.BeginTransactionAsync(isolationLevel, token))
        {
            await ReadAsync(); // Disposal aborts this reader without an abort record.
        }

        // Assert: no lifecycle, physical bracket or durable flush work grows with read count.
        storages.Select(storage => (storage.JournalLength, storage.WriteAheadJournal.LastLsn)).ToArray().ShouldBe(before);
        flushes.Attempts.ShouldBe(beforeFlushes);
        failures.Attempts.ShouldBe(0);
        failures.Remaining.ShouldBe(1);
        storages.ShouldAllBe(storage => !storage.IsOffline);
    }
}
