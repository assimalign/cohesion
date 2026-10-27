using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - V3 descriptor decoding rejects malformed keys and bounded predicates (#1530)")]
    [InlineData("key-kind")]
    [InlineData("predicate-kind")]
    [InlineData("required-child")]
    [InlineData("depth")]
    [InlineData("comparison")]
    [InlineData("constant-kind")]
    [InlineData("flags")]
    [InlineData("unique-array")]
    [InlineData("null-path")]
    [InlineData("trailing")]
    public void Decode_MalformedArrayDescriptor_ShouldFailClosed(string damage)
    {
        // Arrange: write v3 independently of the production encoder.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write(1UL); writer.Write(0UL); writer.Write((byte)4); writer.Write((byte)3);
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write(2); writer.Write("ix"u8);
        writer.Write(1); // One array key.
        writer.Write((byte)(damage == "key-kind" ? 2 : 1));
        writer.Write(damage == "null-path" ? -1 : 1);
        if (damage != "null-path") { writer.Write("p"u8); }
        writer.Write(5); writer.Write("items"u8);
        if (damage == "predicate-kind") { writer.Write((byte)255); }
        else if (damage == "required-child") { writer.Write((byte)6); writer.Write((byte)0); }
        else if (damage is "depth" or "comparison" or "constant-kind")
        {
            if (damage == "depth") { for (int i = 0; i < 33; i++) { writer.Write((byte)6); } }
            writer.Write((byte)1); // Comparison.
            writer.Write(1); writer.Write("p"u8);
            writer.Write((byte)(damage == "comparison" ? 255 : 0));
            writer.Write((byte)(damage == "constant-kind" ? 255 : 4));
            writer.Write(20m);
        }
        else { writer.Write((byte)0); }
        writer.Write((byte)(damage == "flags" ? 4 : damage == "unique-array" ? 1 : 2));
        writer.Write(10UL);
        if (damage == "trailing") { writer.Write((byte)0); }

        // Act.
        var failure = Should.Throw<DocumentCatalogException>(() => DocumentCatalogCodec.Decode(stream.ToArray()));

        // Assert: current-format damage is never mistaken for a legacy-format refusal or schema error.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06105");
        failure.IsFormatRefusal.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Scalar INCLUDE MISSING retains NULLS DISTINCT uniqueness (#1530, #1632)")]
    public async Task CreateIndexAsync_UniqueIncludeMissing_ShouldAllowRepeatedUnknownKeys()
    {
        // Arrange.
        await using var f = await Fixture.Create();
        var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = await f.Catalog.CreateIndexAsync(f.Collection.Id, "uq", [DocumentIndexKey.Scalar("a")], true, true, writer);
        await f.Coordinator.CommitAsync(writer);

        // Act.
        await f.Write("missing1", "{}"); await f.Write("missing2", "{}");
        await f.Write("null1", "{\"a\":null}"); await f.Write("null2", "{\"a\":null}");
        await f.Write("value", "{\"a\":2}");

        // Assert.
        (await ReadRawKeysAsync(f, metadata, Latest())).Count.ShouldBe(5);
        var actual = DocumentCatalog.Open(f.Storage, f.Coordinator).GetIndexes(f.Collection.Id, Latest()).Single();
        actual.ShouldBe(metadata);
        actual.IsUnique.ShouldBeTrue(); actual.IncludeMissing.ShouldBeTrue();
    }
}
