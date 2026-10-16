using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifErasureTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Compaction of forty GiB idat never reads retained image bytes (ISO 14496-12 iloc/idat)")]
    public void Save_SparseImage_CompactsUsingBorrowedSlicesOnly()
    {
        // Arrange: independent large headers plus our writer's metadata, with two tiny metadata items before the image.
        const long imageLength = 40L * 1024 * 1024 * 1024;
        using var seed = new MemoryStream(HeifFixtures.Create("metadata"));
        using var tree = BmffDocument.Load(seed, registry: HeifBoxRegistry.Default);
        var meta = HeifFixtures.Find<MetaBox>(tree.Boxes);
        var locations = HeifFixtures.Find<ItemLocationBox>(meta.Children);
        locations.LengthSize = 8;
        locations.OffsetSize = 8;
        locations.Entries[0].Extents = [new() { Offset = 6, Length = (ulong)imageLength }];
        locations.Entries[1].Extents = [new() { Offset = 0, Length = 4 }];
        locations.Entries[2].Extents = [new() { Offset = 4, Length = 2 }];
        foreach (ref var entry in locations.Entries.AsSpan()) { entry.ConstructionMethod = 1; }
        using var prefix = new MemoryStream();
        prefix.Write(HeifBoxTests.Write(tree.Boxes[0]));
        BmffBoxHeader.Create(BmffBoxType.Meta, meta.GetPayloadSize() + 16 + imageLength + 6).Write(prefix);
        prefix.Write(new byte[4]);
        foreach (var child in meta.Children) { prefix.Write(HeifBoxTests.Write(child)); }
        BmffBoxHeader.Create((BmffBoxType)0x69646174, imageLength + 6).Write(prefix);
        prefix.Write(new byte[] { 0, 0, 0, 0, 65, 66 });
        using var source = new HeifProbeStream(prefix.ToArray(), imageLength);
        using var document = HeifDocument.Load(source);
        var read = source.BytesRead;
        using var failedOutput = new HeifFailingOutput();
        // Act
        document.RemoveExif();
        document.RemoveXmp();
        Should.Throw<IOException>(() => document.Save(failedOutput));
        // Assert
        var idat = HeifFixtures.Find<ItemDataBox>(HeifFixtures.Find<MetaBox>(document.Document.Boxes).Children);
        idat.Payload.Length.ShouldBe(imageLength);
        idat.Payload.IsMaterialized.ShouldBeFalse();
        source.BytesRead.ShouldBe(read);
        HeifFixtures.Find<ItemLocationBox>(HeifFixtures.Find<MetaBox>(document.Document.Boxes).Children).Entries[0].Extents[0].Offset.ShouldBe(0UL);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Compaction relocates other references to retained data (ISO 14496-12 iloc and saio)")]
    public void Save_FragmentedExifAndAuxiliaryReference_RelocatesRetainedBytes()
    {
        // Arrange
        using var source = new MemoryStream(HeifErasureFixtures.WithAuxiliaryOffset(false, true));
        using var document = HeifDocument.Load(source);
        var image = HeifFixtures.Data(document.GetItem(4)!);
        // Act
        document.RemoveExif();
        using var output = new MemoryStream();
        document.Save(output);
        var movie = HeifFixtures.Find<MovieBox>(document.Document.Boxes);
        var table = (SampleTableBox)movie.Children[0];
        var encoded = HeifBoxTests.Write(table.Children[0]);
        var offset = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(16));
        // Assert
        output.ToArray().AsSpan((int)offset, image.Length).ToArray().ShouldBe(image);
        Encoding.ASCII.GetString(output.ToArray()).ShouldNotContain(HeifErasureFixtures.Marker, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Failed compaction restores payloads and permits a corrected retry")]
    public void Save_ReferenceToDeletedBytes_RollsBackPayloadAndLocationChanges()
    {
        // Arrange
        using var source = new MemoryStream(HeifErasureFixtures.WithAuxiliaryOffset(true, false));
        using var document = HeifDocument.Load(source);
        var media = HeifFixtures.Find<MediaDataBox>(document.Document.Boxes);
        var payload = media.Payload;
        document.RemoveExif();
        using var output = new MemoryStream();
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.Save(output)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        media.Payload.ShouldBe(payload);
        output.Length.ShouldBe(0);
        document.Document.Boxes.Remove(HeifFixtures.Find<MovieBox>(document.Document.Boxes));
        document.Save(output);
        Encoding.ASCII.GetString(output.ToArray()).ShouldNotContain(HeifErasureFixtures.Marker, Case.Sensitive);
        output.Position = 0;
        using var loaded = HeifDocument.Load(output);
        HeifFixtures.Data(loaded.GetItem(4)!).ShouldBe(new byte[] { 0x91, 0x92, 0x93, 0x94 });
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Physical Exif/XMP erasure preserves all images (ISO 23008-12 metadata items; ISO 14496-12 iloc)")]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Save_RemovedMetadata_ContainsNoMarkersAndPreservesImages(bool idat, bool secondMdat, bool zeroLength)
    {
        // Arrange
        var original = HeifErasureFixtures.Create(idat, secondMdat: secondMdat, zeroLength: zeroLength);
        Encoding.ASCII.GetString(original).ShouldContain(HeifErasureFixtures.Marker, Case.Sensitive);
        using var source = new MemoryStream(original);
        using var document = HeifDocument.Load(source);
        var first = HeifFixtures.Data(document.GetItem(1)!);
        var fourth = HeifFixtures.Data(document.GetItem(4)!);

        // Act
        document.RemoveExif().ShouldBe(1);
        document.RemoveXmp().ShouldBe(1);
        using var output = new MemoryStream();
        await document.SaveAsync(output, CancellationToken.None);
        var saved = output.ToArray();
        output.Position = 0;
        using var loaded = HeifDocument.Load(output);

        // Assert
        Encoding.ASCII.GetString(saved).ShouldNotContain(HeifErasureFixtures.Marker, Case.Sensitive);
        Encoding.ASCII.GetString(saved).ShouldNotContain(HeifErasureFixtures.XmpMarker, Case.Sensitive);
        loaded.Items.Count.ShouldBe(2);
        HeifFixtures.Data(loaded.GetItem(1)!).ShouldBe(first);
        HeifFixtures.Data(loaded.GetItem(4)!).ShouldBe(fourth);
        foreach (var item in document.Items) { foreach (var segment in item.Data) { segment.IsMaterialized.ShouldBeFalse(); } }
        using var again = new MemoryStream();
        document.Save(again);
        again.ToArray().ShouldBe(saved);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Shared extents retain exactly the bytes needed by surviving items (ISO 14496-12 iloc)")]
    [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 2)] [InlineData(true, 2)]
    public void Save_SharedDeletedExtents_PreservesOverlappingRetainedBytes(bool idat, int sharing)
    {
        // Arrange
        using var source = new MemoryStream(HeifErasureFixtures.Create(idat, sharing));
        using var document = HeifDocument.Load(source);
        var retained = HeifFixtures.Data(document.GetItem(4)!);
        // Act
        document.RemoveItem(2);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var loaded = HeifDocument.Load(output);
        // Assert
        HeifFixtures.Data(loaded.GetItem(4)!).ShouldBe(retained);
        loaded.ExifItems.ShouldBeEmpty();
        Encoding.ASCII.GetString(output.ToArray()).Contains(HeifErasureFixtures.Marker, StringComparison.Ordinal).ShouldBe(sharing == 1);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Replacement and repeated edits erase obsolete payloads (ISO 23008-12 metadata items)")]
    [InlineData(false)] [InlineData(true)]
    public void Save_ReplacedThenRemovedExif_ErasesAllVersionsAcrossSaves(bool idat)
    {
        // Arrange
        using var source = new MemoryStream(HeifErasureFixtures.Create(idat));
        using var document = HeifDocument.Load(source);
        var image = HeifFixtures.Data(document.GetItem(4)!);
        var replacement = new byte[] { 0, 0, 0, 0, 0xfd, 0xae, 0xbd, 0xde, 0xac };
        // Act
        document.ReplaceExif(2, BinarySegment.Create(replacement));
        using var first = new MemoryStream();
        document.Save(first);
        document.RemoveExif();
        document.RemoveItem(4);
        using var second = new MemoryStream();
        document.Save(second);
        // Assert
        Encoding.ASCII.GetString(first.ToArray()).ShouldNotContain(HeifErasureFixtures.Marker, Case.Sensitive);
        second.ToArray().AsSpan().IndexOf(replacement.AsSpan(4)).ShouldBe(-1);
        second.ToArray().AsSpan().IndexOf(image).ShouldBe(-1);
        second.Position = 0;
        using var loaded = HeifDocument.Load(second);
        HeifFixtures.Data(loaded.PrimaryItem!).ShouldBe(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 });
        loaded.GetItem(3).ShouldNotBeNull();
    }
}
