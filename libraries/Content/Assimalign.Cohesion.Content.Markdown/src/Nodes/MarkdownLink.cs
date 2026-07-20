using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// An inline link: a destination, an optional title, and the inline content of the link text.
/// Autolinks (<c>&lt;scheme:…&gt;</c>) parse into this node with the URI as both destination and
/// text.
/// </summary>
public sealed class MarkdownLink : MarkdownInline
{
    private string _destination;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownLink"/> class.
    /// </summary>
    /// <param name="destination">The link destination, unencoded.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="destination"/> is <see langword="null"/>.</exception>
    public MarkdownLink(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _destination = destination;
    }

    /// <summary>Gets or sets the link destination, unencoded.</summary>
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

    /// <summary>Gets or sets the link title, or <see langword="null"/> when none was given.</summary>
    public string? Title { get; set; }

    /// <summary>Gets the inline content of the link text, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
}
