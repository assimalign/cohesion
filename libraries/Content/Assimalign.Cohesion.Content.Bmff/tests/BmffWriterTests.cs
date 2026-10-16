using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffWriterTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Large headers start exactly above the 32-bit boundary")]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_SizeBoundary_SelectsMinimalHeader(bool uuid)
    {
        // Arrange
        var type = uuid ? BmffBoxType.uuid : BmffBoxType.MediaData;
        var headerSize = uuid ? 24 : 8;
        Guid? extended = uuid ? Guid.Empty : null;
        // Act
        var small = BmffBoxHeader.Create(type, uint.MaxValue - headerSize, extendedType: extended);
        var large = BmffBoxHeader.Create(type, uint.MaxValue - headerSize + 1, extendedType: extended);
        // Assert
        small.HeaderSize.ShouldBe(headerSize);
        small.TotalSize.ShouldBe(uint.MaxValue);
        large.HeaderSize.ShouldBe(headerSize + 8);
        large.TotalSize.ShouldBe((long)uint.MaxValue + 9);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Editing nested trees recomputes ancestor sizes")]
    public void Save_ModifiedTree_RecomputesNestedSizes()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Minimal());
        using var document = BmffDocument.Load(source);
        var movie = (MovieBox)document.Boxes[1];
        movie.Children.Add(new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(new byte[3]) });
        ((FileTypeBox)document.Boxes[0]).CompatibleBrands = new[] { FourCC.Parse("iso6").Value };
        using var memory = new MemoryStream();
        using var output = new ProbeStream(memory, false);
        // Act
        document.Save(output);
        memory.Position = 0;
        using var reloaded = BmffDocument.Load(memory);
        // Assert
        var changedMovie = (MovieBox)reloaded.Boxes[1];
        changedMovie.Children.Count.ShouldBe(2);
        changedMovie.Header.TotalSize.ShouldBe(movie.Header.TotalSize + 11);
        ((FileTypeBox)reloaded.Boxes[0]).CompatibleBrands.Length.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Streaming containers work on non-seekable output")]
    public void EndContainer_NestedForwardOnlyOutput_EmitsValidTree()
    {
        // Arrange
        using var memory = new MemoryStream();
        using var output = new ProbeStream(memory, false);
        using var writer = BmffWriter.Create(output);
        // Act
        writer.BeginContainer(BmffBoxType.Movie);
        writer.BeginContainer(BmffBoxType.Track, 8);
        writer.Write(new FreeSpaceBox(0, 0));
        writer.EndContainer();
        writer.EndContainer();
        memory.Position = 0;
        using var document = BmffDocument.Load(memory);
        // Assert
        ((MovieBox)document.Boxes[0]).Children[0].ShouldBeOfType<TrackBox>();
        output.Disposed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Streaming buffers enforce their cap before output")]
    public void Write_BufferCap_RejectsOversizedContainer()
    {
        // Arrange
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output, maxBufferedPayloadSize: 8);
        writer.BeginContainer(BmffBoxType.Movie);
        // Act
        var error = Should.Throw<ContentFormatException>(() => writer.Write(new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(new byte[1]) }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Supplied streaming sizes must match emitted bytes")]
    public void EndContainer_ShortPayload_ReportsInvalidStructure()
    {
        // Arrange
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        writer.BeginContainer(BmffBoxType.Movie, 8);
        // Act
        var error = Should.Throw<ContentFormatException>(() => writer.EndContainer());
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Writer rejects cycles before emitting headers")]
    public void Write_CyclicTree_ReportsInvalidStructure()
    {
        // Arrange
        var movie = new MovieBox(0);
        movie.Children.Add(movie);
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        // Act
        var error = Should.Throw<ContentFormatException>(() => writer.Write(movie));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Zero-sized boxes must remain final")]
    public void Save_NonfinalZeroSize_RejectsBeforeOutput()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("zzzz", zero: true));
        using var document = BmffDocument.Load(source);
        document.Boxes.Add(new FreeSpaceBox(0, 0));
        using var output = new MemoryStream();
        // Act
        var error = Should.Throw<ContentFormatException>(() => document.Save(output));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Root writer seam saves asynchronously to forward-only output")]
    public async Task WriteAsync_RootContract_PreservesBytes()
    {
        // Arrange
        var bytes = BmffFixtures.Minimal();
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        using var memory = new MemoryStream();
        using var output = new ProbeStream(memory, false);
        using var concrete = BmffWriter.Create(output);
        IContentWriter<BmffDocument> writer = concrete;
        // Act
        await writer.WriteAsync(output, document, CancellationToken.None);
        // Assert
        memory.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Root writer seam retains configured depth limits")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_RootContractDepthLimit_RejectsNestedTree(bool asynchronous)
    {
        // Arrange
        using var document = new BmffDocument();
        var movie = new MovieBox(0);
        movie.Children.Add(new TrackBox(size: 0, offset: 0));
        document.Boxes.Add(movie);
        using var output = new MemoryStream();
        using var concrete = BmffWriter.Create(output, maxDepth: 0);
        IContentWriter<BmffDocument> writer = concrete;
        // Act
        var error = asynchronous
            ? await Should.ThrowAsync<ContentFormatException>(async () => await writer.WriteAsync(output, document, CancellationToken.None))
            : Should.Throw<ContentFormatException>(() => writer.Write(output, document));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        output.Length.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Duplex windows bound reads after writes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_DuplexForwardOnlyWindow_StopsAtItsEnd(bool asynchronous)
    {
        // Arrange
        using var memory = new MemoryStream(new byte[8]);
        using var source = new ProbeStream(memory, false);
        using var window = new BmffStream(source, 0, 4);
        window.Write(new byte[3]);
        var bytes = new byte[8];
        // Act
        var count = asynchronous
            ? await window.ReadAsync(bytes, CancellationToken.None)
            : window.Read(bytes);
        // Assert
        count.ShouldBe(1);
        window.Position.ShouldBe(4);
        source.BytesRead.ShouldBe(1);
        window.Read(bytes).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Duplex windows see their newly written bytes")]
    public void Read_DuplexSeekableWindow_SeesExtendedSource()
    {
        // Arrange
        using var memory = new MemoryStream();
        using var window = new BmffStream(memory, 0, 4);
        window.ReadByte().ShouldBe(-1);
        window.Write(new byte[] { 1, 2, 3, 4 });
        window.Position = 0;
        var bytes = new byte[8];
        // Act
        var count = window.Read(bytes);
        // Assert
        count.ShouldBe(4);
        bytes[3].ShouldBe((byte)4);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Explicit source ownership is honored")]
    public void Dispose_OwnedDocument_DisposesOnlyItsSource()
    {
        // Arrange
        using var memory = new MemoryStream(BmffFixtures.Minimal());
        using var source = new ProbeStream(memory, true);
        var document = BmffDocument.Load(source, leaveOpen: false);
        // Act
        document.Dispose();
        // Assert
        source.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Reader and writer never dispose borrowed streams")]
    public void Dispose_Cursors_LeaveCallerStreamsOpen()
    {
        // Arrange
        using var memory = new MemoryStream(BmffFixtures.Box("free"));
        using var source = new ProbeStream(memory, true);
        var reader = BmffReader.Create(source);
        var writer = BmffWriter.Create(source);
        // Act
        reader.Dispose();
        writer.Dispose();
        // Assert
        source.Disposed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Full-box item protection counts its children")]
    public void Save_ItemProtection_PreservesPrefixAndChildren()
    {
        // Arrange
        var bytes = BmffFixtures.Box("meta", BmffFixtures.Join(new byte[4], BmffFixtures.Box("ipro",
            BmffFixtures.Join(new byte[] { 0, 0, 0, 0, 0, 1 }, BmffFixtures.Box("sinf", BmffFixtures.Box("schi"))))));
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        var meta = document.Boxes[0].ShouldBeOfType<MetaBox>();
        meta.Children[0].ShouldBeOfType<ItemProtectionBox>().Children.Count.ShouldBe(1);
        output.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Invalid item-protection counts are structural errors")]
    [InlineData(0)]
    [InlineData(2)]
    public void Load_ItemProtectionCountMismatch_ReportsInvalidStructure(byte count)
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("ipro", BmffFixtures.Join(
            new byte[] { 0, 0, 0, 0, 0, count }, BmffFixtures.Box("sinf"))));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
}
