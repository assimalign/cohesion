using System;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A fenced code block: an optional info string (whose first word conventionally names the
/// language) and the literal, uninterpreted content.
/// </summary>
public sealed class MarkdownCodeBlock : MarkdownBlock
{
    private string _literal = string.Empty;

    /// <summary>
    /// Gets or sets the info string that followed the opening fence, or <see langword="null"/> when
    /// none was given.
    /// </summary>
    public string? Info { get; set; }

    /// <summary>Gets or sets the literal content of the block, line endings included.</summary>
    /// <exception cref="ArgumentNullException">Thrown when the value is <see langword="null"/>.</exception>
    public string Literal
    {
        get => _literal;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _literal = value;
        }
    }
}
