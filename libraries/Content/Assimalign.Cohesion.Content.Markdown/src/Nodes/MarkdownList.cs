using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A bullet or ordered list: an ordered collection of list items with a tightness that controls
/// whether item paragraphs render wrapped (loose) or bare (tight).
/// </summary>
public sealed class MarkdownList : MarkdownBlock
{
    private int _start = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownList"/> class.
    /// </summary>
    /// <param name="isOrdered"><see langword="true"/> for an ordered list; <see langword="false"/> for a bullet list.</param>
    public MarkdownList(bool isOrdered)
    {
        IsOrdered = isOrdered;
    }

    /// <summary>Gets a value indicating whether the list is ordered.</summary>
    public bool IsOrdered { get; }

    /// <summary>Gets or sets the start number of an ordered list. Ignored for bullet lists. The default is one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int Start
    {
        get => _start;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _start = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the list is tight: item paragraphs render without
    /// paragraph wrapping and items without blank lines between them. The default is
    /// <see langword="true"/>.
    /// </summary>
    public bool IsTight { get; set; } = true;

    /// <summary>Gets the items of the list, in order.</summary>
    public IList<MarkdownListItem> Items { get; } = new List<MarkdownListItem>();

    /// <summary>Adds an item to the end of the list.</summary>
    /// <param name="item">The item to add.</param>
    public void Add(MarkdownListItem item) => Items.Add(item);
}
