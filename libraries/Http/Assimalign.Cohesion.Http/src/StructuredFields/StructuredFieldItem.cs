using System;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9651 &#167; 3.3 item: a <see cref="StructuredFieldBareItem"/> together with its
/// <see cref="StructuredFieldParameters"/>. This is both the top-level <c>item</c> field
/// type and the element type of an <see cref="StructuredFieldInnerList"/>.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct StructuredFieldItem : IEquatable<StructuredFieldItem>
{
    /// <summary>
    /// Initializes an item with no parameters.
    /// </summary>
    /// <param name="value">The bare item value.</param>
    public StructuredFieldItem(StructuredFieldBareItem value)
        : this(value, StructuredFieldParameters.Empty)
    {
    }

    /// <summary>
    /// Initializes an item with the specified parameters.
    /// </summary>
    /// <param name="value">The bare item value.</param>
    /// <param name="parameters">The parameters attached to the item.</param>
    public StructuredFieldItem(StructuredFieldBareItem value, StructuredFieldParameters parameters)
    {
        Value = value;
        Parameters = parameters;
    }

    /// <summary>Gets the bare item value.</summary>
    public StructuredFieldBareItem Value { get; }

    /// <summary>Gets the parameters attached to this item.</summary>
    public StructuredFieldParameters Parameters { get; }

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 item (&#167; 4.2, field type
    /// <c>item</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed item.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldItem result)
        => TryParse(input, out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 item (&#167; 4.2, field type
    /// <c>item</c>). On failure, <paramref name="error"/> carries a human-readable explanation.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed item.</param>
    /// <param name="error">When this method returns <see langword="false"/>, the reason parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldItem result, out string? error)
        => StructuredFieldParser.TryParseItem(input, out result, out error);

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 item (&#167; 4.2, field type <c>item</c>).
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed item.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(HttpHeaderValue value, out StructuredFieldItem result)
        => TryParse(value.Value.AsSpan(), out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 item (&#167; 4.2, field type
    /// <c>item</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <returns>The parsed item.</returns>
    /// <exception cref="HttpException">The input is not a well-formed item.</exception>
    public static StructuredFieldItem Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out StructuredFieldItem result, out string? error))
        {
            throw new HttpInvalidStructuredFieldException(error ?? "Malformed structured field item.");
        }
        return result;
    }

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 item (&#167; 4.2, field type <c>item</c>).
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <returns>The parsed item.</returns>
    /// <exception cref="HttpException">The input is not a well-formed item.</exception>
    public static StructuredFieldItem Parse(HttpHeaderValue value) => Parse(value.Value.AsSpan());

    /// <summary>
    /// Serializes this item to its RFC 9651 &#167; 4.1.3 canonical form.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">The item cannot be serialized.</exception>
    public string Serialize()
    {
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        Value.WriteTo(builder);
        Parameters.WriteTo(builder);
    }

    /// <inheritdoc />
    public bool Equals(StructuredFieldItem other) => Value.Equals(other.Value) && Parameters.Equals(other.Parameters);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldItem other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Parameters);

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two items are equal.</summary>
    /// <param name="left">The first item.</param>
    /// <param name="right">The second item.</param>
    /// <returns><see langword="true"/> if the items are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldItem left, StructuredFieldItem right) => left.Equals(right);

    /// <summary>Determines whether two items are unequal.</summary>
    /// <param name="left">The first item.</param>
    /// <param name="right">The second item.</param>
    /// <returns><see langword="true"/> if the items are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldItem left, StructuredFieldItem right) => !left.Equals(right);
}
