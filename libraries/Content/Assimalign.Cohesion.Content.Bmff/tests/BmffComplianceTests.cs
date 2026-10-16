using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffComplianceTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO 14496-12 header truncation corpus")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryRead_EveryHeaderPrefix_ReportsTruncation(bool large, bool uuid)
    {
        // Arrange
        var bytes = BmffFixtures.Box(uuid ? "uuid" : "free", large: large);
        // Act / Assert
        TruncationCorpus.AssertPrefixes(bytes, prefix =>
        {
            using var memory = new MemoryStream(prefix.ToArray());
            using var chunked = new ChunkedReadStream(memory, 1);
            using var input = new NonSeekableStream(chunked);
            return BmffBoxHeader.TryRead(input, 100, null, true, out _);
        }, allowEmpty: true);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO 14496-12 document golden round trip")]
    public void Save_MinimalFile_PreservesEveryByte()
    {
        // Arrange
        var bytes = BmffFixtures.Minimal();
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        source.Position.ShouldBe(source.Length);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        output.ToArray().ShouldBe(bytes);
        document.Boxes.Count.ShouldBe(4);
        ((FileTypeBox)document.Boxes[0]).GetMajorBrand().ShouldBe("isom");
        ((MediaDataBox)document.Boxes[2]).Payload.IsMaterialized.ShouldBeFalse();
        source.Position.ShouldBe(source.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Opaque and UUID headers preserve wire encodings")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void Save_UnknownBox_PreservesHeaderAndPayload(bool large, bool uuid, bool zero)
    {
        // Arrange
        var bytes = BmffFixtures.Box(uuid ? "uuid" : "zzzz", new byte[] { 0, 0xff, 1, 2 }, large, zero);
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source, new BmffReaderOptions { MaxBufferedPayloadSize = 0 });
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        var unknown = document.Boxes[0].ShouldBeOfType<UnknownBox>();
        unknown.Payload.IsMaterialized.ShouldBeFalse();
        unknown.PreservationReason.ShouldBe(ContentFormatErrorCode.Unsupported);
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Seekable scanning skips a synthetic 40 GiB mdat")]
    public void Read_LargeMediaPayload_DoesNotReadMediaBytes()
    {
        // Arrange
        using var source = new SparseMediaStream();
        // Act
        using var document = BmffDocument.Load(source, new BmffReaderOptions { MaxBufferedPayloadSize = 0 });
        // Assert
        document.Boxes.Count.ShouldBe(2);
        ((MediaDataBox)document.Boxes[0]).Payload.Length.ShouldBe(SparseMediaStream.MediaSize);
        source.BytesRead.ShouldBe(24);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Forward-only scan exposes mdat before consuming it")]
    public void Read_NonSeekableSource_ReachesFinalBoxWithBoundedTransfers()
    {
        // Arrange
        var bytes = BmffFixtures.Join(BmffFixtures.Box("mdat", new byte[300000]), BmffFixtures.Box("free"));
        using var memory = new MemoryStream(bytes);
        using var source = new ProbeStream(memory, false);
        using var reader = BmffReader.Create(source, new BmffReaderOptions { MaxBufferedPayloadSize = 0 });
        // Act
        reader.Read().ShouldBeTrue();
        var bytesAfterHeader = source.BytesRead;
        reader.Payload.CopyTo(Stream.Null, 4096); // Caller consumes the bounded media stream.
        reader.Read().ShouldBeTrue();
        var finalType = reader.Header.BoxType;
        reader.Read().ShouldBeFalse();
        // Assert
        bytesAfterHeader.ShouldBe(8);
        finalType.ShouldBe(BmffBoxType.FreeSpace);
        source.MaxReadRequest.ShouldBeLessThanOrEqualTo(4096);
        source.BytesRead.ShouldBe(bytes.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Skipping forward-only mdat uses a fixed transfer buffer")]
    public void Skip_NonSeekableMedia_DrainsWithoutMaterialization()
    {
        // Arrange
        using var memory = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("mdat", new byte[300000]), BmffFixtures.Box("free")));
        using var source = new ProbeStream(memory, false);
        using var reader = BmffReader.Create(source, new BmffReaderOptions { MaxBufferedPayloadSize = 0 });
        // Act
        reader.Read().ShouldBeTrue();
        reader.Skip();
        reader.Read().ShouldBeTrue();
        // Assert
        reader.Header.BoxType.ShouldBe(BmffBoxType.FreeSpace);
        source.MaxReadRequest.ShouldBeLessThanOrEqualTo(131072); // ArrayPool may round the 81920-byte request.
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Non-seekable container descent retains nested bounds")]
    public void ReadChildren_NonSeekableContainers_ReachesSibling()
    {
        // Arrange
        using var memory = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("moov", BmffFixtures.Box("trak", BmffFixtures.Box("free"))), BmffFixtures.Box("skip")));
        using var source = new NonSeekableStream(memory, leaveOpen: true);
        using var reader = BmffReader.Create(source);
        // Act
        reader.Read().ShouldBeTrue();
        using (var movie = reader.ReadChildren())
        {
            movie.Read().ShouldBeTrue();
            using (var track = movie.ReadChildren())
            {
                track.Read().ShouldBeTrue();
                track.Header.BoxType.ShouldBe(BmffBoxType.FreeSpace);
                track.Read().ShouldBeFalse();
            }
            movie.Read().ShouldBeFalse();
        }
        reader.Read().ShouldBeTrue();
        // Assert
        reader.Header.BoxType.ShouldBe(BmffBoxType.Skip);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Invalid declared lengths are rejected before allocation")]
    [InlineData(7u)]
    [InlineData(100u)]
    [InlineData(uint.MaxValue)]
    public void Read_InvalidDeclaredSize_ReportsInvalidStructure(uint size)
    {
        // Arrange
        var bytes = BmffFixtures.Box("zzzz");
        BinaryPrimitives.WriteUInt32BigEndian(bytes, size);
        using var source = new MemoryStream(bytes);
        using var reader = BmffReader.Create(source);
        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.Read());
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Unsigned sizes beyond Int64 are classified as limits")]
    public void Read_UnrepresentableSize_ReportsLimitExceeded()
    {
        // Arrange
        var bytes = BmffFixtures.Box("free", large: true);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), ulong.MaxValue);
        using var source = new MemoryStream(bytes);
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Nested child sizes cannot escape their parent")]
    public void Load_OversizedChild_ReportsChildOffset()
    {
        // Arrange
        var child = BmffFixtures.Box("zzzz");
        BinaryPrimitives.WriteUInt32BigEndian(child, 9);
        using var source = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("moov", child), new byte[] { 1 }));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Nested zero sizes are invalid")]
    public void Load_NestedZeroSize_ReportsInvalidStructure()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("moov", BmffFixtures.Box("free", zero: true)));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Zero-sized non-seekable boxes consume only to EOF")]
    public void Read_ZeroSizeOnUnknownLength_EndsAtEof()
    {
        // Arrange
        using var memory = new MemoryStream(BmffFixtures.Box("mdat", new byte[19], zero: true));
        using var source = new NonSeekableStream(memory, leaveOpen: true);
        using var reader = BmffReader.Create(source);
        // Act
        reader.Read().ShouldBeTrue();
        var size = reader.Header.TotalSize;
        reader.Skip();
        var more = reader.Read();
        // Assert
        size.ShouldBeNull();
        more.ShouldBeFalse();
        memory.Position.ShouldBe(memory.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Depth bombs stop at the configured boundary")]
    public void Load_DepthBomb_ReportsLimitExceeded()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.DepthBomb(10));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { MaxDepth = 3 }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(32);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Materialization caps are checked before leaf reads")]
    public void Load_OversizedFileType_ReportsLimitExceeded()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("ftyp", new byte[12]));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { MaxBufferedPayloadSize = 8 }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        source.Position.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Truncated forward-only payloads carry absolute positions")]
    public void Skip_TruncatedPayload_ReportsMissingByte()
    {
        // Arrange
        var bytes = BmffFixtures.Box("mdat", new byte[1]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 12);
        using var memory = new MemoryStream(bytes);
        using var source = new NonSeekableStream(memory);
        using var reader = BmffReader.Create(source);
        reader.Read().ShouldBeTrue();
        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.Skip());
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(9);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Custom registries are immutable snapshots")]
    public void Build_ReplacedFactory_DoesNotMutatePreviousRegistry()
    {
        // Arrange
        var type = FourCC.Parse("test").ToBmffBoxType();
        var builder = BmffBoxRegistry.CreateBuilder().Register(type, header => new ProfileBox(header));
        var registry = builder.Build();
        builder.Register(type, _ => throw new InvalidOperationException());
        using var source = new MemoryStream(BmffFixtures.Box("test", new byte[4]));
        // Act
        using var document = BmffDocument.Load(source, registry: registry);
        // Assert
        document.Boxes[0].ShouldBeOfType<ProfileBox>();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Unsupported full-box prefixes are preserved or rejected")]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_UnsupportedVersion_ObeysStrictPolicy(bool strict)
    {
        // Arrange
        var bytes = BmffFixtures.Box("meta", new byte[] { 1, 0, 0, 0 });
        using var source = new MemoryStream(bytes);
        // Act / Assert
        if (strict)
        {
            var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { Strict = true }));
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        }
        else
        {
            using var document = BmffDocument.Load(source);
            document.Boxes[0].ShouldBeOfType<UnknownBox>();
            using var output = new MemoryStream();
            document.Save(output);
            output.ToArray().ShouldBe(bytes);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - The default visitor traverses without recursive redispatch")]
    public void Accept_DefaultVisitor_VisitsEachNodeOnce()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Minimal());
        using var document = BmffDocument.Load(source);
        var visitor = new CountingVisitor();
        // Act
        document.Boxes[1].Accept(visitor);
        // Assert
        visitor.Count.ShouldBe(5);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Full-box flags retain all 24 bits")]
    public void Flags_MaximumValue_PreservesBitsAndRejectsOverflow()
    {
        // Arrange
        var box = new ProfileBox(BmffBoxHeader.Create(FourCC.Parse("test").ToBmffBoxType(), 4));
        // Act
        box.Flags = 0xffffff;
        var error = Should.Throw<ArgumentOutOfRangeException>(() => box.Flags = 0x1000000);
        // Assert
        box.Flags.ShouldBe(0xffffffu);
        error.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Cancellation reaches the root reader and writer seam")]
    public async Task ReadAsync_CanceledToken_StopsBeforeIo()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Minimal());
        using var cursor = BmffReader.Create(source);
        IContentReader<BmffDocument> reader = cursor;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(source, cancellation.Token).AsTask());
        // Assert
        source.Position.ShouldBe(0);
    }
}
