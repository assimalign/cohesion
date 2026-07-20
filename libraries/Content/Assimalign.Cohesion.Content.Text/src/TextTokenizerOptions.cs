using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Options controlling which tokens <see cref="TextTokenizer"/> recognizes.
/// </summary>
/// <remarks>
/// A tokenizer compiles the options into an internal match table when it is constructed; mutations
/// after that point affect only tokenizers constructed later. Reuse one options instance across the
/// tokenizers of a format rather than rebuilding the list per call.
/// </remarks>
public sealed class TextTokenizerOptions
{
    /// <summary>
    /// Gets the token definitions to recognize. The list is pre-populated with the default new-line
    /// definitions (<see cref="TextTokenDefinition.CarriageReturnLineFeed"/>,
    /// <see cref="TextTokenDefinition.LineFeed"/>, <see cref="TextTokenDefinition.CarriageReturn"/>);
    /// remove or replace those entries to override the defaults, and add definitions for the tokens a
    /// format cares about.
    /// </summary>
    public IList<TextTokenDefinition> Tokens { get; } = new List<TextTokenDefinition>
    {
        TextTokenDefinition.CarriageReturnLineFeed,
        TextTokenDefinition.LineFeed,
        TextTokenDefinition.CarriageReturn,
    };

    /// <summary>
    /// Gets or sets a value indicating whether runs of space and tab characters between tokens are
    /// emitted as <see cref="TextTokenKind.Whitespace"/> tokens instead of joining the surrounding
    /// text runs. The default is <see langword="false"/>.
    /// </summary>
    public bool TokenizeWhitespace { get; set; }
}
