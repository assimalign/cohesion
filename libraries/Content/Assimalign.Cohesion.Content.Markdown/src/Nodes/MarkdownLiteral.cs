using System;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A run of literal text. Backslash escapes and character references are resolved during parsing,
/// so the text is the final, unencoded content.
/// </summary>
public sealed class MarkdownLiteral : MarkdownInline
{
    private string _text;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownLiteral"/> class.
    /// </summary>
    /// <param name="text">The literal text.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public MarkdownLiteral(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    /// <summary>Gets or sets the literal text.</summary>
    /// <exception cref="ArgumentNullException">Thrown when the value is <see langword="null"/>.</exception>
    public string Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _text = value;
        }
    }
}
