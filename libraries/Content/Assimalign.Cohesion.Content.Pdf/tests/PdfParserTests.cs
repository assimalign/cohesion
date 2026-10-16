using System;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfParserTests
{
    private static IPdfObject Parse(string text, PdfReaderOptions? options = null)
    {
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(text));
        using var input = new PdfInput(source);
        return new PdfParser(input, options ?? new()).ReadObject();
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.3: Real grammar")]
    [InlineData(".5", .5)]
    [InlineData("-.002", -.002)]
    [InlineData("4.", 4.0)]
    [InlineData("+009.87", 9.87)]
    public void ReadObject_RealGrammar_PreservesValue(string text, double expected)
    {
        // Arrange / Act / Assert
        Parse(text).ShouldBeOfType<PdfReal>().Value.ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.3: Integer grammar")]
    [InlineData("+17", 17L)]
    [InlineData("-98", -98L)]
    [InlineData("00987", 987L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void ReadObject_IntegerGrammar_PreservesValue(string text, long expected)
    {
        // Arrange / Act / Assert
        Parse(text).ShouldBeOfType<PdfInteger>().Value.ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.2 and §7.3: Malformed tokens have classified failures")]
    [InlineData("[true", ContentFormatErrorCode.Truncated)]
    [InlineData("<< /A 1", ContentFormatErrorCode.Truncated)]
    [InlineData("(abc", ContentFormatErrorCode.Truncated)]
    [InlineData("<a", ContentFormatErrorCode.Truncated)]
    [InlineData("<a%>", ContentFormatErrorCode.InvalidValue)]
    [InlineData("/A#00", ContentFormatErrorCode.InvalidValue)]
    [InlineData("<< /A 1 /#41 2 >>", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("<< 1 2 >>", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("[)", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("1e3", ContentFormatErrorCode.InvalidValue)]
    [InlineData("9223372036854775808", ContentFormatErrorCode.InvalidValue)]
    [InlineData("NaN", ContentFormatErrorCode.InvalidValue)]
    [InlineData("12abc", ContentFormatErrorCode.InvalidValue)]
    [InlineData("0 0 R", ContentFormatErrorCode.InvalidValue)]
    public void ReadObject_MalformedToken_ReportsCodeAndPosition(string text, ContentFormatErrorCode code)
    {
        // Arrange / Act
        var error = Should.Throw<ContentFormatException>(() => Parse(text));
        // Assert
        error.ErrorCode.ShouldBe(code);
        error.Position.ShouldNotBeNull();
        error.Position!.Value.ShouldBeInRange(0, text.Length);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.2.3 and §7.2.4: Whitespace and comments separate tokens")]
    public void ReadObject_CommentsAndWhiteSpace_SeparatesTokens()
    {
        // Arrange / Act
        var array = Parse("[\0\t\n\f\r 12%comment\r\n34 /A/B true false null]").ShouldBeOfType<PdfArray>();
        // Assert
        array.Count.ShouldBe(7);
        array[0].ShouldBeOfType<PdfInteger>().Value.ShouldBe(12);
        array[1].ShouldBeOfType<PdfInteger>().Value.ShouldBe(34);
        array[2].ShouldBeOfType<PdfName>().ToString().ShouldBe("/A");
        array[6].ShouldBeOfType<PdfNull>();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.4.2: Literal escapes, octal, balanced parentheses and EOL")]
    public void ReadObject_LiteralEscapes_PreservesDecodedBytes()
    {
        // Arrange / Act
        var value = Parse("(a(b)c\\n\\r\\t\\b\\f\\(\\)\\\\\\101\\777\\q\\\r\nZ\r\nQ)").ShouldBeOfType<PdfString>();
        // Assert
        value.Bytes.ToArray().ShouldBe(Encoding.Latin1.GetBytes("a(b)c\n\r\t\b\f()\\A\u00ffqZ\nQ"));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.4.3: Odd hexadecimal nibble is zero-padded")]
    public void ReadObject_HexString_PadsFinalNibble()
    {
        // Arrange / Act
        var value = Parse("< 00 fF A >").ShouldBeOfType<PdfString>();
        // Assert
        value.Bytes.ToArray().ShouldBe(new byte[] { 0, 255, 160 });
        value.IsHexadecimal.ShouldBeTrue();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3: Explicit leniency accepts exponent and last duplicate key")]
    public void ReadObject_LenientSyntax_AppliesDocumentedRecovery()
    {
        // Arrange / Act
        var value = Parse("<< /A 1 /A 2e2 >>", new() { IsLenient = true }).ShouldBeOfType<PdfDictionary>();
        // Assert
        value.Count.ShouldBe(1);
        value[PdfName.Create("A"u8)].ShouldBeOfType<PdfReal>().Value.ShouldBe(200);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.6: Depth and token allocations are bounded")]
    public void ReadObject_ResourceBomb_StopsBeforeAllocation()
    {
        // Arrange / Act / Assert
        Should.Throw<ContentFormatException>(() => Parse("[[[[[0]]]]]", new() { MaxDepth = 3 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<ContentFormatException>(() => Parse("(abcdef)", new() { MaxBufferedBytes = 4 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Stream extent is lazy and uses Length")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReadIndirect_StreamLength_KeepsLazyData(string eol)
    {
        // Arrange
        using var source = new MemoryStream(Encoding.ASCII.GetBytes($"8 2 obj\n<< /Length 3 >>\nstream{eol}abc\nendstream\nendobj"));
        using var input = new PdfInput(source);
        // Act
        var result = new PdfParser(input, new()).ReadIndirect(PdfObjectIdentifier.Create(8, 2));
        // Assert
        var stream = result.Value.ShouldBeOfType<PdfStream>();
        stream.Data.IsMaterialized.ShouldBeFalse();
        stream.Data.ToArray(3).ShouldBe("abc"u8.ToArray());
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Wrong stream Length requires explicit recovery")]
    public void ReadIndirect_WrongLength_StrictFailsAndLenientRecovers()
    {
        // Arrange
        var bytes = Encoding.ASCII.GetBytes("1 0 obj\n<< /Length 2 >>\nstream\nabc\nendstream\nendobj");
        using var source = new MemoryStream(bytes);
        using var input = new PdfInput(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfParser(input, new()).ReadIndirect()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        input.Position = 0;
        new PdfParser(input, new() { IsLenient = true }).ReadIndirect().Value.ShouldBeOfType<PdfStream>().Data.ToArray(3).ShouldBe("abc"u8.ToArray());
    }
}
