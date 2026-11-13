using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Text.Internal;

namespace Assimalign.Cohesion.Database.Text;

/// <summary>An immutable lexeme and its sorted, distinct weighted positions.</summary>
public sealed class TextLexeme : IEquatable<TextLexeme>
{
    internal TextLexeme(string text, TextPosition[] positions)
    {
        Text = text;
        Positions = Array.AsReadOnly(positions);
    }

    /// <summary>Gets the exact lexeme; explicit vector construction performs no analysis.</summary>
    public string Text { get; }

    /// <summary>Gets at most 256 ascending positions. Duplicate positions keep the highest
    /// weight, including at the terminal position or count cutoff.</summary>
    public IReadOnlyList<TextPosition> Positions { get; }

    /// <summary>Creates an explicit lexeme, optionally stripped of positions.</summary>
    /// <param name="text">Nonempty valid Unicode of at most 2,047 UTF-8 bytes, without nulls.</param>
    /// <param name="positions">Positions, or null for a stripped lexeme. Only the earliest 256 are retained.</param>
    /// <returns>An immutable lexeme.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A position is the default value.</exception>
    /// <exception cref="TextException">The lexeme is invalid or contains invalid Unicode.</exception>
    public static TextLexeme Create(string text, IEnumerable<TextPosition>? positions = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Contains('\0') || TextValidation.Utf8Length(text) > TextVector.MaxLexemeBytes)
        {
            throw new TextException(TextErrorCode.InvalidLexeme, ErrorMessages.InvalidLexeme);
        }

        var builder = new TextLexemeBuilder();
        if (positions is not null)
        {
            foreach (TextPosition position in positions)
            {
                builder.Add(position, TextVector.MaxPositionsPerLexeme);
            }
        }

        return new TextLexeme(text, builder.Snapshot());
    }

    /// <inheritdoc />
    public bool Equals(TextLexeme? other)
    {
        if (other is null || Text != other.Text || Positions.Count != other.Positions.Count)
        {
            return false;
        }

        for (int i = 0; i < Positions.Count; i++)
        {
            if (Positions[i] != other.Positions[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TextLexeme other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Text, StringComparer.Ordinal);
        foreach (TextPosition position in Positions)
        {
            hash.Add(position);
        }

        return hash.ToHashCode();
    }
}
