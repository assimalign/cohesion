using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

[Collection(GeneratedValueTypeCollection.Name)]
public sealed class GeneratedValueTypeTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated value types: ";

    [Fact(DisplayName = displayPrefix + "every runtime type preserves equality hashing and order")]
    public void Equality_AllRuntimeTypes_ShouldUseUnderlyingValues()
    {
        // Arrange & Act & Assert
        VerifyValueSemantics(new GeneratedInt(1), new GeneratedInt(1), new GeneratedInt(2));
        VerifyValueSemantics(new GeneratedShort(1), new GeneratedShort(1), new GeneratedShort(2));
        VerifyValueSemantics(new GeneratedLong(1), new GeneratedLong(1), new GeneratedLong(2));
        VerifyValueSemantics(new GeneratedUInt(1), new GeneratedUInt(1), new GeneratedUInt(2));
        VerifyValueSemantics(new GeneratedUShort(1), new GeneratedUShort(1), new GeneratedUShort(2));
        VerifyValueSemantics(new GeneratedULong(1), new GeneratedULong(1), new GeneratedULong(2));
        VerifyValueSemantics(new GeneratedDouble(1.5), new GeneratedDouble(1.5), new GeneratedDouble(2.5));
        VerifyValueSemantics(new GeneratedDecimal(1.5m), new GeneratedDecimal(1.5m), new GeneratedDecimal(2.5m));
        VerifyValueSemantics(new GeneratedString("a"), new GeneratedString("a"), new GeneratedString("b"));
        VerifyValueSemantics(new GeneratedGuid(Guid.Empty), new GeneratedGuid(Guid.Empty), new GeneratedGuid(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        VerifyValueSemantics(new GeneratedUlid(Ulid.Empty), new GeneratedUlid(Ulid.Empty), new GeneratedUlid(Ulid.Parse("00000000000000000000000001")));
        VerifyValueSemantics(new GeneratedExplicitInt(1), new GeneratedExplicitInt(1), new GeneratedExplicitInt(2));
        default(GeneratedInt).Equals(default(GeneratedInt)).ShouldBeTrue();
        default(GeneratedInt).GetHashCode().ShouldBe(0);
        (default(GeneratedInt) < new GeneratedInt(1)).ShouldBeTrue();
    }

    [Fact(DisplayName = displayPrefix + "all struct defaults preserve equality hashes and ordering")]
    public void Equality_DefaultRuntimeValues_ShouldBeSafe()
    {
        // Arrange & Act & Assert
        VerifyDefaultValue(new GeneratedInt(1));
        VerifyDefaultValue(new GeneratedShort(1));
        VerifyDefaultValue(new GeneratedLong(1));
        VerifyDefaultValue(new GeneratedUInt(1));
        VerifyDefaultValue(new GeneratedUShort(1));
        VerifyDefaultValue(new GeneratedULong(1));
        VerifyDefaultValue(new GeneratedDouble(1.5));
        VerifyDefaultValue(new GeneratedDecimal(1.5m));
        VerifyDefaultValue(new GeneratedString("a"));
        VerifyDefaultValue(new GeneratedGuid(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        VerifyDefaultValue(new GeneratedUlid(Ulid.Parse("00000000000000000000000001")));
        VerifyDefaultValue(new GeneratedValidatedString("a"));
        VerifyDefaultValue(new GeneratedRangeInt(1));
        VerifyDefaultValue(new GeneratedIgnoreCaseString("a"));
        VerifyDefaultValue(new GeneratedExplicitInt(1));
    }

    [Fact(DisplayName = displayPrefix + "classes compare by value and null sorts first")]
    public void Equality_ClassValuesAndNull_ShouldBeNullSafe()
    {
        // Arrange
        GeneratedStringClass? missingString = null;
        GeneratedIntClass? missingNumber = null;
        var text = new GeneratedStringClass("a");
        var number = new GeneratedIntClass(1);

        // Act & Assert
        VerifyValueSemantics(text, new GeneratedStringClass("a"), new GeneratedStringClass("b"));
        VerifyValueSemantics(number, new GeneratedIntClass(1), new GeneratedIntClass(2));
        (missingString == null).ShouldBeTrue();
        (missingString != text).ShouldBeTrue();
        (missingString < text).ShouldBeTrue();
        (missingString <= text).ShouldBeTrue();
        (text > missingString).ShouldBeTrue();
        (text >= missingString).ShouldBeTrue();
        (missingString < null).ShouldBeFalse();
        (missingString <= null).ShouldBeTrue();
        (missingString >= null).ShouldBeTrue();
        (missingString > null).ShouldBeFalse();
        text.Equals(missingString).ShouldBeFalse();
        text.CompareTo(missingString).ShouldBeGreaterThan(0);
        (missingNumber == null).ShouldBeTrue();
        (missingNumber != number).ShouldBeTrue();
        (missingNumber < number).ShouldBeTrue();
        (missingNumber <= number).ShouldBeTrue();
        (number > missingNumber).ShouldBeTrue();
        (number >= missingNumber).ShouldBeTrue();
        number.Equals(missingNumber).ShouldBeFalse();
        number.CompareTo(missingNumber).ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = displayPrefix + "string class conversions preserve null and non-null values")]
    public void Conversion_StringClass_ShouldPreserveNullAndValues()
    {
        // Arrange
        string? missing = null;
        string source = "value";

        // Act
        GeneratedStringClass? missingValue = missing;
        string? missingText = missingValue;
        GeneratedStringClass value = source;
        string text = value;

        // Assert
        missingValue.ShouldBeNull();
        missingText.ShouldBeNull();
        value.Value.ShouldBe(source);
        text.ShouldBe(source);
    }

    [Fact(DisplayName = displayPrefix + "numeric class conversion rejects null")]
    public void Conversion_NullNumericClass_ShouldThrowArgumentNullException()
    {
        // Arrange
        GeneratedIntClass? missing = null;

        // Act
        Action convert = () => _ = (int)missing!;

        // Assert
        Should.Throw<ArgumentNullException>(convert);
        int value = new GeneratedIntClass(5);
        value.ShouldBe(5);
    }

    [Fact(DisplayName = displayPrefix + "string class constructors reject null")]
    public void Constructor_NullStringClass_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action construct = () => _ = new GeneratedStringClass(null!);

        // Assert
        Should.Throw<ArgumentNullException>(construct).ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = displayPrefix + "constructors preserve validation reasons and parameter names")]
    public void Constructor_InvalidValue_ShouldThrowArgumentExceptionWithParameterName()
    {
        // Arrange & Act
        Action invalidString = () => _ = new GeneratedValidatedString(string.Empty);
        Action invalidNumber = () => _ = new GeneratedRangeInt(11);

        // Assert
        var stringException = Should.Throw<ArgumentException>(invalidString);
        stringException.ParamName.ShouldBe("value");
        stringException.Message.ShouldContain(GeneratedValidatedString.InvalidMessage, Case.Sensitive);
        var numberException = Should.Throw<ArgumentException>(invalidNumber);
        numberException.ParamName.ShouldBe("value");
        numberException.Message.ShouldContain(GeneratedRangeInt.InvalidMessage, Case.Sensitive);
    }

    [Fact(DisplayName = displayPrefix + "validation can be called without constructing a value")]
    public void IsValid_CandidateValue_ShouldBeCallableStatically()
    {
        // Arrange & Act
        bool validString = GeneratedValidatedString.IsValid("value", out string? validStringMessage);
        bool invalidString = GeneratedValidatedString.IsValid(string.Empty, out string? invalidStringMessage);
        bool validNumber = GeneratedRangeInt.IsValid(5, out string? validNumberMessage);
        bool invalidNumber = GeneratedRangeInt.IsValid(11, out string? invalidNumberMessage);

        // Assert
        validString.ShouldBeTrue();
        validStringMessage.ShouldBeNull();
        invalidString.ShouldBeFalse();
        invalidStringMessage.ShouldBe(GeneratedValidatedString.InvalidMessage);
        validNumber.ShouldBeTrue();
        validNumberMessage.ShouldBeNull();
        invalidNumber.ShouldBeFalse();
        invalidNumberMessage.ShouldBe(GeneratedRangeInt.InvalidMessage);
    }

    [Theory(DisplayName = displayPrefix + "string TryCreate rejects invalid or null input without throwing")]
    [InlineData("")]
    [InlineData("too-long-value")]
    [InlineData(null)]
    public void TryCreate_InvalidOrNullString_ShouldReturnFalse(string? value)
    {
        // Arrange
        GeneratedValidatedString.ValidationCount = 0;
        GeneratedStringClass.ValidationCount = 0;

        // Act
        bool created = GeneratedValidatedString.TryCreate(value, out GeneratedValidatedString result);
        bool createdClass = GeneratedStringClass.TryCreate(value, out GeneratedStringClass? classResult);

        // Assert
        created.ShouldBeFalse();
        result.ShouldBe(default);
        createdClass.ShouldBeFalse();
        classResult.ShouldBeNull();
        GeneratedValidatedString.ValidationCount.ShouldBe(value is null ? 0 : 1);
        GeneratedStringClass.ValidationCount.ShouldBe(value is null ? 0 : 1);
    }

    [Fact(DisplayName = displayPrefix + "TryCreate validates successful candidates exactly once")]
    public void TryCreate_ValidValue_ShouldValidateOnce()
    {
        // Arrange
        GeneratedValidatedString.ValidationCount = 0;
        GeneratedRangeInt.ValidationCount = 0;
        GeneratedStringClass.ValidationCount = 0;
        GeneratedIntClass.ValidationCount = 0;

        // Act
        bool stringCreated = GeneratedValidatedString.TryCreate("value", out GeneratedValidatedString text);
        bool numberCreated = GeneratedRangeInt.TryCreate(5, out GeneratedRangeInt number);
        bool stringClassCreated = GeneratedStringClass.TryCreate("value", out GeneratedStringClass? textClass);
        bool numberClassCreated = GeneratedIntClass.TryCreate(5, out GeneratedIntClass? numberClass);

        // Assert
        stringCreated.ShouldBeTrue();
        text.Value.ShouldBe("value");
        numberCreated.ShouldBeTrue();
        number.Value.ShouldBe(5);
        stringClassCreated.ShouldBeTrue();
        textClass.ShouldNotBeNull().Value.ShouldBe("value");
        numberClassCreated.ShouldBeTrue();
        numberClass.ShouldNotBeNull().Value.ShouldBe(5);
        GeneratedValidatedString.ValidationCount.ShouldBe(1);
        GeneratedRangeInt.ValidationCount.ShouldBe(1);
        GeneratedStringClass.ValidationCount.ShouldBe(1);
        GeneratedIntClass.ValidationCount.ShouldBe(1);
    }

    [Fact(DisplayName = displayPrefix + "numeric TryCreate returns default or null when validation fails")]
    public void TryCreate_InvalidNumericValue_ShouldReturnFalse()
    {
        // Arrange
        GeneratedRangeInt.ValidationCount = 0;
        GeneratedIntClass.ValidationCount = 0;

        // Act
        bool created = GeneratedRangeInt.TryCreate(0, out GeneratedRangeInt result);
        bool createdClass = GeneratedIntClass.TryCreate(0, out GeneratedIntClass? classResult);

        // Assert
        created.ShouldBeFalse();
        result.ShouldBe(default);
        createdClass.ShouldBeFalse();
        classResult.ShouldBeNull();
        GeneratedRangeInt.ValidationCount.ShouldBe(1);
        GeneratedIntClass.ValidationCount.ShouldBe(1);
    }

    [Fact(DisplayName = displayPrefix + "default bypasses validation but formats as an empty string")]
    public void ToString_DefaultValue_ShouldReturnNonNullText()
    {
        // Arrange
        GeneratedValidatedString.ValidationCount = 0;
        GeneratedValidatedString text = default;

        // Act
        string result = text.ToString();

        // Assert
        result.ShouldBe(string.Empty);
        default(GeneratedString).ToString().ShouldBe(string.Empty);
        default(GeneratedIgnoreCaseString).ToString().ShouldBe(string.Empty);
        default(GeneratedInt).ToString().ShouldBe("0");
        default(GeneratedGuid).ToString().ShouldBe(Guid.Empty.ToString());
        default(GeneratedUlid).ToString().ShouldBe(Ulid.Empty.ToString());
        GeneratedValidatedString.ValidationCount.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "all non-string types support generic span parsing and formatting")]
    public void Parse_AllRuntimeTypes_ShouldImplementSpanContracts()
    {
        // Arrange & Act & Assert
        VerifySpanContracts("42", new GeneratedInt(42));
        VerifySpanContracts("42", new GeneratedShort(42));
        VerifySpanContracts("42", new GeneratedLong(42));
        VerifySpanContracts("42", new GeneratedUInt(42));
        VerifySpanContracts("42", new GeneratedUShort(42));
        VerifySpanContracts("42", new GeneratedULong(42));
        VerifySpanContracts("4.5", new GeneratedDouble(4.5));
        VerifySpanContracts("4.5", new GeneratedDecimal(4.5m));
        VerifySpanContracts("00112233-4455-6677-8899-aabbccddeeff", new GeneratedGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));
        VerifySpanContracts("01ARZ3NDEKTSV4RRFFQ69G5FAV", new GeneratedUlid(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")));
        VerifySpanContracts("5", new GeneratedRangeInt(5));
        VerifySpanContracts("5", new GeneratedIntClass(5));
        VerifySpanContracts("42", new GeneratedExplicitInt(42));
    }

    [Theory(DisplayName = displayPrefix + "TryParse returns false for unparseable or out-of-range values")]
    [InlineData("not-a-number")]
    [InlineData("11")]
    [InlineData(null)]
    public void TryParse_UnparseableOrInvalidValue_ShouldReturnFalse(string? input)
    {
        // Arrange & Act
        bool fromString = GeneratedRangeInt.TryParse(input, null, out GeneratedRangeInt stringResult);
        bool fromSpan = GeneratedRangeInt.TryParse(input.AsSpan(), null, out GeneratedRangeInt spanResult);
        bool fromClass = GeneratedIntClass.TryParse(input, null, out GeneratedIntClass? classResult);

        // Assert
        fromString.ShouldBeFalse();
        fromSpan.ShouldBeFalse();
        fromClass.ShouldBeFalse();
        stringResult.ShouldBe(default);
        spanResult.ShouldBe(default);
        classResult.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "Parse reports validation failures as FormatException with the reason")]
    public void Parse_ParseableButInvalidValue_ShouldThrowFormatException()
    {
        // Arrange & Act
        Action parseString = () => GeneratedRangeInt.Parse("11", null);
        Action parseSpan = () => GeneratedRangeInt.Parse("11".AsSpan(), null);
        Action parseClass = () => GeneratedIntClass.Parse("11", null);

        // Assert
        Should.Throw<FormatException>(parseString).Message.ShouldBe(GeneratedRangeInt.InvalidMessage);
        Should.Throw<FormatException>(parseSpan).Message.ShouldBe(GeneratedRangeInt.InvalidMessage);
        Should.Throw<FormatException>(parseClass).Message.ShouldBe(GeneratedIntClass.InvalidMessage);
    }

    [Fact(DisplayName = displayPrefix + "provider-less Parse and TryParse overloads remain available")]
    public void Parse_WithoutProvider_ShouldSupportStringsAndSpans()
    {
        // Arrange
        const string input = "5";

        // Act
        GeneratedRangeInt fromString = GeneratedRangeInt.Parse(input);
        GeneratedRangeInt fromSpan = GeneratedRangeInt.Parse(input.AsSpan());
        bool stringParsed = GeneratedRangeInt.TryParse(input, out GeneratedRangeInt stringResult);
        bool spanParsed = GeneratedRangeInt.TryParse(input.AsSpan(), out GeneratedRangeInt spanResult);

        // Assert
        fromString.Value.ShouldBe(5);
        fromSpan.ShouldBe(fromString);
        stringParsed.ShouldBeTrue();
        spanParsed.ShouldBeTrue();
        stringResult.ShouldBe(fromString);
        spanResult.ShouldBe(fromString);
        GeneratedRangeInt.TryParse("11", out _).ShouldBeFalse();
        GeneratedRangeInt.TryParse("11".AsSpan(), out _).ShouldBeFalse();
        GeneratedIntClass.TryParse(input, out var classResult).ShouldBeTrue();
        classResult.ShouldNotBeNull().Value.ShouldBe(5);
        GeneratedIntClass.TryParse(input.AsSpan(), out var spanClassResult).ShouldBeTrue();
        spanClassResult.ShouldNotBeNull().Value.ShouldBe(5);
    }

    [Fact(DisplayName = displayPrefix + "TryParse validates successful values exactly once")]
    public void TryParse_ValidValue_ShouldValidateOnce()
    {
        // Arrange
        GeneratedRangeInt.ValidationCount = 0;

        // Act
        bool parsed = GeneratedRangeInt.TryParse("5", CultureInfo.InvariantCulture, out GeneratedRangeInt result);

        // Assert
        parsed.ShouldBeTrue();
        result.Value.ShouldBe(5);
        GeneratedRangeInt.ValidationCount.ShouldBe(1);
    }

    [Fact(DisplayName = displayPrefix + "TryFormat delegates format strings and insufficient-buffer behavior")]
    public void TryFormat_FormatsAndSmallBuffer_ShouldDelegateToRuntimeType()
    {
        // Arrange
        ISpanFormattable number = new GeneratedInt(42);
        ISpanFormattable guid = new GeneratedGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        Span<char> destination = stackalloc char[64];
        Span<char> tooSmall = stackalloc char[1];

        // Act & Assert
        number.TryFormat(destination, out int numericLength, "D4", null).ShouldBeTrue();
        destination[..numericLength].ToString().ShouldBe("0042");
        guid.TryFormat(destination, out int guidLength, "N", null).ShouldBeTrue();
        destination[..guidLength].ToString().ShouldBe("00112233445566778899aabbccddeeff");
        number.TryFormat(tooSmall, out int written, default, null).ShouldBeFalse();
        written.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "Guid and Ulid retain their New and Empty values")]
    public void New_GuidAndUlid_ShouldCreateNonEmptyValues()
    {
        // Arrange & Act
        GeneratedGuid guid = GeneratedGuid.New();
        GeneratedUlid ulid = GeneratedUlid.New();

        // Assert
        GeneratedGuid.Empty.Value.ShouldBe(Guid.Empty);
        GeneratedUlid.Empty.Value.ShouldBe(Ulid.Empty);
        guid.ShouldNotBe(GeneratedGuid.Empty);
        ulid.ShouldNotBe(GeneratedUlid.Empty);
    }

    [Fact(DisplayName = displayPrefix + "all runtime types round-trip with source-generated JSON metadata")]
    public void Json_AllRuntimeTypes_ShouldRoundTrip()
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;

        // Act & Assert
        VerifyJsonRoundTrip(new GeneratedInt(42), context.GeneratedInt);
        VerifyJsonRoundTrip(new GeneratedShort(42), context.GeneratedShort);
        VerifyJsonRoundTrip(new GeneratedLong(42), context.GeneratedLong);
        VerifyJsonRoundTrip(new GeneratedUInt(42), context.GeneratedUInt);
        VerifyJsonRoundTrip(new GeneratedUShort(42), context.GeneratedUShort);
        VerifyJsonRoundTrip(new GeneratedULong(42), context.GeneratedULong);
        VerifyJsonRoundTrip(new GeneratedDouble(4.5), context.GeneratedDouble);
        VerifyJsonRoundTrip(new GeneratedDecimal(4.5m), context.GeneratedDecimal);
        VerifyJsonRoundTrip(new GeneratedString("text"), context.GeneratedString);
        VerifyJsonRoundTrip(new GeneratedGuid(Guid.NewGuid()), context.GeneratedGuid);
        VerifyJsonRoundTrip(new GeneratedUlid(Ulid.NewUlid()), context.GeneratedUlid);
        VerifyJsonRoundTrip(new GeneratedValidatedString("text"), context.GeneratedValidatedString);
        VerifyJsonRoundTrip(new GeneratedRangeInt(5), context.GeneratedRangeInt);
        VerifyJsonRoundTrip(new GeneratedIgnoreCaseString("Text"), context.GeneratedIgnoreCaseString);
        VerifyJsonRoundTrip(new GeneratedStringClass("text"), context.GeneratedStringClass);
        VerifyJsonRoundTrip(new GeneratedIntClass(5), context.GeneratedIntClass);
        VerifyJsonRoundTrip(new GeneratedExplicitInt(42), context.GeneratedExplicitInt);
    }

    [Fact(DisplayName = displayPrefix + "JSON null becomes a default struct or null class")]
    public void Json_Null_ShouldPreserveNoValueState()
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;

        // Act & Assert
        JsonSerializer.Deserialize("null", context.GeneratedInt).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedShort).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedLong).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedUInt).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedUShort).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedULong).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedDouble).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedDecimal).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedString).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedValidatedString).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedRangeInt).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedGuid).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedUlid).ShouldBe(default);
        JsonSerializer.Deserialize("null", context.GeneratedStringClass).ShouldBeNull();
        JsonSerializer.Deserialize("null", context.GeneratedIntClass).ShouldBeNull();
        JsonSerializer.Serialize(default, context.GeneratedString).ShouldBe("null");
        JsonSerializer.Serialize(default, context.GeneratedValidatedString).ShouldBe("null");
        JsonSerializer.Serialize<GeneratedStringClass>(null!, context.GeneratedStringClass).ShouldBe("null");
        JsonSerializer.Serialize<GeneratedIntClass>(null!, context.GeneratedIntClass).ShouldBe("null");
    }

    [Fact(DisplayName = displayPrefix + "invalid JSON values report JsonException with validation reasons")]
    public void Json_InvalidValues_ShouldThrowJsonException()
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;

        // Act
        Action invalidString = () => JsonSerializer.Deserialize("\"\"", context.GeneratedValidatedString);
        Action invalidNumber = () => JsonSerializer.Deserialize("11", context.GeneratedRangeInt);
        Action invalidUlid = () => JsonSerializer.Deserialize("\"not-a-ulid\"", context.GeneratedUlid);
        Action invalidKind = () => JsonSerializer.Deserialize("true", context.GeneratedInt);
        Action invalidClass = () => JsonSerializer.Deserialize("11", context.GeneratedIntClass);

        // Assert
        Should.Throw<JsonException>(invalidString).Message.ShouldContain(GeneratedValidatedString.InvalidMessage, Case.Sensitive);
        Should.Throw<JsonException>(invalidNumber).Message.ShouldContain(GeneratedRangeInt.InvalidMessage, Case.Sensitive);
        Should.Throw<JsonException>(invalidUlid).Message.ShouldNotBeNullOrEmpty();
        Should.Throw<JsonException>(invalidKind);
        Should.Throw<JsonException>(invalidClass).Message.ShouldContain(GeneratedIntClass.InvalidMessage, Case.Sensitive);
    }

    [Fact(DisplayName = displayPrefix + "every runtime type can be used as a JSON dictionary key")]
    public void Json_DictionaryKeys_ShouldRoundTripAllRuntimeTypes()
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");

            // Act & Assert
            VerifyDictionaryKey(new GeneratedInt(42), "42", context.IntDictionary);
            VerifyDictionaryKey(new GeneratedShort(42), "42", context.ShortDictionary);
            VerifyDictionaryKey(new GeneratedLong(42), "42", context.LongDictionary);
            VerifyDictionaryKey(new GeneratedUInt(42), "42", context.UIntDictionary);
            VerifyDictionaryKey(new GeneratedUShort(42), "42", context.UShortDictionary);
            VerifyDictionaryKey(new GeneratedULong(42), "42", context.ULongDictionary);
            VerifyDictionaryKey(new GeneratedDouble(4.5), "4.5", context.DoubleDictionary);
            VerifyDictionaryKey(new GeneratedDecimal(4.5m), "4.5", context.DecimalDictionary);
            VerifyDictionaryKey(new GeneratedString("text"), "text", context.StringDictionary);
            VerifyDictionaryKey(new GeneratedGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")), "00112233-4455-6677-8899-aabbccddeeff", context.GuidDictionary);
            VerifyDictionaryKey(new GeneratedUlid(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), "01ARZ3NDEKTSV4RRFFQ69G5FAV", context.UlidDictionary);
            VerifyDictionaryKey(new GeneratedValidatedString("text"), "text", context.ValidatedStringDictionary);
            VerifyDictionaryKey(new GeneratedRangeInt(5), "5", context.RangeIntDictionary);
            VerifyDictionaryKey(new GeneratedIgnoreCaseString("Text"), "Text", context.IgnoreCaseStringDictionary);
            VerifyDictionaryKey(new GeneratedStringClass("text"), "text", context.StringClassDictionary);
            VerifyDictionaryKey(new GeneratedIntClass(5), "5", context.IntClassDictionary);
            VerifyDictionaryKey(new GeneratedExplicitInt(42), "42", context.ExplicitIntDictionary);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact(DisplayName = displayPrefix + "invalid JSON dictionary keys preserve parse and validation failures")]
    public void Json_InvalidDictionaryKey_ShouldThrowJsonException()
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;

        // Act
        Action invalidString = () => JsonSerializer.Deserialize("{\"\":\"value\"}", context.ValidatedStringDictionary);
        Action invalidNumber = () => JsonSerializer.Deserialize("{\"11\":\"value\"}", context.RangeIntDictionary);
        Action invalidUlid = () => JsonSerializer.Deserialize("{\"not-a-ulid\":\"value\"}", context.UlidDictionary);
        Action unparseableNumber = () => JsonSerializer.Deserialize("{\"x\":\"value\"}", context.IntDictionary);

        // Assert
        Should.Throw<JsonException>(invalidString).Message.ShouldContain(GeneratedValidatedString.InvalidMessage, Case.Sensitive);
        Should.Throw<JsonException>(invalidNumber).Message.ShouldContain(GeneratedRangeInt.InvalidMessage, Case.Sensitive);
        Should.Throw<JsonException>(invalidUlid);
        Should.Throw<JsonException>(unparseableNumber);
    }

    [Fact(DisplayName = displayPrefix + "default string dictionary keys cannot become JSON property names")]
    public void Json_DefaultStringDictionaryKey_ShouldThrowJsonException()
    {
        // Arrange
        var values = new Dictionary<GeneratedString, string> { [default] = "value" };

        // Act
        Action serialize = () => JsonSerializer.Serialize(values, GeneratedValueTypeJsonContext.Default.StringDictionary);

        // Assert
        Should.Throw<JsonException>(serialize);
    }

    private static void VerifyValueSemantics<T>(T first, T equal, T greater)
        where T : notnull, IEquatable<T>, IComparable<T>, IEqualityOperators<T, T, bool>, IComparisonOperators<T, T, bool>
    {
        (first == equal).ShouldBeTrue();
        (first != greater).ShouldBeTrue();
        first.Equals(equal).ShouldBeTrue();
        first.Equals((object)equal).ShouldBeTrue();
        first.Equals(new object()).ShouldBeFalse();
        first.GetHashCode().ShouldBe(equal.GetHashCode());
        new HashSet<T> { first, equal, greater }.Count.ShouldBe(2);
        first.CompareTo(equal).ShouldBe(0);
        first.CompareTo(greater).ShouldBeLessThan(0);
        (first < greater).ShouldBeTrue();
        (first <= equal).ShouldBeTrue();
        (greater > first).ShouldBeTrue();
        (equal >= first).ShouldBeTrue();
    }

    private static void VerifyDefaultValue<T>(T greater)
        where T : struct, IEquatable<T>, IComparable<T>, IEqualityOperators<T, T, bool>, IComparisonOperators<T, T, bool>
    {
        T first = default;
        T second = default;
        (first == second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        new HashSet<T> { first, second }.Count.ShouldBe(1);
        first.CompareTo(second).ShouldBe(0);
        (first < greater).ShouldBeTrue();
        first.ToString().ShouldNotBeNull();
    }

    private static void VerifySpanContracts<T>(string input, T expected)
        where T : ISpanParsable<T>, ISpanFormattable, IEquatable<T>
    {
        T.Parse(input, CultureInfo.InvariantCulture).ShouldBe(expected);
        T.Parse(input.AsSpan(), CultureInfo.InvariantCulture).ShouldBe(expected);
        T.TryParse(input, CultureInfo.InvariantCulture, out T? parsedString).ShouldBeTrue();
        parsedString.ShouldBe(expected);
        T.TryParse(input.AsSpan(), CultureInfo.InvariantCulture, out T? parsedSpan).ShouldBeTrue();
        parsedSpan.ShouldBe(expected);
        T.TryParse("not-a-value", null, out _).ShouldBeFalse();
        T.TryParse("not-a-value".AsSpan(), null, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => T.Parse("not-a-value", null));
        Span<char> destination = stackalloc char[128];
        expected.TryFormat(destination, out int written, default, CultureInfo.InvariantCulture).ShouldBeTrue();
        destination[..written].ToString().ShouldBe(input);
        expected.ToString(null, CultureInfo.InvariantCulture).ShouldBe(input);
    }

    private static void VerifyJsonRoundTrip<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        string json = JsonSerializer.Serialize(value, typeInfo);
        T? roundTrip = JsonSerializer.Deserialize(json, typeInfo);
        roundTrip.ShouldBe(value);
    }

    private static void VerifyDictionaryKey<T>(T key, string expectedName, JsonTypeInfo<Dictionary<T, string>> typeInfo)
        where T : notnull
    {
        var dictionary = new Dictionary<T, string> { [key] = "value" };
        string json = JsonSerializer.Serialize(dictionary, typeInfo);
        json.ShouldBe("{\"" + expectedName + "\":\"value\"}");
        Dictionary<T, string>? roundTrip = JsonSerializer.Deserialize(json, typeInfo);
        roundTrip.ShouldNotBeNull().Count.ShouldBe(1);
        roundTrip[key].ShouldBe("value");
    }
}
