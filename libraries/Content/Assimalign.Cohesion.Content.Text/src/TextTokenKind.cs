namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// The structural category of a <see cref="TextToken"/>.
/// </summary>
public enum TextTokenKind
{
    /// <summary>
    /// A run of characters between recognized tokens. Text is the implicit kind — it is produced by
    /// the tokenizer, never assigned to a <see cref="TextTokenDefinition"/>.
    /// </summary>
    Text = 0,

    /// <summary>A match of a <see cref="TextTokenDefinition"/> categorized as a structural delimiter.</summary>
    Delimiter,

    /// <summary>A match of a <see cref="TextTokenDefinition"/> categorized as a line terminator.</summary>
    NewLine,

    /// <summary>
    /// A run of space or tab characters (when <see cref="TextTokenizerOptions.TokenizeWhitespace"/>
    /// is enabled), or a match of a <see cref="TextTokenDefinition"/> categorized as whitespace.
    /// </summary>
    Whitespace,
}
