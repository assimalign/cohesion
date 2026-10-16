using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A four-byte identifier encoded as ISO-8859-1 and stored as a big-endian unsigned integer.</summary>
// Deviates from the generated value-object rule per the Stage 1 brief: retain exactly the specified raw binary surface.
public readonly struct FourCC : IEquatable<FourCC>
{
    private FourCC(uint value) => Value = value;

    /// <summary>Gets the four bytes packed in big-endian order.</summary>
    public uint Value { get; }

    /// <summary>Creates a code from its packed representation.</summary>
    /// <param name="value">The packed big-endian value.</param>
    /// <returns>The code.</returns>
    public static FourCC FromUInt32(uint value) => new FourCC(value);

    /// <summary>Creates a code from exactly four bytes.</summary>
    /// <param name="value">The four encoded bytes.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentException">The span is not exactly four bytes long.</exception>
    public static FourCC FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException(ErrorMessages.FourCCLength, nameof(value));
        }
        return FromUInt32(BinaryPrimitives.ReadUInt32BigEndian(value));
    }

    /// <summary>Parses exactly four ISO-8859-1 characters, including values such as ©nam.</summary>
    /// <param name="value">The four characters, each at most U+00FF.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="FormatException">The value is not four ISO-8859-1 characters.</exception>
    public static FourCC Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out var result))
        {
            throw new FormatException(ErrorMessages.FourCCInvalid);
        }
        return result;
    }

    /// <summary>Tries to parse exactly four ISO-8859-1 characters.</summary>
    /// <param name="value">The characters to parse.</param>
    /// <param name="result">The code, or default on failure.</param>
    /// <returns>Whether the characters were valid.</returns>
    public static bool TryParse(string? value, out FourCC result)
    {
        result = default;
        if (value is null || value.Length != 4)
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (value[i] > 0xFF)
            {
                return false;
            }
            bytes[i] = (byte)value[i];
        }
        result = FromBytes(bytes);
        return true;
    }

    /// <summary>Writes the code into the first four destination bytes.</summary>
    /// <param name="destination">A destination with at least four bytes.</param>
    /// <exception cref="ArgumentException">The destination is shorter than four bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < 4)
        {
            throw new ArgumentException(ErrorMessages.DestinationTooSmall, nameof(destination));
        }
        BinaryPrimitives.WriteUInt32BigEndian(destination, Value);
    }

    /// <inheritdoc/>
    public bool Equals(FourCC other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FourCC other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Compares the raw four bytes.</summary>
    /// <param name="left">The first code.</param>
    /// <param name="right">The second code.</param>
    /// <returns>Whether the codes are equal.</returns>
    public static bool operator ==(FourCC left, FourCC right) => left.Equals(right);

    /// <summary>Compares the raw four bytes for inequality.</summary>
    /// <param name="left">The first code.</param>
    /// <param name="right">The second code.</param>
    /// <returns>Whether the codes differ.</returns>
    public static bool operator !=(FourCC left, FourCC right) => !left.Equals(right);

    /// <summary>Formats printable ISO-8859-1 characters as-is and control bytes as backslash-x followed by two uppercase hex digits.</summary>
    /// <returns>The diagnostic representation; escaped control bytes are not Parse input.</returns>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[4];
        WriteTo(bytes);
        var result = new StringBuilder(16);
        foreach (var value in bytes)
        {
            if (value < 0x20 || (value >= 0x7F && value < 0xA0))
            {
                result.Append("\\x");
                result.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append((char)value);
            }
        }
        return result.ToString();
    }
}
