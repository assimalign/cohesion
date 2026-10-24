using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Registration decoding: malformed index metadata reports page and slot (#1372)")]
    [InlineData("length", false)]
    [InlineData("header", false)]
    [InlineData("format", false)]
    [InlineData("identity", false)]
    [InlineData("length", true)]
    [InlineData("header", true)]
    [InlineData("format", true)]
    [InlineData("identity", true)]
    public async Task Read_MalformedRegistration_ShouldReportPhysicalLocation(string damage, bool fence)
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode(
            (ulong)fixture.Storage.ReserveTransactionSequence(), ["Person"], Properties("rank", 7)),
            TestLabelIds.All, writer);
        await fixture.Coordinator.CommitAsync(writer);
        StorageUnit unit;
        using (var scan = fixture.Storage.GetUnitIterator(1))
        {
            scan.MoveNext().ShouldBeTrue();
            unit = scan.Current;
        }
        byte[] original = unit.Data.ToArray();
        byte[] malformed = damage == "length" ? original[..^1] : original.AsSpan().ToArray();
        if (damage == "header") { malformed[16] = byte.MaxValue; }
        if (damage == "format") { malformed[17] = byte.MaxValue; }
        if (damage == "identity") { BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(26, 8), 0); }
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = Should.Throw<StorageCorruptionException>(() =>
            {
                if (fence) { GraphStore.EnsureIndexFormat(fixture.Storage); }
                else { GraphStore.Open(fixture.Storage, fixture.Coordinator); }
            });
            failure.PageId.ShouldBe(unit.PageId);
            failure.SlotIndex.ShouldBe(unit.SlotIndex);
            failure.Message.ShouldContain($"slot {unit.SlotIndex}", Case.Sensitive);
        }
        finally
        {
            using var repair = fixture.Storage.BeginTransaction();
            fixture.Storage.UpdateEntry(repair, unit.PageId, unit.SlotIndex, original);
            repair.Commit();
        }
        var reader = await fixture.Begin();
        GraphStore.Open(fixture.Storage, fixture.Coordinator).FindNode(node.Id, reader.Snapshot).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Decoding: a confirmed malformed payload reports page and slot (#1372)")]
    [InlineData("find")]
    [InlineData("open")]
    [InlineData("label")]
    [InlineData("format")]
    public async Task Read_MalformedGraphPayload_ShouldReportItsPhysicalLocation(string operation)
    {
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
        byte[] malformed = original.AsSpan().ToArray();
        // Graph format 4's node label count follows the fixed 34-byte header.
        if (operation == "format") { malformed[17] = byte.MaxValue; }
        else { BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(34, 4), -1); }
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }
        try
        {
            var failure = Should.Throw<StorageCorruptionException>(() =>
            {
                if (operation == "find") { fixture.Store.FindNode(node.Id, reader.Snapshot); }
                else if (operation == "open") { GraphStore.Open(fixture.Storage, fixture.Coordinator); }
                else if (operation == "format") { GraphStore.EnsureIndexFormat(fixture.Storage); }
                else { fixture.Store.GetNodes("Person", reader.Snapshot).ToArray(); }
            });
            failure.PageId.ShouldBe(unit.PageId);
            failure.SlotIndex.ShouldBe(unit.SlotIndex);
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
}
