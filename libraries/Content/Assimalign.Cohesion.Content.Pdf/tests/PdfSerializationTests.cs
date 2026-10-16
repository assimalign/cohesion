using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfSerializationTests
{
    internal static string Serialize(IPdfObject value)
    {
        using var bytes = new MemoryStream();
        new PdfObjectSerializer(new PdfOutput(bytes)).Write(value);
        return Encoding.Latin1.GetString(bytes.ToArray());
    }
    public static IEnumerable<object[]> Objects()
    {
        yield return new object[] { new PdfNull(), "null" };
        yield return new object[] { PdfBoolean.Create(true), "true" };
        yield return new object[] { PdfBoolean.Create(false), "false" };
        yield return new object[] { PdfInteger.Create(long.MinValue), "-9223372036854775808" };
        yield return new object[] { PdfInteger.Create(long.MaxValue), "9223372036854775807" };
        yield return new object[] { PdfName.Create("a /#%()<>[]{}\t\xff"u8), "/a#20#2F#23#25#28#29#3C#3E#5B#5D{}#09#C3#BF" };
        yield return new object[] { PdfName.Create(ReadOnlySpan<byte>.Empty), "/" };
        yield return new object[] { PdfString.Create("a(b)c\\\n\r\t\b\f\0z"u8), "(a\\(b\\)c\\\\\\n\\r\\t\\b\\f\\000z)" };
        yield return new object[] { PdfString.Create(new byte[] { 0, 255, 65 }), "<00FF41>" };
        yield return new object[] { PdfString.Create(new byte[] { 0, 65 }), "(\\000A)" };
        yield return new object[] { PdfString.Create("hi"u8, true), "<6869>" };
        yield return new object[] { PdfString.Create(""u8), "()" };
        yield return new object[] { PdfIndirectReference.Create(PdfObjectIdentifier.Create(42, 7)), "42 7 R" };
        var array = new PdfArray(); array.Add(PdfInteger.Create(1)); array.Add(new PdfNull());
        yield return new object[] { array, "[1 null]" };
        var dictionary = new PdfDictionary(); dictionary[PdfName.Create("A B"u8)] = array;
        yield return new object[] { dictionary, "<< /A#20B [1 null] >>" };
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.2–§7.3.10: Canonical object encodings round-trip")]
    [MemberData(nameof(Objects))]
    public void Write_AllObjects_UsesCanonicalSyntax(IPdfObject value, string expected)
    {
        // Arrange / Act
        var encoded = Serialize(value);
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(encoded));
        using var input = new PdfInput(source);
        var parsed = new PdfParser(input, new()).ReadObject();
        // Assert
        encoded.ShouldBe(expected);
        Serialize(parsed).ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.3: Finite reals preserve Double precision without exponents")]
    [InlineData(1.0, "1.")]
    [InlineData(1.25, "1.25")]
    [InlineData(0.0000001, "0.0000001")]
    [InlineData(1e20, "100000000000000000000.")]
    [InlineData(1.0000000000000002, "1.0000000000000002")]
    [InlineData(9007199254740992.0, "9007199254740992.")]
    [InlineData(double.Epsilon, null)]
    [InlineData(double.MaxValue, null)]
    [InlineData(-double.MaxValue, null)]
    [InlineData(-0.0, "-0.")]
    public void Write_RealBoundaries_RoundTripsExactly(double value, string? expected)
    {
        // Arrange
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            // Act
            var text = Serialize(PdfReal.Create(value));
            using var source = new MemoryStream(Encoding.ASCII.GetBytes(text));
            using var input = new PdfInput(source);
            var parsed = new PdfParser(input, new()).ReadObject().ShouldBeOfType<PdfReal>();
            // Assert
            if (expected is not null) { text.ShouldBe(expected); }
            text.ShouldNotContain("E", Case.Sensitive);
            BitConverter.DoubleToInt64Bits(parsed.Value).ShouldBe(BitConverter.DoubleToInt64Bits(value));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Stream Length matches copied encoded bytes")]
    public void Write_Stream_CorrectsLengthWithoutDecoding()
    {
        // Arrange
        var dictionary = new PdfDictionary(); dictionary[PdfName.Create("Length"u8)] = PdfInteger.Create(999);
        dictionary[PdfName.Create("Filter"u8)] = PdfName.Create("DCTDecode"u8);
        var stream = PdfStream.Create(dictionary, BinarySegment.Create("opaque"u8.ToArray()));
        // Act / Assert
        Serialize(stream).ShouldBe("<< /Length 6 /Filter /DCTDecode >>\nstream\nopaque\nendstream");
        dictionary[PdfName.Create("Length"u8)].ShouldBeOfType<PdfInteger>().Value.ShouldBe(999);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.6–§7.3.8: Direct cycles and depth overflow fail loudly")]
    public void Write_CyclicOrDeepContainers_ReportsClassifiedFailure()
    {
        // Arrange
        var cycle = new PdfArray(); cycle.Add(cycle);
        var nested = new PdfArray(); nested.Add(new PdfArray());
        // Act / Assert
        Should.Throw<ContentFormatException>(() => Serialize(cycle)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        Should.Throw<ContentFormatException>(() => new PdfObjectSerializer(new PdfOutput(Stream.Null), maxDepth: 0).Write(nested))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
}
