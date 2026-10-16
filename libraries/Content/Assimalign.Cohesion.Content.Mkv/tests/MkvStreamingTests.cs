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

public sealed class MkvStreamingTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §§6.2,8: Nonseekable live clusters skip blocks above buffer cap")]
    public void Read_LivePartialReads_DoesNotBufferBlocks()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document =>
        {
            document.Segment.IsUnknownSize = true;
            foreach (var element in document.Segment.Children)
            {
                if (element.Identifier.EncodedValue != 0x1F43B675) { continue; }
                element.IsUnknownSize = true;
                MkvFixtures.Find(element, 0xA3).Value = BinarySegment.Create(MkvFixtures.Block(1, 0, 0x80, new byte[4096]));
            }
        });
        using var memory = new MemoryStream(bytes);
        using var chunks = new ChunkedReadStream(memory, 3);
        using var forward = new NonSeekableStream(chunks);
        using var reader = EbmlReader.Create(forward, options: new EbmlReaderOptions { MaxBufferedValueLength = 64 }, registry: MkvSchema.Registry);
        var clusters = 0;

        // Act
        Walk();

        // Assert
        clusters.ShouldBe(3);
        memory.Position.ShouldBe(memory.Length);
        void Walk()
        {
            while (reader.ReadNext())
            {
                if (reader.Current.Identifier.EncodedValue == 0x1F43B675) { clusters++; }
                if (reader.Current.Type == EbmlElementType.MasterElement) { reader.EnterMaster(); Walk(); reader.LeaveMaster(); }
                else { reader.Skip(); }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §§6.2,8: Forward-only writer emits unknown-size masters")]
    public void Write_ForwardOnlyOutput_RoundTripsLiveMasters()
    {
        // Arrange
        using var input = new MemoryStream(MkvFixtures.Create(cues: false, live: true));
        using var document = MkvDocument.Load(input);
        using var output = new MemoryStream();
        using var forward = new ForwardWriteStream(output);

        // Act
        using (var writer = EbmlWriter.Create(forward, MkvSchema.Matroska))
        {
            writer.WriteElement(document.Ebml.Header.Element);
            writer.WriteElement(document.Segment);
        }
        output.Position = 0;
        using var reloaded = MkvDocument.Load(output);

        // Assert
        reloaded.IsLive.ShouldBeTrue();
        reloaded.Tracks.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §15: A 40 GiB block is lazy during document load and lookup")]
    public async Task Load_FortyGiBBlock_ReadsOnlyFraming()
    {
        // Arrange
        const long payloadLength = 40L * 1024 * 1024 * 1024;
        using var prefix = new MemoryStream();
        prefix.Write(MkvFixtures.Create(cues: false, live: true));
        MkvFixtures.Id(0xA3).Write(prefix); VInt.EncodeSize((ulong)payloadLength).Write(prefix);
        prefix.Write(new byte[] { 0x81, 0, 0, 0x80 });
        using var source = new SparseReadStream(prefix.ToArray(), prefix.Length + payloadLength - 4);

        // Act
        using var document = MkvDocument.Load(source);
        var sample = await document.Tracks[0].Samples.GetSampleAsync(6, CancellationToken.None);

        // Assert
        sample!.Value.ByteLength.ShouldBe(payloadLength - 4);
        source.BytesRead.ShouldBeLessThan(16_384);
        document.Tracks[0].Samples.Count.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §16: Nested source windows preserve absolute frame positions")]
    public async Task Load_NestedSourceWindow_UsesAbsoluteOffsets()
    {
        // Arrange
        var bytes = MkvFixtures.Create();
        using var source = new MemoryStream(); source.Write(new byte[100]); source.Write(bytes);
        using var outer = ContentSliceStream.Create(source, 40, source.Length - 40);
        using var inner = outer.CreateSlice(60, bytes.Length);
        using var document = MkvDocument.Load(inner);
        using var originalSource = new MemoryStream(bytes);
        using var original = MkvDocument.Load(originalSource);

        // Act
        var actual = await document.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None);
        var expected = await original.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None);

        // Assert
        actual!.Value.ByteOffset.ShouldBe(expected!.Value.ByteOffset + 100);
        using var copy = new MemoryStream(); document.Save(copy); copy.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10: Sample queries honor cancellation and borrowed lifetime")]
    public async Task ReadSamples_CancelAndDispose_HonorsLifetime()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create());
        var document = MkvDocument.Load(source);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => document.Tracks[0].Samples.GetSampleAsync(0, cancellation.Token).AsTask());
        await using (var samples = document.Tracks[0].Samples.ReadSamplesAsync(CancellationToken.None).GetAsyncEnumerator())
        { (await samples.MoveNextAsync()).ShouldBeTrue(); }
        document.Dispose();
        source.CanRead.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => document.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None).AsTask());
        source.Position = 0;
        await using (var owned = MkvDocument.Load(source, leaveOpen: false)) { owned.Tracks.Count.ShouldBe(2); }
        source.CanRead.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §6.2 and RFC 8794 §11.3: Edited-master CRC is removed")]
    public void Save_EditChecksummedInfo_RemovesOnlyAffectedChecksum()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document => MkvFixtures.Find(document.Segment, 0x1549A966).Children.Insert(0,
            EbmlElement.Create(MkvFixtures.Id(0xBF), EbmlElementType.Binary, BinarySegment.Create(new byte[4]))));
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source, new MkvReaderOptions { Ebml = new EbmlReaderOptions { VerifyCrc32 = true } });
        using var identical = new MemoryStream(); document.Save(identical); identical.ToArray().ShouldBe(bytes);
        var info = MkvFixtures.Find(document.Segment, 0x1549A966);
        MkvFixtures.Find(info, 0x7BA9).Value = "Changed title";

        // Act
        using var output = new MemoryStream(); using var forward = new ForwardWriteStream(output);
        document.Save(forward); output.Position = 0;
        using var reloaded = MkvDocument.Load(output, new MkvReaderOptions { Ebml = new EbmlReaderOptions { VerifyCrc32 = true } });

        // Assert
        reloaded.Name.ShouldBe("Changed title");
        foreach (var child in MkvFixtures.Find(reloaded.Segment, 0x1549A966).Children) { child.Identifier.EncodedValue.ShouldNotBe(0xBFUL); }
    }
}
