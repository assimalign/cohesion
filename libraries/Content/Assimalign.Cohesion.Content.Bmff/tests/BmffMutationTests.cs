using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffMutationTests
{
    public static IEnumerable<object[]> LeafCases()
    {
        foreach (var value in BmffModelFixtures.Cases)
        {
            if (value.Type is "meta" or "tref")
            {
                continue;
            }

            yield return [value.Type, "ISO/IEC 14496-12 " + value.Clause];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 strategic fields preserve trailing bytes")]
    [MemberData(nameof(LeafCases))]
    public async Task Save_ParsedTrailingBytes_PreservesEncoding(string type, string clause)
    {
        // Arrange
        var encoded = BmffModelFixtures.Write(BmffModelFixtures.Create(type));
        var bytes = BmffFixtures.Box(type, BmffFixtures.Join(encoded.AsSpan(8).ToArray(), new byte[] { 0xf1, 0xa2, 0xb3 }), large: true);
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        // Act
        await document.SaveAsync(output, CancellationToken.None);
        // Assert
        clause.ShouldStartWith("ISO/IEC 14496-12");
        (document.Boxes[0] is UnknownBox).ShouldBeFalse();
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.5 fast start relocates chunk data")]
    public void Save_MovieMovedBeforeMedia_PreservesSampleBytesAndIsIdempotent()
    {
        // Arrange
        var media = new byte[] { 11, 12, 13, 21, 22, 23 };
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0), new ChunkOffsetBox(0, 0) { Entries = [8, 11] });
        using var source = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("mdat", media), BmffModelFixtures.Write(movie)));
        using var document = BmffDocument.Load(source);
        var loadedMovie = (MovieBox)document.Boxes[1];
        document.Boxes.RemoveAt(1);
        document.Boxes.Insert(0, loadedMovie);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        // Act
        document.RelocateMediaOffsets();
        document.Save(first);
        document.Save(second);
        // Assert
        var chunks = loadedMovie.Children[0].ShouldBeOfType<ChunkOffsetBox>();
        var bytes = first.ToArray();
        bytes.AsSpan((int)chunks.Entries[0], 3).ToArray().ShouldBe(media.AsSpan(0, 3).ToArray());
        bytes.AsSpan((int)chunks.Entries[1], 3).ToArray().ShouldBe(media.AsSpan(3, 3).ToArray());
        second.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.5 and 8.11.3 growth and shrink relocate references")]
    public void Save_GrowthThenShrink_UpdatesChunkItemAndFragmentBases()
    {
        // Arrange
        var chunks = new ChunkOffsetBox(0, 0) { Entries = [8] };
        var baseBox = new TrackFragmentHeaderBox(0, 0) { Flags = 1, BaseDataOffset = 8 };
        var locations = new ItemLocationBox(0, 0) { Entries = [
            new() { ItemId = 1, BaseOffset = 8, Extents = [new() { Length = 3 }] },
            new() { ItemId = 2, BaseOffset = 0, Extents = [new() { Offset = 8, Length = 3 }] },
            new() { ItemId = 3, DataReferenceIndex = 1, BaseOffset = 8, Extents = [new() { Length = 3 }] }
        ] };
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0), chunks, baseBox, locations);
        using var source = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("mdat", [1, 2, 3]), BmffModelFixtures.Write(movie)));
        using var document = BmffDocument.Load(source);
        var loadedMovie = (MovieBox)document.Boxes[1];
        document.Boxes.RemoveAt(1);
        document.Boxes.Insert(0, loadedMovie);
        chunks = (ChunkOffsetBox)loadedMovie.Children[0];
        baseBox = (TrackFragmentHeaderBox)loadedMovie.Children[1];
        locations = (ItemLocationBox)loadedMovie.Children[2];
        var extra = BmffModelFixtures.Opaque("test", [1, 2, 3]);
        loadedMovie.Children.Insert(0, extra);
        // Act
        document.RelocateMediaOffsets();
        var grown = chunks.Entries[0];
        loadedMovie.Children.Remove(extra);
        document.RelocateMediaOffsets();
        // Assert
        chunks.Entries[0].ShouldBe(grown - 11);
        baseBox.BaseDataOffset.ShouldBe((ulong)chunks.Entries[0]);
        locations.Entries[0].BaseOffset.ShouldBe((ulong)chunks.Entries[0]);
        locations.Entries[1].Extents[0].Offset.ShouldBe((ulong)chunks.Entries[0]);
        locations.Entries[2].BaseOffset.ShouldBe(8UL);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.5 promotion includes its own size delta")]
    public void RelocateMediaOffsets_OverflowingChunk_PromotesAndConvergesWithoutMediaReads()
    {
        // Arrange
        const uint original = uint.MaxValue - 1;
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0), new ChunkOffsetBox(0, 0) { Entries = [original] });
        using var source = new SparseMediaStream(BmffModelFixtures.Write(movie));
        using var document = BmffDocument.Load(source);
        var loaded = (MovieBox)document.Boxes[1];
        document.Boxes.RemoveAt(1);
        document.Boxes.Insert(0, loaded);
        // Act
        document.RelocateMediaOffsets();
        document.RelocateMediaOffsets();
        // Assert
        var offsets = loaded.Children[0].ShouldBeOfType<ChunkOffset64BitBox>();
        offsets.Entries[0].ShouldBe((ulong)original + (ulong)loaded.GetPayloadSize() + 8);
        source.BytesRead.ShouldBeLessThan(100);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.8.7-12 rebuilds relative fragment offsets")]
    public void Save_RebuiltMoof_UpdatesRunAndRandomAccess()
    {
        // Arrange
        var run = new TrackFragmentRunBox(0, 0) { Flags = 0x201, DataOffset = 0, Entries = [new() { Size = 3 }] };
        var moof = BmffModelFixtures.Container(new MovieFragmentBox(0, 0), new MovieFragmentHeaderBox(0, 0),
            BmffModelFixtures.Container(new TrackFragmentBox(0, 0), new TrackFragmentHeaderBox(0, 0) { Flags = 0x20000 }, run));
        run.DataOffset = (int)moof.GetPayloadSize() + 16;
        var mfra = BmffModelFixtures.Container(new MovieFragmentRandomAccessBox(0, 0),
            new TrackFragmentRandomAccessBox(0, 0) { Entries = [new() { MoofOffset = 0, TrafNumber = 1, TrunNumber = 1, SampleNumber = 1 }] },
            new MovieFragmentRandomAccessOffsetBox(0, 0));
        ((MovieFragmentRandomAccessOffsetBox)mfra.Children[1]).MfraSize = (uint)mfra.GetPayloadSize() + 8;
        using var source = new MemoryStream(BmffFixtures.Join(BmffModelFixtures.Write(moof), BmffFixtures.Box("mdat", [7, 8, 9]), BmffModelFixtures.Write(mfra)));
        using var document = BmffDocument.Load(source);
        var loaded = (MovieFragmentBox)document.Boxes[0];
        var loadedRun = (TrackFragmentRunBox)((TrackFragmentBox)loaded.Children[1]).Children[1];
        var oldOffset = loadedRun.DataOffset;
        loaded.Children.Add(BmffModelFixtures.Opaque("test", [42]));
        document.Boxes.Insert(0, BmffModelFixtures.Opaque("test", [1, 2]));
        using var output = new MemoryStream();
        // Act
        document.Save(output);
        // Assert
        loadedRun.DataOffset.ShouldBe(oldOffset + 9);
        output.ToArray().AsSpan(10 + loadedRun.DataOffset!.Value, 3).ToArray().ShouldBe(new byte[] { 7, 8, 9 });
        var loadedMfra = (MovieFragmentRandomAccessBox)document.Boxes[3];
        ((TrackFragmentRandomAccessBox)loadedMfra.Children[0]).Entries[0].MoofOffset.ShouldBe(10UL);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.5 invalid relocation rolls back")]
    public void RelocateMediaOffsets_RemovedMedia_RestoresOriginalReferences()
    {
        // Arrange
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0), new ChunkOffsetBox(0, 0) { Entries = [8] });
        using var source = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("mdat", [1, 2]), BmffModelFixtures.Write(movie)));
        using var document = BmffDocument.Load(source);
        document.Boxes.RemoveAt(0);
        var offsets = (ChunkOffsetBox)((MovieBox)document.Boxes[0]).Children[0];
        var original = offsets.Entries;
        // Act
        var error = Should.Throw<ContentFormatException>(() => document.RelocateMediaOffsets());
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        ReferenceEquals(offsets.Entries, original).ShouldBeTrue();
        offsets.Entries[0].ShouldBe(8u);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 4.2 large child promotes ancestor header")]
    public void Write_LargeChild_EmitsLargeAncestorHeaderBeforePayload()
    {
        // Arrange: a 40 GiB synthetic payload refuses all media reads.
        using var source = new SparseMediaStream();
        var media = new MediaDataBox(0, 0) { Payload = BinarySegment.Create(source, 16, SparseMediaStream.MediaSize) };
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0), media);
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        // Act
        Should.Throw<InvalidOperationException>(() => writer.Write(movie));
        // Assert
        var headers = output.ToArray();
        BinaryPrimitives.ReadUInt32BigEndian(headers).ShouldBe(1u);
        BinaryPrimitives.ReadUInt64BigEndian(headers.AsSpan(8)).ShouldBe((ulong)SparseMediaStream.MediaSize + 32);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 4.2 and 8.7.5 profile prefixes count toward media positions")]
    public void Save_ProfilePrefixWithMedia_AccountsForPrefixDuringRelocation()
    {
        // Arrange: header(8), profile prefix(1), stco(20), mdat header(8), sample(3).
        var offsets = new ChunkOffsetBox(0, 0) { Entries = [37] };
        var bytes = BmffFixtures.Box("cont", BmffFixtures.Join(new byte[] { 42 }, BmffModelFixtures.Write(offsets), BmffFixtures.Box("mdat", [7, 8, 9])));
        var registry = BmffBoxRegistry.CreateBuilder().Register(FourCC.Parse("cont").ToBmffBoxType(), header => new ProfileContainer(header)).Build();
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source, registry: registry);
        var container = (ProfileContainer)document.Boxes[0];
        container.Children.Insert(0, BmffModelFixtures.Opaque("zzzz", [1]));
        using var output = new MemoryStream();
        // Act
        document.Save(output);
        // Assert
        var relocated = ((ChunkOffsetBox)container.Children[1]).Entries[0];
        relocated.ShouldBe(46u);
        output.ToArray().AsSpan((int)relocated, 3).ToArray().ShouldBe(new byte[] { 7, 8, 9 });
    }

}
