using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A block quote: a container of block-level nodes.
/// </summary>
public sealed class MarkdownBlockQuote : MarkdownBlock
{
    /// <summary>Gets the blocks contained in the quote, in order.</summary>
    public IList<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();
}
