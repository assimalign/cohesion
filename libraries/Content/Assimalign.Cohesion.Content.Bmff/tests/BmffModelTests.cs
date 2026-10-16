using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffModelTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var value in BmffModelFixtures.Cases)
        {
            yield return [value.Type, "ISO/IEC 14496-12 " + value.Clause];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Strategic fields parse and emit their ISO/IEC 14496-12 clause")]
    [MemberData(nameof(Cases))]
    public void Load_StrategicBox_ParsesAndRoundTrips(string type, string clause)
    {
        // Arrange
        var bytes = BmffModelFixtures.Write(BmffModelFixtures.Create(type));
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source, new BmffReaderOptions { Strict = true });
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        clause.ShouldStartWith("ISO/IEC 14496-12");
        (document.Boxes[0] is UnknownBox).ShouldBeFalse();
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.6.1.3 v1 composition offset is signed")]
    public void Read_SignedCompositionOffset_DecodesNegativeValue()
    {
        // Arrange: hand-authored full-box fields independent of the package writer.
        using var source = new MemoryStream(BmffFixtures.Box("ctts", [1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0xff, 0xff, 0xff, 0xfd]));
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        var box = document.Boxes[0].ShouldBeOfType<SampleCompositionTimeBox>();
        box.Entries[0].SampleCount.ShouldBe(2u);
        box.Entries[0].SampleOffset.ShouldBe(-3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.4.2 packed language uses five bits per letter")]
    public void Read_PackedLanguage_DecodesEnglish()
    {
        // Arrange
        using var source = new MemoryStream(BmffModelFixtures.Write(BmffModelFixtures.Create("mdhd")));
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        document.Boxes[0].ShouldBeOfType<MediaHeaderBox>().Language.ShouldBe("eng");
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.3.3 supports 4, 8 and 16 bit sizes")]
    [InlineData(4, 15)]
    [InlineData(8, 255)]
    [InlineData(16, 65535)]
    public void Read_CompactSizeWidth_PreservesValues(byte width, ushort maximum)
    {
        // Arrange
        var box = new CompactSampleSizeBox(0, 0) { FieldSize = width, Entries = [1, maximum, 3] };
        using var source = new MemoryStream(BmffModelFixtures.Write(box));
        // Act
        using var document = BmffDocument.Load(source);
        // Assert
        document.Boxes[0].ShouldBeOfType<CompactSampleSizeBox>().Entries.ShouldBe(box.Entries);
    }
}
