using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class ChildRootFailureWireTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Wire: child failures carry a code and close the connection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ChildFailure_ShouldReturnCodedTerminalError(bool catalog)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var harness = await BlobServerHarness.StartAsync(configureEngine: options =>
        {
            options.StorageStrategy = strategy;
            options.CheckpointInterval = TimeSpan.FromHours(1);
            options.PageWriteBackInterval = TimeSpan.FromHours(1);
            options.MaintenanceInterval = TimeSpan.FromHours(1);
        });
        harness.Engine.TryGetDatabase(BlobServerHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var embedded = await database!.CreateSessionAsync(CancellationToken.None);
        var files = await embedded.GetContainerAsync(BlobServerHarness.ContainerName, CancellationToken.None);
        await using (var upload = await files.OpenWriteAsync("one", cancellationToken: CancellationToken.None))
        { await upload.WriteAsync(new byte[1000], CancellationToken.None); }
        var storage = database.DataStorage;
        PageId pageId;
        int slot = 0;
        byte[] original = [];
        if (catalog)
        {
            using var iterator = storage.GetUnitIterator(0);
            iterator.MoveNext().ShouldBeTrue();
            pageId = iterator.Current.PageId;
            slot = iterator.Current.SlotIndex;
            original = iterator.Current.Data.ToArray();
        }
        else { pageId = storage.GetOwnerPages(1UL << 63).First(); }
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var faults = strategy.Faults(BlobServerHarness.DatabaseName);
        if (catalog)
        {
            var malformed = original.ToArray();
            malformed[17] = 255;
            using var bracket = storage.BeginTransaction();
            storage.UpdateEntry(bracket, pageId, slot, malformed);
            bracket.Commit();
        }
        else
        {
            int capacity = storage.BufferPoolCapacity;
            storage.BufferPoolCapacity = 1;
            var other = storage.GetOwnerPages(0).First();
            using (storage.PageManager.GetPage(other)) { }
            storage.BufferPoolCapacity = capacity;
            faults.RottenPage = pageId;
            faults.WrapCorruption = _ => new StorageIOException("Injected device read failure.");
        }
        try
        {
            // Act: a content read may have sent TransferStart before its first failing chunk.
            await client.SendAsync(BlobProtocolMessageType.Read, new BlobReadMessage(BlobServerHarness.ContainerName, "one").Encode());
            ProtocolFrame frame;
            do { frame = (await client.ReadAsync()).ShouldNotBeNull(); } while (frame.Type != ProtocolMessageType.Error);
            var failure = ProtocolErrorMessage.Decode(frame.Payload.Span);

            // Assert
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            failure.Message.ShouldStartWith(catalog ? "COHDBB006: " : "COHDBB005: ", Case.Sensitive);
            (await client.ReadAsync()).ShouldBeNull();
            if (!catalog) { faults.RottenPageReads.ShouldBeGreaterThan(0); }
        }
        finally
        {
            faults.Clear();
            if (catalog)
            {
                using var bracket = storage.BeginTransaction();
                storage.UpdateEntry(bracket, pageId, slot, original);
                bracket.Commit();
            }
        }
        (await files.GetPropertiesAsync("one", CancellationToken.None)).ShouldNotBeNull();
    }
}
