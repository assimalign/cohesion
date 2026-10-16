using System;
using System.Buffers.Binary;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffIndexRelocationTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - sidx ISO 14496-12 8.16.3 widens first offset without buffering")]
    public void Relocate_LargeGap_PromotesSegmentIndex()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Join(BmffIndexFixtures.Sidx(0, [12]), BmffFixtures.Box("mdat", [1, 2, 3, 4])));
        using var sparse = new SparseMediaStream();
        using var document = BmffDocument.Load(source);
        document.Boxes.Insert(1, new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(sparse, 16, SparseMediaStream.MediaSize) });
        // Act
        document.RelocateMediaOffsets();
        // Assert
        BmffIndexFixtures.Payload(document.Boxes[0])[0].ShouldBe((byte)1);
        BmffIndexFixtures.FirstOffset(document.Boxes[0]).ShouldBe((ulong)SparseMediaStream.MediaSize + 16);
        sparse.BytesRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Profile relocation rules are immutable, checkpointed and transactional")]
    [InlineData(false)] [InlineData(true)]
    public void Relocate_ProfileRule_AppliesOrRestoresCapturedFields(bool fail)
    {
        // Arrange: zzzz points at the first byte of mdat; invalid saio forces rollback after it updates.
        var bytes = BmffFixtures.Join(BmffFixtures.Box("zzzz", BmffMalformedFixtures.Words(0, 24)), BmffFixtures.Box("mdat", [7]),
            fail ? BmffIndexFixtures.Saio([9999]) : Array.Empty<byte>());
        var builder = BmffBoxRegistry.CreateBuilder().RegisterRelocation((BmffBoxType)0x7a7a7a7a, BmffRelocationProfile.Capture);
        var registry = builder.Build();
        builder.RegisterRelocation((BmffBoxType)0x7a7a7a7a, _ => throw new InvalidOperationException());
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source, registry: registry);
        var original = BmffModelFixtures.Write(document.Boxes[0]);
        // Act / Assert
        if (fail)
        {
            Should.Throw<ContentFormatException>(() => document.RelocateMediaOffsets(100));
            BmffModelFixtures.Write(document.Boxes[0]).ShouldBe(original);
        }
        else
        {
            document.RelocateMediaOffsets(100);
            document.RelocateMediaOffsets(200);
            document.RelocateMediaOffsets(200);
            BinaryPrimitives.ReadUInt64BigEndian(BmffIndexFixtures.Payload(document.Boxes[0])).ShouldBe(224UL);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Index table counts obey limits before allocation")]
    [InlineData("sidx")] [InlineData("saio")]
    public void Load_IndexCountBeyondLimit_ReportsLimitExceeded(string type)
    {
        // Arrange
        var bytes = type == "sidx" ? BmffIndexFixtures.Sidx(0, [12]) : BmffIndexFixtures.Saio([8]);
        using var source = new MemoryStream(bytes);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { MaxTableEntryCount = 0 }))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - sidx/saio ISO 14496-12 8.16.3 and 8.7.9 field preservation")]
    [InlineData((byte)0, false)] [InlineData((byte)1, true)]
    public void Save_Indices_RoundTripsFields(byte version, bool typed)
    {
        // Arrange
        var bytes = BmffFixtures.Join(BmffIndexFixtures.Sidx(0, [12], version), BmffFixtures.Box("mdat", [1, 2, 3, 4]), BmffIndexFixtures.Saio([8], version, typed));
        using var input = new MemoryStream(bytes);
        using var document = BmffDocument.Load(input, new BmffReaderOptions { Strict = true });
        using var output = new MemoryStream();
        // Act
        document.Save(output);
        // Assert
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - sidx ISO 14496-12 8.16.3 updates nested index size")]
    public void Relocate_NestedIndexAndGrownMedia_UpdatesBothReferenceSizes()
    {
        // Arrange: root sidx references the child sidx, which references the following mdat.
        var bytes = BmffFixtures.Join(BmffIndexFixtures.Sidx(0, [0x8000002c]), BmffIndexFixtures.Sidx(0, [12]), BmffFixtures.Box("mdat", [1, 2, 3, 4]));
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        ((MediaDataBox)document.Boxes[2]).Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5 });
        // Act
        document.RelocateMediaOffsets();
        // Assert
        BinaryPrimitives.ReadUInt32BigEndian(BmffIndexFixtures.Payload(document.Boxes[1]).AsSpan(24)).ShouldBe(13u);
        BinaryPrimitives.ReadUInt32BigEndian(BmffIndexFixtures.Payload(document.Boxes[0]).AsSpan(24)).ShouldBe(0x8000002cu);
        BmffIndexFixtures.FirstOffset(document.Boxes[0]).ShouldBe(0UL);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - saio ISO 14496-12 8.7.9 promotes and checkpoints")]
    public void Relocate_LargeOutputOffset_PromotesAuxiliaryOffsetsOnce()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Join(BmffIndexFixtures.Saio([28]), BmffFixtures.Box("mdat", [7])));
        using var document = BmffDocument.Load(source);
        // Act
        document.RelocateMediaOffsets(uint.MaxValue);
        var moved = BmffIndexFixtures.AuxiliaryOffset(document.Boxes[0]);
        document.RelocateMediaOffsets(uint.MaxValue);
        // Assert
        BmffIndexFixtures.Payload(document.Boxes[0])[0].ShouldBe((byte)1);
        moved.ShouldBe((ulong)uint.MaxValue + 32);
        BmffIndexFixtures.AuxiliaryOffset(document.Boxes[0]).ShouldBe(moved);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - saio ISO 14496-12 8.7.9 tfhd bases and opaque auxiliary bytes")]
    [InlineData(false)] [InlineData(true)]
    public void Relocate_FragmentAuxiliaryData_UsesTrackFragmentBase(bool explicitBase)
    {
        // Arrange: senc is opaque, saio points at its payload within moof.
        var header = new TrackFragmentHeaderBox(0, 0) { Flags = explicitBase ? 1u : 0x020000u, BaseDataOffset = explicitBase ? 0UL : null };
        var prefix = BmffModelFixtures.Write(header).Length;
        var saio = BmffIndexFixtures.Saio([(ulong)(16 + prefix + 20 + 8)]);
        var bytes = BmffFixtures.Box("moof", BmffFixtures.Box("traf", BmffFixtures.Join(BmffModelFixtures.Write(header), saio, BmffFixtures.Box("senc", [9, 8, 7]))));
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        var moof = (MovieFragmentBox)document.Boxes[0];
        var traf = (TrackFragmentBox)moof.Children[0];
        traf.Children.Insert(1, new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(new byte[5]) });
        // Act
        document.RelocateMediaOffsets(100);
        using var output = new MemoryStream();
        output.Position = 100;
        document.Save(output);
        // Assert
        var address = 100 + (long)BmffIndexFixtures.AuxiliaryOffset(traf.Children[2]);
        output.ToArray().AsSpan((int)address, 3).ToArray().ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - index bounds ISO 14496-12 8.16.3 and 8.7.9")]
    [InlineData("sidx")] [InlineData("saio")]
    public void Load_TruncatedIndex_ReportsFormatError(string type)
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box(type, [0, 0, 0, 0]));
        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldNotBeNull();
    }
}
