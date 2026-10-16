using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvMalformedTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.1: Every truncated block header is classified")]
    public void Parse_TruncatedBlockHeaders_ReportsAbsoluteTruncation()
    {
        // Arrange
        byte[] header = [0x40, 0x81, 0, 0, 0x80];

        // Act / Assert
        TruncationCorpus.AssertPrefixes(header, bytes =>
        {
            try { MkvBlock.Parse(BinarySegment.Create(bytes), 100); return true; }
            catch (ContentFormatException error) { error.Position.ShouldBe(100 + bytes.Length); throw; }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §§5,6: Every truncated EBML header is classified")]
    public void Read_TruncatedEbmlHeaders_UsesSharedCorpus()
    {
        // Arrange
        byte[] header = [0x1A, 0x45, 0xDF, 0xA3, 0x80];

        // Act / Assert
        TruncationCorpus.AssertPrefixes(header, bytes =>
        {
            using var memory = new MemoryStream(bytes.ToArray());
            using var chunks = new ChunkedReadStream(memory, 1);
            using var reader = EbmlReader.Create(chunks, MkvSchema.Matroska);
            return reader.ReadNext();
        }, allowEmpty: true);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: Lace lengths cannot exceed block bytes")]
    [InlineData(0x82, new byte[] { 2, 100, 100, 1 })]
    [InlineData(0x84, new byte[] { 2, 1, 2 })]
    [InlineData(0x86, new byte[] { 2, 0x81, 0x80, 1 })]
    [InlineData(0x82, new byte[] { 0, 1 })]
    public void Parse_InvalidLace_RejectsBounds(byte flags, byte[] data)
    {
        // Arrange
        var block = MkvFixtures.Block(1, 0, flags, data);

        // Act
        var error = Should.Throw<ContentFormatException>(() => MkvBlock.Parse(BinarySegment.Create(block), 200));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        error.Position!.Value.ShouldBeGreaterThanOrEqualTo(200);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.1: A block must name a declared track")]
    public async Task ReadSamples_UndeclaredTrack_ReportsStructureError()
    {
        // Arrange
        var bytes = MkvFixtures.Create();
        using (var original = MkvDocument.Load(new MemoryStream(bytes)))
        {
            var block = MkvFixtures.Find(MkvFixtures.Find(original.Segment, 0x1F43B675), 0xA3);
            bytes[block.Offset + block.Identifier.Length + block.SizeLength] = 0x87;
        }
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        var error = await Should.ThrowAsync<ContentFormatException>(() => document.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None).AsTask());

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position!.Value.ShouldBeGreaterThan(document.Segment.Offset);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.5,16: Cue positions stay inside their Segment")]
    public void Load_CueOutsideSegment_RejectsPosition()
    {
        // Arrange
        var bytes = Rewrite(document => MkvFixtures.Find(MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1C53BB6B).Children[0], 0xB7), 0xF1).Value = ulong.MaxValue, cues: true);

        // Act
        using var source = new MemoryStream(bytes);
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(source));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        error.Position.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §6: Oversize declared payload is refused without allocation")]
    public void Read_OversizeDeclaration_ReportsTruncation()
    {
        // Arrange
        using var source = new MemoryStream();
        VInt.FromEncoded(0x18538067).Write(source); VInt.EncodeSize(1UL << 45).Write(source); source.Position = 0;
        using var reader = EbmlReader.Create(source, MkvSchema.Matroska);

        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §§8.1,11.1: Recursive metadata depth is bounded")]
    public void Load_RecursiveTagBomb_ReportsDepthLimit()
    {
        // Arrange
        var bytes = Rewrite(document =>
        {
            var simple = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1254C367).Children[0], 0x67C8);
            for (var i = 0; i < 20; i++)
            {
                var next = EbmlElement.Create(MkvFixtures.Id(0x67C8), EbmlElementType.MasterElement);
                next.Children.Add(EbmlElement.Create(MkvFixtures.Id(0x45A3), EbmlElementType.Utf8String, "NEST"));
                simple.Children.Add(next); simple = next;
            }
        }, metadata: true);
        using var source = new MemoryStream(bytes);

        // Act
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(source, new MkvReaderOptions { Ebml = new EbmlReaderOptions { MaxDepth = 8 } }));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §15: Track, cue, node and buffer limits precede allocation")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Load_MetadataCaps_ReportsLimit(int kind)
    {
        // Arrange
        var options = new MkvReaderOptions();
        if (kind == 0) { options.MaxTracks = 1; }
        if (kind == 1) { options.MaxCuePoints = 1; }
        if (kind == 2) { options.Ebml.MaxElementCount = 4; }
        if (kind == 3) { options.Ebml.MaxBufferedValueLength = 4; }
        using var source = new MemoryStream(MkvFixtures.Create());

        // Act
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(source, options));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.6: Attachment bytes stay lazy until bounded buffering")]
    public void ReadAttachmentData_AboveCap_RefusesMaterialization()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, metadata: true));
        using var document = MkvDocument.Load(source, new MkvReaderOptions { MaxAttachmentBufferLength = 3 });
        var attached = MkvFixtures.Find(document.Segment, 0x1941A469).Children[0];

        // Act
        var error = Should.Throw<ContentFormatException>(() => document.ReadAttachmentData(attached));

        // Assert
        ((BinarySegment)MkvFixtures.Find(attached, 0x465C).Value!).IsMaterialized.ShouldBeFalse();
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: Frame count is capped before allocating lace arrays")]
    public void Parse_FrameLimit_ReportsLimit()
    {
        // Arrange / Act
        var error = Should.Throw<ContentFormatException>(() => MkvBlock.Parse(BinarySegment.Create(MkvFixtures.Laced(MkvLacing.Ebml)), 100, maxFrames: 2));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §11: Timestamp overflow never escapes as OverflowException")]
    public async Task ReadSamples_TimestampOverflow_ReportsInvalidValue()
    {
        // Arrange
        var bytes = Rewrite(document => MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1F43B675), 0xE7).Value = ulong.MaxValue);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        var error = await Should.ThrowAsync<ContentFormatException>(() => document.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None).AsTask());

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.4: Track identity is unique")]
    [InlineData(0xD7UL)] [InlineData(0x73C5UL)]
    public void Load_DuplicateTrackIdentity_RejectsStructure(ulong field)
    {
        // Arrange
        var bytes = Rewrite(document =>
        {
            var tracks = MkvFixtures.Find(document.Segment, 0x1654AE6B);
            MkvFixtures.Find(tracks.Children[1], field).Value = MkvFixtures.Find(tracks.Children[0], field).Value;
        });
        using var source = new MemoryStream(bytes);

        // Act
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(source));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §4.3 and WebM guidelines: Unsupported WebM codec is explicit")]
    public void Load_WebMWithMatroskaCodec_RejectsSubset()
    {
        // Arrange
        var bytes = Rewrite(document => MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0], 0x86).Value = "V_MPEG4/ISO/AVC", webm: true);
        using var source = new MemoryStream(bytes);

        // Act
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(source));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
    }

    internal static byte[] Rewrite(Action<MkvDocument> change, bool metadata = false, bool webm = false, bool cues = false)
    {
        using var input = new MemoryStream(MkvFixtures.Create(webm: webm, cues: cues, metadata: metadata));
        using var document = MkvDocument.Load(input);
        change(document);
        using var output = new MemoryStream(); document.Ebml.Save(output); return output.ToArray();
    }
}
