using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types.Text.Internal;

namespace Assimalign.Cohesion.Database.Types.Text;

/// <summary>An immutable PostgreSQL-compatible tsvector value, ordered by UTF-8 lexeme bytes.</summary>
/// <remarks>This is a value model; no wire format, query parser, index or text renderer is implied.</remarks>
public sealed class TextVector : IEquatable<TextVector>
{
    /// <summary>The maximum UTF-8 bytes in an explicit lexeme.</summary>
    public const int MaxLexemeBytes = 2047;

    /// <summary>The maximum stored position; higher input positions clamp to this value.</summary>
    public const int MaxPosition = 16383;

    /// <summary>The maximum number of distinct positions in an explicit lexeme.</summary>
    public const int MaxPositionsPerLexeme = 256;

    /// <summary>The maximum PostgreSQL lexeme/position payload (one MiB minus one byte).</summary>
    public const int MaxPayloadBytes = (1 << 20) - 1;

    internal TextVector(TextLexeme[] lexemes, int payloadBytes)
    {
        Lexemes = Array.AsReadOnly(lexemes);
        PayloadBytes = payloadBytes;
    }

    /// <summary>Gets the empty vector.</summary>
    public static TextVector Empty { get; } = new(Array.Empty<TextLexeme>(), 0);

    /// <summary>Gets distinct lexemes in Unicode scalar (UTF-8 byte) order.</summary>
    public IReadOnlyList<TextLexeme> Lexemes { get; }

    /// <summary>Gets the PostgreSQL payload size, including alignment and position counts;
    /// excludes the datum header and four-byte lexeme directory entries.</summary>
    internal int PayloadBytes { get; }

    /// <summary>Creates a vector by merging explicit lexemes without applying an analyzer.</summary>
    /// <param name="lexemes">Lexemes; duplicate text merges its positions and weights.</param>
    /// <returns>An immutable vector with at most 256 positions per lexeme.</returns>
    /// <exception cref="ArgumentNullException">The sequence or one of its lexemes is null.</exception>
    /// <exception cref="TextException">The vector exceeds its payload limit.</exception>
    public static TextVector Create(IEnumerable<TextLexeme> lexemes)
    {
        ArgumentNullException.ThrowIfNull(lexemes);
        var builder = new TextVectorBuilder();
        foreach (TextLexeme lexeme in lexemes)
        {
            ArgumentNullException.ThrowIfNull(lexeme);
            builder.Add(lexeme.Text, lexeme.Positions);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public bool Equals(TextVector? other)
    {
        if (other is null || Lexemes.Count != other.Lexemes.Count)
        {
            return false;
        }

        for (int i = 0; i < Lexemes.Count; i++)
        {
            if (!Lexemes[i].Equals(other.Lexemes[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TextVector other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (TextLexeme lexeme in Lexemes)
        {
            hash.Add(lexeme);
        }

        return hash.ToHashCode();
    }
}
