using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvIndexRegressionTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.5: Repeated cue seeks read only relevant cluster framing")]
    public async Task Seek_RepeatedCueQuery_AvoidsUnrelatedBlockReads()
    {
        // Arrange
        var bytes = MkvFixtures.Create(clusterCount: 64);
        using var source = new SparseReadStream(bytes, bytes.Length);
        using var document = MkvDocument.Load(source);
        var query = new MediaTimestamp(125, 2);
        await document.Tracks[0].Samples.SeekAsync(query, CancellationToken.None);
        var before = source.BytesRead;

        // Act
        var result = await document.Tracks[0].Samples.SeekAsync(query, CancellationToken.None);

        // Assert
        result!.Value.SyncSample.PresentationTime.ShouldBe(new MediaTimestamp(62, 1));
        result.Value.TargetSample.Index.ShouldBe(125);
        (source.BytesRead - before).ShouldBeLessThan(128);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.5: Cue codec-state changes refuse unsafe range claims")]
    public async Task Seek_CueCodecState_RefusesRange()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document =>
        {
            var positions = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1C53BB6B).Children[0], 0xB7);
            positions.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xEA), EbmlElementType.UnsignedInteger, 1UL));
        }, cues: true);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act / Assert
        await Should.ThrowAsync<NotSupportedException>(() => document.Tracks[0].Samples.SeekAsync(new MediaTimestamp(2, 1), CancellationToken.None).AsTask());
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§10.4,11: Presentation reordering and ties preserve source ordinals")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindSample_ReorderedAndEqualTimes_UsesFloorAndLowestIndex(bool cues)
    {
        // Arrange
        var bytes = RewriteIndexed(document =>
        {
            var cluster = MkvFixtures.Find(document.Segment, 0x1F43B675);
            var first = true;
            foreach (var child in cluster.Children)
            {
                if (child.Identifier.EncodedValue != 0xA3) { continue; }
                var block = ((BinarySegment)child.Value!).ToArray(64);
                if (block[0] != 0x81) { continue; }
                child.Value = BinarySegment.Create(MkvFixtures.Block(1, first ? (short)500 : (short)0, first ? (byte)0x80 : (byte)0, new byte[] { 1 }));
                first = false;
            }
            cluster.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xA3), EbmlElementType.Binary,
                BinarySegment.Create(MkvFixtures.Block(1, 500, 0x80, new byte[] { 1 }))));
        }, cues: cues);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        var floor = await document.Tracks[0].Samples.FindSampleAsync(new MediaTimestamp(1, 4), CancellationToken.None);
        var tie = await document.Tracks[0].Samples.FindSampleAsync(new MediaTimestamp(1, 2), CancellationToken.None);
        var before = await document.Tracks[0].Samples.FindSampleAsync(new MediaTimestamp(-1, 1), CancellationToken.None);
        var seek = await document.Tracks[0].Samples.SeekAsync(new MediaTimestamp(3, 4), CancellationToken.None);

        // Assert
        floor!.Value.Index.ShouldBe(1);
        tie!.Value.Index.ShouldBe(0);
        before.ShouldBeNull();
        seek!.Value.SyncSample.Index.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.3,10.4: BlockGroup references change sync status and range coverage")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seek_BlockGroupReference_CoversResolvedDependency(bool cues)
    {
        // Arrange
        var bytes = RewriteIndexed(document =>
        {
            var cluster = MkvFixtures.Find(document.Segment, 0x1F43B675);
            var group = EbmlElement.Create(MkvFixtures.Id(0xA0), EbmlElementType.MasterElement);
            group.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xA1), EbmlElementType.Binary,
                BinarySegment.Create(MkvFixtures.Block(1, 750, 0, new byte[] { 1 }))));
            group.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xFB), EbmlElementType.SignedInteger, -750L));
            cluster.Children.Add(group);
        }, cues: cues);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        var result = await document.Tracks[0].Samples.SeekAsync(new MediaTimestamp(3, 4), CancellationToken.None);

        // Assert
        result!.Value.SyncSample.Index.ShouldBe(0);
        result.Value.TargetSample.Index.ShouldBe(2);
        result.Value.TargetSample.IsSync.ShouldBeFalse();
        result.Value.ByteLength.ShouldBeGreaterThan(result.Value.TargetSample.ByteLength);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.3,10.4: Unresolved references cannot claim a safe range")]
    public async Task Seek_UnresolvedBlockReference_RefusesRange()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document =>
        {
            var group = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1F43B675), 0xA0);
            group.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xFB), EbmlElementType.SignedInteger, 999L));
        }, metadata: true);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act / Assert
        // A later independent subtitle block supplies a sync candidate, but an unresolved native
        // dependency still prevents the conservative full-source range from being declared safe.
        await Should.ThrowAsync<NotSupportedException>(() => document.Tracks[2].Samples.SeekAsync(new MediaTimestamp(2, 1), CancellationToken.None).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10: Adversarial headers never leak primitive indexing or overflow errors")]
    public void Parse_DeterministicHostileCorpus_UsesFormatErrors()
    {
        // Arrange
        var random = new Random(9559);

        // Act / Assert
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var bytes = new byte[random.Next(0, 64)]; random.NextBytes(bytes);
            try
            {
                var block = MkvBlock.Parse(BinarySegment.Create(bytes), 500);
                foreach (var frame in block.Frames) { frame.Length.ShouldBeGreaterThanOrEqualTo(0); }
            }
            catch (ContentFormatException error)
            {
                error.ErrorCode.ShouldNotBe(ContentFormatErrorCode.Unspecified);
                error.Position!.Value.ShouldBeGreaterThanOrEqualTo(500);
            }
        }
    }

    private static byte[] RewriteIndexed(Action<MkvDocument> change, bool cues)
    {
        // These tests author different media blocks. Generic EBML save deliberately does not
        // relocate Matroska indexes, so patch the writer-produced fixture's fixed-width fields.
        var bytes = MkvMalformedTests.Rewrite(change, cues: cues);
        if (!cues) { return bytes; }
        using var source = new MemoryStream(bytes);
        using var native = EbmlDocument.Load(source, MkvSchema.Matroska);
        var segment = native.Body.Elements[0];
        var dataStart = segment.Offset + segment.Identifier.Length + segment.SizeLength;
        var clusters = new List<EbmlElement>();
        foreach (var element in segment.Children)
        { if (element.Identifier.EncodedValue == 0x1F43B675) { clusters.Add(element); } }
        var points = MkvFixtures.Find(segment, 0x1C53BB6B).Children;
        for (var index = 0; index < points.Count; index++)
        { Patch(MkvFixtures.Find(MkvFixtures.Find(points[index], 0xB7), 0xF1), clusters[index]); }
        foreach (var seek in MkvFixtures.Find(segment, 0x114D9B74).Children)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(((BinarySegment)MkvFixtures.Find(seek, 0x53AB).Value!).ToArray(4));
            Patch(MkvFixtures.Find(seek, 0x53AC), MkvFixtures.Find(segment, id));
        }
        return bytes;

        void Patch(EbmlElement field, EbmlElement target)
        {
            source.Position = field.Offset + field.Identifier.Length;
            var size = (int)VInt.Read(source, 8, null).Value;
            var offset = (int)source.Position;
            var value = (ulong)(target.Offset - dataStart);
            for (var index = size - 1; index >= 0; index--)
            { bytes[offset + index] = (byte)value; value >>= 8; }
            value.ShouldBe(0UL);
        }
    }
}
