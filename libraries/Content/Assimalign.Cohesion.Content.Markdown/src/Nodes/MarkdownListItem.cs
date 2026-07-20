using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A single item of a <see cref="MarkdownList"/>: a container of block-level nodes. Items exist
/// only inside lists, so the type derives from <see cref="MarkdownNode"/> rather than
/// <see cref="MarkdownBlock"/>.
/// </summary>
public sealed class MarkdownListItem : MarkdownNode
{
    /// <summary>Gets the blocks contained in the item, in order.</summary>
    public IList<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();
}
