using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents a typed, immutable claim or attribute value. Values are structural snapshots:
/// the factory methods defensively copy all caller-provided content, so later mutation of a
/// source buffer or collection never changes a materialized value, and cyclic value graphs
/// cannot be constructed.
/// </summary>
/// <remarks>
/// <para>
/// An uninitialized (<see langword="default" />) instance has <see cref="Kind" /> ==
/// <see cref="IdentityValueKind.Undefined" />: every <c>As*</c> accessor throws, every
/// <c>TryGet*</c> accessor returns <see langword="false" />, <see cref="ToString" /> returns
/// an empty string, and it equals only other undefined values. Use <see cref="Null" /> to
/// represent an explicit null value.
/// </para>
/// <para>
/// Equality is structural and kind-sensitive: values of different kinds are never equal.
/// <see cref="IdentityValueKind.Double" /> values compare by exact bit pattern (so
/// <see cref="double.NaN" /> equals itself and positive and negative zero differ), and
/// <see cref="IdentityValueKind.DateTime" /> values compare by instant and offset (the same
/// instant expressed in two offsets is not equal). Object member order is insignificant;
/// array element order is significant.
/// </para>
/// </remarks>
public readonly struct IdentityClaimValue : IEquatable<IdentityClaimValue>
{
    /// <summary>
    /// The maximum nesting depth of <see cref="IdentityValueKind.Array" /> and
    /// <see cref="IdentityValueKind.Object" /> values. Values sourced from untrusted
    /// protocol data are bounded so structural equality and rendering can never exhaust
    /// the stack.
    /// </summary>
    public const int MaxDepth = 64;

    // String, boxed decimal, byte[], ReadOnlyCollection<IdentityClaimValue>, or
    // ReadOnlyDictionary<string, IdentityClaimValue> depending on kind.
    private readonly object? _reference;
    // Boolean (0/1), Integer, Double (bit pattern), or DateTime ticks depending on kind.
    private readonly long _primitive;
    // DateTime offset minutes, or Array/Object nesting depth, depending on kind.
    private readonly int _auxiliary;
    private readonly IdentityValueKind _kind;

    private IdentityClaimValue(IdentityValueKind kind, object? reference, long primitive, int auxiliary)
    {
        _kind = kind;
        _reference = reference;
        _primitive = primitive;
        _auxiliary = auxiliary;
    }

    /// <summary>
    /// Gets the normalized shape of the value.
    /// </summary>
    public IdentityValueKind Kind => _kind;

    /// <summary>
    /// Gets a value indicating whether this value is the explicit null value.
    /// </summary>
    public bool IsNull => _kind == IdentityValueKind.Null;

    /// <summary>
    /// Gets a value indicating whether this value is uninitialized.
    /// </summary>
    public bool IsUndefined => _kind == IdentityValueKind.Undefined;

    /// <summary>
    /// Gets the explicit null value.
    /// </summary>
    public static IdentityClaimValue Null { get; } = new(IdentityValueKind.Null, null, 0, 0);

    #region Factories

    /// <summary>
    /// Creates a string value.
    /// </summary>
    /// <param name="value">The string content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.String" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value" /> is null.</exception>
    public static IdentityClaimValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new IdentityClaimValue(IdentityValueKind.String, value, 0, 0);
    }

    /// <summary>
    /// Creates a Boolean value.
    /// </summary>
    /// <param name="value">The Boolean content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Boolean" />.</returns>
    public static IdentityClaimValue FromBoolean(bool value)
    {
        return new IdentityClaimValue(IdentityValueKind.Boolean, null, value ? 1 : 0, 0);
    }

    /// <summary>
    /// Creates a 64-bit integer value.
    /// </summary>
    /// <param name="value">The integer content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Integer" />.</returns>
    public static IdentityClaimValue FromInteger(long value)
    {
        return new IdentityClaimValue(IdentityValueKind.Integer, null, value, 0);
    }

    /// <summary>
    /// Creates an IEEE-754 double value. Use this for JSON-sourced fractional numbers.
    /// </summary>
    /// <param name="value">The double content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Double" />.</returns>
    public static IdentityClaimValue FromDouble(double value)
    {
        return new IdentityClaimValue(IdentityValueKind.Double, null, BitConverter.DoubleToInt64Bits(value), 0);
    }

    /// <summary>
    /// Creates an exact decimal value. Use this for XML-schema-sourced decimal numbers.
    /// </summary>
    /// <param name="value">The decimal content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Decimal" />.</returns>
    public static IdentityClaimValue FromDecimal(decimal value)
    {
        return new IdentityClaimValue(IdentityValueKind.Decimal, value, 0, 0);
    }

    /// <summary>
    /// Creates a date-time value. Use this for source-typed date values (for example SAML
    /// <c>xs:dateTime</c>), not for numeric epoch timestamps.
    /// </summary>
    /// <param name="value">The date-time content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.DateTime" />.</returns>
    public static IdentityClaimValue FromDateTime(DateTimeOffset value)
    {
        return new IdentityClaimValue(
            IdentityValueKind.DateTime,
            null,
            value.Ticks,
            (int)value.Offset.TotalMinutes);
    }

    /// <summary>
    /// Creates a binary value by copying the provided bytes.
    /// </summary>
    /// <param name="value">The binary content. The bytes are copied.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Binary" />.</returns>
    public static IdentityClaimValue FromBinary(ReadOnlySpan<byte> value)
    {
        return new IdentityClaimValue(IdentityValueKind.Binary, value.ToArray(), 0, 0);
    }

    /// <summary>
    /// Creates an array value by snapshotting the provided values.
    /// </summary>
    /// <param name="values">The element values. The sequence is copied.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Array" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values" /> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when an element is undefined.</exception>
    /// <exception cref="IdentityModelException">Thrown when the resulting nesting depth exceeds <see cref="MaxDepth" />.</exception>
    public static IdentityClaimValue FromArray(IEnumerable<IdentityClaimValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var elements = new List<IdentityClaimValue>(values is IReadOnlyCollection<IdentityClaimValue> sized ? sized.Count : 4);
        var depth = 1;

        foreach (var element in values)
        {
            if (element.IsUndefined)
            {
                throw new ArgumentException("An array value must not contain undefined elements.", nameof(values));
            }

            depth = Math.Max(depth, element.Depth + 1);
            elements.Add(element);
        }

        if (depth > MaxDepth)
        {
            throw new IdentityModelException(
                $"The claim value nesting depth exceeds the maximum of {MaxDepth}.");
        }

        return new IdentityClaimValue(
            IdentityValueKind.Array,
            new ReadOnlyCollection<IdentityClaimValue>(elements.ToArray()),
            0,
            depth);
    }

    /// <summary>
    /// Creates an object value by snapshotting the provided members.
    /// </summary>
    /// <param name="members">The named members. The sequence is copied; member names compare ordinally.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Object" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="members" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a member name is null, empty, or duplicated, or when a member value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the resulting nesting depth exceeds <see cref="MaxDepth" />.</exception>
    public static IdentityClaimValue FromObject(IEnumerable<KeyValuePair<string, IdentityClaimValue>> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var snapshot = new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
        var depth = 1;

        foreach (var (name, value) in members)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(members));

            if (value.IsUndefined)
            {
                throw new ArgumentException(
                    $"The object member '{name}' must not have an undefined value.",
                    nameof(members));
            }

            if (!snapshot.TryAdd(name, value))
            {
                throw new ArgumentException(
                    $"The object member '{name}' is duplicated.",
                    nameof(members));
            }

            depth = Math.Max(depth, value.Depth + 1);
        }

        if (depth > MaxDepth)
        {
            throw new IdentityModelException(
                $"The claim value nesting depth exceeds the maximum of {MaxDepth}.");
        }

        return new IdentityClaimValue(
            IdentityValueKind.Object,
            new ReadOnlyDictionary<string, IdentityClaimValue>(snapshot),
            0,
            depth);
    }

    #endregion

    #region Conversions

    /// <summary>
    /// Converts a string to a claim value. A null string converts to <see cref="Null" />.
    /// </summary>
    /// <param name="value">The string content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.String" />, or <see cref="Null" /> when <paramref name="value" /> is null.</returns>
    public static implicit operator IdentityClaimValue(string? value)
        => value is null ? Null : FromString(value);

    /// <summary>
    /// Converts a Boolean to a claim value.
    /// </summary>
    /// <param name="value">The Boolean content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Boolean" />.</returns>
    public static implicit operator IdentityClaimValue(bool value) => FromBoolean(value);

    /// <summary>
    /// Converts an integer to a claim value.
    /// </summary>
    /// <param name="value">The integer content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Integer" />.</returns>
    public static implicit operator IdentityClaimValue(long value) => FromInteger(value);

    /// <summary>
    /// Converts a double to a claim value.
    /// </summary>
    /// <param name="value">The double content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Double" />.</returns>
    public static implicit operator IdentityClaimValue(double value) => FromDouble(value);

    /// <summary>
    /// Converts a decimal to a claim value.
    /// </summary>
    /// <param name="value">The decimal content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.Decimal" />.</returns>
    public static implicit operator IdentityClaimValue(decimal value) => FromDecimal(value);

    /// <summary>
    /// Converts a date-time to a claim value.
    /// </summary>
    /// <param name="value">The date-time content.</param>
    /// <returns>A value of kind <see cref="IdentityValueKind.DateTime" />.</returns>
    public static implicit operator IdentityClaimValue(DateTimeOffset value) => FromDateTime(value);

    #endregion

    #region Accessors

    /// <summary>
    /// Gets the string content.
    /// </summary>
    /// <returns>The string content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not a string.</exception>
    public string AsString()
        => _kind == IdentityValueKind.String ? (string)_reference! : throw KindMismatch(IdentityValueKind.String);

    /// <summary>
    /// Gets the Boolean content.
    /// </summary>
    /// <returns>The Boolean content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not a Boolean.</exception>
    public bool AsBoolean()
        => _kind == IdentityValueKind.Boolean ? _primitive != 0 : throw KindMismatch(IdentityValueKind.Boolean);

    /// <summary>
    /// Gets the integer content.
    /// </summary>
    /// <returns>The integer content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not an integer.</exception>
    public long AsInteger()
        => _kind == IdentityValueKind.Integer ? _primitive : throw KindMismatch(IdentityValueKind.Integer);

    /// <summary>
    /// Gets the double content.
    /// </summary>
    /// <returns>The double content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not a double.</exception>
    public double AsDouble()
        => _kind == IdentityValueKind.Double ? BitConverter.Int64BitsToDouble(_primitive) : throw KindMismatch(IdentityValueKind.Double);

    /// <summary>
    /// Gets the decimal content.
    /// </summary>
    /// <returns>The decimal content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not a decimal.</exception>
    public decimal AsDecimal()
        => _kind == IdentityValueKind.Decimal ? (decimal)_reference! : throw KindMismatch(IdentityValueKind.Decimal);

    /// <summary>
    /// Gets the date-time content.
    /// </summary>
    /// <returns>The date-time content.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not a date-time.</exception>
    public DateTimeOffset AsDateTime()
        => _kind == IdentityValueKind.DateTime
            ? new DateTimeOffset(_primitive, TimeSpan.FromMinutes(_auxiliary))
            : throw KindMismatch(IdentityValueKind.DateTime);

    /// <summary>
    /// Gets the binary content.
    /// </summary>
    /// <returns>The binary content. The returned memory must not be mutated.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not binary.</exception>
    public ReadOnlyMemory<byte> AsBinary()
        => _kind == IdentityValueKind.Binary ? (byte[])_reference! : throw KindMismatch(IdentityValueKind.Binary);

    /// <summary>
    /// Gets the array elements.
    /// </summary>
    /// <returns>The array elements.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not an array.</exception>
    public IReadOnlyList<IdentityClaimValue> AsArray()
        => _kind == IdentityValueKind.Array
            ? (ReadOnlyCollection<IdentityClaimValue>)_reference!
            : throw KindMismatch(IdentityValueKind.Array);

    /// <summary>
    /// Gets the object members.
    /// </summary>
    /// <returns>The object members keyed by ordinal member name.</returns>
    /// <exception cref="IdentityModelException">Thrown when the value is not an object.</exception>
    public IReadOnlyDictionary<string, IdentityClaimValue> AsObject()
        => _kind == IdentityValueKind.Object
            ? (ReadOnlyDictionary<string, IdentityClaimValue>)_reference!
            : throw KindMismatch(IdentityValueKind.Object);

    /// <summary>
    /// Attempts to get the string content.
    /// </summary>
    /// <param name="value">When this method returns, contains the string content, if the value is a string.</param>
    /// <returns><see langword="true" /> when the value is a string; otherwise <see langword="false" />.</returns>
    public bool TryGetString([NotNullWhen(true)] out string? value)
    {
        value = _kind == IdentityValueKind.String ? (string)_reference! : null;
        return _kind == IdentityValueKind.String;
    }

    /// <summary>
    /// Attempts to get the Boolean content.
    /// </summary>
    /// <param name="value">When this method returns, contains the Boolean content, if the value is a Boolean.</param>
    /// <returns><see langword="true" /> when the value is a Boolean; otherwise <see langword="false" />.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = _kind == IdentityValueKind.Boolean && _primitive != 0;
        return _kind == IdentityValueKind.Boolean;
    }

    /// <summary>
    /// Attempts to get the integer content.
    /// </summary>
    /// <param name="value">When this method returns, contains the integer content, if the value is an integer.</param>
    /// <returns><see langword="true" /> when the value is an integer; otherwise <see langword="false" />.</returns>
    public bool TryGetInteger(out long value)
    {
        value = _kind == IdentityValueKind.Integer ? _primitive : 0;
        return _kind == IdentityValueKind.Integer;
    }

    /// <summary>
    /// Attempts to get the double content.
    /// </summary>
    /// <param name="value">When this method returns, contains the double content, if the value is a double.</param>
    /// <returns><see langword="true" /> when the value is a double; otherwise <see langword="false" />.</returns>
    public bool TryGetDouble(out double value)
    {
        value = _kind == IdentityValueKind.Double ? BitConverter.Int64BitsToDouble(_primitive) : 0;
        return _kind == IdentityValueKind.Double;
    }

    /// <summary>
    /// Attempts to get the decimal content.
    /// </summary>
    /// <param name="value">When this method returns, contains the decimal content, if the value is a decimal.</param>
    /// <returns><see langword="true" /> when the value is a decimal; otherwise <see langword="false" />.</returns>
    public bool TryGetDecimal(out decimal value)
    {
        value = _kind == IdentityValueKind.Decimal ? (decimal)_reference! : 0;
        return _kind == IdentityValueKind.Decimal;
    }

    /// <summary>
    /// Attempts to get the date-time content.
    /// </summary>
    /// <param name="value">When this method returns, contains the date-time content, if the value is a date-time.</param>
    /// <returns><see langword="true" /> when the value is a date-time; otherwise <see langword="false" />.</returns>
    public bool TryGetDateTime(out DateTimeOffset value)
    {
        value = _kind == IdentityValueKind.DateTime
            ? new DateTimeOffset(_primitive, TimeSpan.FromMinutes(_auxiliary))
            : default;
        return _kind == IdentityValueKind.DateTime;
    }

    /// <summary>
    /// Attempts to get the binary content.
    /// </summary>
    /// <param name="value">When this method returns, contains the binary content, if the value is binary.</param>
    /// <returns><see langword="true" /> when the value is binary; otherwise <see langword="false" />.</returns>
    public bool TryGetBinary(out ReadOnlyMemory<byte> value)
    {
        value = _kind == IdentityValueKind.Binary ? (byte[])_reference! : default;
        return _kind == IdentityValueKind.Binary;
    }

    /// <summary>
    /// Attempts to get the array elements.
    /// </summary>
    /// <param name="values">When this method returns, contains the array elements, if the value is an array.</param>
    /// <returns><see langword="true" /> when the value is an array; otherwise <see langword="false" />.</returns>
    public bool TryGetArray([NotNullWhen(true)] out IReadOnlyList<IdentityClaimValue>? values)
    {
        values = _kind == IdentityValueKind.Array ? (ReadOnlyCollection<IdentityClaimValue>)_reference! : null;
        return _kind == IdentityValueKind.Array;
    }

    /// <summary>
    /// Attempts to get the object members.
    /// </summary>
    /// <param name="members">When this method returns, contains the object members, if the value is an object.</param>
    /// <returns><see langword="true" /> when the value is an object; otherwise <see langword="false" />.</returns>
    public bool TryGetObject([NotNullWhen(true)] out IReadOnlyDictionary<string, IdentityClaimValue>? members)
    {
        members = _kind == IdentityValueKind.Object ? (ReadOnlyDictionary<string, IdentityClaimValue>)_reference! : null;
        return _kind == IdentityValueKind.Object;
    }

    #endregion

    #region Equality

    /// <inheritdoc />
    public bool Equals(IdentityClaimValue other)
    {
        if (_kind != other._kind)
        {
            return false;
        }

        switch (_kind)
        {
            case IdentityValueKind.Undefined:
            case IdentityValueKind.Null:
                return true;
            case IdentityValueKind.String:
                return string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal);
            case IdentityValueKind.Boolean:
            case IdentityValueKind.Integer:
            case IdentityValueKind.Double:
                return _primitive == other._primitive;
            case IdentityValueKind.Decimal:
                return ((decimal)_reference!).Equals((decimal)other._reference!);
            case IdentityValueKind.DateTime:
                return _primitive == other._primitive && _auxiliary == other._auxiliary;
            case IdentityValueKind.Binary:
                return ((byte[])_reference!).AsSpan().SequenceEqual((byte[])other._reference!);
            case IdentityValueKind.Array:
            {
                var left = (ReadOnlyCollection<IdentityClaimValue>)_reference!;
                var right = (ReadOnlyCollection<IdentityClaimValue>)other._reference!;
                if (left.Count != right.Count)
                {
                    return false;
                }

                for (var index = 0; index < left.Count; index++)
                {
                    if (!left[index].Equals(right[index]))
                    {
                        return false;
                    }
                }

                return true;
            }
            case IdentityValueKind.Object:
            {
                var left = (ReadOnlyDictionary<string, IdentityClaimValue>)_reference!;
                var right = (ReadOnlyDictionary<string, IdentityClaimValue>)other._reference!;
                if (left.Count != right.Count)
                {
                    return false;
                }

                foreach (var (name, value) in left)
                {
                    if (!right.TryGetValue(name, out var counterpart) || !value.Equals(counterpart))
                    {
                        return false;
                    }
                }

                return true;
            }
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IdentityClaimValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int)_kind);

        switch (_kind)
        {
            case IdentityValueKind.String:
                hash.Add((string)_reference!, StringComparer.Ordinal);
                break;
            case IdentityValueKind.Boolean:
            case IdentityValueKind.Integer:
            case IdentityValueKind.Double:
                hash.Add(_primitive);
                break;
            case IdentityValueKind.Decimal:
                hash.Add((decimal)_reference!);
                break;
            case IdentityValueKind.DateTime:
                hash.Add(_primitive);
                hash.Add(_auxiliary);
                break;
            case IdentityValueKind.Binary:
                hash.AddBytes((byte[])_reference!);
                break;
            case IdentityValueKind.Array:
            {
                var elements = (ReadOnlyCollection<IdentityClaimValue>)_reference!;
                for (var index = 0; index < elements.Count; index++)
                {
                    hash.Add(elements[index].GetHashCode());
                }

                break;
            }
            case IdentityValueKind.Object:
            {
                // Member order is insignificant, so combine per-member hashes order-independently.
                var members = (ReadOnlyDictionary<string, IdentityClaimValue>)_reference!;
                var combined = 0;
                foreach (var (name, value) in members)
                {
                    combined ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(name), value.GetHashCode());
                }

                hash.Add(combined);
                break;
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two claim values are structurally equal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true" /> when the values are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(IdentityClaimValue left, IdentityClaimValue right) => left.Equals(right);

    /// <summary>
    /// Determines whether two claim values are structurally unequal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true" /> when the values are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(IdentityClaimValue left, IdentityClaimValue right) => !left.Equals(right);

    #endregion

    /// <summary>
    /// Renders the value using culture-invariant formatting. Undefined values render as an
    /// empty string; date-times render in ISO 8601; binary renders as Base64.
    /// </summary>
    /// <returns>The culture-invariant rendering.</returns>
    public override string ToString()
    {
        switch (_kind)
        {
            case IdentityValueKind.Undefined:
                return string.Empty;
            case IdentityValueKind.Null:
                return "null";
            case IdentityValueKind.String:
                return (string)_reference!;
            case IdentityValueKind.Boolean:
                return _primitive != 0 ? "true" : "false";
            case IdentityValueKind.Integer:
                return _primitive.ToString(CultureInfo.InvariantCulture);
            case IdentityValueKind.Double:
                return BitConverter.Int64BitsToDouble(_primitive).ToString(CultureInfo.InvariantCulture);
            case IdentityValueKind.Decimal:
                return ((decimal)_reference!).ToString(CultureInfo.InvariantCulture);
            case IdentityValueKind.DateTime:
                return new DateTimeOffset(_primitive, TimeSpan.FromMinutes(_auxiliary))
                    .ToString("O", CultureInfo.InvariantCulture);
            case IdentityValueKind.Binary:
                return Convert.ToBase64String((byte[])_reference!);
            case IdentityValueKind.Array:
            {
                var elements = (ReadOnlyCollection<IdentityClaimValue>)_reference!;
                var builder = new StringBuilder("[");
                for (var index = 0; index < elements.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(elements[index].ToString());
                }

                return builder.Append(']').ToString();
            }
            case IdentityValueKind.Object:
            {
                var members = (ReadOnlyDictionary<string, IdentityClaimValue>)_reference!;
                var builder = new StringBuilder("{");
                var first = true;
                foreach (var (name, value) in members)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(name).Append(": ").Append(value.ToString());
                }

                return builder.Append('}').ToString();
            }
            default:
                return string.Empty;
        }
    }

    private int Depth => _kind is IdentityValueKind.Array or IdentityValueKind.Object ? _auxiliary : 0;

    private IdentityModelException KindMismatch(IdentityValueKind expected)
        => new($"The claim value is of kind '{_kind}', not '{expected}'.");
}
