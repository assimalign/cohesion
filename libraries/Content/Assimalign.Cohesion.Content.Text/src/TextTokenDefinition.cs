using System;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// A literal token recognized by <see cref="TextTokenizer"/>: the exact text to match, an optional
/// caller-assigned identifier for parser dispatch, and the kind matches are reported as.
/// </summary>
/// <remarks>
/// Matching is ordinal and case-sensitive. When definitions share a first character the longest
/// match wins, so registering both <c>**</c> and <c>*</c> tokenizes <c>**</c> as a single token.
/// Definitions are immutable and safe to share across tokenizers and options instances.
/// </remarks>
public sealed class TextTokenDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextTokenDefinition"/> class.
    /// </summary>
    /// <param name="text">The exact text to match. Must be non-empty.</param>
    /// <param name="id">An optional caller-assigned identifier carried by matching tokens for parser dispatch.</param>
    /// <param name="kind">The kind matching tokens are reported as. <see cref="TextTokenKind.Text"/> is not permitted — text is the implicit kind of runs between matches.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="text"/> is empty, or when <paramref name="kind"/> is <see cref="TextTokenKind.Text"/> or not a defined kind.</exception>
    public TextTokenDefinition(string text, int id = 0, TextTokenKind kind = TextTokenKind.Delimiter)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (kind is not (TextTokenKind.Delimiter or TextTokenKind.NewLine or TextTokenKind.Whitespace))
        {
            throw new ArgumentException("A definition must be a delimiter, new-line, or whitespace token; text is the implicit kind of runs between matches.", nameof(kind));
        }

        Text = text;
        Id = id;
        Kind = kind;
    }

    /// <summary>Gets the exact text this definition matches.</summary>
    public string Text { get; }

    /// <summary>Gets the caller-assigned identifier carried by matching tokens, or zero when unassigned.</summary>
    public int Id { get; }

    /// <summary>Gets the kind matching tokens are reported as.</summary>
    public TextTokenKind Kind { get; }

    /// <summary>Gets the definition for the carriage-return/line-feed terminator (<c>\r\n</c>).</summary>
    public static TextTokenDefinition CarriageReturnLineFeed { get; } = new("\r\n", kind: TextTokenKind.NewLine);

    /// <summary>Gets the definition for the line-feed terminator (<c>\n</c>).</summary>
    public static TextTokenDefinition LineFeed { get; } = new("\n", kind: TextTokenKind.NewLine);

    /// <summary>Gets the definition for the lone carriage-return terminator (<c>\r</c>).</summary>
    public static TextTokenDefinition CarriageReturn { get; } = new("\r", kind: TextTokenKind.NewLine);

    /// <summary>Returns the matched text of the definition.</summary>
    /// <returns>The exact text this definition matches.</returns>
    public override string ToString() => Text;
}
