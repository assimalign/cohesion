using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public sealed class Mp4MalformedTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - CMAF initialization requires defaults (ISO/IEC 23000-19 7.3; ISO/IEC 14496-12 8.8.3)")]
    public void Load_InitializationWithoutTrex_ShouldRejectCmafClaim()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Fragmented(), document =>
        {
            while (document.Boxes.Count > 2) { document.Boxes.RemoveAt(2); }
            Mp4MediaFixtures.Find<MovieExtensionBox>((MovieBox)document.Boxes[1]).Children.Clear();
        });
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Unsupported offset relocation is explicit (ISO/IEC 14496-12 8.7.12, 8.16.3)")]
    [InlineData("sidx")][InlineData("saio")]
    public void FastStart_UnknownDependentOffsets_ShouldRefuseWithoutMovingMovie(string code)
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Progressive());
        using var document = Mp4Profile.LoadDocument(source);
        var movie = document.Boxes[2];
        document.Boxes.Add(Mp4Fixtures.Opaque(code, [0, 0, 0, 0]));
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4Profile.FastStart(document));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        document.Boxes[2].ShouldBeSameAs(movie);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Profile nesting budget (ISO/IEC 14496-12 4.2, 8.5.2)")]
    public void Load_DeepSampleEntryChildren_ShouldRetainBmffDepthLimit()
    {
        // Arrange
        var entry = Mp4Fixtures.Container(new Avc1SampleEntryBox(),
            Mp4Fixtures.Container(new Avc1SampleEntryBox(), new Avc1SampleEntryBox()));
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry));
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4Profile.LoadDocument(source, new BmffReaderOptions { MaxDepth = 2 }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Contradictory brands (ISO/IEC 14496-12 8.1.2; ISO/IEC 23000-19 7.3)")]
    [InlineData("M4A ")][InlineData("cmfc")][InlineData("cmf2")][InlineData("cmfs")][InlineData("cmfl")]
    [InlineData("dash")][InlineData("msdh")][InlineData("msix")]
    public void Load_BrandContradictsStructure_ShouldThrowOrDiagnose(string brand)
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
            ((FileTypeBox)document.Boxes[0]).MajorBrand = FourCC.Parse(brand).Value);
        using var strictSource = new MemoryStream(bytes);
        using var lenientSource = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(strictSource));
        using var file = Mp4File.Load(lenientSource, new Mp4ReaderOptions { Strict = false });
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(0);
        file.Diagnostics.Count.ShouldBeGreaterThan(0);
        foreach (var diagnostic in file.Diagnostics) { diagnostic.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure); }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Inconsistent table counts (ISO/IEC 14496-12 8.6.1.2, 8.7.3)")]
    public void Load_StszDisagreesWithStts_ShouldRejectAtTimingTable()
    {
        // Arrange
        long position = 0;
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
        {
            var times = Mp4MediaFixtures.Find<SampleDecodingTimeBox>((MovieBox)document.Boxes[2]);
            position = times.Offset;
            times.Entries[0].SampleCount--;
        });
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(position);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Chunk bounds (ISO/IEC 14496-12 8.7.5)")]
    public void Load_ChunkOutsideMdat_ShouldRejectAtChunkTable()
    {
        // Arrange
        long position = 0;
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
        {
            var offsets = Mp4MediaFixtures.Find<ChunkOffsetBox>((MovieBox)document.Boxes[2]);
            position = offsets.Offset;
            offsets.Entries[0] = 0;
        });
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(position);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Fragment data bounds (ISO/IEC 14496-12 8.8.7-8.8.8)")]
    [InlineData(int.MaxValue)][InlineData(int.MinValue)]
    public void Load_TrunOutsideFragment_ShouldRejectAtRun(int offset)
    {
        // Arrange
        long position = 0;
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Fragmented(), document =>
        {
            var run = Mp4MediaFixtures.Find<TrackFragmentRunBox>((MovieFragmentBox)document.Boxes[2]);
            position = run.Offset;
            run.DataOffset = offset;
        });
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(position);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Child box containment (ISO/IEC 14496-12 4.2, 8.5.2)")]
    public void Load_ConfigurationOverflowsSampleEntry_ShouldReject()
    {
        // Arrange
        var bytes = Mp4MediaFixtures.Progressive();
        var typeOffset = bytes.AsSpan().IndexOf("avcC"u8);
        typeOffset.ShouldBeGreaterThan(0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(typeOffset - 4), 0x7fffffff);
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(typeOffset - 4);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Every visual prefix truncation has absolute position (ISO/IEC 14496-12 8.5.2.2)")]
    public void Load_TruncatedVisualPrefix_ShouldReportAbsoluteEnd()
    {
        // Arrange / Act / Assert: independent byte vectors for all fixed-prefix boundaries.
        for (var length = 0; length < 78; length++)
        {
            var bytes = Mp4Fixtures.Write(Mp4Fixtures.Opaque("avc1", new byte[length]));
            using var source = new MemoryStream();
            source.Write(new byte[13]);
            source.Write(bytes);
            source.Position = 13;
            var error = Should.Throw<ContentFormatException>(() => Mp4Profile.LoadDocument(source));
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            error.Position.ShouldBe(13 + bytes.Length);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Reader discovery limits (ISO/IEC 14496-12 4.2, 8.5.2)")]
    public void Load_ExceededLimits_ShouldRejectBeforeExpansion()
    {
        // Arrange
        var bytes = Mp4MediaFixtures.Progressive();
        // Act / Assert
        using var trackSource = new MemoryStream(bytes);
        Should.Throw<ContentFormatException>(() => Mp4File.Load(trackSource, new Mp4ReaderOptions { MaxTrackCount = 0 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        using var prefixSource = new MemoryStream(bytes);
        Should.Throw<ContentFormatException>(() => Mp4File.Load(prefixSource, new Mp4ReaderOptions
        {
            ContainerOptions = new BmffReaderOptions { MaxBufferedPayloadSize = 77 }
        })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        using var runSource = new MemoryStream(Mp4MediaFixtures.Fragmented());
        Should.Throw<ContentFormatException>(() => Mp4File.Load(runSource, new Mp4ReaderOptions { MaxFragmentRunCount = 1 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - CMAF relative addressing policy (ISO/IEC 23000-19 7.3; ISO/IEC 14496-12 8.8.7)")]
    public void Load_CmafWithoutMoofBase_ShouldDiagnoseInLenientMode()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Fragmented(implicitBase: true), document =>
            ((FileTypeBox)document.Boxes[0]).MajorBrand = FourCC.Parse("cmfc").Value);
        using var source = new MemoryStream(bytes);
        // Act
        using var file = Mp4File.Load(source, new Mp4ReaderOptions { Strict = false });
        // Assert
        file.Diagnostics.Count.ShouldBe(2);
        foreach (var error in file.Diagnostics) { error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure); }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Unknown entry preservation (ISO/IEC 14496-12 8.5.2)")]
    public async Task Load_UnknownSampleEntry_ShouldPreserveAndRefuseUnsafeQueries()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
        {
            var description = Mp4MediaFixtures.Find<SampleDescriptionBox>((MovieBox)document.Boxes[2]);
            description.Children[0] = Mp4Fixtures.Opaque("zzzz", [1, 2, 3]);
        });
        using var source = new MemoryStream(bytes);
        // Act
        using var file = Mp4File.Load(source);
        // Assert
        file.Tracks[0].CodecId.ShouldBe("zzzz");
        file.Tracks[0].Samples.CanSeek.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await file.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None));
        using var output = new MemoryStream();
        file.Document.Save(output);
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Complex edit programs are explicit (ISO/IEC 14496-12 8.6.6)")]
    public void Load_NonUnitEdit_ShouldReportUnsupported()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(delay: true), document =>
            Mp4MediaFixtures.Find<EditListBox>((MovieBox)document.Boxes[2]).Entries[1].MediaRateInteger = 2);
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => Mp4File.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        error.Position.ShouldNotBeNull();
    }

}
