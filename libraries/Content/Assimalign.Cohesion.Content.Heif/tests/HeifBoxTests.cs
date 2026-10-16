using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifBoxTests
{
    public static IEnumerable<object[]> Boxes() => HeifBoxFixtures.Boxes();

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Read/Write: ISO/IEC 23008-12 properties, 14496-12 items and AVIF 2.2.1 preserve bytes")]
    [MemberData(nameof(Boxes))]
    public void ReadWrite_EveryBox_PreservesBytes(BmffBox box, string clause)
    {
        // Arrange
        clause.ShouldNotBeNullOrWhiteSpace();
        using var source = new MemoryStream(Write(box));

        // Act
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        document.Boxes[0].GetType().ShouldBe(box.GetType());
        output.ToArray().ShouldBe(source.ToArray());
        if (document.Boxes[0] is Av1ConfigurationBox av1) { av1.Payload.IsMaterialized.ShouldBeFalse(); }
        if (document.Boxes[0] is ItemDataBox data) { data.Payload.IsMaterialized.ShouldBeFalse(); }
        if (document.Boxes[0] is ColourInformationBox colour && colour.ColourType.Value == 0x70726f66) { colour.Profile.IsMaterialized.ShouldBeFalse(); }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 14496-12 8.11.14.3 independently encoded ipma vector")]
    public void Read_IndependentAssociationVector_DecodesEssentialAndIndex()
    {
        // Arrange: size=25, v1, flags=1, one item 0x10000, two associations.
        byte[] bytes = [0, 0, 0, 25, 105, 112, 109, 97, 1, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0, 0, 2, 0x81, 0x2c, 0, 7];
        using var source = new MemoryStream(bytes);

        // Act
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
        var box = document.Boxes[0].ShouldBeOfType<ItemPropertyAssociationBox>();

        // Assert
        box.Entries[0].ItemId.ShouldBe(65536u);
        box.Entries[0].Associations[0].PropertyIndex.ShouldBe((ushort)300);
        box.Entries[0].Associations[0].Essential.ShouldBeTrue();
        box.Entries[0].Associations[1].Essential.ShouldBeFalse();
    }

    internal static byte[] Write(BmffBox box)
    {
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        writer.Write(box);
        return output.ToArray();
    }
}
