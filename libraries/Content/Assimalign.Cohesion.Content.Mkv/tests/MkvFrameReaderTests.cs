using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvFrameReaderTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§10,22: Forward-only live frames match seekable SimpleBlock and Block frames for all laces")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public void ReadFrame_NonSeekableLiveSource_EqualsSeekableFrames(int chunkSize)
    {
        // Arrange — own writer emits unknown Segment/Clusters, all laces and BlockGroups.
        var bytes = MkvFixtures.Create(webm: false, cues: false, live: true, metadata: true, laces: true);
        using var seekable = new MemoryStream(bytes);
        var expected = MkvStreamingFixtures.Frames(seekable, streaming: false);
        using var memory = new MemoryStream(bytes);
        using var chunks = new ChunkedReadStream(memory, chunkSize);
        using var forward = new NonSeekableStream(chunks);

        // Act
        var actual = MkvStreamingFixtures.Frames(forward, streaming: true);

        // Assert
        actual.Count.ShouldBe(18);
        actual.Count.ShouldBe(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Track.ShouldBe(expected[i].Track);
            actual[i].Timestamp.ShouldBe(expected[i].Timestamp);
            actual[i].Lacing.ShouldBe(expected[i].Lacing);
            actual[i].Keyframe.ShouldBe(expected[i].Keyframe);
            actual[i].Invisible.ShouldBe(expected[i].Invisible);
            actual[i].Discardable.ShouldBe(expected[i].Discardable);
            actual[i].Offset.ShouldBe(expected[i].Offset);
            actual[i].Bytes.ShouldBe(expected[i].Bytes);
        }
        memory.Position.ShouldBe(memory.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: Partially read and disposed frames drain to the next lace boundary")]
    [InlineData(MkvLacing.Xiph)]
    [InlineData(MkvLacing.FixedSize)]
    [InlineData(MkvLacing.Ebml)]
    public void ReadFrame_PartialLace_DrainsAndInvalidates(MkvLacing lacing)
    {
        // Arrange
        var bytes = MkvStreamingFixtures.EncodeBlock(MkvFixtures.Laced(lacing));
        using var memory = new MemoryStream(bytes);
        using var forward = new NonSeekableStream(memory);
        using var reader = EbmlReader.Create(forward);
        reader.ReadNext().ShouldBeTrue();
        using var block = MkvBlockReader.Create(reader);
        using var first = block.ReadFrame()!;
        first.CanSeek.ShouldBeFalse();
        first.ReadByte().ShouldBe(1);
        first.Dispose();

        // Act
        using var second = block.ReadFrame()!;
        using var copied = new MemoryStream();
        second.CopyTo(copied);

        // Assert
        copied.ToArray().ShouldBe(lacing == MkvLacing.FixedSize ? new byte[] { 3, 4 } : new byte[] { 3, 4, 5 });
        Should.Throw<ObjectDisposedException>(() => first.ReadByte());
        using var last = block.ReadFrame()!;
        last.ReadByte().ShouldBe(lacing == MkvLacing.FixedSize ? 5 : 6);
        block.ReadFrame().ShouldBeNull();
        reader.ReadNext().ShouldBeFalse();
        memory.Position.ShouldBe(memory.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10: Early block disposal leaves EBML at the next sibling")]
    public void ReadNext_AbandonedBlock_DrainsPayload()
    {
        // Arrange
        var bytes = MkvStreamingFixtures.EncodeBlock(MkvFixtures.Laced(MkvLacing.Ebml));
        using var memory = new MemoryStream();
        memory.Write(bytes);
        memory.Write(new byte[] { 0xEC, 0x80 });
        memory.Position = 0;
        using var forward = new NonSeekableStream(memory);
        using var reader = EbmlReader.Create(forward);
        reader.ReadNext().ShouldBeTrue();
        var block = MkvBlockReader.Create(reader);
        using var frame = block.ReadFrame()!;
        frame.ReadByte().ShouldBe(1);
        block.Dispose();

        // Act
        reader.ReadNext().ShouldBeTrue();

        // Assert
        reader.Current.Identifier.EncodedValue.ShouldBe(0xECUL);
        reader.Current.Offset.ShouldBe(bytes.Length);
        Should.Throw<ObjectDisposedException>(() => frame.ReadByte());
        memory.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10: Large frames stream asynchronously above the EBML buffer cap")]
    public async Task ReadAsync_LargeFrame_StreamsWithoutMaterializing()
    {
        // Arrange
        var payload = new byte[4096];
        payload.AsSpan().Fill(0xAC);
        using var memory = new MemoryStream(MkvStreamingFixtures.EncodeBlock(MkvFixtures.Block(1, -5, 0x89, payload)));
        using var chunks = new ChunkedReadStream(memory, 7);
        using var forward = new NonSeekableStream(chunks);
        using var reader = EbmlReader.Create(forward, options: new EbmlReaderOptions { MaxBufferedValueLength = 0 });
        reader.ReadNext().ShouldBeTrue();
        using var block = MkvBlockReader.Create(reader);
        using var frame = block.ReadFrame()!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => frame.ReadAsync(new byte[1], cancellation.Token).AsTask());
        using var output = new MemoryStream();

        // Act
        await frame.CopyToAsync(output, CancellationToken.None);

        // Assert
        output.ToArray().ShouldBe(payload);
        block.RelativeTimestamp.ShouldBe((short)-5);
        block.IsKeyframe.ShouldBeTrue();
        block.IsInvisible.ShouldBeTrue();
        block.IsDiscardable.ShouldBeTrue();
        block.FrameCount.ShouldBe(1);
        block.ReadFrame().ShouldBeNull();
        reader.ReadNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10: Truncated forward-only frame bytes report the absolute source position")]
    public void ReadFrame_TruncatedPayload_ReportsAbsolutePosition()
    {
        // Arrange — an intact block header followed by fewer bytes than the declared frame.
        var bytes = MkvStreamingFixtures.EncodeBlock(MkvFixtures.Block(1, 0, 0x80, new byte[10]));
        using var memory = new MemoryStream();
        memory.Write(new byte[100]);
        memory.Write(bytes.AsSpan(0, bytes.Length - 3));
        using var window = ContentSliceStream.Create(memory, 100, memory.Length - 100);
        using var forward = ContentSliceStream.CreateForwardOnly(window, window.Length);
        using var reader = EbmlReader.Create(forward);
        reader.ReadNext().ShouldBeTrue();
        using var block = MkvBlockReader.Create(reader);
        using var frame = block.ReadFrame()!;

        // Act / Assert
        block.FrameOffsets[0].ShouldBe(106);
        var error = Should.Throw<ContentFormatException>(() => frame.CopyTo(Stream.Null));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(memory.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: Forward-only malformed laces match seekable error classifications")]
    [InlineData(0x82, new byte[] { 2, 100, 100, 1 })]
    [InlineData(0x84, new byte[] { 2, 1, 2 })]
    [InlineData(0x86, new byte[] { 2, 0x81, 0x80, 1 })]
    [InlineData(0x82, new byte[] { 0, 1 })]
    public void Create_InvalidLace_ReportsSameError(byte flags, byte[] data)
    {
        // Arrange
        var payload = MkvFixtures.Block(1, 0, flags, data);
        using var memory = new MemoryStream(MkvStreamingFixtures.EncodeBlock(payload));
        using var forward = new NonSeekableStream(memory);
        using var reader = EbmlReader.Create(forward);
        reader.ReadNext().ShouldBeTrue();
        var expected = Should.Throw<ContentFormatException>(() => MkvBlock.Parse(BinarySegment.Create(payload), reader.Current.DataOffset));

        // Act / Assert
        var actual = Should.Throw<ContentFormatException>(() => MkvBlockReader.Create(reader));
        actual.ErrorCode.ShouldBe(expected.ErrorCode);
        actual.Position.ShouldBe(expected.Position);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: Forward-only lace counts are capped before arrays are allocated")]
    public void Create_LaceCountOverLimit_ReportsLimit()
    {
        // Arrange
        using var memory = new MemoryStream(MkvStreamingFixtures.EncodeBlock(MkvFixtures.Laced(MkvLacing.FixedSize)));
        using var forward = new NonSeekableStream(memory);
        using var reader = EbmlReader.Create(forward);
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        Should.Throw<ContentFormatException>(() => MkvBlockReader.Create(reader, maxFrames: 2))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
}
