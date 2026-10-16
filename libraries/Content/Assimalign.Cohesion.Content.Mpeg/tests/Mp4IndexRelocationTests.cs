using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public class Mp4IndexRelocationTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Fast start preserves indexed media and auxiliary bytes (ISO 14496-12 8.16.3, 8.7.9)")]
    [InlineData(false)] [InlineData(true)]
    public async Task FastStart_Indices_ResolveIdenticalSampleAndAuxiliaryData(bool wide)
    {
        // Arrange
        using var source = new MemoryStream(Mp4IndexFixtures.Create(wide));
        using var document = Mp4Profile.LoadDocument(source);

        // Act
        Mp4Profile.FastStart(document);
        using var output = new MemoryStream();
        await document.SaveAsync(output, CancellationToken.None);
        var bytes = output.ToArray();
        output.Position = 0;
        using var file = Mp4File.Load(output);

        // Assert
        for (var i = 0; i < 4; i++)
        {
            var sample = (await file.Tracks[0].Samples.GetSampleAsync(i, CancellationToken.None)).ShouldNotBeNull();
            bytes.AsSpan((int)sample.ByteOffset, 3).ToArray().ShouldBe(new byte[] { (byte)(i * 3 + 1), (byte)(i * 3 + 2), (byte)(i * 3 + 3) });
        }
        var index = file.Document.Boxes[1];
        var encodedIndex = Mp4Fixtures.Write(index);
        var first = wide ? BinaryPrimitives.ReadUInt64BigEndian(encodedIndex.AsSpan(28)) : BinaryPrimitives.ReadUInt32BigEndian(encodedIndex.AsSpan(24));
        var mediaOffset = index.Offset + encodedIndex.Length + (long)first;
        bytes.AsSpan((int)mediaOffset + 8, 16).ToArray().ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0xa1, 0xb2, 0xc3, 0xd4 });
        var table = Mp4MediaFixtures.Find<SampleTableBox>((MovieBox)file.Document.Boxes[2]);
        foreach (var box in table.Children)
        {
            if ((uint)box.BoxType != 0x7361696f) { continue; }
            var encoded = Mp4Fixtures.Write(box);
            var auxiliaryOffset = wide ? BinaryPrimitives.ReadUInt64BigEndian(encoded.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(24));
            bytes.AsSpan((int)auxiliaryOffset, 4).ToArray().ShouldBe(new byte[] { 0xa1, 0xb2, 0xc3, 0xd4 });
        }
        using var repeated = new MemoryStream();
        document.Save(repeated);
        repeated.ToArray().ShouldBe(bytes);
    }
}
