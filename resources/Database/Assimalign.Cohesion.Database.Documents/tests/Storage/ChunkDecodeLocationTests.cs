using System;
using System.IO;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;

public sealed class ChunkDecodeLocationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Storage] - Decoding: confirmed malformed chunks report page and slot (#1372)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MalformedChunk_ShouldReportLocation(bool tombstone)
    {
        using var storage = DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "decode");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        byte[] payload = [7];
        byte[] original = DocumentChunkCodec.Encode(payload, TransactionSequence.None);
        (PageId PageId, int SlotIndex) location;
        using (var bracket = storage.BeginTransaction())
        {
            location = storage.InsertChunk(bracket, original);
            bracket.Commit();
        }
        var content = new DocumentContentReference(DocumentStorage.PackLocation(location.PageId, location.SlotIndex),
            payload.Length, ~DocumentContentChecksum.Append(uint.MaxValue, payload));
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        byte[] malformed = original.AsSpan().ToArray();
        malformed[17] = byte.MaxValue;
        using (var bracket = storage.BeginTransaction())
        {
            storage.UpdateEntry(bracket, location.PageId, location.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = await Should.ThrowAsync<StorageCorruptionException>(async () =>
            {
                if (tombstone) { await storage.TombstoneContentAsync(coordinator, reader, content); }
                else { using var stream = storage.OpenRead(content); stream.ReadByte(); }
            });
            failure.PageId.ShouldBe(location.PageId);
            failure.SlotIndex.ShouldBe(location.SlotIndex);
            failure.Code.ShouldBe("CK06003");
            failure.Message.ShouldContain($"slot {location.SlotIndex}", Case.Sensitive);
        }
        finally
        {
            using var repair = storage.BeginTransaction();
            storage.UpdateEntry(repair, location.PageId, location.SlotIndex, original);
            repair.Commit();
        }
        using (var stream = storage.OpenRead(content)) { stream.ReadByte().ShouldBe(7); }
        await coordinator.RollbackAsync(reader);
    }
}
