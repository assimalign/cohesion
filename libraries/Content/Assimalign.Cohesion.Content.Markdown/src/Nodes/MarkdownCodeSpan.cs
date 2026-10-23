using System;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// An inline code span: literal, uninterpreted content.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("MarkdownCodeSpan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MarkdownCodeSpan : MarkdownInline
{
    private string _literal;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownCodeSpan"/> class.
    /// </summary>
    /// <param name="literal">The literal content.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="literal"/> is <see langword="null"/>.</exception>
    public MarkdownCodeSpan(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        _literal = literal;
    }

    /// <summary>Gets or sets the literal content of the span.</summary>
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

    private sealed class DebugView
    {
        private readonly MarkdownCodeSpan _target;

        public DebugView(MarkdownCodeSpan value)
        {
            _target = value;
        }

        public string Literal => _target.Literal;
    }
}
