using System;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9651 &#167; 3.3 / &#167; 4.2.4-4.2.10 conformance tests for the eight bare item types, parsed as
/// single-item fields via <see cref="StructuredFieldItem"/>. Covers valid values, range and
/// syntax boundaries, strict fail-parsing, and canonical round-tripping.
/// </summary>
public class StructuredFieldBareItemTests
{
    private static StructuredFieldBareItem ParseBare(string input)
    {
        StructuredFieldItem.TryParse(input, out StructuredFieldItem item, out string? error).ShouldBeTrue(error);
        return item.Value;
    }

    // ============================================================================
    // Integer (§3.3.1 / §4.2.4)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Integer: valid values parse")]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("-42", -42L)]
    [InlineData("999999999999999", 999_999_999_999_999L)]
    [InlineData("-999999999999999", -999_999_999_999_999L)]
    [InlineData("042", 42L)]
    public void Integer_ValidValues_Parse(string input, long expected)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.Integer);
        item.AsInteger().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Integer: out-of-range and malformed fail")]
    [InlineData("1000000000000000")]   // 16 digits, exceeds range
    [InlineData("-1000000000000000")]
    [InlineData("9999999999999999")]   // 16 digits
    [InlineData("-")]                   // sign with no digits
    [InlineData("+5")]                  // leading plus not allowed
    public void Integer_OutOfRangeOrMalformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    // ============================================================================
    // Decimal (§3.3.2 / §4.2.4)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Decimal: valid values round-trip canonically")]
    [InlineData("1.0", "1.0")]
    [InlineData("1.5", "1.5")]
    [InlineData("1.50", "1.5")]        // trailing zero dropped in canonical form
    [InlineData("1.25", "1.25")]
    [InlineData("1.125", "1.125")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("0.001", "0.001")]
    [InlineData("123456789012.0", "123456789012.0")]
    public void Decimal_ValidValues_RoundTrip(string input, string canonical)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.Decimal);
        item.Serialize().ShouldBe(canonical);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Decimal: malformed fail")]
    [InlineData("1.")]                     // no fractional digits
    [InlineData(".5")]                      // no integer digits (starts with '.')
    [InlineData("1.2345")]                  // more than three fractional digits
    [InlineData("1234567890123.0")]         // more than 12 integer digits
    public void Decimal_Malformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Decimal: serialization rounds half-to-even")]
    public void Decimal_Serialization_RoundsHalfToEven()
    {
        StructuredFieldBareItem.FromDecimal(0.0025m).Serialize().ShouldBe("0.002");
        StructuredFieldBareItem.FromDecimal(0.0035m).Serialize().ShouldBe("0.004");
    }

    // ============================================================================
    // String (§3.3.3 / §4.2.5)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV String: valid values parse")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("\"\"", "")]
    [InlineData("\"hello world\"", "hello world")]
    [InlineData("\"escaped \\\" quote\"", "escaped \" quote")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    public void String_ValidValues_Parse(string input, string expected)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.String);
        item.AsString().ShouldBe(expected);
        item.Serialize().ShouldBe(input);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV String: malformed fail")]
    [InlineData("\"unterminated")]
    [InlineData("\"bad \\x escape\"")]
    [InlineData("\"trailing backslash\\")]
    public void String_Malformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV String: non-ASCII character is rejected at construction")]
    public void String_NonAscii_Rejected()
    {
        Should.Throw<ArgumentException>(() => StructuredFieldBareItem.FromString("café"));
    }

    // ============================================================================
    // Token (§3.3.4 / §4.2.6)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Token: valid values parse and round-trip")]
    [InlineData("foo")]
    [InlineData("text/plain")]
    [InlineData("*")]
    [InlineData("a:b/c")]
    [InlineData("foo123")]
    public void Token_ValidValues_RoundTrip(string input)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.Token);
        item.AsToken().ShouldBe(input);
        item.Serialize().ShouldBe(input);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Token: invalid token rejected at construction")]
    public void Token_Invalid_Rejected()
    {
        Should.Throw<ArgumentException>(() => StructuredFieldBareItem.FromToken("1leading-digit"));
        Should.Throw<ArgumentException>(() => StructuredFieldBareItem.FromToken("has space"));
    }

    // ============================================================================
    // Byte Sequence (§3.3.5 / §4.2.7)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV ByteSequence: base64 decodes and round-trips")]
    public void ByteSequence_Base64_RoundTrip()
    {
        StructuredFieldBareItem item = ParseBare(":aGVsbG8=:");

        item.Type.ShouldBe(StructuredFieldType.ByteSequence);
        Encoding.ASCII.GetString(item.AsByteSequence().ToArray()).ShouldBe("hello");
        item.Serialize().ShouldBe(":aGVsbG8=:");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV ByteSequence: empty sequence is valid")]
    public void ByteSequence_Empty_Valid()
    {
        StructuredFieldBareItem item = ParseBare("::");

        item.Type.ShouldBe(StructuredFieldType.ByteSequence);
        item.AsByteSequence().Length.ShouldBe(0);
        item.Serialize().ShouldBe("::");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV ByteSequence: malformed fail")]
    [InlineData(":aGVsbG8:")]      // missing padding
    [InlineData(":not base64!:")]  // characters outside the base64 alphabet
    [InlineData(":aGVsbG8=")]      // unterminated
    public void ByteSequence_Malformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    // ============================================================================
    // Boolean (§3.3.6 / §4.2.8)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Boolean: valid values parse")]
    [InlineData("?1", true)]
    [InlineData("?0", false)]
    public void Boolean_ValidValues_Parse(string input, bool expected)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.Boolean);
        item.AsBoolean().ShouldBe(expected);
        item.Serialize().ShouldBe(input);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Boolean: malformed fail")]
    [InlineData("?2")]
    [InlineData("?")]
    [InlineData("?true")]
    public void Boolean_Malformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    // ============================================================================
    // Date (§3.3.7 / §4.2.9)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Date: valid values parse and round-trip")]
    [InlineData("@1659578233", 1659578233L)]
    [InlineData("@0", 0L)]
    [InlineData("@-1659578233", -1659578233L)]
    public void Date_ValidValues_RoundTrip(string input, long expectedSeconds)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.Date);
        item.AsDate().ShouldBe(expectedSeconds);
        item.Serialize().ShouldBe(input);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV Date: a decimal date is rejected")]
    [InlineData("@1659578233.0")]
    [InlineData("@")]
    public void Date_Decimal_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Date: DateTimeOffset factory round-trips")]
    public void Date_DateTimeOffsetFactory_RoundTrips()
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(1659578233L);

        StructuredFieldBareItem item = StructuredFieldBareItem.FromDate(when);

        item.AsDateTimeOffset().ShouldBe(when);
        item.Serialize().ShouldBe("@1659578233");
    }

    // ============================================================================
    // Display String (§3.3.8 / §4.2.10)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV DisplayString: valid values parse and round-trip")]
    [InlineData("%\"hello\"", "hello")]
    [InlineData("%\"caf%c3%a9\"", "café")]
    [InlineData("%\"%e2%82%ac\"", "€")]  // euro sign
    public void DisplayString_ValidValues_RoundTrip(string input, string expected)
    {
        StructuredFieldBareItem item = ParseBare(input);

        item.Type.ShouldBe(StructuredFieldType.DisplayString);
        item.AsDisplayString().ShouldBe(expected);
        item.Serialize().ShouldBe(input);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - SFV DisplayString: malformed fail")]
    [InlineData("%\"bad %XY hex\"")]        // uppercase / non-hex
    [InlineData("%\"truncated %c\"")]        // truncated percent-encoding
    [InlineData("%hello\"")]                  // missing opening quote
    [InlineData("%\"unterminated")]
    [InlineData("%\"bad %ff byte\"")]        // 0xFF alone is invalid UTF-8
    public void DisplayString_Malformed_Fail(string input)
    {
        StructuredFieldItem.TryParse(input, out _, out _).ShouldBeFalse();
    }

    // ============================================================================
    // Accessor type-safety
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV BareItem: mismatched accessor throws")]
    public void BareItem_MismatchedAccessor_Throws()
    {
        StructuredFieldBareItem integer = StructuredFieldBareItem.FromInteger(5);

        Should.Throw<InvalidOperationException>(() => integer.AsString());
        Should.Throw<InvalidOperationException>(() => integer.AsBoolean());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV BareItem: factory range validation throws")]
    public void BareItem_FactoryRangeValidation_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StructuredFieldBareItem.FromInteger(1_000_000_000_000_000L));
        Should.Throw<ArgumentOutOfRangeException>(() => StructuredFieldBareItem.FromDate(-1_000_000_000_000_000L));
        Should.Throw<ArgumentOutOfRangeException>(() => StructuredFieldBareItem.FromDecimal(1_000_000_000_000m));
    }
}
