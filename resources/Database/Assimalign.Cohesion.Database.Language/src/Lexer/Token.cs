using System;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a single lexical token scanned from a query statement.
/// </summary>
public readonly ref struct Token
{
    /// <summary>Initializes a token over a span of the original query source.</summary>
    /// <param name="type">The token classification.</param>
    /// <param name="value">The token text, borrowed from the source.</param>
    /// <param name="position">The zero-based character offset at which the token begins.</param>
    public Token(TokenType type, ReadOnlySpan<char> value, int position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    /// <summary>
    /// The classification of this token.
    /// </summary>
    public TokenType Type { get; }

    /// <summary>
    /// The raw text of the token from the source input.
    /// </summary>
    public ReadOnlySpan<char> Value { get; }

    /// <summary>
    /// The zero-based character position in the source where this token begins.
    /// </summary>
    public int Position { get; }

    /// <summary>Formats the token classification and source text.</summary>
    /// <returns>The classification followed by the token text.</returns>
    public override string ToString() => $"{Type}: {Value.ToString()}";
}
