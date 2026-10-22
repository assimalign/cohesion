using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Legacy index records are refused before recovery writes (#1135)")]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    public async Task EnsureIndexFormat_V1IndexRecord_ShouldRefuseWithoutWritesAsync(byte kind)
    {
        // Arrange: hand-build a v1 header on an otherwise current physical record.
        await using var fixture = await Fixture.Create();
        await fixture.CreateIndex("by_a", "a");
        using (var records = fixture.Storage.GetUnitIterator(kind == 5 ? 1UL : 0UL))
        {
            while (records.MoveNext())
            {
                var unit = records.Current;
                if (unit.Data.Span[16] != kind) { continue; }
                var bytes = unit.Data.ToArray();
                if (kind == 4)
                {
                    // V1 held one path and no count or UNIQUE flag.
                    int pathsOffset = 38 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(34));
                    bytes = bytes[..pathsOffset].Concat(bytes[(pathsOffset + 4)..^9]).Concat(bytes[^8..]).ToArray();
                }
                else { bytes = bytes[..^1]; } // V1 registrations had no flag.
                bytes[17] = 1;
                using var bracket = fixture.Storage.BeginTransaction();
                fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
                bracket.Commit();
                break;
            }
        }
        var before = fixture.Data.ToArray();
        var journal = fixture.Journal.ToArray();

        // Act.
        var error = Should.Throw<DocumentCatalogException>(() => DocumentCatalog.EnsureIndexFormat(fixture.Storage));

        // Assert.
        error.Message.ShouldContain("found 1, supported 2");
        error.Message.ShouldContain("Recreate");
        fixture.Data.ToArray().ShouldBe(before);
        fixture.Journal.ToArray().ShouldBe(journal);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Index paths compare by value and cannot change (#1135)")]
    public void Equals_EquivalentPathLists_ShouldHaveValueEquality()
    {
        // Arrange.
        string[] paths = ["a", "b.c"];
        var id = Guid.NewGuid();
        var first = new DocumentIndexMetadata(id, "ix", paths, true, 10);
        var second = new DocumentIndexMetadata(id, "ix", ["a", "b.c"], true, 10);

        // Act.
        paths[0] = "changed";

        // Assert.
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
        new[] { first }.SequenceEqual([second]).ShouldBeTrue();
        first.Paths.ShouldBe(["a", "b.c"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Default index metadata has an empty leading path and can be formatted (#1135)")]
    public void Path_DefaultMetadata_ShouldBeEmptyAndAllowFormatting()
    {
        // Arrange.
        var metadata = default(DocumentIndexMetadata);

        // Act.
        string path = metadata.Path;
        string text = metadata.ToString();

        // Assert.
        path.ShouldBeEmpty();
        text.ShouldContain("DocumentIndexMetadata", Case.Sensitive);
        metadata.ShouldBe(default);
        metadata.GetHashCode().ShouldBe(default(DocumentIndexMetadata).GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Composite UNIQUE records round trip on reopen (#1135)")]
    public async Task Open_CompositeUniqueIndex_ShouldRoundTripV2RecordsAsync()
    {
        // Arrange.
        await using var fixture = await Fixture.Create();
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var definition = await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "uq", ["a", "b[0]", "['x.y']"], true, writer);
        await fixture.Coordinator.CommitAsync(writer);

        // Act.
        DocumentCatalog.EnsureIndexFormat(fixture.Storage);
        var reopened = DocumentCatalog.Open(fixture.Storage, fixture.Coordinator);
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Assert: decoding creates a different path list, with identical value equality.
        var actual = reopened.GetIndexes(fixture.Collection.Id, reader.Snapshot).ShouldHaveSingleItem();
        actual.ShouldBe(definition); actual.IsUnique.ShouldBeTrue();
        actual.Paths.ShouldBe(["a", "b[0]", "['x.y']"]);
        using var registrations = fixture.Storage.GetUnitIterator(1);
        registrations.MoveNext().ShouldBeTrue();
        registrations.Current.Data.Span[17].ShouldBe((byte)2);
        registrations.Current.Data.Span[^1].ShouldBe((byte)1);
        await fixture.Coordinator.CommitAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Raw composite keys preserve SQL++ collation and missing navigation (#1135)")]
    public async Task CreateIndexAsync_MixedComponents_ShouldEncodeCollationAndNavigationAsync()
    {
        // Arrange: navigation through null is MISSING, reading that null is NULL.
        await using var fixture = await Fixture.Create();
        string[] json = ["{}", "{\"b\":null}", "{\"b\":false}", "{\"b\":true}", "{\"b\":2.0}", "{\"b\":\"x\"}", "{\"b\":[]}", "{\"b\":{}}"];
        for (int i = 0; i < json.Length; i++) { await fixture.Write(i.ToString(), "{\"a\":1" + (json[i] == "{}" ? "}" : "," + json[i][1..])); }
        await fixture.Write("leading-missing", "{\"b\":2}");
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "mixed", ["a", "b"], false, writer);
        await fixture.Coordinator.CommitAsync(writer);
        var snapshot = new TransactionSnapshot(TransactionSequence.None, TransactionSequence.None, new TransactionSequence(ulong.MaxValue), []);
        using var registrations = fixture.Storage.GetUnitIterator(1);
        registrations.MoveNext().ShouldBeTrue();
        long root = BinaryPrimitives.ReadInt64LittleEndian(registrations.Current.Data.Span[26..]);
        var manager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = fixture.Storage,
            TransactionSource = _ => throw new InvalidOperationException("Only raw reads are used."),
            ExistingIndexes = [new BTreeIndexRegistration(metadata.ObjectId, new IndexDefinition("mixed"), root)]
        });
        manager.TryGetIndex(metadata.ObjectId, metadata.Name, out var tree).ShouldBeTrue();

        // Act.
        var keys = new List<byte[]>();
        await using (var cursor = tree.OpenCursor(snapshot, new IndexKeyRange(null, null, true, true)))
        { while (await cursor.MoveNextAsync()) { keys.Add(cursor.CurrentKey.Encoded.ToArray()); } }

        // Assert: eight entries in exact component-kind order; no leading-MISSING entry.
        var prefix = new byte[] {4}.Concat(IndexKey.From(new DatabaseKeyWriter().AppendDecimal(1m)).Encoded.ToArray()).ToArray();
        keys.Count.ShouldBe(8);
        for (int i = 0; i < keys.Count; i++)
        {
            keys[i][..prefix.Length].ShouldBe(prefix);
            keys[i][prefix.Length].ShouldBe((byte)i);
        }
        // A leading navigation path must omit every entry, including the document with b:null.
        writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var navigation = await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "navigation", ["b.x", "a"], true, writer);
        await fixture.Coordinator.CommitAsync(writer);
        using var navigationRegistrations = fixture.Storage.GetUnitIterator(1);
        long navigationRoot = 0;
        while (navigationRegistrations.MoveNext())
        {
            var bytes = navigationRegistrations.Current.Data.Span;
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[18..]) == navigation.ObjectId)
            {
                navigationRoot = BinaryPrimitives.ReadInt64LittleEndian(bytes[26..]);
            }
        }
        navigationRoot.ShouldBeGreaterThan(0);
        var navigationManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = fixture.Storage,
            TransactionSource = _ => throw new InvalidOperationException("Only raw reads are used."),
            ExistingIndexes = [new BTreeIndexRegistration(navigation.ObjectId, new IndexDefinition(navigation.Name), navigationRoot)]
        });
        navigationManager.TryGetIndex(navigation.ObjectId, navigation.Name, out var navigationTree).ShouldBeTrue();
        await using var navigationCursor = navigationTree.OpenCursor(snapshot, new IndexKeyRange(null, null, true, true));
        (await navigationCursor.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Truncated registrations fail through the catalog boundary (#1135)")]
    public async Task EnsureIndexFormat_TruncatedRegistration_ShouldFailWithCatalogExceptionAsync()
    {
        // Arrange.
        await using var fixture = await Fixture.Create();
        await fixture.CreateIndex("ix", "a");
        PageId? damagedPage = null;
        int damagedSlot = -1;
        using (var registrations = fixture.Storage.GetUnitIterator(1))
        {
            registrations.MoveNext().ShouldBeTrue();
            var unit = registrations.Current;
            damagedPage = unit.PageId;
            damagedSlot = unit.SlotIndex;
            using var bracket = fixture.Storage.BeginTransaction();
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, unit.Data.Span[..^1]);
            bracket.Commit();
        }

        // Act / Assert.
        var error = Should.Throw<DocumentCatalogException>(() => DocumentCatalog.EnsureIndexFormat(fixture.Storage));
        var corruption = error.InnerException.ShouldBeOfType<StorageCorruptionException>();
        corruption.PageId.ShouldBe(damagedPage);
        corruption.SlotIndex.ShouldBe(damagedSlot);
        corruption.InnerException.ShouldBeOfType<DocumentCatalogException>()
            .InnerException.ShouldBeOfType<System.IO.EndOfStreamException>();
    }

}
