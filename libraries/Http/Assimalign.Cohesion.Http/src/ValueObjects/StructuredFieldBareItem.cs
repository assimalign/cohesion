using System;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9651 &#167; 3.3 bare item: one of Integer, Decimal, String, Token, Byte Sequence,
/// Boolean, Date, or Display String. This is the leaf value carried by a
/// <see cref="StructuredFieldItem"/> and by every parameter.
/// </summary>
/// <remarks>
/// <para>
/// The bare item is an immutable, allocation-light discriminated value. Numeric kinds
/// (Integer, Decimal, Date, Boolean) are stored inline; the textual and binary kinds
/// (String, Token, Display String, Byte Sequence) hold a single reference. Read the
/// <see cref="Type"/> before calling a typed accessor; a mismatched accessor throws
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Factory methods validate their input against the RFC range and syntax rules so an
/// instance always serializes to a canonical form (RFC 9651 &#167; 4.1.3.1).
/// </para>
/// </remarks>
[DebuggerDisplay("{Type}: {ToString(),nq}")]
public readonly struct StructuredFieldBareItem : IEquatable<StructuredFieldBareItem>
{
    /// <summary>The inclusive maximum magnitude of an Integer or Date (RFC 9651 &#167; 3.3.1).</summary>
    internal const long MaxInteger = 999_999_999_999_999L;

    /// <summary>The inclusive minimum magnitude of an Integer or Date (RFC 9651 &#167; 3.3.1).</summary>
    internal const long MinInteger = -999_999_999_999_999L;

    private readonly StructuredFieldType _type;
    private readonly long _numeric;     // Integer, Date (unix seconds), Boolean (0/1)
    private readonly decimal _decimal;  // Decimal
    private readonly object? _reference; // string (String/Token/DisplayString) or byte[] (ByteSequence)

    private StructuredFieldBareItem(StructuredFieldType type, long numeric, decimal @decimal, object? reference)
    {
        _type = type;
        _numeric = numeric;
        _decimal = @decimal;
        _reference = reference;
    }

    /// <summary>Gets the concrete kind of this bare item.</summary>
    public StructuredFieldType Type => _type;

    #region Factories

    /// <summary>
    /// Creates an Integer bare item (RFC 9651 &#167; 3.3.1).
    /// </summary>
    /// <param name="value">The integer value; must be within &#177;999,999,999,999,999.</param>
    /// <returns>The Integer bare item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the RFC 9651 integer range.</exception>
    public static StructuredFieldBareItem FromInteger(long value)
    {
        if (value < MinInteger || value > MaxInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Integer is outside the RFC 9651 range of ±999,999,999,999,999.");
        }
        return new StructuredFieldBareItem(StructuredFieldType.Integer, value, 0m, null);
    }

    /// <summary>
    /// Creates a Decimal bare item (RFC 9651 &#167; 3.3.2).
    /// </summary>
    /// <param name="value">The decimal value; its integer component must have at most 12 digits.</param>
    /// <returns>The Decimal bare item.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The integer component of <paramref name="value"/> has more than 12 digits.</exception>
    public static StructuredFieldBareItem FromDecimal(decimal value)
    {
        decimal integerPart = decimal.Truncate(Math.Abs(value));
        if (integerPart >= 1_000_000_000_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Decimal integer component exceeds the RFC 9651 limit of 12 digits.");
        }
        return new StructuredFieldBareItem(StructuredFieldType.Decimal, 0L, value, null);
    }

    /// <summary>
    /// Creates a String bare item (RFC 9651 &#167; 3.3.3).
    /// </summary>
    /// <param name="value">The string value; every character must be printable ASCII (%x20-7E).</param>
    /// <returns>The String bare item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a character outside %x20-7E.</exception>
    public static StructuredFieldBareItem FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (char c in value)
        {
            if (c < ' ' || c > '~')
            {
                throw new ArgumentException("String contains a character outside the printable ASCII range (%x20-7E).", nameof(value));
            }
        }
        return new StructuredFieldBareItem(StructuredFieldType.String, 0L, 0m, value);
    }

    /// <summary>
    /// Creates a Token bare item (RFC 9651 &#167; 3.3.4).
    /// </summary>
    /// <param name="value">The token value; must match the <c>sf-token</c> grammar.</param>
    /// <returns>The Token bare item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not match the <c>sf-token</c> grammar.</exception>
    public static StructuredFieldBareItem FromToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!StructuredFieldGrammar.IsValidToken(value))
        {
            throw new ArgumentException("Value does not match the RFC 9651 sf-token grammar.", nameof(value));
        }
        return new StructuredFieldBareItem(StructuredFieldType.Token, 0L, 0m, value);
    }

    /// <summary>
    /// Creates a Byte Sequence bare item (RFC 9651 &#167; 3.3.5).
    /// </summary>
    /// <param name="value">The raw bytes.</param>
    /// <returns>The Byte Sequence bare item.</returns>
    public static StructuredFieldBareItem FromByteSequence(ReadOnlySpan<byte> value)
        => new(StructuredFieldType.ByteSequence, 0L, 0m, value.ToArray());

    /// <summary>
    /// Creates a Boolean bare item (RFC 9651 &#167; 3.3.6).
    /// </summary>
    /// <param name="value">The boolean value.</param>
    /// <returns>The Boolean bare item.</returns>
    public static StructuredFieldBareItem FromBoolean(bool value)
        => new(StructuredFieldType.Boolean, value ? 1L : 0L, 0m, null);

    /// <summary>
    /// Creates a Date bare item (RFC 9651 &#167; 3.3.7).
    /// </summary>
    /// <param name="unixTimeSeconds">Seconds relative to the Unix epoch; must be within &#177;999,999,999,999,999.</param>
    /// <returns>The Date bare item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unixTimeSeconds"/> is outside the RFC 9651 integer range.</exception>
    public static StructuredFieldBareItem FromDate(long unixTimeSeconds)
    {
        if (unixTimeSeconds < MinInteger || unixTimeSeconds > MaxInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(unixTimeSeconds), unixTimeSeconds, "Date is outside the RFC 9651 range of ±999,999,999,999,999 seconds.");
        }
        return new StructuredFieldBareItem(StructuredFieldType.Date, unixTimeSeconds, 0m, null);
    }

    /// <summary>
    /// Creates a Date bare item (RFC 9651 &#167; 3.3.7) from a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="value">The instant; truncated to whole seconds since the Unix epoch.</param>
    /// <returns>The Date bare item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the RFC 9651 integer range.</exception>
    public static StructuredFieldBareItem FromDate(DateTimeOffset value)
        => FromDate(value.ToUnixTimeSeconds());

    /// <summary>
    /// Creates a Display String bare item (RFC 9651 &#167; 3.3.8).
    /// </summary>
    /// <param name="value">The Unicode string; it is conveyed as percent-encoded UTF-8 on the wire.</param>
    /// <returns>The Display String bare item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static StructuredFieldBareItem FromDisplayString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new StructuredFieldBareItem(StructuredFieldType.DisplayString, 0L, 0m, value);
    }

    // Unchecked factories for the parser, which has already validated ranges and syntax
    // against the RFC grammar. These skip the re-validation the public factories perform and,
    // for byte sequences, take ownership of the supplied array without copying.
    internal static StructuredFieldBareItem CreateBoolean(bool value) => new(StructuredFieldType.Boolean, value ? 1L : 0L, 0m, null);
    internal static StructuredFieldBareItem CreateInteger(long value) => new(StructuredFieldType.Integer, value, 0m, null);
    internal static StructuredFieldBareItem CreateDecimal(decimal value) => new(StructuredFieldType.Decimal, 0L, value, null);
    internal static StructuredFieldBareItem CreateString(string value) => new(StructuredFieldType.String, 0L, 0m, value);
    internal static StructuredFieldBareItem CreateToken(string value) => new(StructuredFieldType.Token, 0L, 0m, value);
    internal static StructuredFieldBareItem CreateByteSequence(byte[] value) => new(StructuredFieldType.ByteSequence, 0L, 0m, value);
    internal static StructuredFieldBareItem CreateDate(long unixTimeSeconds) => new(StructuredFieldType.Date, unixTimeSeconds, 0m, null);
    internal static StructuredFieldBareItem CreateDisplayString(string value) => new(StructuredFieldType.DisplayString, 0L, 0m, value);

    #endregion

    #region Accessors

    /// <summary>Gets the Integer value.</summary>
    /// <returns>The integer value.</returns>
    /// <exception cref="InvalidOperationException">This item is not an <see cref="StructuredFieldType.Integer"/>.</exception>
    public long AsInteger() => _type == StructuredFieldType.Integer ? _numeric : throw WrongType(StructuredFieldType.Integer);

    /// <summary>Gets the Decimal value.</summary>
    /// <returns>The decimal value.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.Decimal"/>.</exception>
    public decimal AsDecimal() => _type == StructuredFieldType.Decimal ? _decimal : throw WrongType(StructuredFieldType.Decimal);

    /// <summary>Gets the String value.</summary>
    /// <returns>The string value.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.String"/>.</exception>
    public string AsString() => _type == StructuredFieldType.String ? (string)_reference! : throw WrongType(StructuredFieldType.String);

    /// <summary>Gets the Token value.</summary>
    /// <returns>The token value.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.Token"/>.</exception>
    public string AsToken() => _type == StructuredFieldType.Token ? (string)_reference! : throw WrongType(StructuredFieldType.Token);

    /// <summary>Gets the Byte Sequence value.</summary>
    /// <returns>The raw bytes.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.ByteSequence"/>.</exception>
    public ReadOnlyMemory<byte> AsByteSequence() => _type == StructuredFieldType.ByteSequence ? (byte[])_reference! : throw WrongType(StructuredFieldType.ByteSequence);

    /// <summary>Gets the Boolean value.</summary>
    /// <returns>The boolean value.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.Boolean"/>.</exception>
    public bool AsBoolean() => _type == StructuredFieldType.Boolean ? _numeric != 0L : throw WrongType(StructuredFieldType.Boolean);

    /// <summary>Gets the Date value as seconds relative to the Unix epoch.</summary>
    /// <returns>The Unix time in seconds.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.Date"/>.</exception>
    public long AsDate() => _type == StructuredFieldType.Date ? _numeric : throw WrongType(StructuredFieldType.Date);

    /// <summary>Gets the Date value as a <see cref="DateTimeOffset"/>.</summary>
    /// <returns>The instant represented by this date.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.Date"/>.</exception>
    public DateTimeOffset AsDateTimeOffset() => DateTimeOffset.FromUnixTimeSeconds(AsDate());

    /// <summary>Gets the Display String value.</summary>
    /// <returns>The Unicode string value.</returns>
    /// <exception cref="InvalidOperationException">This item is not a <see cref="StructuredFieldType.DisplayString"/>.</exception>
    public string AsDisplayString() => _type == StructuredFieldType.DisplayString ? (string)_reference! : throw WrongType(StructuredFieldType.DisplayString);

    private InvalidOperationException WrongType(StructuredFieldType expected)
        => new($"Structured field bare item is of type '{_type}', not '{expected}'.");

    #endregion

    #region Serialization

    /// <summary>
    /// Serializes this bare item to its RFC 9651 &#167; 4.1.3.1 canonical form.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">The item cannot be serialized (for example, a Display String
    /// containing an unpaired surrogate).</exception>
    public string Serialize()
    {
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        switch (_type)
        {
            case StructuredFieldType.Integer:
                builder.Append(_numeric);
                break;
            case StructuredFieldType.Decimal:
                StructuredFieldGrammar.WriteDecimal(builder, _decimal);
                break;
            case StructuredFieldType.String:
                StructuredFieldGrammar.WriteString(builder, (string)_reference!);
                break;
            case StructuredFieldType.Token:
                StructuredFieldGrammar.WriteToken(builder, (string)_reference!);
                break;
            case StructuredFieldType.ByteSequence:
                StructuredFieldGrammar.WriteByteSequence(builder, (byte[])_reference!);
                break;
            case StructuredFieldType.Boolean:
                builder.Append('?').Append(_numeric != 0L ? '1' : '0');
                break;
            case StructuredFieldType.Date:
                builder.Append('@').Append(_numeric);
                break;
            case StructuredFieldType.DisplayString:
                StructuredFieldGrammar.WriteDisplayString(builder, (string)_reference!);
                break;
            default:
                throw new HttpInvalidStructuredFieldException($"Unknown structured field bare item type '{_type}'.");
        }
    }

    #endregion

    #region Equality

    /// <inheritdoc />
    public bool Equals(StructuredFieldBareItem other)
    {
        if (_type != other._type)
        {
            return false;
        }
        return _type switch
        {
            StructuredFieldType.Integer or StructuredFieldType.Date or StructuredFieldType.Boolean => _numeric == other._numeric,
            StructuredFieldType.Decimal => _decimal == other._decimal,
            StructuredFieldType.String or StructuredFieldType.Token or StructuredFieldType.DisplayString =>
                string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
            StructuredFieldType.ByteSequence => ((byte[])_reference!).AsSpan().SequenceEqual((byte[])other._reference!),
            _ => false,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldBareItem other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        switch (_type)
        {
            case StructuredFieldType.Integer:
            case StructuredFieldType.Date:
            case StructuredFieldType.Boolean:
                return HashCode.Combine(_type, _numeric);
            case StructuredFieldType.Decimal:
                return HashCode.Combine(_type, _decimal);
            case StructuredFieldType.String:
            case StructuredFieldType.Token:
            case StructuredFieldType.DisplayString:
                return HashCode.Combine(_type, ((string)_reference!).GetHashCode(StringComparison.Ordinal));
            case StructuredFieldType.ByteSequence:
                var hash = new HashCode();
                hash.Add(_type);
                hash.AddBytes((byte[])_reference!);
                return hash.ToHashCode();
            default:
                return _type.GetHashCode();
        }
    }

    /// <summary>Determines whether two bare items are equal.</summary>
    /// <param name="left">The first bare item.</param>
    /// <param name="right">The second bare item.</param>
    /// <returns><see langword="true"/> if the items are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldBareItem left, StructuredFieldBareItem right) => left.Equals(right);

    /// <summary>Determines whether two bare items are unequal.</summary>
    /// <param name="left">The first bare item.</param>
    /// <param name="right">The second bare item.</param>
    /// <returns><see langword="true"/> if the items are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldBareItem left, StructuredFieldBareItem right) => !left.Equals(right);

    #endregion

    /// <summary>
    /// Returns the RFC 9651 canonical form of this bare item, or a diagnostic string if it
    /// cannot be serialized.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    public override string ToString()
    {
        try
        {
            return Serialize();
        }
        catch (HttpException)
        {
            return $"<{_type}>";
        }
    }
}
