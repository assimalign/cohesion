using System;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Configuration that tells <see cref="TokenLexer"/> which words are keywords or
/// built-in functions for a particular query language (SQL, OQL, GQL).
/// </summary>
public ref struct TokenLexerOptions
{
    /// <summary>
    /// Reserved keywords for the target language (e.g. SELECT, MATCH, DEFINE).
    /// </summary>
    public required ReadOnlySpan<string> Keywords { get; set; }

    /// <summary>
    /// Built-in function names for the target language (e.g. COUNT, SUM, size).
    /// </summary>
    public ReadOnlySpan<string> Functions { get; set; }

    /// <summary>
    /// When <c>true</c>, keyword and function matching is case-sensitive.
    /// Defaults to <c>false</c> (case-insensitive), which suits SQL and GQL.
    /// </summary>
    public bool IsCaseSensitive { get; set; }
}
