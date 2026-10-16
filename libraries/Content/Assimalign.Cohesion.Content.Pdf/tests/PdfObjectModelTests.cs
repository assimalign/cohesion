using System;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfObjectModelTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.5: Name escapes have byte equality")]
    public void Parse_EscapedName_PreservesBytesAndEquality()
    {
        // Arrange / Act
        var name = PdfName.Parse("/A#20B#ff"u8);
        var other = PdfName.Create(new byte[] { 65, 32, 66, 255 });
        // Assert
        name.ShouldBe(other);
        name.GetHashCode().ShouldBe(other.GetHashCode());
        name.ToString().ShouldBe("/A#20B#FF");
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.5: Invalid names fail deterministically")]
    [InlineData("/A#0")]
    [InlineData("/A#GG")]
    [InlineData("/A#00")]
    [InlineData("/A B")]
    public void Parse_InvalidName_ThrowsFormatError(string token)
    {
        // Arrange / Act / Assert
        Should.Throw<ContentFormatException>(() => PdfName.Parse(Encoding.ASCII.GetBytes(token)))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.4: Strings retain copied bytes and representation")]
    public void Create_StringBytes_PreservesBinaryData()
    {
        // Arrange
        var bytes = new byte[] { 0, 255, 65 };
        // Act
        var value = PdfString.Create(bytes, true);
        bytes[0] = 1;
        // Assert
        value.Bytes.ToArray().ShouldBe(new byte[] { 0, 255, 65 });
        value.IsHexadecimal.ShouldBeTrue();
        value.DecodeText(Encoding.Latin1).Length.ShouldBe(3);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.7: Dictionary replacement preserves ordering")]
    public void Set_EquivalentName_PreservesOrderAndLookup()
    {
        // Arrange
        var dictionary = new PdfDictionary();
        var first = PdfName.Create("First"u8);
        var second = PdfName.Create("Second"u8);
        dictionary[first] = PdfInteger.Create(1);
        dictionary[second] = new PdfNull();
        // Act
        dictionary[PdfName.Parse("/Fi#72st"u8)] = PdfInteger.Create(2);
        // Assert
        dictionary.Keys.ShouldBe(new[] { first, second });
        dictionary[first].ShouldBeOfType<PdfInteger>().Value.ShouldBe(2);
        dictionary.Remove(first).ShouldBeTrue();
        dictionary.ContainsKey(first).ShouldBeFalse();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.10: Reference equality includes generation")]
    public void Equals_IndirectReferences_UsesBothComponents()
    {
        // Arrange
        var first = PdfIndirectReference.Create(PdfObjectIdentifier.Create(12, 1));
        // Act / Assert
        first.ShouldBe(PdfIndirectReference.Create(PdfObjectIdentifier.Create(12, 1)));
        first.ShouldNotBe(PdfIndirectReference.Create(PdfObjectIdentifier.Create(12, 2)));
        Should.Throw<ArgumentOutOfRangeException>(() => PdfIndirectReference.Create(default));
        Should.Throw<ArgumentOutOfRangeException>(() => PdfObjectIdentifier.Create(1, 65536));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.3 and §7.3.6: Editable array invariants")]
    public void Add_ArrayValues_EnforcesModelInvariants()
    {
        // Arrange
        var array = new PdfArray();
        // Act
        array.Add(PdfBoolean.Create(true));
        array[0] = PdfReal.Create(.5);
        // Assert
        array[0].ShouldBeOfType<PdfReal>().Value.ShouldBe(.5);
        Should.Throw<ArgumentNullException>(() => array.Add(null!));
        Should.Throw<ArgumentOutOfRangeException>(() => PdfReal.Create(double.NaN));
        array.RemoveAt(0);
        array.Count.ShouldBe(0);
    }
}
