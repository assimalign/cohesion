using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffFlagVersionTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.2.2, 8.3.2 and 8.4.2 header versions retain raw fields")]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Read_HeaderVersions_RetainsTimesAndFixedPointBits(byte version)
    {
        // Arrange
        var time = version == 0 ? uint.MaxValue : (ulong)uint.MaxValue + 17;
        var movie = new MovieHeaderBox(0, 0) { Version = version, CreationTime = time, ModificationTime = time, Duration = time, Rate = FixedPoint16x16.FromRaw(0x10001), Volume = FixedPoint8x8.FromRaw(-17) };
        movie.Reserved[0] = 0x91;
        movie.PreDefined[5] = 0x83;
        var track = new TrackHeaderBox(0, 0) { Version = version, CreationTime = time, ModificationTime = time, Duration = time, Layer = -3 };
        var media = new MediaHeaderBox(0, 0) { Version = version, CreationTime = time, ModificationTime = time, Duration = time };
        var bytes = BmffFixtures.Join(BmffModelFixtures.Write(movie), BmffModelFixtures.Write(track), BmffModelFixtures.Write(media));
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        var parsed = document.Boxes[0].ShouldBeOfType<MovieHeaderBox>();
        parsed.CreationTime.ShouldBe(time);
        parsed.Duration.ShouldBe(time);
        parsed.Rate.RawValue.ShouldBe(0x10001);
        parsed.Volume.RawValue.ShouldBe((short)-17);
        document.Boxes[1].ShouldBeOfType<TrackHeaderBox>().CreationTime.ShouldBe(time);
        document.Boxes[2].ShouldBeOfType<MediaHeaderBox>().CreationTime.ShouldBe(time);
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.6.1.3 v0 composition offset is unsigned")]
    public void Read_UnsignedCompositionOffset_RetainsHighBit()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("ctts", BmffMalformedFixtures.Words(0, 1, 1, uint.MaxValue)));
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        document.Boxes[0].ShouldBeOfType<SampleCompositionTimeBox>().Entries[0].SampleOffset.ShouldBe((long)uint.MaxValue);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.5.2 sample-entry FourCCs use opaque context")]
    public void Read_RegisteredSampleEntryType_RemainsLazyAndOpaque()
    {
        // Arrange: hdlr is a registered leaf elsewhere, but stsd children are sample entries.
        var description = BmffModelFixtures.Container(new SampleDescriptionBox(0, 0), BmffModelFixtures.Opaque("hdlr", [1, 2, 3]));
        using var source = new MemoryStream(BmffModelFixtures.Write(description));
        // Act
        using var document = BmffDocument.Load(source, new BmffReaderOptions { Strict = true, MaxBufferedPayloadSize = 0 });
        // Assert
        var box = document.Boxes[0].ShouldBeOfType<SampleDescriptionBox>();
        box.Children[0].ShouldBeOfType<UnknownBox>().Payload.IsMaterialized.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.12.1-5 protection signalling retains opaque scheme details")]
    public void Read_ProtectionScheme_ParsesSignallingAndPreservesDetails()
    {
        // Arrange
        var protection = BmffModelFixtures.Container(new ProtectionSchemeInfoBox(0, 0),
            BmffModelFixtures.Create("frma"), BmffModelFixtures.Create("schm"),
            BmffModelFixtures.Container(new SchemeInformationBox(0, 0), BmffModelFixtures.Opaque("tenc", [0, 1, 2, 3])));
        var bytes = BmffModelFixtures.Write(protection);
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        var parsed = document.Boxes[0].ShouldBeOfType<ProtectionSchemeInfoBox>();
        parsed.Children[0].ShouldBeOfType<OriginalFormatBox>().DataFormat.ShouldBe(FourCC.Parse("avc1"));
        parsed.Children[1].ShouldBeOfType<SchemeTypeBox>().SchemeType.ShouldBe(FourCC.Parse("cenc"));
        ((SchemeInformationBox)parsed.Children[2]).Children[0].ShouldBeOfType<UnknownBox>();
        BmffModelFixtures.Write(parsed).ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.8.7 every tfhd presence flag is independent")]
    public void Read_AllHeaderFlagCombinations_PreservesOptionalFields()
    {
        // Arrange
        for (uint bits = 0; bits < 32; bits++)
        {
            var flags = (bits & 3) | ((bits & 28) << 1);
            var box = new TrackFragmentHeaderBox(0, 0)
            {
                Flags = flags,
                BaseDataOffset = (flags & 1) != 0 ? 0x100000001UL : null,
                SampleDescriptionIndex = (flags & 2) != 0 ? 2u : null,
                DefaultSampleDuration = (flags & 8) != 0 ? 3u : null,
                DefaultSampleSize = (flags & 16) != 0 ? 4u : null,
                DefaultSampleFlags = (flags & 32) != 0 ? 5u : null
            };
            using var source = new MemoryStream(BmffModelFixtures.Write(box));
            // Act
            using var document = BmffDocument.Load(source);
            // Assert
            var parsed = document.Boxes[0].ShouldBeOfType<TrackFragmentHeaderBox>();
            parsed.Flags.ShouldBe(flags);
            parsed.BaseDataOffset.ShouldBe(box.BaseDataOffset);
            parsed.SampleDescriptionIndex.ShouldBe(box.SampleDescriptionIndex);
            parsed.DefaultSampleDuration.ShouldBe(box.DefaultSampleDuration);
            parsed.DefaultSampleSize.ShouldBe(box.DefaultSampleSize);
            parsed.DefaultSampleFlags.ShouldBe(box.DefaultSampleFlags);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.8.8 every trun field flag and offset sign")]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Read_AllRunFlagCombinations_PreservesFields(byte version)
    {
        // Arrange
        for (uint bits = 0; bits < 64; bits++)
        {
            var flags = (bits & 1) | ((bits & 2) << 1) | ((bits & 60) << 6);
            if ((flags & 0x404) == 0x404)
            {
                continue; // ISO forbids both flag sources.
            }

            var box = new TrackFragmentRunBox(0, 0)
            {
                Version = version, Flags = flags, DataOffset = (flags & 1) != 0 ? -17 : null, FirstSampleFlags = (flags & 4) != 0 ? 9u : null,
                Entries = [new() { Duration = 10, Size = 11, Flags = 12, CompositionTimeOffset = version == 0 ? uint.MaxValue : -13 }]
            };
            var bytes = BmffModelFixtures.Write(box);
            using var source = new MemoryStream(bytes);
            // Act
            using var document = BmffDocument.Load(source);
            // Assert
            var parsed = document.Boxes[0].ShouldBeOfType<TrackFragmentRunBox>();
            parsed.DataOffset.ShouldBe(box.DataOffset);
            parsed.FirstSampleFlags.ShouldBe(box.FirstSampleFlags);
            parsed.Entries[0].Duration.ShouldBe((flags & 0x100) != 0 ? 10u : 0);
            parsed.Entries[0].Size.ShouldBe((flags & 0x200) != 0 ? 11u : 0);
            parsed.Entries[0].Flags.ShouldBe((flags & 0x400) != 0 ? 12u : 0);
            parsed.Entries[0].CompositionTimeOffset.ShouldBe((flags & 0x800) != 0 ? version == 0 ? uint.MaxValue : -13 : 0);
            BmffModelFixtures.Write(parsed).ShouldBe(bytes);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.11.3 iloc versions and widths remain explicit")]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Read_ItemLocationVersions_PreservesWidths(byte version)
    {
        // Arrange
        foreach (var width in new byte[] { 0, 4, 8 })
        {
            var box = new ItemLocationBox(0, 0) { Version = version, OffsetSize = width, LengthSize = width, BaseOffsetSize = width, IndexSize = version == 0 ? (byte)13 : width,
                Entries = [new() { ItemId = 1, ConstructionMethod = version == 0 ? (ushort)0 : (ushort)1, Reserved = version == 0 ? (ushort)0 : (ushort)0xabc, Extents = [new()] }] };
            var bytes = BmffModelFixtures.Write(box);
            using var source = new MemoryStream(bytes);
            // Act
            using var document = BmffDocument.Load(source);
            // Assert
            var parsed = document.Boxes[0].ShouldBeOfType<ItemLocationBox>();
            parsed.Version.ShouldBe(version);
            parsed.OffsetSize.ShouldBe(width);
            parsed.IndexSize.ShouldBe(box.IndexSize);
            BmffModelFixtures.Write(parsed).ShouldBe(bytes);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.3.3 odd compact table retains low padding nibble")]
    public void Read_OddCompactPadding_PreservesReservedBits()
    {
        // Arrange
        var payload = BmffFixtures.Join(BmffMalformedFixtures.Words(0, 4, 1), new byte[] { 0x3f });
        var bytes = BmffFixtures.Box("stz2", payload);
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        var box = document.Boxes[0].ShouldBeOfType<CompactSampleSizeBox>();
        box.Entries.ShouldBe(new ushort[] { 3 });
        BmffModelFixtures.Write(box).ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.9.3 sgpd fixed and explicit lengths")]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Read_GroupDescriptionVersions_PreservesDescriptions(byte version)
    {
        // Arrange
        var box = new SampleGroupDescriptionBox(0, 0) { Version = version, GroupingType = FourCC.Parse("roll"), DefaultLength = version == 1 ? 2u : 0u, Entries = [new byte[] { 0xff, 0xfe }, new byte[] { 0, 0 }] };
        var bytes = BmffModelFixtures.Write(box);
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        var parsed = document.Boxes[0].ShouldBeOfType<SampleGroupDescriptionBox>();
        parsed.Entries[0].ShouldBe(box.Entries[0]);
        parsed.Entries[1].ShouldBe(box.Entries[1]);
        BmffModelFixtures.Write(parsed).ShouldBe(bytes);
    }
}
