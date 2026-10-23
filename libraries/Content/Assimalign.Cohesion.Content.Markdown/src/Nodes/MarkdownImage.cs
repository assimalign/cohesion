using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// An inline image: a source destination, an optional title, and the inline content used as the
/// alternative text.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("MarkdownImage")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MarkdownImage : MarkdownInline
{
    private string _destination;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownImage"/> class.
    /// </summary>
    /// <param name="destination">The image destination, unencoded.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="destination"/> is <see langword="null"/>.</exception>
    public MarkdownImage(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _destination = destination;
    }

    /// <summary>Gets or sets the image destination, unencoded.</summary>
    /// <exception cref="ArgumentNullException">Thrown when the value is <see langword="null"/>.</exception>
    public string Destination
    {
        get => _destination;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _destination = value;
        }
    }

    /// <summary>Gets or sets the image title, or <see langword="null"/> when none was given.</summary>
    public string? Title { get; set; }

    /// <summary>Gets the inline content rendered as the image's alternative text, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();

    private sealed class DebugView
    {
        private readonly MarkdownImage _target;

        public DebugView(MarkdownImage value)
        {
            _target = value;
        }

        public string Destination => _target.Destination;
        public string? Title => _target.Title;
        public IList<MarkdownInline> Inlines => _target.Inlines;
    }
}
