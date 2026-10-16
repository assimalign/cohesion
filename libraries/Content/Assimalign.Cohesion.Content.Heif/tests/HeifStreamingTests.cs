using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifStreamingTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 14496-12 8.11.11 forty GiB idat remains lazy")]
    public void Load_FortyGiBIdat_DoesNotReadImagePayload()
    {
        // Arrange: writer-authored metadata, then independently emitted large meta/idat headers.
        const long length = 40L * 1024 * 1024 * 1024;
        using var seed = new MemoryStream(HeifFixtures.Create("heic"));
        using var tree = BmffDocument.Load(seed, registry: HeifBoxRegistry.Default);
        var meta = HeifFixtures.Find<MetaBox>(tree.Boxes);
        var iloc = HeifFixtures.Find<ItemLocationBox>(meta.Children);
        iloc.LengthSize = 8;
        iloc.Entries[0].ConstructionMethod = 1;
        iloc.Entries[0].Extents[0] = new() { Offset = 0, Length = (ulong)length };
        using var prefix = new MemoryStream();
        prefix.Write(HeifBoxTests.Write(tree.Boxes[0]));
        BmffBoxHeader.Create(BmffBoxType.Meta, meta.GetPayloadSize() + 16 + length).Write(prefix);
        prefix.Write(new byte[4]);
        foreach (var child in meta.Children)
        {
            prefix.Write(HeifBoxTests.Write(child));
        }
        BmffBoxHeader.Create((BmffBoxType)0x69646174, length).Write(prefix);
        using var source = new HeifProbeStream(prefix.ToArray(), length);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true, MaxBufferedPayloadSize = 1024 });

        // Assert
        document.PrimaryImage!.Data[0].Length.ShouldBe(length);
        document.PrimaryImage.Data[0].IsMaterialized.ShouldBeFalse();
        source.BytesRead.ShouldBeLessThan(4096);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read/Save: ISO/IEC 14496-12 4.2 streaming and 8.11.3 relocation work without seeking output")]
    public async Task Save_NonSeekableOutput_CopiesUnchangedImages()
    {
        // Arrange
        using var memory = new MemoryStream(HeifFixtures.Create("metadata"));
        using var source = new HeifProbeStream(memory, true);
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var bytes = HeifFixtures.Data(document.PrimaryImage!);
        document.RemoveExif();
        using var output = new MemoryStream();
        using var forward = new HeifProbeStream(output, false);

        // Act
        await document.SaveAsync(forward, CancellationToken.None);
        output.Position = 0;
        using var reloaded = HeifDocument.Load(output, new() { Strict = true });

        // Assert
        HeifFixtures.Data(reloaded.PrimaryImage!).ShouldBe(bytes);
        source.MaximumRead.ShouldBeLessThanOrEqualTo(81920);
        source.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 14496-12 4.2 forward-only header traversal descends and skips payloads")]
    public void Read_NonSeekableInput_DescendsIntoMeta()
    {
        // Arrange
        using var memory = new MemoryStream(HeifFixtures.Create("grid"));
        using var source = new HeifProbeStream(memory, false);
        using var reader = BmffReader.Create(source, null, HeifBoxRegistry.Default);

        // Act
        reader.Read().ShouldBeTrue();
        reader.Header.BoxType.ShouldBe(BmffBoxType.FileType);
        reader.Read().ShouldBeTrue();
        using (var children = reader.ReadChildren())
        {
            var count = 0;
            while (children.Read()) { count++; children.Skip(); }
            count.ShouldBe(7);
        }
        reader.Read().ShouldBeTrue();

        // Assert
        reader.Header.BoxType.ShouldBe(BmffBoxType.MediaData);
        reader.Skip();
        reader.Read().ShouldBeFalse();
        source.MaximumRead.ShouldBeLessThanOrEqualTo(81920);
    }
}
