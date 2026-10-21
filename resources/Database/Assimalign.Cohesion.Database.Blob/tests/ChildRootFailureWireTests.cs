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
            Should.Throw<StorageIOException>(() => { using var probe = storage.PageManager.GetPage(pageId); });
            faults.RottenPageReads.ShouldBeGreaterThan(0);
        }
        try
        {
            // Act: a content read may have sent TransferStart before its first failing chunk.
            await client.SendAsync(BlobProtocolMessageType.Read, new BlobReadMessage(BlobServerHarness.ContainerName, "one").Encode());
            ProtocolFrame frame = default;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                frame = (await client.ReadAsync()).ShouldNotBeNull();
                if (frame.Type == ProtocolMessageType.Error) { break; }
            }
            frame.Type.ShouldBe(ProtocolMessageType.Error);
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
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Begin: storage failures are coded in process and terminal on the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransaction_StorageFailure_ShouldCodeAtBothBoundaries(bool rawIo)
    {
        // Arrange
        await using var harness = await BlobServerHarness.StartAsync();
        harness.Engine.TryGetDatabase(BlobServerHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var session = await database!.CreateSessionAsync(CancellationToken.None);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        int attempts = 0;
        Exception cause = rawIo ? new System.IO.IOException("Injected begin device failure.") : new StorageIOException("Injected begin storage failure.");
        database.BeforeTransactionBegin = () => { Interlocked.Increment(ref attempts); throw cause; };

        // Act
        var embedded = await Should.ThrowAsync<DatabaseException>(() => session.BeginTransactionAsync(cancellationToken: CancellationToken.None).AsTask());
        await client.SendAsync(BlobProtocolMessageType.GetProperties, new BlobGetPropertiesMessage(BlobServerHarness.ContainerName, "one").Encode());
        var failure = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        database.BeforeTransactionBegin = null;

        // Assert
        attempts.ShouldBe(2);
        embedded.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB005");
        embedded.InnerException.ShouldBeSameAs(cause);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("COHDBB005: ", Case.Sensitive);
        (await client.ReadAsync()).ShouldBeNull();
        (await session.GetContainerAsync(BlobServerHarness.ContainerName, CancellationToken.None)).Name.ShouldBe(BlobServerHarness.ContainerName);
    }

}
