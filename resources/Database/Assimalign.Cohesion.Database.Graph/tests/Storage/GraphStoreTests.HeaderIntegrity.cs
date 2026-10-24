using System;
using System.Buffers.Binary;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Cached reads: an invalid fixed header cannot become absence (found during #1372)")]
    [InlineData("kind")]
    [InlineData("identity")]
    public async Task FindNode_InvalidFixedHeader_ShouldReportCorruption(string damage)
    {
        // Arrange: retain the catalogued physical reference to a committed node.
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode(
            (ulong)fixture.Storage.ReserveTransactionSequence(), ["Person"], Properties("rank", 7)),
            TestLabelIds.All, writer);
        await fixture.Coordinator.CommitAsync(writer);
        var reader = await fixture.Begin();
        fixture.Store.FindNode(node.Id, reader.Snapshot).ShouldNotBeNull();
        StorageUnit unit;
        using (var scan = fixture.Storage.GetUnitIterator(2))
        {
            scan.MoveNext().ShouldBeTrue();
            unit = scan.Current;
            scan.MoveNext().ShouldBeFalse();
        }
        byte[] original = unit.Data.ToArray();
        byte[] malformed = original.AsSpan().ToArray();
        if (damage == "kind") { malformed[16] = byte.MaxValue; }
        else { malformed.AsSpan(18, 8).Clear(); }
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }

        try
        {
            // Act
            var failure = Should.Throw<StorageCorruptionException>(() =>
                fixture.Store.FindNode(node.Id, reader.Snapshot));

            // Assert: only a valid replacement record can invalidate a cached reference.
            failure.PageId.ShouldBe(unit.PageId);
            failure.Code.ShouldBe("CK06003");
            failure.Message.ShouldContain($"slot {unit.SlotIndex}", Case.Sensitive);
        }
        finally
        {
            using var repair = fixture.Storage.BeginTransaction();
            fixture.Storage.UpdateEntry(repair, unit.PageId, unit.SlotIndex, original);
            repair.Commit();
        }
        fixture.Store.FindNode(node.Id, reader.Snapshot).ShouldNotBeNull().Id.ShouldBe(node.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Cached reads: a valid replacement identity still invalidates the old reference (found during #1372)")]
    public async Task FindNode_ValidReplacementIdentity_ShouldRemainAbsentForTheOldIdentity()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode(
            (ulong)fixture.Storage.ReserveTransactionSequence(), ["Person"], Properties("rank", 7)),
            TestLabelIds.All, writer);
        await fixture.Coordinator.CommitAsync(writer);
        var reader = await fixture.Begin();
        StorageUnit unit;
        using (var scan = fixture.Storage.GetUnitIterator(2))
        {
            scan.MoveNext().ShouldBeTrue();
            unit = scan.Current;
        }
        byte[] original = unit.Data.ToArray();
        byte[] replacement = original.AsSpan().ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(replacement.AsSpan(18, 8),
            (ulong)fixture.Storage.ReserveTransactionSequence());
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, replacement);
            bracket.Commit();
        }
        try
        {
            // Act / Assert: a well-formed different identity is still reclamation.
            fixture.Store.FindNode(node.Id, reader.Snapshot).ShouldBeNull();
        }
        finally
        {
            using var repair = fixture.Storage.BeginTransaction();
            fixture.Storage.UpdateEntry(repair, unit.PageId, unit.SlotIndex, original);
            repair.Commit();
        }
        GraphStore.Open(fixture.Storage, fixture.Coordinator).FindNode(node.Id, reader.Snapshot)
            .ShouldNotBeNull().Id.ShouldBe(node.Id);
    }
}
