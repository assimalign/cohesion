namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Base type for the Markdown document model. The set of node kinds is closed: blocks derive from
/// <see cref="MarkdownBlock"/>, inlines from <see cref="MarkdownInline"/>, and
/// <see cref="MarkdownDocument"/> and <see cref="MarkdownListItem"/> are the two structural
/// containers outside those families.
/// </summary>
public abstract class MarkdownNode
{
    private protected MarkdownNode()
    {
    }
}
