using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;
using Recorder = Assimalign.Cohesion.Testing.EventSourceRecorder;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection(nameof(BlobDatabaseEventSourceCollection))]
public sealed class BlobOperationOutcomeTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Complete: cleanup preserves durable success; memory and explicit-transaction failures propagate (#1246)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CompleteAsync_SnapshotCleanupFails_ShouldPreserveCommittedOutcome(bool explicitTransaction, bool outOfMemory)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recorder = new Recorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = BlobDatabaseEngine.Create("cleanup-outcome", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(cancellationToken: timeout.Token);
        var container = await session.CreateContainerAsync("items", timeout.Token);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(timeout.Token) : null;
        Exception failure = outOfMemory ? new OutOfMemoryException() : new ObjectDisposedException("snapshot pin");
        int releases = 0;
        database.BeforeSnapshotPinRelease = () =>
        {
            database.BeforeSnapshotPinRelease = null;
            releases++;
            throw failure;
        };
        async Task WriteAsync()
        {
            await using var stream = await container.OpenWriteAsync("one", cancellationToken: timeout.Token);
            await stream.WriteAsync(new byte[] { 42 }, timeout.Token);
        }

        // Act
        try
        {
            if (outOfMemory || explicitTransaction)
            {
                (await Should.ThrowAsync<Exception>(WriteAsync)).ShouldBeSameAs(failure);
            }
            else { await WriteAsync(); }
        }
        finally { database.BeforeSnapshotPinRelease = null; }

        // Assert: cleanup released the session hold, and only a committed success emits a warning.
        releases.ShouldBe(1);
        var warnings = recorder.Events.Where(item => item.EventId == 14).ToArray();
        if (!outOfMemory && !explicitTransaction)
        {
            var warning = warnings.ShouldHaveSingleItem();
            warning.Level.ShouldBe(EventLevel.Warning);
            warning.PayloadNames.ShouldBe(["database", "exceptionType"]);
            warning.Payload.ShouldBe(["test", typeof(ObjectDisposedException).FullName]);
        }
        else { warnings.ShouldBeEmpty(); }
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        if (transaction is not null) { await transaction.RollbackAsync(timeout.Token); }
        session.CurrentTransaction.ShouldBeNull();
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", timeout.Token);
        await using var observer = await reopened.CreateSessionAsync(cancellationToken: timeout.Token);
        var reopenedContainer = await observer.GetContainerAsync("items", timeout.Token);
        if (!explicitTransaction)
        {
            await using var stream = await reopenedContainer.OpenReadAsync("one", timeout.Token);
            var bytes = new byte[1];
            (await stream.ReadAsync(bytes, timeout.Token)).ShouldBe(1);
            bytes[0].ShouldBe((byte)42);
        }
        else { (await reopenedContainer.GetPropertiesAsync("one", timeout.Token)).ShouldBeNull(); }
    }
}
