using System;
using System.Globalization;
using System.Text;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Core.Tests;

public sealed class UlidParsingTests
{
    private const string displayPrefix = "Cohesion Test [Core] - ULID parsing: ";
    private const string canonical = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly ITestOutputHelper _output;

    public UlidParsingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = displayPrefix + "all text overloads reject invalid alphabet characters at every position")]
    [InlineData('!')]
    [InlineData(' ')]
    [InlineData('-')]
    [InlineData('\0')]
    [InlineData('I')]
    [InlineData('L')]
    [InlineData('O')]
    [InlineData('U')]
    [InlineData('i')]
    [InlineData('l')]
    [InlineData('o')]
    [InlineData('u')]
    [InlineData('\u007f')]
    [InlineData('\u00e9')]
    [InlineData('\uff10')]
    [InlineData('\ud800')]
    public void Parse_InvalidAlphabet_ShouldRejectEveryOverload(char invalid)
    {
        // Arrange
        char[] characters = canonical.ToCharArray();

        for (int position = 0; position < characters.Length; position++)
        {
            characters[position] = invalid;
            string input = new string(characters);

            // Act and assert
            ShouldRejectEveryOverload(input);
            characters[position] = canonical[position];
        }
    }

    [Theory(DisplayName = displayPrefix + "all overloads reject a leading digit above seven")]
    [InlineData('8')]
    [InlineData('9')]
    [InlineData('A')]
    [InlineData('Z')]
    [InlineData('a')]
    [InlineData('z')]
    public void Parse_Overflow_ShouldRejectEveryOverload(char leading)
    {
        // Arrange
        string input = leading + canonical.Substring(1);

        // Act and assert
        ShouldRejectEveryOverload(input);
    }

    [Theory(DisplayName = displayPrefix + "all overloads reject wrong lengths with FormatException")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(27)]
    [InlineData(52)]
    public void Parse_WrongLength_ShouldRejectEveryOverload(int length)
    {
        // Arrange
        string input = new string('0', length);

        // Act and assert
        ShouldRejectEveryOverload(input);
    }

    [Fact(DisplayName = displayPrefix + "null strings and empty spans are rejected consistently")]
    public void Parse_Null_ShouldRejectEveryOverload()
    {
        // Arrange
        string? input = null;

        // Act and assert
        ShouldRejectEveryOverload(input);
    }

    [Fact(DisplayName = displayPrefix + "UTF-8 overloads reject every non-alphabet byte at every position")]
    public void Parse_InvalidUtf8Bytes_ShouldReject()
    {
        // Arrange
        byte[] input = Encoding.UTF8.GetBytes(canonical);

        for (int value = 0; value <= byte.MaxValue; value++)
        {
            if (alphabet.Contains(char.ToUpperInvariant((char)value), StringComparison.Ordinal))
            {
                continue;
            }

            for (int position = 0; position < input.Length; position++)
            {
                input[position] = (byte)value;

                // Act
                bool parsed = Ulid.TryParse(input.AsSpan(), out Ulid result);

                // Assert
                parsed.ShouldBeFalse($"byte {value} at position {position}");
                result.ShouldBe(default);
                Should.Throw<FormatException>(() => Ulid.Parse(input.AsSpan()));
                input[position] = (byte)canonical[position];
            }
        }
    }

    [Theory(DisplayName = displayPrefix + "canonical and lowercase inputs preserve exact bytes in every overload")]
    [InlineData("00000000000000000000000000", "00000000000000000000000000000000")]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    [InlineData(canonical, "01563E3AB5D3D6764C61EFB99302BD5B")]
    [InlineData("01arz3ndektsv4rrffq69g5fav", "01563E3AB5D3D6764C61EFB99302BD5B")]
    [InlineData("01ArZ3nDeKtSv4rRfFq69g5FaV", "01563E3AB5D3D6764C61EFB99302BD5B")]
    public void Parse_ValidText_ShouldPreserveBytes(string input, string expectedHex)
    {
        // Arrange
        byte[] expectedBytes = Convert.FromHexString(expectedHex);
        var expected = new Ulid(expectedBytes);

        // Act and assert
        ShouldParseEveryOverload(input, expected);
        expected.ToString().ShouldBe(input.ToUpperInvariant());
        expected.ToByteArray().ShouldBe(expectedBytes);
    }

    [Fact(DisplayName = displayPrefix + "every allowed alphabet character is accepted in either case")]
    public void Parse_CompleteAlphabet_ShouldAcceptEitherCase()
    {
        // Arrange
        char[] input = new string('0', 26).ToCharArray();

        foreach (char character in alphabet)
        {
            for (int position = 1; position < input.Length; position++)
            {
                input[position] = character;
                string text = new string(input);
                var expected = new Ulid(new NUlid.Ulid(text).ToByteArray());

                // Act and assert
                ShouldParseEveryOverload(text, expected);
                ShouldParseEveryOverload(text.ToLowerInvariant(), expected);
                input[position] = '0';
            }
        }
    }

    [Fact(DisplayName = displayPrefix + "arbitrary valid bytes retain existing encoding and round trips")]
    public void Parse_ValidByteRoundTrips_ShouldPreserveEncoding()
    {
        // Arrange
        var random = new Random(1472);
        byte[] bytes = new byte[16];
        Span<char> characters = stackalloc char[26];
        Span<byte> utf8 = stackalloc byte[26];

        for (int iteration = 0; iteration < 1024; iteration++)
        {
            random.NextBytes(bytes);
            var expected = new Ulid(bytes);
            string text = new NUlid.Ulid(bytes).ToString();

            // Act
            bool wroteCharacters = expected.TryWriteStringify(characters);
            bool wroteUtf8 = expected.TryWriteStringify(utf8);

            // Assert
            wroteCharacters.ShouldBeTrue();
            wroteUtf8.ShouldBeTrue();
            expected.ToString().ShouldBe(text);
            characters.SequenceEqual(text.AsSpan()).ShouldBeTrue();
            utf8.SequenceEqual(Encoding.UTF8.GetBytes(text)).ShouldBeTrue();
            ShouldParseEveryOverload(text, expected);
            Ulid.Parse(text).ToByteArray().ShouldBe(bytes);
        }
    }

    [Fact(DisplayName = displayPrefix + "warmed valid parse paths allocate zero bytes")]
    public void Parse_HotValidPaths_ShouldNotAllocate()
    {
        // Arrange
        byte[] utf8 = Encoding.UTF8.GetBytes(canonical);
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            ExerciseHotValidPaths(canonical, utf8);
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            succeeded &= ExerciseHotValidPaths(canonical, utf8);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        succeeded.ShouldBeTrue();
        _output.WriteLine($"All 10 valid Parse/TryParse overloads over 10,000 iterations allocated {allocatedBytes} bytes.");
        allocatedBytes.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "warmed invalid TryParse paths allocate zero bytes")]
    public void TryParse_HotInvalidPaths_ShouldNotAllocate()
    {
        // Arrange
        string invalid = "0000000000000000000000000\uff10";
        byte[] invalidUtf8 = Encoding.UTF8.GetBytes("0000000000000000000000000!");
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            ExerciseHotInvalidPaths(invalid, invalidUtf8);
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool rejected = true;
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            rejected &= ExerciseHotInvalidPaths(invalid, invalidUtf8);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        rejected.ShouldBeTrue();
        _output.WriteLine($"All 5 invalid TryParse overloads over 10,000 iterations allocated {allocatedBytes} bytes.");
        allocatedBytes.ShouldBe(0);
    }

    private static void ShouldRejectEveryOverload(string? input)
    {
        byte[] utf8 = input is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(input);
        Ulid.TryParse(input, out Ulid value).ShouldBeFalse();
        value.ShouldBe(default);
        Ulid.TryParse(input, CultureInfo.InvariantCulture, out value).ShouldBeFalse();
        value.ShouldBe(default);
        Ulid.TryParse(input.AsSpan(), out value).ShouldBeFalse();
        value.ShouldBe(default);
        Ulid.TryParse(input.AsSpan(), CultureInfo.InvariantCulture, out value).ShouldBeFalse();
        value.ShouldBe(default);
        Ulid.TryParse(utf8.AsSpan(), out value).ShouldBeFalse();
        value.ShouldBe(default);
        Should.Throw<FormatException>(() => Ulid.Parse(input!));
        Should.Throw<FormatException>(() => Ulid.Parse(input!, CultureInfo.InvariantCulture));
        Should.Throw<FormatException>(() => Ulid.Parse(input.AsSpan()));
        Should.Throw<FormatException>(() => Ulid.Parse(input.AsSpan(), CultureInfo.InvariantCulture));
        Should.Throw<FormatException>(() => Ulid.Parse(utf8.AsSpan()));
    }

    private static void ShouldParseEveryOverload(string input, Ulid expected)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(input);
        Ulid.Parse(input).ShouldBe(expected);
        Ulid.Parse(input, CultureInfo.InvariantCulture).ShouldBe(expected);
        Ulid.Parse(input.AsSpan()).ShouldBe(expected);
        Ulid.Parse(input.AsSpan(), CultureInfo.InvariantCulture).ShouldBe(expected);
        Ulid.Parse(utf8.AsSpan()).ShouldBe(expected);
        Ulid.TryParse(input, out Ulid value).ShouldBeTrue();
        value.ShouldBe(expected);
        Ulid.TryParse(input, CultureInfo.InvariantCulture, out value).ShouldBeTrue();
        value.ShouldBe(expected);
        Ulid.TryParse(input.AsSpan(), out value).ShouldBeTrue();
        value.ShouldBe(expected);
        Ulid.TryParse(input.AsSpan(), CultureInfo.InvariantCulture, out value).ShouldBeTrue();
        value.ShouldBe(expected);
        Ulid.TryParse(utf8.AsSpan(), out value).ShouldBeTrue();
        value.ShouldBe(expected);
    }

    private static bool ExerciseHotValidPaths(string text, byte[] utf8)
    {
        Ulid value = Ulid.Parse(text);
        bool succeeded = value == Ulid.Parse(text, CultureInfo.InvariantCulture);
        succeeded &= value == Ulid.Parse(text.AsSpan());
        succeeded &= value == Ulid.Parse(text.AsSpan(), CultureInfo.InvariantCulture);
        succeeded &= value == Ulid.Parse(utf8.AsSpan());
        succeeded &= Ulid.TryParse(text, out Ulid parsed) && parsed == value;
        succeeded &= Ulid.TryParse(text, CultureInfo.InvariantCulture, out parsed) && parsed == value;
        succeeded &= Ulid.TryParse(text.AsSpan(), out parsed) && parsed == value;
        succeeded &= Ulid.TryParse(text.AsSpan(), CultureInfo.InvariantCulture, out parsed) && parsed == value;
        succeeded &= Ulid.TryParse(utf8.AsSpan(), out parsed) && parsed == value;
        return succeeded;
    }

    private static bool ExerciseHotInvalidPaths(string text, byte[] utf8)
    {
        bool rejected = !Ulid.TryParse(text, out _);
        rejected &= !Ulid.TryParse(text, CultureInfo.InvariantCulture, out _);
        rejected &= !Ulid.TryParse(text.AsSpan(), out _);
        rejected &= !Ulid.TryParse(text.AsSpan(), CultureInfo.InvariantCulture, out _);
        rejected &= !Ulid.TryParse(utf8.AsSpan(), out _);
        return rejected;
    }
}
